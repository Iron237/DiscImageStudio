"""JSON/NDJSON boundary for DVD chart/photo calibration; never accesses a drive."""
import json
import os
from pathlib import Path
import sys
import traceback
for key in ('OPENBLAS_NUM_THREADS', 'OMP_NUM_THREADS', 'MKL_NUM_THREADS'):
    os.environ[key] = '1'
os.environ['OPENBLAS_MAIN_FREE'] = '1'
from dvdcal.chart import create_chart
from dvdcal.photos import import_photos, register_photo
from dvdcal.fit import build_profile


def emit(message, fraction=None):
    print(json.dumps(dict(message=str(message), fraction=fraction), ensure_ascii=False), flush=True)


def execute(request):
    handler = {'dvdcal-create': create_chart, 'dvdcal-import': import_photos,
               'dvdcal-register': register_photo, 'dvdcal-build': build_profile}.get(request['operation'])
    if handler is None:
        raise ValueError('Unknown DVD calibration operation.')
    result = handler(request, emit)
    emit('Completed: ' + result, 1.)
    return result


if __name__ == '__main__':
    try:
        execute(json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig')))
    except Exception as error:
        emit('ERROR: ' + str(error))
        traceback.print_exc(file=sys.stderr)
        sys.exit(1)
