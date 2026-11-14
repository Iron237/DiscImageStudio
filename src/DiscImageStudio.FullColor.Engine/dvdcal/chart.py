"""One-disc search chart. Values are logical scrambled motifs, never RGB colors."""
from __future__ import annotations
import csv
import json
import math
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw, ImageFont
import cv2
from .common import CHART_SCHEMA, REVISION, read_json, save_json, sha


def create_chart(request, emit=lambda *args: None):
    root = Path(request['directory']).resolve()
    root.mkdir(parents=True, exist_ok=False)
    geometry = request['geometry']
    inner, outer = geometry['innerRadiusMm'], geometry['outerRadiusMm']
    if (geometry['sectors'] != 2297888 or not 0 < inner < outer <= 60
            or not all(math.isfinite(float(v)) for v in geometry.values())):
        raise ValueError('请先应用威宝 43533 的完整盘面几何。')
    source = read_json(request['sourceProfilePath'])
    textures = [{k: t[k] for k in ('index', 'motif')} for t in source['textures']]
    if len(textures) != 1154 or textures[1152]['motif'] != 'A5' or textures[1153]['motif'] != '92':
        raise ValueError('DCS 原始纹理清单不完整。')
    dictionary = cv2.aruco.getPredefinedDictionary(cv2.aruco.DICT_4X4_50)
    marker_radius = min(53., outer - 4.5)
    markers = []
    glyphs = {'T': ['11111','00100','00100','00100','00100','00100','00100'],
              'R': ['11110','10001','10001','11110','10100','10010','10001'],
              'B': ['11110','10001','10001','11110','10001','10001','11110'],
              'L': ['10000','10000','10000','10000','10000','10000','11111']}
    for i, (name, x, y) in enumerate([('T', 0, -marker_radius), ('R', marker_radius, 0),
                                     ('B', 0, marker_radius), ('L', -marker_radius, 0)]):
        bits = cv2.aruco.generateImageMarker(dictionary, 10 + i, 6)
        markers.append(dict(id=10 + i, name=name, x=x, y=y, size=4.8, quiet=.8,
                            bits=[''.join('1' if v else '0' for v in row) for row in bits],
                            labelX=x * .87, labelY=y * .87, labelCellMm=.5, labelBits=glyphs[name]))
    pitch, size = 2.1, 1.8
    slots = []
    for gy in range(-29, 30):
        for gx in range(-29, 30):
            x, y = gx * pitch, gy * pitch
            radii = [math.hypot(x + dx, y + dy) for dx in (-size / 2, size / 2)
                     for dy in (-size / 2, size / 2)]
            if min(radii) < inner + .6 or max(radii) > outer - .6:
                continue
            if any(abs(x - m['x']) < 4.5 and abs(y - m['y']) < 4.5 for m in markers):
                continue
            if any(abs(x - m['labelX']) < 3 and abs(y - m['labelY']) < 3.5 for m in markers):
                continue
            slots.append(dict(gridX=gx, gridY=gy, x=x, y=y, radius=math.hypot(x, y)))
    if len(slots) < 1330:
        raise ValueError('当前几何放不下完整的一张盘标定布局。')
    rng = np.random.default_rng(43533)
    rng.shuffle(slots)
    patches = []

    def place(kind, components, radius=None, texture=None):
        index = len(slots) - 1 if radius is None else min(range(len(slots)), key=lambda i: abs(slots[i]['radius'] - radius))
        position = slots.pop(index)
        patches.append(dict(id=f'P{len(patches):04}', kind=kind, texture=texture,
                            components=components, **position))

    anchors = set(np.linspace(0, 1153, 64, dtype=int).tolist())
    for t in textures:
        i = t['index']
        place('pure', [dict(texture=i, weight=1.)], (inner + outer) / 2 if i in anchors else None, i)
    for radius in (inner + 4.5, outer - 4.5):
        for i in sorted(anchors):
            place('repeat', [dict(texture=i, weight=1.)], radius, i)
    for i in range(48):
        a, b = int((i * 97) % 1152), int((i * 173 + 251) % 1152)
        weight = (.25, .5, .75)[i % 3]
        place('holdout-mixture', [dict(texture=min(a, b), weight=weight),
                                  dict(texture=max(a, b), weight=1 - weight)])
    chart = dict(schema=CHART_SCHEMA, sourceRevision=REVISION, targetMedia='Verbatim DVD-R 43533',
                 geometry=geometry, pitchMm=pitch, patchSizeMm=size, backgroundTexture=1152,
                 ditherSeed=17, textures=textures, patches=patches, markers=markers,
                 intendedWriteSpeed='4x', capture=request.get('capture', {}),
                 physicalColorValidated=False, layoutColors='texture identifiers, not predicted colors')
    save_json(root / 'chart.json', chart)
    render_layout(chart, root / 'layout.png')
    with (root / 'patch-key.csv').open('w', encoding='utf-8-sig', newline='') as stream:
        writer = csv.writer(stream)
        writer.writerow(['patch', 'kind', 'x_mm', 'y_mm', 'radius_mm', 'components_json'])
        for p in patches:
            writer.writerow([p['id'], p['kind'], p['x'], p['y'], p['radius'], json.dumps(p['components'])])
    with (root / 'capture-plan.csv').open('w', encoding='utf-8-sig', newline='') as stream:
        writer = csv.writer(stream)
        writer.writerow(['file_name', 'nominal_rotation_deg', 'notes'])
        for angle in range(0, 360, 5):
            writer.writerow([f'{angle:03}deg.DNG', angle, '固定相机、灯、曝光、白平衡；只绕盘心旋转'])
    instructions = '''43533 一张盘纹理标定\n\nlayout.png 是纹理编号示意图，不是显色预测，不要将它作为图片转换刻录。\n使用 DIS 的“导出标定 ISO”生成完整镜像，以空白 43533 DVD-R、同一光驱支持的 4× 写入一次。\n四个定位标记：上 T=10、右 R=11、下 B=12、左 L=13。标记与照片原图均须清晰。\n固定一盏白光单 LED 灯和相机，找好显色位置后记录灯和相机的位置，保持不动。\n锁定 ISO、快门、对焦、白平衡；关闭 HDR、夜景和滤镜，优先 RAW/DNG。\n每转约 5° 拍摄一张完整盘面原图，转满 360°；首尾补拍同一方向检查漂移。\n导入照片后优先自动定位；识别失败可在 GUI 中依 T、R、B、L 顺序点击四个标记中心。\n照片过曝、模糊、角度不足时重拍同一张盘，不需要因此重烧。\n相机照片建立的是固定光路的相对颜色响应，不能冒充标准 XYZ 或通用光谱测量。\n重复块用于检查半径差异；混合验证块不参与基础响应拟合。\n质量报告通过后，再用一张新盘验证人物、肤色、灰阶和渐变。\n'''
    (root / '拍摄说明.txt').write_text(instructions, encoding='utf-8-sig')
    emit(f'标定布局已生成：1154 个基础纹理、128 个径向重复块、48 个混色验证块，{len(slots)} 个空余位置。', 1.)
    return str(root / 'chart.json')


def render_layout(chart, path, pixels=1800):
    image = Image.new('RGB', (pixels, pixels), '#202124')
    draw = ImageDraw.Draw(image)
    unit = pixels / 120
    def box(x, y, size):
        return tuple(round(v * unit) for v in (x - size / 2 + 60, y - size / 2 + 60,
                                               x + size / 2 + 60, y + size / 2 + 60))
    g = chart['geometry']
    draw.ellipse(box(0, 0, g['outerRadiusMm'] * 2), fill='#404248')
    draw.ellipse(box(0, 0, g['innerRadiusMm'] * 2), fill='#202124')
    for p in chart['patches']:
        i = p['texture'] if p['texture'] is not None else 1300 + int(p['id'][1:])
        color = tuple(64 + ((i * v + 31) % 176) for v in (67, 113, 157))
        draw.rectangle(box(p['x'], p['y'], chart['patchSizeMm']), fill=color)
    for marker in chart['markers']:
        draw.rectangle(box(marker['x'], marker['y'], marker['size'] + 2 * marker['quiet']), fill='white')
        for row, line in enumerate(marker['bits']):
            for col, bit in enumerate(line):
                side = marker['size'] / 6
                draw.rectangle(box(marker['x'] + (col - 2.5) * side,
                                   marker['y'] + (row - 2.5) * side, side), fill='white' if bit == '1' else 'black')
        cell = marker['labelCellMm']
        for row, line in enumerate(marker['labelBits']):
            for col, bit in enumerate(line):
                draw.rectangle(box(marker['labelX'] + (col - 2) * cell,
                                   marker['labelY'] + (row - 3) * cell, cell), fill='black' if bit == '1' else 'white')
    image.save(path)
