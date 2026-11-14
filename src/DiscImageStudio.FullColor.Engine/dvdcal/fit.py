"""Build a DCS v3 LUT from measured convex mixtures, without the unpublished v5 trainer.

Every nonzero recipe component has observed angular support. No 95058 response
is used as a fallback. Validation mixtures and radial repeats are held out.
"""
from __future__ import annotations
from concurrent.futures import ThreadPoolExecutor
import gzip
import hashlib
import math
import os
from pathlib import Path
import struct
import shutil
import numpy as np
from scipy.spatial import ConvexHull, Delaunay, QhullError
from PIL import Image, ImageDraw
from .common import PROFILE_SCHEMA, load_session, session_chart, save_json, sha, worker_limit

METRIC = np.sqrt([.25, .5, .25])
LUMA = np.array([.2126, .7152, .0722])


def use_frame(frame, experimental=False):
    return bool(frame.get('poseValid') or (experimental and frame.get('homography') is not None
                                         and frame.get('observations')))


def observations(session, chart, experimental=False):
    by_id = {p['id']: p for p in chart['patches']}
    samples = [[] for _ in chart['textures']]
    validation = []
    for frame in session['frames']:
        if not use_frame(frame, experimental):
            continue
        for obs in frame['observations']:
            if not obs['valid']:
                continue
            p = by_id[obs['patch']]
            beta = (math.degrees(math.atan2(p['y'], p['x'])) + frame['rotationDeg']) % 360
            value = np.array(obs['linear'], float)
            if value.shape != (3,) or not np.isfinite(value).all() or np.any((value < 0) | (value > 1)):
                raise ValueError('取样颜色必须是 0–1 范围内的有限线性 RGB。')
            if p['kind'] == 'pure':
                samples[p['texture']].append((beta, value))
            else:
                validation.append((p, beta, value))
    curves = []
    for values in samples:
        values.sort(key=lambda item: item[0])
        # Multiple captures of the same pose are averaged, never counted as new angles.
        grouped = {}
        for angle, rgb in values:
            grouped.setdefault(round(angle, 4), []).append(rgb)
        angles = np.array(sorted(grouped), float)
        colors = np.array([np.mean(grouped[a], axis=0) for a in angles]).reshape(-1, 3)
        if len(angles) >= 2:
            curves.append((np.r_[angles[-2:] - 360, angles, angles[:2] + 360],
                           np.concatenate((colors[-2:], colors, colors[:2]))))
        else:
            curves.append((angles, colors))
    return curves, validation


def response(curve, angles, max_gap=7.5):
    x, colors = curve
    angles = np.asarray(angles, float)
    values = np.full((angles.size, 3), np.nan)
    if len(x) < 2:
        return values
    right = np.clip(np.searchsorted(x, angles, side='right'), 1, len(x) - 1)
    left = right - 1
    span = x[right] - x[left]
    supported = (angles >= x[0]) & (angles <= x[-1]) & (span <= max_gap + 1e-6)
    f = np.clip((angles - x[left]) / np.maximum(span, 1e-12), 0, 1)
    values[supported] = ((1 - f[:, None]) * colors[left] + f[:, None] * colors[right])[supported]
    return values


def grid_targets(black, white):
    rgb = np.stack(np.meshgrid(*([np.linspace(0, 1, 17)] * 3), indexing='ij'), axis=-1).reshape(-1, 3)
    rgb = np.where(rgb <= .04045, rgb / 12.92, ((rgb + .055) / 1.055) ** 2.4)
    y = rgb @ LUMA
    target_y = (np.cbrt(black) + (np.cbrt(white) - np.cbrt(black)) * np.cbrt(y)) ** 3
    result = rgb * (target_y / np.maximum(y, 1e-30))[:, None]
    result[y == 0] = target_y[y == 0, None]
    return result


def project_recipes(colors, texture_ids, targets):
    """Exact convex interpolation inside; closest hull triangle outside the gamut."""
    unique, first = np.unique(np.round(colors, 9), axis=0, return_index=True)
    ids = np.asarray(texture_ids)[first]
    points = unique * METRIC
    triangulation = Delaunay(points)
    scaled = targets * METRIC
    cells = triangulation.find_simplex(scaled, tol=1e-9)
    result_ids = np.zeros((len(targets), 4), dtype='<u2')
    result_weights = np.zeros((len(targets), 4), dtype='<f4')
    inside = cells >= 0
    selected = cells[inside]
    transform = triangulation.transform[selected]
    bary = np.einsum('ijk,ik->ij', transform[:, :3], scaled[inside] - transform[:, 3])
    bary = np.column_stack((bary, 1 - bary.sum(axis=1)))
    bary = np.maximum(0, bary)
    bary /= bary.sum(axis=1, keepdims=True)
    result_ids[inside] = ids[triangulation.simplices[selected]]
    result_weights[inside] = bary
    outside = np.flatnonzero(~inside)
    if len(outside):
        query = scaled[outside]
        best = np.full(len(query), np.inf)
        for triangle in ConvexHull(points).simplices:
            a, b, c = points[triangle]
            ab, ac = b - a, c - a
            normal = np.cross(ab, ac)
            norm2 = normal @ normal
            if norm2 < 1e-24:
                continue
            projection = query - ((query - a) @ normal / norm2)[:, None] * normal
            dot00, dot01, dot11 = ab @ ab, ab @ ac, ac @ ac
            denom = dot00 * dot11 - dot01 * dot01
            u = ((projection - a) @ ab * dot11 - (projection - a) @ ac * dot01) / denom
            v = ((projection - a) @ ac * dot00 - (projection - a) @ ab * dot01) / denom
            weights = np.column_stack((1 - u - v, u, v))
            valid = np.all(weights >= -1e-10, axis=1)
            candidates = [(weights, projection, valid)]
            for i, j in ((0, 1), (1, 2), (0, 2)):
                start, end = points[triangle[i]], points[triangle[j]]
                edge = end - start
                t = np.clip((query - start) @ edge / max(edge @ edge, 1e-24), 0, 1)
                w = np.zeros((len(query), 3))
                w[:, i], w[:, j] = 1 - t, t
                candidates.append((w, start + t[:, None] * edge, np.ones(len(query), bool)))
            for weights, projected, valid in candidates:
                distance = np.sum((query - projected) ** 2, axis=1)
                better = valid & (distance < best)
                best[better] = distance[better]
                result_ids[outside[better], :3] = ids[triangle]
                result_weights[outside[better], :3] = np.maximum(0, weights[better])
        result_weights[outside] /= result_weights[outside].sum(axis=1, keepdims=True)
    return result_ids, result_weights, float(np.mean(inside))


def tone_range(colors):
    hull = ConvexHull(colors)
    coefficient = hull.equations[:, :3].sum(axis=1)
    bounds = -hull.equations[:, 3] / np.where(abs(coefficient) > 1e-12, coefficient, 1)
    lower = max(0., float(np.max(bounds[coefficient < -1e-12], initial=0)))
    upper = min(1., float(np.min(bounds[coefficient > 1e-12], initial=1)))
    neutral = upper > lower + 1e-5 and not np.any((abs(coefficient) <= 1e-12) & (hull.equations[:, 3] > 1e-10))
    if neutral:
        black = max(1e-6, lower + (upper - lower) * .002)
        white = upper - (upper - lower) * .002
    else:
        values = colors @ LUMA
        black, white = max(1e-6, float(np.quantile(values, .02))), float(np.quantile(values, .98))
    if white <= black + 1e-5:
        raise ValueError('该角度缺少可区分的明暗范围。')
    return black, white, neutral, hull


def report_files(root, report, coverage=None):
    save_json(root / 'quality.json', report)
    text = ['DVD-R 43533 实测色表质量报告', '', report['status'], report.get('message', ''),
            '', '颜色误差是相机相对线性 RGB 误差，不是标准 ΔE。',
            '新色表仍需另用一张盘验证实际图片；软件报告不等于实盘认证。', '']
    for key, value in report.items():
        if key not in ('message', 'status'):
            text.append(f'{key}: {value}')
    (root / 'quality.txt').write_text('\n'.join(text), encoding='utf-8-sig')
    if coverage is not None:
        image = Image.new('RGB', (1080, 160), '#202124')
        draw = ImageDraw.Draw(image)
        for i, value in enumerate(coverage[:360]):
            color = (int(255 * (1 - value)), int(200 * value), 60)
            draw.rectangle((i * 3, 159 - int(value * 140), i * 3 + 2, 159), fill=color)
        image.save(root / 'coverage.png')


def build_profile(request, emit=lambda *args: None):
    session_path = Path(request['sessionPath']).resolve()
    session = load_session(session_path)
    if 'capture' in request:
        session['capture'] = request['capture']
        save_json(session_path, session)
    chart = session_chart(session)
    root = Path(request['directory']).resolve()
    root.mkdir(parents=True, exist_ok=False)
    experimental = request.get('experimental') is True
    gap = float(request.get('maxResponseGapDeg', 30 if experimental else 7.5))
    if not math.isfinite(gap) or not 7.5 <= gap <= 60 or (not experimental and gap != 7.5):
        raise ValueError('放宽角度插值必须明确使用实验建表；实验间隔范围为 7.5–60°。')
    curves, validation = observations(session, chart, experimental)
    angles = np.arange(-1, 362, dtype=float)
    cube = np.stack([response(curve, angles, gap) for curve in curves], axis=1)
    finite = np.isfinite(cube).all(axis=2)
    supported = finite[:-2] & finite[1:-1] & finite[2:]
    coverage = supported.mean(axis=1)
    minimum = float(request.get('minimumCoverage', .9))
    width = int(request.get('windowWidthDeg', 60))
    if not .5 <= minimum <= 1 or not 5 <= width <= 120:
        raise ValueError('覆盖率必须在 50%–100%，观看窗口必须在 5°–120°。')
    report = dict(schema='dis-dvd-color-quality-v1', status='needs-data',
                  measuredTextures=sum(len(c[0]) >= 2 for c in curves),
                  usableFrames=sum(f.get('poseValid', False) for f in session['frames']),
                  photoCount=len(session['frames']), errorUnits='normalized camera-relative linear RGB',
                  synthetic=bool(session.get('synthetic')), physicalColorValidated=False)
    if experimental:
        report.update(experimental=True, standardValidationPassed=False,
                      trainingFrames=sum(use_frame(f, True) for f in session['frames']),
                      overrides=dict(maxResponseGapDeg=gap, standardMaxResponseGapDeg=7.5,
                                     minimumCoverage=minimum,
                                     acceptedUnstablePoseFrames=sum(use_frame(f, True) and not f.get('poseValid', False)
                                                                   for f in session['frames'])))
    scores = np.full(361, -np.inf)
    for angle in range(361):
        if coverage[angle] < minimum:
            continue
        colors = cube[angle + 1, supported[angle]]
        covariance = np.cov(colors.T)
        white = max(1e-6, float(np.quantile(colors @ LUMA, .95)))
        if np.min(np.linalg.eigvalsh(covariance / white**2)) < 1e-8:
            continue
        scores[angle] = float(max(0, np.linalg.det(covariance)) ** (1 / 6) / white)
    requested_start = request.get('windowStartDeg')
    starts = range(0, 361 - width) if requested_start is None else [int(requested_start)]
    candidates = [(float(np.mean(scores[start:start + width + 1])), start)
                  for start in starts if 0 <= start <= 360 - width
                  and np.isfinite(scores[start:start + width + 1]).all()]
    if not candidates:
        report['message'] = '没有覆盖足够纹理的连续观看窗口。请查看 coverage.png，补拍缺角度、过曝或未配准的照片。'
        report_files(root, report, coverage)
        raise ValueError(report['message'] + ' 报告：' + str(root / 'quality.txt'))
    _, start = max(candidates)
    end = start + width
    emit(f'实测观看窗口：{start}°–{end}°；按凸组合重新计算配方，不借用 95058 颜色。', .05)
    nodes = []
    payloads = []

    def build_node(angle):
        ids = np.flatnonzero(supported[angle])
        colors = cube[angle + 1, ids]
        black, white, neutral, hull = tone_range(colors)
        target = grid_targets(black, white)
        indices, weights, reachable = project_recipes(colors, ids, target)
        dtype = np.dtype([('ids', '<u2', (4,)), ('weights', '<f4', (4,))])
        packed = np.empty(4913, dtype=dtype)
        packed['ids'], packed['weights'] = indices, weights
        node = dict(angle=angle, candidateCount=len(ids), toneBlackY=black, toneWhiteY=white,
                    vertices=[], equations=hull.equations.tolist(), gridReachableFraction=reachable,
                    neutralRangeMeasured=neutral)
        return node, packed.tobytes()

    workers = min(worker_limit(128 * 1024**2), width + 1)
    try:
        with ThreadPoolExecutor(max_workers=workers) as pool:
            for i, (node, payload) in enumerate(pool.map(build_node, range(start, end + 1))):
                nodes.append(node)
                payloads.append(payload)
                emit(f'实测 LUT {i + 1}/{width + 1} · {workers} 个并行任务', .05 + .8 * (i + 1) / (width + 1))
    except (QhullError, ValueError) as error:
        report.update(status='needs-data', message='实测色域退化或明暗范围不足，请检查光路和取样照片：' + str(error)[:250])
        report_files(root, report, coverage)
        raise ValueError(report['message']) from error
    reference = float(np.median([node['toneWhiteY'] for node in nodes]))
    errors = {'repeat': [], 'holdout-mixture': []}
    for patch, beta, measured in validation:
        if not start <= beta <= end:
            continue
        components = [response(curves[c['texture']], [beta], gap)[0] * c['weight'] for c in patch['components']]
        predicted = np.sum(components, axis=0)
        if np.isfinite(predicted).all():
            errors[patch['kind']].append(float(np.sqrt(np.sum(((predicted - measured) * METRIC) ** 2)) / reference))
    report.update(windowStartDeg=start, windowEndDeg=end, minimumCoverage=float(min(coverage[start:end + 1])),
                  meanGridReachableFraction=float(np.mean([n['gridReachableFraction'] for n in nodes])),
                  buildMethod='measured-convex-RGB-DCS-v3; not the unpublished DCS-v5 trainer')
    for kind, values in errors.items():
        report[kind] = dict(count=len(values), median=None if not values else float(np.median(values)),
                            p95=None if not values else float(np.quantile(values, .95)))
    passed = all(len(v) >= 24 and np.median(v) <= .12 and np.quantile(v, .95) <= .35 for v in errors.values())
    if not passed and not experimental:
        report.update(status='review-required', message='重复块或独立混色验证未通过工程阈值（中位误差≤0.12，P95≤0.35）。请复查配准、曝光、半径差异和光路；本次不发布可应用色表。')
        report_files(root, report, coverage)
        raise ValueError(report['message'] + ' 报告：' + str(root / 'quality.txt'))
    if experimental:
        report.update(status='experimental-unvalidated', independentChecksPassed=bool(passed),
                      message='实验色表：已按明确要求接纳摆位偏差、放宽角度插值并保留验证误差；未通过标准标定，供预览和试验。')
    else:
        report.update(status='software-checks-passed', message='覆盖率、重复块与独立混色块检查通过；等待新盘验证图片效果。')
    report_files(root, report, coverage)
    lut = b'DCCLUT01' + struct.pack('<III', 17, len(nodes), 4913) + b''.join(payloads)
    lut_path = root / 'measured-lut.bin.gz'
    with gzip.GzipFile(filename=str(lut_path), mode='wb', mtime=0) as stream:
        stream.write(lut)
    textures = []
    for texture, curve in zip(chart['textures'], curves):
        x, colors = curve
        margin = max(8., gap + .5)
        samples = [dict(angle=float(a), linear=np.round(c, 9).tolist())
                   for a, c in zip(x, colors) if start - margin <= a <= end + margin]
        textures.append(dict(index=texture['index'], cellId=f'T{texture["index"]:04}',
                             textureId=f'T{texture["index"]:04}', motif=texture['motif'], samples=samples))
    provenance = dict(schema=PROFILE_SCHEMA, media=chart['targetMedia'], geometry=chart['geometry'],
                      chartSha256=session['chartSha256'], sessionSha256=sha(session_path),
                      reportSha256=sha(root / 'quality.json'), capture=session.get('capture', {}),
                      sourcePhotoHashes=[f['sha256'] for f in session['frames'] if use_frame(f, experimental)],
                      synthetic=bool(session.get('synthetic')), physicalColorValidated=False,
                      validationStatus=report['status'], errorUnits=report['errorUnits'])
    if experimental:
        provenance.update(experimental=True, standardValidationPassed=False, overrides=report['overrides'])
    # Later photo appends/registration edits must not change a previously built profile's evidence.
    shutil.copyfile(session_path, root / 'measurements.json')
    shutil.copyfile(session['chartPath'], root / 'chart.json')
    profile = dict(format='dvd-continuous-color-map-v3', mappingMode='luminance-slice-hue-v1',
                   sourceSha256=sha(session_path), lutSha256=hashlib.sha256(lut).hexdigest().upper(),
                   lutFile=lut_path.name, gridSize=17, recipeComponents=4, recipeBanks=1,
                   referenceLinearWhite=reference, neutralCoolShift=0, maxResponseGapDeg=gap,
                   ditherSeed=17, windowStartDeg=start, windowEndDeg=end, modelStartDeg=start,
                   modelEndDeg=end, nodes=nodes, textures=textures, disCalibration=provenance)
    save_json(root / 'profile.json', profile)
    emit('自建实测色表和质量报告已保存。', 1.)
    return str(root / 'profile.json')
