"""Experimental spectral surrogate and measured-response color solver.

IMPORTANT: the spectrum model is not a validated optical model of Verbatim 64099.
It uses synthetic DATA frames, nominal 74.5 nm sampling and explicit DC examples.
EDC/LDC parity, the actual drive DC strategy, material reflectance/phase, the
2D track array, polarization, substrate refraction and pupil integration are
not reproduced. The measured mode bypasses this surrogate.
"""
from __future__ import annotations
import hashlib, json, math
from pathlib import Path
import numpy as np
from scipy.ndimage import gaussian_filter1d
from scipy.optimize import nnls
from .core import recording_frame

RGB_TO_XYZ=np.array([[.4124564,.3575761,.1804375],[.2126729,.7151522,.0721750],[.0193339,.1191920,.9503041]])
XYZ_TO_RGB=np.linalg.inv(RGB_TO_XYZ)


def to_linear(rgb):
    rgb=np.asarray(rgb,float)
    return np.where(rgb<=.04045,rgb/12.92,((rgb+.055)/1.055)**2.4)


def to_srgb(rgb):
    rgb=np.maximum(np.asarray(rgb,float),0)
    return np.clip(np.where(rgb<=.0031308,12.92*rgb,1.055*rgb**(1/2.4)-.055),0,1)


def cmf1931_approx(w):
    """Wyman, Sloan, Shirley (JCGT 2013), asymmetric-Gaussian fit.
    Approximation, not the official tabulated CIE observer data.
    """
    w=np.asarray(w,float)
    def g(c,l,r):
        t=(w-c)*np.where(w<c,l,r)
        return np.exp(-.5*t*t)
    x=.362*g(442,.0624,.0374)+1.056*g(599.8,.0264,.0323)-.065*g(501.1,.049,.0382)
    y=.821*g(568.8,.0213,.0247)+.286*g(530.9,.0613,.0322)
    z=1.217*g(437,.0845,.0278)+.681*g(459,.0385,.0725)
    return np.maximum(np.stack([x,y,z],axis=-1),0)


def texture_list():
    # Id 0 means ordinary zero HOST bytes, not a controlled Q=00 texture.
    out=[{'id':'USER_ZERO','qHex':None,'kind':'ordinary-background'}]
    out += [{'id':f'Q{q:02X}','qHex':f'{q:02X}','kind':'constant-Q'} for q in range(256)]
    # A small reproducible multi-byte search, chosen for diversity, not measured color.
    seen=set()
    rng=np.random.default_rng(64099)
    for n in (2,3,4):
        for _ in range(24):
            v=bytes(rng.integers(0,256,size=n,dtype=np.uint8)).hex().upper()
            if v not in seen:
                seen.add(v);out.append({'id':'M'+v,'qHex':v,'kind':'physical-ECC-byte-motif'})
    return out


def texture_digest(textures):
    return hashlib.sha256(json.dumps(textures,sort_keys=True).encode()).hexdigest()


def build_spectra(path: Path, log=print):
    textures=texture_list(); grid=np.linspace(0,3.0,3073)
    spectra=np.zeros((len(textures),len(grid)),np.float32)
    for k,t in enumerate(textures):
        if k==0: continue
        motif=np.frombuffer(bytes.fromhex(t['qHex']),dtype=np.uint8)
        accum=np.zeros(len(grid))
        # Two explicit, intentionally different DC masks. Neither is claimed to
        # match the drive. BIS is uncontrolled pseudorandom context.
        for seed in (30190,64099):
            rng=np.random.default_rng(seed)
            levels=[];term=False;level=0
            for frame in range(8):
                ecc=motif[(frame*155+np.arange(155))%len(motif)].copy()
                ecc[[38,77,116]]=rng.integers(0,256,size=3,dtype=np.uint8)
                dc=rng.integers(0,2,size=28,dtype=np.uint8)
                wave,term=recording_frame(ecc.tobytes(),frame,dc,term,level)
                level=int(wave[-1]);levels.append(wave)
            signal=np.concatenate(levels).astype(float)*2-1
            signal-=signal.mean()
            window=np.hanning(len(signal));norm=np.sum(window**2)
            power=np.abs(np.fft.rfft(signal*window,n=32768))**2/norm
            f=np.fft.rfftfreq(32768,d=.0745)
            # Rectangular channel-cell form factor; scalar 1D approximation.
            power*=np.sinc(f*.0745)**2
            accum+=np.interp(grid,f,power)
        spectra[k]=accum/2
        if k%32==0: log(f'Spectral candidates: {k}/{len(textures)-1}')
    path.parent.mkdir(parents=True,exist_ok=True)
    np.savez_compressed(path,frequency=grid,spectra=spectra,
        textures=json.dumps(textures),model='synthetic-data-frame-17pp-1d-v1')
    return textures,grid,spectra


def load_spectra(path):
    with np.load(path,allow_pickle=False) as data:
        return json.loads(str(data['textures'])),data['frequency'].copy(),data['spectra'].copy()


def optical_signature(cfg):
    obj={'geometry':cfg['geometry'],'optics':cfg['optics']}
    return hashlib.sha256(json.dumps(obj,sort_keys=True).encode()).hexdigest()


def local_geometry(x,y,cfg):
    p=np.array([x,y,0.],float); r=math.hypot(x,y)
    t=np.array([y/r,-x/r,0.]) # clockwise tangent at data side; sign does not affect |delta|
    radial=np.array([x/r,y/r,0.])
    light=np.array(cfg['optics']['lightPositionMm'],float)-p
    view=np.array(cfg['optics']['viewerPositionMm'],float)-p
    if light[2]<=0 or view[2]<=0: raise ValueError('Both light and observer must be on the data side, Z > 0.')
    h=light/np.linalg.norm(light)+view/np.linalg.norm(view)
    return abs(float(h@t)),float(h@radial)


class Response:
    def __init__(self,cfg,textures,frequency,spectra,measurements=None):
        self.cfg=cfg;self.textures=textures;self.freq=frequency;self.spectra=spectra
        self.wavelength=np.arange(380.,781.,2.)
        self.cmf=cmf1931_approx(self.wavelength)
        self.measured=False;self.cache={};self.ids=np.arange(len(textures))
        self.zones=None
        if measurements is not None:
            m=json.loads(Path(measurements).read_text(encoding='utf-8'))
            if m.get('schema')!='bdcolor-measurements-v1': raise ValueError('Unsupported measurements schema.')
            if m.get('opticalSignature')!=optical_signature(cfg):
                raise ValueError('Measurements do not match geometry/lighting configuration.')
            if m.get('textureDigest')!=texture_digest(textures): raise ValueError('Texture bank mismatch.')
            self.zones=[]
            for zone in m['zones']:
                values={k:v for k,v in zone['XYZ'].items() if v is not None}
                if len(values)<4 or 'USER_ZERO' not in values:
                    raise ValueError('Each measured zone requires USER_ZERO and >=3 other fully measured XYZ values.')
                indices=[];xyz=[]
                for k,t in enumerate(textures):
                    if t['id'] in values:
                        val=np.asarray(values[t['id']],float)
                        if val.shape!=(3,) or not np.all(np.isfinite(val)) or np.any(val<0):
                            raise ValueError('XYZ must be finite nonnegative triples in one common scale.')
                        indices.append(k);xyz.append(val)
                self.zones.append((np.asarray(zone['centerMm'],float),np.asarray(indices),np.asarray(xyz)))
            if not self.zones: raise ValueError('No measured zones.')
            self.measured=True;self.scale=1.
        else:
            if cfg['optics'].get('illuminantModel')!='equal-energy':
                raise ValueError('Model mode currently implements equal-energy illumination only; use measured XYZ for your LED.')
            # Single normalization across the whole ROI; never normalize each texture.
            patch=cfg['patch'];cx,cy=patch['centerMm'];w,h=patch['widthMm'],patch['heightMm']
            samples=[]
            if cfg.get('imageLayout')=='disc-annulus':
                points=[(rr*math.cos(a),rr*math.sin(a))
                    for rr in np.linspace(cfg['geometry']['innerRadiusMm'],cfg['geometry']['outerRadiusMm'],3)
                    for a in np.linspace(0,2*math.pi,32,endpoint=False)]
            else:
                points=[(xx,yy) for xx in np.linspace(cx-w/2,cx+w/2,3) for yy in np.linspace(cy-h/2,cy+h/2,3)]
            for xx,yy in points:
                delta,_=local_geometry(xx,yy,cfg);samples.append(self.raw_xyz(delta))
            self.scale=max(float(max(a[:,1].max() for a in samples)),1e-12)

    def raw_xyz(self,delta):
        key=round(float(delta),5)
        if key in self.cache: return self.cache[key]
        query=delta/(self.wavelength/1000.)
        samples=np.array([np.interp(query,self.freq,s,left=0,right=0) for s in self.spectra])
        sigma=float(self.cfg['optics'].get('spectralBlurNm',9))/2
        if sigma>0: samples=gaussian_filter1d(samples,sigma,axis=1,mode='nearest')
        xyz=samples@self.cmf*2
        if len(self.cache)<400: self.cache[key]=xyz
        return xyz

    def at(self,x,y):
        if self.measured:
            nearest=min(self.zones,key=lambda z:np.sum((z[0]-[x,y])**2))
            return nearest[1],nearest[2]
        delta,_=local_geometry(x,y,self.cfg)
        xyz=self.raw_xyz(delta).copy()/self.scale
        background=float(self.cfg['optics'].get('backgroundLevel',.008))
        # Modeling assumption only: dark floor is not physically measured.
        xyz += background*np.array([.95047,1.,1.08883])
        xyz[0]=background*np.array([.95047,1.,1.08883])
        return self.ids,xyz


def fit_weights(ids,xyz,target):
    # Solve against every texture: a neutral target may require saturated primaries,
    # so selecting only nearby chromaticities would give a falsely small gamut.
    a=xyz.T
    aa=np.vstack([a,np.full(len(ids),8.)]); bb=np.r_[target,8.]
    w,_=nnls(aa,bb,maxiter=4000)
    if w.sum()<1e-12: w[0]=1
    w/=w.sum()
    keep=w>1e-7
    w=w[keep];w/=w.sum();sel=np.flatnonzero(keep)
    return ids[sel],w,w@xyz[sel]
