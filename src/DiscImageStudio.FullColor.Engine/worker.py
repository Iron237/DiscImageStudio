"""Typed JSON/NDJSON boundary for DIS's native Full Color workspace.

GPL-3.0-only, based on BDColorLab 64099 0.1. No physical drive access.
The first run rebuilds the synthetic spectrum bank in the user's work directory.
"""
from __future__ import annotations
import json
import math
import hashlib
from pathlib import Path
import re
import sys
import traceback
import uuid
import shutil
from bdcolor.colour import build_spectra
from bdcolor.pipeline import build_plan, calibration_plan, save_json, image_to_rgb, render_patch
from bdcolor.writer import export, verify_export
from PIL import Image, ImageOps


def emit(message, fraction=None):
    text = str(message)
    match = re.search(r'(?:row |Export: )([\d,]+)/([\d,]+)', text)
    if match:
        a, b = (int(v.replace(',', '')) for v in match.groups())
        fraction = a / b
    print(json.dumps({'message': text, 'fraction': fraction}, ensure_ascii=False), flush=True)


def sample_design_composite(request, directory):
    """Sample the physical ROI from the already composed disc, preserving mm placement."""
    radius = float(request['canvasOuterRadiusMm'])
    if not math.isfinite(radius) or radius <= 0:
        raise ValueError('Invalid design canvas radius.')
    image = image_to_rgb(request.get('toneInputPath') or request['inputPath'])
    if image.width != image.height:
        raise ValueError('The design composite must be a square disc-coordinate raster.')
    shutil.copyfile(request['inputPath'],directory / 'design-composite.png')
    if request.get('nativeDitherPath'):
        shutil.copyfile(request['nativeDitherPath'],directory / 'native-dither.png')
    cfg=request['configuration']
    cfg['designRaster']={'width':image.width,'height':image.height,'radiusMm':radius}
    p = render_patch(cfg)
    cx, cy = p['centerMm']
    width, height = p['widthMm'], p['heightMm']
    box = ((cx - width/2 + radius) * image.width / (2*radius),
           (radius - cy - height/2) * image.height / (2*radius),
           (cx + width/2 + radius) * image.width / (2*radius),
           (radius - cy + height/2) * image.height / (2*radius))
    if cfg.get('imageLayout')!='disc-annulus' and not (0 <= box[0] < box[2] <= image.width and 0 <= box[1] < box[3] <= image.height):
        raise ValueError('The color patch must stay within the design canvas.')
    nx, ny = round(width / p['macrocellMm']), round(height / p['macrocellMm'])
    if min(nx, ny) <= 0:
        raise ValueError('Color sampling dimensions must be positive.')
    border=max(0,math.ceil(max(-box[0],-box[1],box[2]-image.width,box[3]-image.height)))+2
    expanded=ImageOps.expand(image,border=border,fill='white')
    sampled = expanded.resize((nx, ny), Image.Resampling.LANCZOS, box=tuple(v+border for v in box))
    path = directory / 'design-patch.png'
    sampled.save(path)
    return path


def execute(request):
    operation = request['operation']
    directory = Path(request['directory'])
    if operation in ('preview', 'calibration'):
        optics = request['configuration']['optics']
        for key in ('lightPositionMm', 'viewerPositionMm'):
            position = optics[key]
            if len(position) != 3 or not all(math.isfinite(v) for v in position) or position[2] <= 0:
                raise ValueError(key + ' must contain three finite coordinates with Z > 0.')
        for key in ('spectralBlurNm', 'backgroundLevel', 'targetExposure'):
            value = optics[key]
            if not math.isfinite(value) or value < 0 or (key == 'targetExposure' and value == 0):
                raise ValueError(key + ' must be finite and nonnegative; exposure must be positive.')
        directory.mkdir(parents=True, exist_ok=False)
        config = directory / 'configuration.json'
        save_json(config, request['configuration'])
        spectra = Path(request['spectra'])
        if not spectra.exists():
            emit('Preparing synthetic texture spectra (first run only).')
            temporary = spectra.with_name(spectra.stem + '.' + uuid.uuid4().hex + '.building.npz')
            build_spectra(temporary, log=emit)
            temporary.replace(spectra)
        if operation == 'preview':
            if request.get('inputLayout') != 'disc-composite':
                raise ValueError('Full Color consumes the active design composite, not a separate image.')
            source = sample_design_composite(request, directory)
            save_json(config, request['configuration'])
            result = build_plan(source, config, spectra, directory,
                                request.get('measurementsPath') or None, log=emit)
        else:
            request['configuration']['imageLayout']='patch'
            save_json(config, request['configuration'])
            result = calibration_plan(config, spectra, directory, log=emit)
        plan = json.loads(Path(result).read_text(encoding='utf-8'))
        plan['designSource'] = {'fingerprint': request['designFingerprint'],
            'canvasOuterRadiusMm': request['canvasOuterRadiusMm'],
            'compositeSha256': hashlib.sha256(Path(request['inputPath']).read_bytes()).hexdigest() if operation == 'preview' else None}
        save_json(result, plan)
    elif operation == 'export':
        result = export(request['planPath'], request['outputPath'],
                        allow_unmeasured=bool(request.get('allowUnmeasured')),
                        resume=bool(request.get('resume')), log=emit)
        verify_export(request['planPath'], result, log=emit)
    else:
        raise ValueError('Unsupported full-color operation: ' + operation)
    emit('Completed: ' + str(result), 1.0)
    return str(result)


if __name__ == '__main__':
    try:
        execute(json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig')))
    except Exception as exc:
        emit('ERROR: ' + str(exc))
        traceback.print_exc(file=sys.stderr)
        sys.exit(1)
