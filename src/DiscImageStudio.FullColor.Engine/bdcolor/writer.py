"""Full-size raw 2048-byte BD-R image export. Never opens a physical drive.

The .iso extension is used like DIS's pure-drawing output: there is NO ISO9660
or UDF filesystem. Burn as an image; do not repack it as a data disc.
"""
from __future__ import annotations
import hashlib,json,math,os,shutil,time
from pathlib import Path
import numpy as np
from .core import (Geometry,MaskBank,payload_layout,SECTOR_BYTES,CLUSTER_BYTES,
                   CHANNEL_BITS_PER_RUB)
from .colour import texture_digest,optical_signature
from .pipeline import save_json,render_patch


class Renderer:
    def __init__(self,plan_path):
        self.path=Path(plan_path);self.plan=json.loads(self.path.read_text(encoding='utf-8'))
        if self.plan.get('schema')!='bdcolor-plan-v1': raise ValueError('Unsupported plan schema.')
        self.cfg=self.plan['configuration'];self.geometry=Geometry(self.cfg)
        if self.plan['textureDigest']!=texture_digest(self.plan['textures']): raise ValueError('Texture metadata was changed.')
        if self.plan['opticalSignature']!=optical_signature(self.cfg): raise ValueError('Geometry/optics changed after color solve. Rebuild the plan.')
        gridpath=self.path.parent/self.plan['gridFile']
        if hashlib.sha256(gridpath.read_bytes()).hexdigest()!=self.plan['gridSha256']: raise ValueError('Texture grid checksum mismatch.')
        self.grid=np.load(gridpath,allow_pickle=False)
        if self.grid.shape!=(self.plan['gridHeight'],self.plan['gridWidth']): raise ValueError('Texture grid shape mismatch.')
        if self.grid.dtype!=np.uint16 or self.grid.max()>=len(self.plan['textures']): raise ValueError('Invalid texture IDs.')
        p=render_patch(self.cfg);self.cx,self.cy=p['centerMm'];self.w,self.h=p['widthMm'],p['heightMm'];self.micro=p['microcellMm']
        if abs(self.grid.shape[1]*self.micro-self.w)>1e-7 or abs(self.grid.shape[0]*self.micro-self.h)>1e-7: raise ValueError('Grid scale mismatch.')
        self.xmin,self.xmax=self.cx-self.w/2,self.cx+self.w/2
        self.ymin,self.ymax=self.cy-self.h/2,self.cy+self.h/2
        active=np.nonzero(self.grid)
        self.has_active=bool(len(active[0]))
        # Cull against the written-dot bounds, while indexing against the original
        # full-disc origin. A small design must not scan 25 GB of empty payloads.
        if self.has_active:
            ax0=self.xmin+int(active[1].min())*self.micro;ax1=self.xmin+(int(active[1].max())+1)*self.micro
            ay0=self.ymax-(int(active[0].max())+1)*self.micro;ay1=self.ymax-int(active[0].min())*self.micro
        else: ax0,ax1,ay0,ay1=self.xmin,self.xmax,self.ymin,self.ymax
        acx,acy=(ax0+ax1)/2,(ay0+ay1)/2
        self.center_angle=math.atan2(acy,acx)
        self.halfangle=max(abs(math.remainder(math.atan2(y,x)-self.center_angle,2*math.pi))
            for x in (ax0,ax1)for y in (ay0,ay1))
        if ax0<=0<=ax1 and ay0<=0<=ay1: self.halfangle=math.pi
        self.rmin=math.hypot(max(ax0,-ax1,0),max(ay0,-ay1,0))
        self.rmax=max(math.hypot(x,y)for x in (ax0,ax1)for y in (ay0,ay1))
        self.occupancy=None
        if 0<len(active[0])<100000:
            # A full-disc grid may contain a few distant marks (including edge
            # antialiasing). Their common bounding box would span the whole disc.
            # Conservatively rasterize dot footprints into a coarse polar mask.
            self.radial_step=.5;self.angular_bins=720
            self.angular_step=2*math.pi/self.angular_bins
            rows=math.ceil((self.geometry.ro+p['macrocellMm'])/self.radial_step)+3
            occupancy=np.zeros((rows,self.angular_bins),bool)
            xx=self.xmin+(active[1]+.5)*self.micro;yy=self.ymax-(active[0]+.5)*self.micro
            rr=np.hypot(xx,yy);radial=np.floor(rr/self.radial_step).astype(int)
            angular=np.floor(np.arctan2(yy,xx)/self.angular_step).astype(int)
            half_diagonal=self.micro/math.sqrt(2)
            radial_padding=math.ceil(half_diagonal/self.radial_step)
            angular_padding=math.ceil(math.asin(min(1,half_diagonal/max(float(rr.min()),half_diagonal)))/self.angular_step)
            for dr in range(-radial_padding,radial_padding+1):
                for da in range(-angular_padding,angular_padding+1):
                    occupancy[(radial+dr).clip(0,rows-1),(angular+da)%self.angular_bins]=True
            doubled=np.tile(occupancy,(1,2))
            self.occupancy=np.pad(np.cumsum(doubled,axis=1,dtype=np.int32),((0,0),(1,0)))
        self.offsets,self.ecc_index=payload_layout();self.order=np.argsort(self.offsets);self.sorted_offsets=self.offsets[self.order]
        self.motifs=[None if t['qHex'] is None else np.frombuffer(bytes.fromhex(t['qHex']),dtype=np.uint8)for t in self.plan['textures']]
        self.maskbank=None
        self.motif_period=math.lcm(*[len(m) for m in self.motifs if m is not None])
        if self.motif_period>4096: raise ValueError('Combined motif period is too large for this demo.')
        self.motif_lut=np.zeros((len(self.motifs),self.motif_period),np.uint8)
        for k,m in enumerate(self.motifs):
            if m is not None: self.motif_lut[k]=m[np.arange(self.motif_period)%len(m)]

    def candidates(self):
        if not self.has_active: return np.empty(0,np.int64)
        clusters=self.geometry.n//32
        rs,aa=self.geometry.polar_at_bits(np.arange(clusters+1)*CHANNEL_BITS_PER_RUB)
        distance=np.abs(np.remainder((aa[:-1]+aa[1:])/2-self.center_angle+math.pi,2*math.pi)-math.pi)
        ok=(rs[1:]>=self.rmin)&(rs[:-1]<=self.rmax)&(distance<=(aa[:-1]-aa[1:])/2+self.halfangle+1e-8)
        if self.occupancy is not None:
            start=np.floor(aa[1:]/self.angular_step).astype(np.int64)%self.angular_bins
            span=np.minimum(self.angular_bins,np.ceil((aa[:-1]-aa[1:])/self.angular_step).astype(int)+2)
            occupied=np.zeros(clusters,bool)
            # Include both radial endpoints and a guard bin for track/bin ties.
            for radial in (np.floor(rs[:-1]/self.radial_step).astype(int),np.floor(rs[1:]/self.radial_step).astype(int)):
                for dr in (-1,0,1):
                    row=(radial+dr).clip(0,self.occupancy.shape[0]-1)
                    occupied |= self.occupancy[row,start+span]>self.occupancy[row,start]
            ok &= occupied
        return np.flatnonzero(ok)

    def patch_payload_indices(self,cluster):
        """Conservative angular filtering using the exact constant-pitch angle law.
        The final rectangle test uses the same nominal centres as DIS.
        """
        g=self.geometry;base=cluster*CHANNEL_BITS_PER_RUB
        _,ang=g.polar_at_bits(np.array([base,base+CHANNEL_BITS_PER_RUB]))
        mid=(ang[0]+ang[1])/2
        target=self.center_angle+round((mid-self.center_angle)/(2*math.pi))*2*math.pi
        portions=[]
        for shift in (-2*math.pi,0,2*math.pi):
            lo=max(ang[1],target+shift-self.halfangle-1e-9)
            hi=min(ang[0],target+shift+self.halfangle+1e-9)
            if hi<lo: continue
            # angle = angle0 - (r-ri)/b. High angle occurs first.
            ra=g.ri+g.b*(g.angle0-hi);rb=g.ri+g.b*(g.angle0-lo)
            a=float(g.bits_at_radius(ra))-base-4
            b=float(g.bits_at_radius(rb))-base+4
            ia=np.searchsorted(self.sorted_offsets,a);ib=np.searchsorted(self.sorted_offsets,b,side='right')
            if ib>ia: portions.append(self.order[ia:ib])
        if not portions: return np.empty(0,np.int64),np.empty(0,np.int64)
        # Distinct unwrapped angular windows are disjoint. Retain physical order;
        # sorting payload indices here would cost more than the optical mapping.
        indices=portions[0] if len(portions)==1 else np.concatenate(portions)
        x,y=g.xy_at_bits(base+self.offsets[indices])
        good=(x>=self.xmin)&(x<self.xmax)&(y>=self.ymin)&(y<self.ymax)
        indices=indices[good];x=x[good];y=y[good]
        ix=np.floor((x-self.xmin)/self.micro).astype(int)
        iy=np.floor((self.ymax-y)/self.micro).astype(int)
        ix=np.clip(ix,0,self.grid.shape[1]-1);iy=np.clip(iy,0,self.grid.shape[0]-1)
        return indices,self.grid[iy,ix].astype(np.int64)

    def render_cluster(self,cluster):
        if not 0<=cluster<self.geometry.n//32: raise ValueError('Cluster outside full geometry.')
        indices,ids=self.patch_payload_indices(cluster)
        nonzero=ids!=0;indices=indices[nonzero];ids=ids[nonzero]
        buf=np.zeros(CLUSTER_BYTES,np.uint8)
        if len(indices)==0: return buf
        if self.maskbank is None: self.maskbank=MaskBank()
        mask=self.maskbank.for_cluster(cluster)
        # A multi-byte motif is indexed in physical ECC-byte order, NOT ISO
        # logical payload order. BIS, parity and DC remain under drive control.
        physical=cluster*76880+self.ecc_index[indices]
        q=self.motif_lut[ids,physical%self.motif_period]
        buf[indices]=q^mask[indices%2048]
        return buf


def _initialize_output(file,length):
    """Create the full logical zero range, without CRT zero-writing on Windows.

    FSCTL_SET_ZERO_DATA guarantees zero reads and deallocates the sparse range.
    Never use SetFileValidData: it can expose uninitialized data. Only fresh
    outputs reach this function; resumed files are never reinitialized.
    """
    if os.name=='nt':
        try:
            import ctypes,msvcrt
            from ctypes import wintypes
            kernel=ctypes.WinDLL('kernel32',use_last_error=True)
            control=kernel.DeviceIoControl
            control.argtypes=[wintypes.HANDLE,wintypes.DWORD,ctypes.c_void_p,wintypes.DWORD,
                              ctypes.c_void_p,wintypes.DWORD,ctypes.POINTER(wintypes.DWORD),ctypes.c_void_p]
            control.restype=wintypes.BOOL
            seek=kernel.SetFilePointerEx
            seek.argtypes=[wintypes.HANDLE,ctypes.c_longlong,ctypes.c_void_p,wintypes.DWORD]
            seek.restype=wintypes.BOOL
            eof=kernel.SetEndOfFile;eof.argtypes=[wintypes.HANDLE];eof.restype=wintypes.BOOL
            handle=wintypes.HANDLE(msvcrt.get_osfhandle(file.fileno()))
            returned=wintypes.DWORD()
            if not control(handle,0x900c4,None,0,None,0,ctypes.byref(returned),None):
                raise ctypes.WinError(ctypes.get_last_error())
            if not seek(handle,length,None,0) or not eof(handle):
                raise ctypes.WinError(ctypes.get_last_error())
            class ZeroRange(ctypes.Structure):
                _fields_=[('start',ctypes.c_longlong),('end',ctypes.c_longlong)]
            zeros=ZeroRange(0,length)
            if not control(handle,0x980c8,ctypes.byref(zeros),ctypes.sizeof(zeros),
                           None,0,ctypes.byref(returned),None):
                raise ctypes.WinError(ctypes.get_last_error())
            file.seek(0)
            return True
        except (OSError,AttributeError):
            # Restart from zero if native initialization partially succeeded.
            # The conservative fallback still guarantees every unwritten byte.
            file.seek(0);file.truncate(0)
    file.truncate(length)
    file.seek(0)
    return False


def export(plan_path,output,allow_unmeasured=False,fragment_clusters=None,start_cluster=None,resume=False,work_limit=None,log=print):
    renderer=Renderer(plan_path);p=renderer.plan;out=Path(output)
    if p['responseMode'].startswith('unmeasured') and not allow_unmeasured:
        raise ValueError('This image uses an UNMEASURED color surrogate. Inspect preview/error maps, then explicitly add --allow-unmeasured to export a trial image. No accurate physical color is promised.')
    out.parent.mkdir(parents=True,exist_ok=True)
    partial=out.with_name(out.name+'.partial')
    checkpoint=out.with_name(out.name+'.partial.resume.json')
    if out.exists(): raise FileExistsError('Output already exists; choose another path.')
    if partial.exists() and not resume: raise FileExistsError('A .partial exists; use --resume with the SAME plan, or choose another path.')
    if resume and (not partial.exists() or not checkpoint.exists()): raise FileNotFoundError('Both .partial and .partial.resume.json are required for resume.')
    if work_limit is not None and int(work_limit)<=0: raise ValueError('work-limit must be >0.')
    candidates=renderer.candidates()
    if fragment_clusters is not None:
        if out.suffix.lower()!='.bin': raise ValueError('Diagnostic fragments MUST use .bin, never .iso. They are not burnable images.')
        count=int(fragment_clusters)
        if count<=0: raise ValueError('Fragment cluster count must be >0.')
        start=int(start_cluster) if start_cluster is not None else int(candidates[len(candidates)//2])
        if start<0 or start+count>renderer.geometry.n//32: raise ValueError('Fragment range outside disc.')
        length=count*CLUSTER_BYTES;work=np.arange(start,start+count);base=start
    else:
        if out.suffix.lower()!='.iso': raise ValueError('Full export requires .iso extension.')
        length=renderer.geometry.n*SECTOR_BYTES;work=candidates;base=0
        if not resume and shutil.disk_usage(out.parent).free<length+128*1024*1024:
            raise OSError(f'Need at least {length+128*1024*1024:,} free bytes (conservative check even with sparse output).')
    log(f'Output logical length: {length:,} bytes; geometry stays {renderer.geometry.n:,} sectors.')
    if fragment_clusters is None: log(f'Optical patch touches at most {len(work):,} / {renderer.geometry.n//32:,} clusters; other user bytes are valid zeros.')
    plan_hash=hashlib.sha256(Path(plan_path).read_bytes()).hexdigest()
    begin=0;sample_hashes={};accumulated=0.
    if resume:
        previous=json.loads(checkpoint.read_text(encoding='utf-8'))
        if previous['planSha256']!=plan_hash or previous['logicalSizeBytes']!=length or previous['candidateCount']!=len(work):
            raise ValueError('Resume checkpoint does not match this plan/range.')
        if partial.stat().st_size!=length: raise ValueError('Partial file size changed.')
        begin=int(previous['nextCandidate']);sample_hashes=previous['sampleClusterSha256'];accumulated=float(previous['seconds'])
        if not 0<=begin<=len(work): raise ValueError('Bad resume cursor.')
        log(f'Resuming at candidate {begin:,}/{len(work):,}.')
    stop=len(work) if work_limit is None else min(len(work),begin+int(work_limit))
    clock=time.monotonic();last=clock
    def save_checkpoint(next_index,stream):
        stream.flush();os.fsync(stream.fileno())
        state={'planSha256':plan_hash,'logicalSizeBytes':length,'candidateCount':len(work),
            'nextCandidate':next_index,'sampleClusterSha256':sample_hashes,
            'seconds':accumulated+time.monotonic()-clock}
        tmp=checkpoint.with_suffix('.tmp')
        save_json(tmp,state);tmp.replace(checkpoint)
    with open(partial,'r+b' if resume else 'xb') as f:
        if not resume:
            _initialize_output(f,length);save_checkpoint(0,f)
        try:
            for k in range(begin,stop):
                cluster=work[k];data=renderer.render_cluster(int(cluster))
                f.seek((int(cluster)-base)*CLUSTER_BYTES)
                # Write even zero clusters when resuming: this makes replay idempotent.
                if np.any(data) or resume: f.write(data.tobytes())
                if k<3 or k==len(work)//2 or k==len(work)-1:
                    sample_hashes[str(int(cluster))]=hashlib.sha256(data.tobytes()).hexdigest()
                if (k+1)%1000==0:save_checkpoint(k+1,f)
                now=time.monotonic()
                if now-last>2 or k+1==stop:
                    log(f'Export: {k+1:,}/{len(work):,} candidate clusters');last=now
            save_checkpoint(stop,f)
        except BaseException:
            # The last committed checkpoint is safe; uncommitted clusters are
            # regenerated on --resume. A .partial must NEVER be burned.
            log('Partial export retained. Resume with the identical plan; never burn the .partial.')
            raise
    if stop<len(work):
        log(f'Paused safely at {stop:,}/{len(work):,}. Continue with --resume.')
        return None
    if partial.stat().st_size!=length: raise IOError('Incorrect output length.')
    partial.rename(out)
    metadata={'format':'bdcolor-raw-user-sectors-v1' if fragment_clusters is None else 'bdcolor-NONBURNABLE-fragment-v1',
        'burnableContainer':fragment_clusters is None,'physicalBurnTested':False,'colorValidated':False,
        'noFilesystem':True,'logicalSizeBytes':length,'geometry':renderer.geometry.g,
        'planSha256':hashlib.sha256(Path(plan_path).read_bytes()).hexdigest(),
        'gridSha256':p['gridSha256'],'textureDigest':p['textureDigest'],'opticalSignature':p['opticalSignature'],
        'responseMode':p['responseMode'],'startCluster':base,'sampleClusterSha256':sample_hashes,
        'seconds':round(accumulated+time.monotonic()-clock,3),
        'warning':'Pure drawing raw sectors; no ISO9660/UDF. This file is not an upstream DIS bd-r-image-v1 sidecar; DIS AA/44 preview cannot reproduce these colors.'}
    save_json(str(out)+'.json',metadata)
    checkpoint.unlink(missing_ok=True)
    return out


def verify_export(plan_path,image_path,log=print):
    """Reconstructs selected clusters and independently reverses mask values.
    This is SOFTWARE verification, not a physical readback / optics test.
    """
    r=Renderer(plan_path);path=Path(image_path)
    m=json.loads(Path(str(path)+'.json').read_text(encoding='utf-8'))
    if m['planSha256']!=hashlib.sha256(Path(plan_path).read_bytes()).hexdigest(): raise ValueError('Wrong plan for this export.')
    if path.stat().st_size!=m['logicalSizeBytes']: raise ValueError('Wrong image size.')
    base=m['startCluster'];checked=0
    with open(path,'rb')as f:
        for key,expected_hash in m['sampleClusterSha256'].items():
            cluster=int(key);f.seek((cluster-base)*CLUSTER_BYTES);b=f.read(CLUSTER_BYTES)
            if hashlib.sha256(b).hexdigest()!=expected_hash: raise ValueError(f'Cluster checksum failed: {cluster}')
            expected=r.render_cluster(cluster).tobytes()
            if b!=expected: raise ValueError(f'Payload regeneration failed: {cluster}')
            # Independent scalar scrambler implementation for selected bytes.
            from .core import scrambler_mask,PSN_BASE
            mask=scrambler_mask(PSN_BASE+cluster*32)
            idx,ids=r.patch_payload_indices(cluster)
            for loc in np.linspace(0,max(0,len(idx)-1),min(41,len(idx))).astype(int):
                at=int(idx[loc]);tid=int(ids[loc]);raw=b[at]
                if tid==0:
                    if raw!=0: raise ValueError('Background user payload is not zero.')
                else:
                    motif=r.motifs[tid];q=int(motif[(cluster*76880+int(r.ecc_index[at]))%len(motif)])
                    if (raw^int(mask[at%2048]))!=q: raise ValueError('Inverse scrambling verification failed.')
            checked+=1
    log(f'Software verification passed: {checked} sampled clusters; byte length and anti-scrambling checked. No physical/optical claim.')
    return checked
