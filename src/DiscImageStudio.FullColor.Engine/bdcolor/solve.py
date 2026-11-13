"""Independent color-row jobs with schedule-independent optical/raster results."""
import math
import os
from contextlib import closing,nullcontext
import numpy as np
from .colour import fit_weights,local_geometry
from .dis_dither import allocate
from .parallel import worker_count,requested_workers,completed_map,available_memory,enable_all_cpus


_process_context = None


def _initialize_color_process(context):
    global _process_context
    _process_context = context
    if context['allCpus']: enable_all_cpus()


def _color_process_rows(bounds):
    return _solve_rows(_process_context,bounds)


def _solve_rows(context,bounds,rng=None):
    cfg,response,target,valid,p = (context[k] for k in ('cfg','response','target','valid','patch'))
    nx,sub=context['nx'],context['sub']
    start,end=bounds;rows=end-start
    cx,cy=p['centerMm'];w,h=p['widthMm'],p['heightMm'];macro,micro=p['macrocellMm'],p['microcellMm']
    grid=np.zeros((rows*sub,nx*sub),np.uint16)
    predicted=np.zeros((rows,nx,3));residual=np.zeros((rows,nx));delta=np.zeros((rows,nx));mismatch=np.zeros((rows,nx))
    N=sub*sub
    for iy in range(start,end):
        ly=iy-start;y=cy+h/2-(iy+.5)*macro
        if 'rowRngStates' in context:
            rng=np.random.default_rng()
            rng.bit_generator.state=context['rowRngStates'][iy]
        for ix in range(nx):
            x=cx-w/2+(ix+.5)*macro
            if not valid[iy,ix]: continue
            if not response.measured and not np.any(target[iy,ix]):
                predicted[ly,ix]=cfg['optics']['backgroundLevel']*np.array([.95047,1.,1.08883])
                residual[ly,ix]=np.linalg.norm(predicted[ly,ix])
                delta[ly,ix],mismatch[ly,ix]=local_geometry(x,y,cfg)
                continue
            ids,xyz=response.at(x,y)
            chosen,weights,_=fit_weights(ids,xyz,target[iy,ix])
            if 'tone' in cfg:
                source=cfg['designRaster'];sw,sh=source['width'],source['height'];radius=source['radiusMm']
                mx=cx-w/2+(ix*sub+np.arange(sub)+.5)*micro
                my=cy+h/2-(iy*sub+np.arange(sub)+.5)*micro
                px=np.clip(np.floor((mx+radius)*sw/(2*radius)).astype(np.int64),0,sw-1)
                py=np.clip(np.floor((radius-my)*sh/(2*radius)).astype(np.int64),0,sh-1)
                selected=allocate(chosen,weights,(py[:,None]*sw+px[None,:]).ravel(),cfg['tone']['ditherStrength'])
            else:
                u=(np.arange(N)+rng.random())/N
                selected=chosen[np.searchsorted(np.cumsum(weights),u,side='right').clip(max=len(chosen)-1)]
                rng.shuffle(selected)
            grid[ly*sub:(ly+1)*sub,ix*sub:(ix+1)*sub]=selected.reshape(sub,sub)
            actual_weights=np.array([(selected==k).mean() for k in ids])
            pr=actual_weights@xyz
            predicted[ly,ix]=pr
            residual[ly,ix]=np.linalg.norm(pr-target[iy,ix])
            delta[ly,ix],mismatch[ly,ix]=local_geometry(x,y,cfg)
    return start,end,grid,predicted,residual,delta,mismatch


def _freeze_response_cache(response,target,valid,p):
    if response.measured: return
    # The original model caches the FIRST 400 rounded-delta keys, with values
    # calculated at their first exact position. Prime precisely that row-major
    # prefix once, before copying it to children. A per-child cold cache would
    # subtly change spectra/NNLS choices when task scheduling changes.
    cx,cy=p['centerMm'];w,h=p['widthMm'],p['heightMm'];macro=p['macrocellMm']
    ys,xs=np.nonzero(valid & np.any(target!=0,axis=2))
    for iy,ix in zip(ys.tolist(),xs.tolist()):
        if len(response.cache)>=400: break
        response.at(cx-w/2+(ix+.5)*macro,cy+h/2-(iy+.5)*macro)


def solve_colors(cfg,response,target,valid,p,workers=None,log=print):
    ny,nx=valid.shape;sub=round(p['macrocellMm']/p['microcellMm'])
    if requested_workers(workers)==0: enable_all_cpus()
    maximum=worker_count(workers)
    count=min(maximum,ny)
    if os.name=='nt': count=min(count,61)  # Windows ProcessPoolExecutor limit.
    if requested_workers(workers)==0:
        nonzero=int(np.count_nonzero(valid & np.any(target!=0,axis=2)))
        cost=nonzero+int(valid.sum())/128
        count=min(count,max(1,math.ceil(cost/512)))
    memory=available_memory()
    if memory:
        arrays=target.nbytes+valid.nbytes+response.spectra.nbytes+sum(v.nbytes for v in response.cache.values())
        # Account for the interpreter/SciPy and each process's private copy of
        # the optical tables. Leave 30% of currently available RAM for the UI/OS.
        count=min(count,max(1,int(memory*.7)//(192*1024*1024+arrays)))
    context={'cfg':cfg,'response':response,'target':target,'valid':valid,'patch':p,'nx':nx,'sub':sub,
             'allCpus':requested_workers(workers)==0}
    rng=np.random.default_rng(64099)
    if count>1:
        _freeze_response_cache(response,target,valid,p)
        if 'tone' not in cfg:
            # Preserve the historical random stream too. Shuffle consumes random
            # indices, independent of selected palette values, so row-start RNG
            # states can be captured without performing any optical solve.
            states=[];scratch=np.arange(sub*sub,dtype=response.ids.dtype)
            for iy in range(ny):
                states.append(rng.bit_generator.state)
                used=valid[iy] & (np.any(target[iy]!=0,axis=1) | response.measured)
                for _ in np.flatnonzero(used): rng.random();rng.shuffle(scratch)
            context['rowRngStates']=states
    grid=np.zeros((ny*sub,nx*sub),np.uint16)
    predicted=np.zeros((ny,nx,3));residual=np.zeros((ny,nx));delta=np.zeros((ny,nx));mismatch=np.zeros((ny,nx))
    batch_rows=max(1,min(8,math.ceil(ny/(count*4))))
    batches=((y,min(ny,y+batch_rows)) for y in range(0,ny,batch_rows))
    log(f'CPU color solve: {count}/{maximum} processes; {batch_rows} rows/batch; one BLAS thread/process.')
    results=(map(lambda bounds:_solve_rows(context,bounds,rng),batches) if count==1
             else completed_map(_color_process_rows,batches,count,initializer=_initialize_color_process,initargs=(context,)))
    completed=last_reported=0
    manager=closing(results) if hasattr(results,'close') else nullcontext(results)
    with manager as rows:
        for start,end,g,pr,err,d,m in rows:
            grid[start*sub:end*sub]=g;predicted[start:end]=pr;residual[start:end]=err;delta[start:end]=d;mismatch[start:end]=m
            completed+=end-start
            if completed-last_reported>=8 or completed==ny:
                log(f'Color solve: row {completed}/{ny}');last_reported=completed
    return grid,predicted,residual,delta,mismatch,count
