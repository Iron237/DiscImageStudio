"""BD-R payload geometry and anti-scrambling.

GPL-3.0-only. Formulas and 17PP conversion ported from DiscImageStudio
v1.5.0, JiaFeiMiao-K-Cat and contributors. See THIRD_PARTY.md.
This writes 2048-byte user sectors, NOT channel bits or a video Blu-ray.
The writer in the drive supplies EDC/ECC/BIS/DC-control/17PP.
"""
from __future__ import annotations
import math
from functools import lru_cache
import numpy as np

SECTOR_BYTES = 2048
SECTORS_PER_CLUSTER = 32
CLUSTER_BYTES = 65536
PSN_BASE = 0x100000
TOTAL_SECTORS = 12219392
CHANNEL_BITS_PER_CLUSTER = 496 * 1932
CHANNEL_BITS_PER_RUB = 2760 + CHANNEL_BITS_PER_CLUSTER + 1104
BIT_MM = 74.5e-6


def load_geometry(cfg: dict) -> dict:
    g = cfg.get('geometry', cfg)
    n = int(g['totalSectors'])
    usable = int(g.get('usableSectors', n))
    ri, ro = float(g['innerRadiusMm']), float(g['outerRadiusMm'])
    if n != TOTAL_SECTORS:
        raise ValueError('The 64099 demo requires the full 12219392-sector geometry. Use a diagnostic .bin fragment for software tests; never shrink disc geometry.')
    if usable != n:
        raise ValueError('This demo requires an unformatted, no-spare full-capacity BD-R: usableSectors == totalSectors. Do not shrink geometry to fit a formatted disc.')
    if not (0 < ri < ro <= 60):
        raise ValueError('Invalid recording radii.')
    if any(float(g.get(k, 0)) != 0 for k in ('pitchLinear','pitchQuadratic','pitchCubic')):
        raise ValueError('v0.1 implements the constant-pitch 64099 preset only; nonzero cubic coefficients are not silently ignored.')
    return dict(g, totalSectors=n, usableSectors=usable, innerRadiusMm=ri,
                outerRadiusMm=ro, startAngleDegrees=float(g.get('startAngleDegrees', 0)))


class Geometry:
    def __init__(self, cfg: dict):
        self.g = load_geometry(cfg)
        self.n = self.g['totalSectors']
        self.ri, self.ro = self.g['innerRadiusMm'], self.g['outerRadiusMm']
        self.length = self.n / 32 * CHANNEL_BITS_PER_RUB * BIT_MM
        lo, hi = 1e-15, 1.
        for _ in range(100):
            b = (lo + hi) / 2
            length = self.primitive(self.ro, b) - self.primitive(self.ri, b)
            if length > self.length: lo = b
            else: hi = b
        self.b = (lo + hi) / 2
        self.inner_primitive = self.primitive(self.ri, self.b)
        self.angle0 = math.radians(self.g['startAngleDegrees'] % 360)

    @staticmethod
    def primitive(r, b):
        return (r*np.sqrt(r*r+b*b) + b*b*np.arcsinh(r/b))/(2*b)

    def polar_at_bits(self, channels):
        channels = np.asarray(channels, dtype=np.float64)
        s = channels * BIT_MM
        r = np.sqrt(self.ri*self.ri + 2*self.b*s)
        for _ in range(2):
            err = self.primitive(r, self.b) - self.inner_primitive - s
            r -= err*self.b/np.sqrt(r*r+self.b*self.b)
        a = self.angle0 - (r-self.ri)/self.b
        return r, a

    def xy_at_bits(self, channels):
        r, a = self.polar_at_bits(channels)
        return r*np.cos(a), r*np.sin(a)

    def bits_at_radius(self, r):
        return (self.primitive(np.asarray(r), self.b)-self.inner_primitive)/BIT_MM


@lru_cache(maxsize=1)
def payload_layout():
    """One cluster in logical payload order, after LDC/BIS mapping.
    Offsets are NOMINAL byte centres, not exact variable-length 17PP boundaries.
    Matches BluRayImageMapping.PayloadLayout in upstream v1.5.0.
    """
    p = np.arange(CLUSTER_BYTES, dtype=np.int64)
    serial = (p//2048)*2052 + p%2048
    col, row = serial//216, serial%216
    frame = 2*row + col%2
    ldc_col = (col//2 + 152 - (row*3)%152)%152
    ecc_col = ldc_col + ldc_col//38
    midpoint = ecc_col*8 + 4
    dc = np.where(midpoint < 25, 0, 1+(midpoint-25)//45)
    offsets = 2760 + frame*1932 + 30 + (midpoint+dc)*1.5
    ecc_index = frame*155+ecc_col
    return offsets.astype(np.float64), ecc_index.astype(np.int64)


def scrambler_mask(psn: int, length: int = 2048) -> np.ndarray:
    if not 0 <= length <= 2052: raise ValueError('Invalid data-frame mask length.')
    state = 0x8000 | ((int(psn)>>5)&0x7fff)
    out = np.empty(length, dtype=np.uint8)
    for i in range(length):
        out[i] = state&255
        for _ in range(8):
            f = ((state>>15)^(state>>14)^(state>>12)^(state>>3))&1
            state = ((state<<1)|f)&0xffff
    return out


class MaskBank:
    """All 32768 cluster seed masks, 64 MiB. No per-byte Python loop at export."""
    def __init__(self):
        states = np.arange(65536,dtype=np.uint32)
        nxt = states.copy()
        for _ in range(8):
            f = ((nxt>>15)^(nxt>>14)^(nxt>>12)^(nxt>>3))&1
            nxt = ((nxt<<1)|f)&0xffff
        nxt = nxt.astype(np.uint16)
        state = np.arange(32768,65536,dtype=np.uint16)
        self.bank = np.empty((32768,2048),dtype=np.uint8)
        for i in range(2048):
            self.bank[:,i] = state&255
            state = nxt[state]

    def for_cluster(self, cluster: int):
        # PSN_BASE >> 5 is 32768, so the 15 low seed bits repeat by cluster.
        return self.bank[cluster & 32767]


def _next_word(bits, pos, prev):
    n = len(bits)-pos
    pair = int(bits[pos])*2 + int(bits[pos+1])
    if pair == 1: return 2, 0b010, False
    if pair == 2: return 2, 0b001, False
    if pair == 3: return 2, 0 if prev else 0b101, False
    if n == 2: return 2, 0, True
    pair2 = int(bits[pos+2])*2+int(bits[pos+3])
    if pair2 == 1: return 4,0b000100,False
    if pair2 == 2: return 4,0b010000,False
    if pair2 == 3: return 4,0b010100,False
    if n == 4: return 4,0b010100,True
    pair3 = int(bits[pos+4])*2+int(bits[pos+5])
    if n >= 8 and bits[pos+6] == 0 and bits[pos+7] == 0:
        if pair3 == 0: return 8,0b010100100100,False
        if pair3 == 2: return 8,0b000100100100,False
    return 6, [0b010100000,0b010100100,0b000100000,0b000100100][pair3],False


def encode_17pp(bits, previous=0):
    """Exact Figure-41 conversion port, with termination flag. Not a drive emulator."""
    b = np.asarray(bits,dtype=np.uint8)
    if len(b)%2 or previous not in (0,1) or np.any(b>1):
        raise ValueError('17PP needs complete 0/1 pairs and a 0/1 preceding bit.')
    out = np.empty(len(b)//2*3,dtype=np.uint8)
    pos=dst=0; term=False
    while pos<len(b):
        if len(b)-pos>=8 and tuple(b[pos:pos+6])==(1,1,0,1,1,1):
            ln,v,t = _next_word(b,pos+6,1)
            if v>>(ln//2*3-3) == 0b010: ln,v,term=6,0b001000000,False
            else: ln,v,term=_next_word(b,pos,previous)
        else: ln,v,term=_next_word(b,pos,previous)
        k=ln//2*3
        for j in range(k): out[dst+j]=(v>>(k-1-j))&1
        previous=int(out[dst+k-1]);dst+=k;pos+=ln
    return out,term


SYNC_SEQUENCE=[0,1,2,3,3,1,4,1,5,5,4,3,4,6,6,3,5,3,2,2,5,6,5,1,1,6,2,6,4,4,2]
SYNC_SIGNATURE=[1,0b010010,0b101000,0b100001,0b000100,0b001001,0b010000]

def recording_frame(ecc_bytes, frame_index, dc_bits, prev_termination=False, initial_level=0):
    """Diagnostic 1932-bit frame with EXPLICIT DC bits, not the drive's strategy."""
    arr=np.frombuffer(bytes(ecc_bytes),dtype=np.uint8)
    if len(arr)!=155 or len(dc_bits)!=28: raise ValueError('155 ECC bytes and 28 DC bits required.')
    raw=np.unpackbits(arr)
    data=[];s=0
    for block in range(28):
        n=25 if block==0 else 45
        data.extend(raw[s:s+n]);data.append(int(dc_bits[block]));s+=n
    body=0b001010000000010000000010 | ((1<<23) if prev_termination else 0)
    value=(body<<6)|SYNC_SIGNATURE[SYNC_SEQUENCE[frame_index%31]]
    sync=np.array([(value>>(29-i))&1 for i in range(30)],dtype=np.uint8)
    encoded,term=encode_17pp(data,int(sync[-1]))
    mod=np.concatenate([sync,encoded])
    levels=(np.cumsum(mod,dtype=np.uint32)+initial_level)&1
    return levels.astype(np.uint8), term
