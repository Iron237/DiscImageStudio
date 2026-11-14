"""Immutable originals, automatic ArUco registration, manual four-center fallback."""
from __future__ import annotations
from concurrent.futures import ThreadPoolExecutor
import io
import math
import os
from pathlib import Path
import re
import numpy as np
import cv2
from PIL import Image, ImageCms, ImageOps, ImageDraw
from .common import SESSION_SCHEMA, linear, srgb, load_chart, load_session, save_json, sha, worker_limit

cv2.setNumThreads(1)

RAW_EXTENSIONS = {'.dng', '.nef', '.cr2', '.cr3', '.arw', '.rw2', '.orf', '.raf', '.pef'}


def fixed_white_balance(raw):
    values = list(raw.camera_whitebalance)
    if len(values) < 3 or not all(math.isfinite(v) and v > 0 for v in values[:3]):
        values = list(raw.daylight_whitebalance)
    if len(values) == 3:
        values.append(values[1])
    if len(values) == 4 and (not math.isfinite(values[3]) or values[3] <= 0):
        values[3] = values[1]  # RGB DNGs often leave the second green multiplier at zero.
    return values


def decode(path, white_balance=None):
    """All RAW frames share one WB; no per-frame auto exposure or tone mapping."""
    if Path(path).suffix.lower() in RAW_EXTENSIONS:
        import rawpy
        with rawpy.imread(str(path)) as raw:
            wb = white_balance or fixed_white_balance(raw)
            if len(wb) != 4 or not all(math.isfinite(v) and v > 0 for v in wb):
                raise ValueError('RAW 白平衡无效，请指定同一组固定白平衡。')
            values = raw.postprocess(use_auto_wb=False, use_camera_wb=False, user_wb=wb,
                                    no_auto_bright=True, gamma=(1, 1), output_bps=16,
                                    output_color=rawpy.ColorSpace.sRGB, adjust_maximum_thr=0, bright=1.)
        rgb = values.astype(np.float32) / 65535
        return rgb, dict(mode='RAW-linear-sRGB', whiteBalance=wb)
    with Image.open(path) as source:
        source = ImageOps.exif_transpose(source)
        if source.width * source.height > 128_000_000:
            raise ValueError('照片超过 1.28 亿像素，请先导出同一色彩设置的 TIFF。')
        icc = source.info.get('icc_profile')
        if icc:
            source = ImageCms.profileToProfile(source, ImageCms.ImageCmsProfile(io.BytesIO(icc)),
                                               ImageCms.createProfile('sRGB'), outputMode='RGB')
        values = np.asarray(source.convert('RGB'), dtype=np.float32) / 255
    return linear(values), dict(mode='processed-sRGB-approximation', whiteBalance=None)


def project(matrix, points):
    points = np.asarray(points, np.float64)
    homogeneous = np.column_stack((points, np.ones(len(points)))) @ np.asarray(matrix).T
    return homogeneous[:, :2] / homogeneous[:, 2:3]


def marker_corners(marker):
    x, y, half = marker['x'], marker['y'], marker['size'] / 2
    return [[x - half, y - half], [x + half, y - half],
            [x + half, y + half], [x - half, y + half]]


def registration(rgb, chart, manual=None):
    height, width = rgb.shape[:2]
    if manual is not None:
        points = np.asarray(manual, np.float64)
        if points.shape != (4, 2) or not np.isfinite(points).all() or np.any((points < 0) | (points > 1)):
            raise ValueError('按 T、R、B、L 顺序提供四个照片内的标记中心。')
        world = np.array([[m['x'], m['y']] for m in chart['markers']], np.float64)
        image_points = points * [width, height]
        matrix, _ = cv2.findHomography(world, image_points)
        if matrix is None or abs(np.linalg.det(matrix)) < 1e-9:
            raise ValueError('四点共线或次序不正确，无法配准。')
        # A convex marker-center quadrilateral is required; reject crossing clicks.
        if not cv2.isContourConvex(image_points.astype(np.float32)):
            raise ValueError('四点次序不正确，请依次点击 T、R、B、L。')
        return matrix, 'manual-four-centers', 0.
    display = np.uint8(np.clip(srgb(rgb), 0, 1) * 255 + .5)
    scale = min(1., 3200 / max(width, height))
    gray = cv2.cvtColor(display, cv2.COLOR_RGB2GRAY)
    if scale < 1:
        gray = cv2.resize(gray, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA)
    parameters = cv2.aruco.DetectorParameters()
    parameters.detectInvertedMarker = True
    parameters.cornerRefinementMethod = cv2.aruco.CORNER_REFINE_SUBPIX
    detector = cv2.aruco.ArucoDetector(cv2.aruco.getPredefinedDictionary(cv2.aruco.DICT_4X4_50), parameters)
    corners, ids, _ = detector.detectMarkers(gray)
    found = {} if ids is None else {int(i): c.reshape(4, 2) / scale for i, c in zip(ids.flat, corners)}
    pairs = [(m, found[m['id']]) for m in chart['markers'] if m['id'] in found]
    if len(pairs) < 3:
        return None, 'needs-manual-registration', None
    world = np.concatenate([marker_corners(m) for m, _ in pairs]).astype(np.float64)
    observed = np.concatenate([c for _, c in pairs]).astype(np.float64)
    matrix, mask = cv2.findHomography(world, observed, cv2.RANSAC, max(1.5, width / 1500))
    if matrix is None or int(mask.sum()) < 10:
        return None, 'needs-manual-registration', None
    residual = float(np.sqrt(np.mean(np.sum((project(matrix, world) - observed) ** 2, axis=1))))
    if residual > max(3., width / 700):
        return None, 'registration-residual-too-large', residual
    return matrix, 'automatic-aruco', residual


def sample_patches(rgb, matrix, chart):
    height, width = rgb.shape[:2]
    patches = chart['patches']
    # Center ROI limits angular averaging and excludes adjacent motifs/gaps.
    offsets = np.linspace(-.22, .22, 9) * chart['patchSizeMm']
    grid = np.array([(x, y) for y in offsets for x in offsets])
    centers = np.array([[p['x'], p['y']] for p in patches])
    pixels = project(matrix, (centers[:, None, :] + grid).reshape(-1, 2)).reshape(len(patches), -1, 2)
    inside = np.all((pixels[:, :, 0] >= 1) & (pixels[:, :, 0] < width - 2)
                    & (pixels[:, :, 1] >= 1) & (pixels[:, :, 1] < height - 2), axis=1)
    sampled = cv2.remap(rgb, pixels[:, :, 0].astype(np.float32), pixels[:, :, 1].astype(np.float32),
                        cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT, borderValue=0)
    # The central ROI must span enough source pixels for meaningful averaging.
    spans = np.linalg.norm(pixels[:, 0] - pixels[:, -1], axis=1)
    clipped = np.mean(np.any(sampled >= .985, axis=2), axis=1)
    ordered = np.sort(sampled, axis=1)
    means = np.mean(ordered[:, 8:-8], axis=1)
    spread = np.mean(np.std(sampled, axis=1), axis=1)
    return [dict(patch=p['id'], linear=means[i].astype(float).tolist(),
                 valid=bool(inside[i] and spans[i] >= 6 and clipped[i] <= .05),
                 clippedFraction=float(clipped[i]), roiSpanPixels=float(spans[i]),
                 spatialStd=float(spread[i])) for i, p in enumerate(patches)]


def preview(rgb, path, matrix, chart, observations=None):
    display = Image.fromarray(np.uint8(np.clip(srgb(rgb), 0, 1) * 255 + .5))
    display.thumbnail((1600, 1600), Image.Resampling.LANCZOS)
    if matrix is not None:
        draw = ImageDraw.Draw(display)
        scale = display.width / rgb.shape[1]
        states = {} if observations is None else {o['patch']: o['valid'] for o in observations}
        for patch in chart['patches']:
            x, y = patch['x'], patch['y']
            half = chart['patchSizeMm'] * .22
            corners = project(matrix, [[x-half, y-half], [x+half, y-half], [x+half, y+half], [x-half, y+half]]) * scale
            draw.line([tuple(p) for p in corners] + [tuple(corners[0])],
                      fill='#48b275' if states.get(patch['id'], False) else '#ed5454', width=1)
        for marker, center in zip(chart['markers'], project(matrix, [[m['x'], m['y']] for m in chart['markers']])):
            x, y = center * scale
            draw.ellipse((x - 6, y - 6, x + 6, y + 6), outline='#00ff66', width=2)
            draw.text((x + 8, y), marker['name'], fill='#00ff66')
    display.save(path)


def process_frame(path, chart, directory, white_balance=None, manual=None):
    path = Path(path).resolve()
    digest = sha(path)
    rgb, processing = decode(path, white_balance)
    matrix, mode, residual = registration(rgb, chart, manual)
    frame_id = digest[:20]
    image_path = Path(directory) / (frame_id + '.png')
    sampled = [] if matrix is None else sample_patches(rgb, matrix, chart)
    preview(rgb, image_path, matrix, chart, sampled)
    frame = dict(id=frame_id, path=str(path), sha256=digest, width=rgb.shape[1], height=rgb.shape[0],
                 previewPath=str(image_path), processing=processing, registration=mode,
                 reprojectionPixels=residual, homography=None if matrix is None else matrix.tolist(),
                 observations=sampled)
    if manual is not None:
        frame['manualPoints'] = manual
    return frame


def update_poses(session):
    registered = [f for f in session['frames'] if f['homography'] is not None]
    if not registered:
        session['summary'] = '未识别到完整定位标记：请选择照片执行四点配准。'
        return
    reference = next((f for f in registered if f['id'] == session.get('referenceFrameId')), registered[0])
    session['referenceFrameId'] = reference['id']
    inverse = np.linalg.inv(reference['homography'])
    for frame in session['frames']:
        frame['poseValid'] = False
        if frame['homography'] is None:
            frame['status'] = '需四点配准'
            continue
        relative = inverse @ frame['homography']
        relative /= relative[2, 2]
        singular = np.linalg.svd(relative[:2, :2], compute_uv=False)
        drift = float(np.linalg.norm(relative[:2, 2]))
        perspective = float(np.linalg.norm(relative[2, :2]) * 60)
        good = bool(np.all(np.abs(singular - 1) < .035) and drift < .8 and perspective < .025)
        angle = math.degrees(math.atan2(relative[1, 0] - relative[0, 1], relative[0, 0] + relative[1, 1])) % 360
        frame.update(rotationDeg=angle, poseValid=good, centerDriftMm=drift,
                     status=('已配准' if good else '相机或盘心移动，需重拍'),
                     validPatches=sum(o['valid'] for o in frame['observations']))
    good_count = sum(f['poseValid'] for f in session['frames'])
    modes = {f['processing']['mode'] for f in session['frames']}
    note = '；JPEG/TIFF 为相机处理后的近似响应' if 'processed-sRGB-approximation' in modes else ''
    session['summary'] = f'{good_count}/{len(session["frames"])} 张照片配准可用' + note


def import_photos(request, emit=lambda *args: None):
    chart_path = Path(request['chartPath']).resolve()
    chart = load_chart(chart_path)
    root = Path(request['directory']).resolve()
    root.mkdir(parents=True, exist_ok=True)
    session_path = root / 'session.json'
    if session_path.exists():
        session = load_session(session_path)
        if session['chartSha256'] != sha(chart_path):
            raise ValueError('这个会话属于另一张标定盘。')
    else:
        session = dict(schema=SESSION_SCHEMA, chartPath=str(chart_path), chartSha256=sha(chart_path),
                       capture=request.get('capture', chart.get('capture', {})), frames=[],
                       synthetic=bool(request.get('synthetic', False)))
    raw_wb = session.get('rawWhiteBalance')
    paths = sorted(set(str(Path(p).resolve()) for p in request['photos']),
                   key=lambda p: [int(v) if v.isdigit() else v.lower() for v in re.split(r'(\d+)', p)])
    if not paths:
        raise ValueError('请选择原始照片。')
    first_raw = next((p for p in paths if Path(p).suffix.lower() in RAW_EXTENSIONS), None)
    if first_raw and raw_wb is None:
        import rawpy
        with rawpy.imread(first_raw) as raw:
            raw_wb = fixed_white_balance(raw)
        session['rawWhiteBalance'] = raw_wb
    (root / 'previews').mkdir(exist_ok=True)
    # Decode several images concurrently, without multiplying full-resolution image buffers by every CPU.
    workers = min(len(paths), worker_limit(1024**3))
    emit(f'照片处理：{workers} 个并行任务；原图不改写。', 0.)
    with ThreadPoolExecutor(max_workers=workers) as pool:
        futures = [pool.submit(process_frame, p, chart, root / 'previews', raw_wb) for p in paths]
        for index, (path, future) in enumerate(zip(paths, futures)):
            try:
                frame = future.result()
                session['frames'] = [f for f in session['frames'] if f['path'] != path]
                if not any(f['id'] == frame['id'] for f in session['frames']):
                    session['frames'].append(frame)
                emit(f'已取样 {index + 1}/{len(paths)}：{Path(path).name} · {frame["registration"]}', (index + 1) / len(paths))
            except Exception as error:
                session.setdefault('importErrors', []).append(dict(path=path, error=str(error)))
                emit(f'照片未导入：{Path(path).name} · {error}')
            update_poses(session)
            save_json(session_path, session)
    return str(session_path)


def register_photo(request, emit=lambda *args: None):
    path = Path(request['sessionPath']).resolve()
    session = load_session(path)
    chart = load_chart(session['chartPath'])
    index = next(i for i, f in enumerate(session['frames']) if f['id'] == request['frameId'])
    old = session['frames'][index]
    if sha(old['path']) != old['sha256']:
        raise ValueError('照片原文件已改变，请重新导入。')
    frame = process_frame(old['path'], chart, path.parent / 'previews', session.get('rawWhiteBalance'), request['points'])
    session['frames'][index] = frame
    update_poses(session)
    save_json(path, session)
    emit(session['summary'], 1.)
    return str(path)
