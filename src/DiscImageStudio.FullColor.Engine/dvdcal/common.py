from __future__ import annotations
import hashlib
import json
import os
from pathlib import Path
import time
import uuid
import numpy as np

CHART_SCHEMA = 'dis-dvd-texture-chart-v1'
SESSION_SCHEMA = 'dis-dvd-color-session-v1'
PROFILE_SCHEMA = 'dis-dvd-measured-profile-v1'
REVISION = 'ef44b13694e9873fc47ea9e0040ec5af502995ae'


def worker_limit(bytes_per_job):
    """Use available CPUs while leaving half of currently available RAM for the GUI/OS."""
    count = max(1, os.cpu_count() or 1)
    available = 2 * 1024**3
    if os.name == 'nt':
        import ctypes
        class Status(ctypes.Structure):
            _fields_ = [('length', ctypes.c_ulong), ('load', ctypes.c_ulong)] + [
                (name, ctypes.c_ulonglong) for name in ('totalPhys', 'availPhys', 'totalPage', 'availPage',
                                                       'totalVirtual', 'availVirtual', 'extended')]
        status = Status()
        status.length = ctypes.sizeof(status)
        if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
            available = status.availPhys
        process, system = ctypes.c_size_t(), ctypes.c_size_t()
        if ctypes.windll.kernel32.GetProcessAffinityMask(ctypes.windll.kernel32.GetCurrentProcess(), ctypes.byref(process), ctypes.byref(system)):
            count = min(count, process.value.bit_count())
    elif hasattr(os, 'sysconf'):
        available = os.sysconf('SC_AVPHYS_PAGES') * os.sysconf('SC_PAGE_SIZE')
        if hasattr(os, 'sched_getaffinity'):
            count = len(os.sched_getaffinity(0))
    return max(1, min(count, int(available * .5 // bytes_per_job)))


def read_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def sha(path):
    digest = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest().upper()


def save_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + '.tmp-' + uuid.uuid4().hex)
    try:
        with temporary.open('w', encoding='utf-8', newline='\n') as stream:
            json.dump(value, stream, ensure_ascii=False, indent=2, allow_nan=False)
            stream.flush()
            os.fsync(stream.fileno())
        deadline = time.monotonic() + 4
        while True:
            try:
                os.replace(temporary, path)
                break
            except PermissionError:
                if time.monotonic() >= deadline:
                    raise
                time.sleep(.1)
    finally:
        temporary.unlink(missing_ok=True)


def linear(rgb):
    rgb = np.asarray(rgb, dtype=np.float32)
    return np.where(rgb <= .04045, rgb / 12.92, ((rgb + .055) / 1.055) ** 2.4)


def srgb(rgb):
    rgb = np.maximum(0, np.asarray(rgb))
    return np.where(rgb <= .0031308, rgb * 12.92, 1.055 * rgb ** (1 / 2.4) - .055)


def load_chart(path):
    chart = read_json(path)
    if chart.get('schema') != CHART_SCHEMA:
        raise ValueError('请选择 DVD 纹理标定盘的 chart.json。')
    textures = chart['textures']
    if len(textures) != 1154 or any(t['index'] != i or not 1 <= len(bytes.fromhex(t['motif'])) <= 128
                                    for i, t in enumerate(textures)):
        raise ValueError('标定盘纹理清单无效。')
    if chart['geometry']['sectors'] != 2297888:
        raise ValueError('当前标定盘仅支持完整容量的 43533 DVD-R。')
    return chart


def session_chart(session):
    path = session['chartPath']
    if sha(path) != session['chartSha256']:
        raise ValueError('标定盘清单已经改变，请重新打开原始 chart.json。')
    return load_chart(path)


def load_session(path):
    session = read_json(path)
    if session.get('schema') != SESSION_SCHEMA:
        raise ValueError('不是 DVD 实测色表会话。')
    session_chart(session)
    return session
