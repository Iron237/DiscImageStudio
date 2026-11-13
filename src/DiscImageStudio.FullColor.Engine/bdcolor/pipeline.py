from __future__ import annotations
import hashlib,json,math
from pathlib import Path
import numpy as np
from PIL import Image,ImageOps,ImageDraw
from .core import Geometry
from .colour import (load_spectra,Response,RGB_TO_XYZ,XYZ_TO_RGB,to_linear,to_srgb,
                     optical_signature,texture_digest)
from .solve import solve_colors


def save_json(path,obj):
    Path(path).write_text(json.dumps(obj,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')


def load_config(path):
    cfg=json.loads(Path(path).read_text(encoding='utf-8'))
    Geometry(cfg)
    p=cfg['patch'];cx,cy=map(float,p['centerMm']);w,h=map(float,[p['widthMm'],p['heightMm']])
    micro=float(p['microcellMm']);macro=float(p['macrocellMm'])
    if not all(math.isfinite(v) for v in (cx,cy,w,h,micro,macro)) or min(w,h,micro,macro)<=0:
        raise ValueError('Patch dimensions must be positive finite values.')
    ratio=macro/micro
    if abs(ratio-round(ratio))>1e-8 or not 2<=round(ratio)<=32:
        raise ValueError('macrocellMm must be 2..32 integer multiples of microcellMm.')
    if cfg.get('imageLayout')!='disc-annulus':
        for d in (w,h):
            if abs(d/macro-round(d/macro))>1e-8: raise ValueError('Patch width/height must be multiples of macrocellMm.')
    ri,ro=cfg['geometry']['innerRadiusMm'],cfg['geometry']['outerRadiusMm']
    closest_x=max(abs(cx)-w/2,0);closest_y=max(abs(cy)-h/2,0)
    if math.hypot(closest_x,closest_y)<ri or max(math.hypot(x,y)for x in (cx-w/2,cx+w/2)for y in (cy-h/2,cy+h/2))>ro:
        raise ValueError('The entire rectangular patch must lie inside the recordable annulus.')
    if not .01 <= micro <= .5: raise ValueError('microcellMm outside demo limits 0.01..0.5 mm.')
    return cfg


def render_patch(cfg):
    """Keep calibration ROI separate from the full-disc image coordinate grid."""
    p=dict(cfg['patch'])
    if cfg.get('imageLayout')=='disc-annulus':
        side=math.ceil(2*cfg['geometry']['outerRadiusMm']/p['macrocellMm'])*p['macrocellMm']
        p.update(centerMm=[0.,0.],widthMm=side,heightMm=side)
    return p


def check_output_dir(out):
    out=Path(out)
    if (out/'plan.json').exists():
        raise FileExistsError(f'{out}/plan.json already exists. Use a new output directory; existing plans are never overwritten.')
    out.mkdir(parents=True,exist_ok=True);return out


def image_to_rgb(path):
    with Image.open(path) as src:
        src=ImageOps.exif_transpose(src)
        # Honor embedded ICC profile when possible. No silent profile drop.
        profile=src.info.get('icc_profile')
        if profile:
            from PIL import ImageCms
            import io
            try:
                src=ImageCms.profileToProfile(src,ImageCms.ImageCmsProfile(io.BytesIO(profile)),
                    ImageCms.createProfile('sRGB'),outputMode='RGBA')
            except Exception as ex: raise ValueError('Embedded ICC could not be converted; export an sRGB PNG first.')from ex
        elif src.mode not in ('RGB','RGBA','L','LA','P','1'):
            raise ValueError('Unsupported untagged image color space. Export an sRGB PNG/JPEG.')
        rgba=src.convert('RGBA');bg=Image.new('RGBA',rgba.size,(255,255,255,255));bg.alpha_composite(rgba)
        return bg.convert('RGB')


def captioned_preview(image,text,path):
    im=image.convert('RGB').resize((800,round(image.height*800/image.width)),Image.Resampling.NEAREST)
    canvas=Image.new('RGB',(im.width,im.height+54),'white');canvas.paste(im,(0,54))
    ImageDraw.Draw(canvas).text((12,14),text,fill='black',font_size=18)
    canvas.save(path)


def build_plan(input_path,config_path,spectra_path,out,measurements=None,log=print,workers=None):
    cfg=load_config(config_path);out=check_output_dir(out)
    textures,freq,spec=load_spectra(spectra_path)
    response=Response(cfg,textures,freq,spec,measurements)
    p=render_patch(cfg);w,h=p['widthMm'],p['heightMm'];cx,cy=p['centerMm'];macro=p['macrocellMm'];micro=p['microcellMm']
    nx,ny=round(w/macro),round(h/macro);sub=round(macro/micro)
    im=image_to_rgb(input_path)
    fitted=ImageOps.pad(im,(nx,ny),method=Image.Resampling.LANCZOS,color='black')
    rgb=np.asarray(fitted)/255.
    # Native AdjustedGray outputs dot density in BOTH modes. Applying an automatic
    # transfer afterward would cancel the design's gamma switch (or apply it twice).
    # The standalone legacy engine still accepts ordinary encoded sRGB images.
    linear=rgb if 'tone' in cfg else to_linear(rgb)
    target=linear@RGB_TO_XYZ.T*cfg['optics']['targetExposure']
    xs=cx-w/2+(np.arange(nx)+.5)*macro;ys=cy+h/2-(np.arange(ny)+.5)*macro
    radii=np.hypot(xs[None,:],ys[:,None])
    valid=np.ones((ny,nx),bool)
    if cfg.get('imageLayout')=='disc-annulus':
        valid=(radii>=cfg['geometry']['innerRadiusMm'])&(radii<=cfg['geometry']['outerRadiusMm'])
    texture_grid,predicted,residual,delta_grid,mismatch,cpu_workers=solve_colors(cfg,response,target,valid,p,workers,log)
    display=to_srgb((predicted/cfg['optics']['targetExposure'])@XYZ_TO_RGB.T)
    intended=Image.fromarray(np.round((to_srgb(linear) if 'tone' in cfg else rgb)*255).astype(np.uint8))
    modeled=Image.fromarray(np.round(display*255).astype(np.uint8))
    alpha=Image.fromarray(np.uint8(valid)*255)
    intended.putalpha(alpha);modeled.putalpha(alpha)
    intended.save(out/'target.png');modeled.save(out/'predicted.png')
    captioned_preview(modeled,'MODEL FIT - NOT A PHOTOGRAPH' if not response.measured else 'MEASURED RESPONSE FIT - NOT A PHOTOGRAPH',out/'preview.png')
    err=np.clip(residual/max(cfg['optics']['targetExposure'],1e-6),0,1)
    heat=np.stack([err,np.zeros_like(err),1-err],-1)
    heat_image=Image.fromarray(np.uint8(heat*255));heat_image.putalpha(alpha)
    heat_image.resize((800,round(800*ny/nx)),Image.Resampling.NEAREST).save(out/'gamut_error.png')
    # False-color texture ID map is diagnostic, never an optical prediction.
    false_rng=np.random.default_rng(7);colors=false_rng.integers(25,240,size=(len(textures),3),dtype=np.uint8);colors[0]=0
    Image.fromarray(colors[texture_grid]).save(out/'texture_ids_false_color.png')
    np.save(out/'texture_ids.npy',texture_grid)
    gridhash=hashlib.sha256((out/'texture_ids.npy').read_bytes()).hexdigest()
    plan={'schema':'bdcolor-plan-v1','kind':'image','configuration':cfg,'textures':textures,
          'textureDigest':texture_digest(textures),'opticalSignature':optical_signature(cfg),
          'responseMode':'measured' if response.measured else 'unmeasured-17pp-1d-surrogate',
          'gridFile':'texture_ids.npy','gridSha256':gridhash,'inputSha256':hashlib.sha256(Path(input_path).read_bytes()).hexdigest(),
          'inputName':Path(input_path).name,'gridWidth':texture_grid.shape[1],'gridHeight':texture_grid.shape[0],
          'execution':{'colorWorkers':cpu_workers},
          'metrics':{'meanXYZResidual':float(residual[valid].mean()),'maximumXYZResidual':float(residual.max()),
                     'activeMacrocellCount':int(valid.sum()),'coloredMacrocellCount':int(np.count_nonzero(np.any(linear>0,axis=2)&valid)),
                     'deltaRange':[float(delta_grid[valid].min()),float(delta_grid[valid].max())],
                     'maxAbsRadialMismatch':float(np.abs(mismatch).max()),'textureCountUsed':int(np.unique(texture_grid).size)},
          'warnings':['Preview is not a photograph and cannot validate color.','2D/radial diffraction, drive DC and material response are not simulated.',
                      'This is a raw user-sector image without filesystem; keep files out of this optical experiment.']}
    save_json(out/'plan.json',plan)
    save_json(out/'configuration.json',cfg)
    if measurements: (out/'measurements_used.json').write_text(Path(measurements).read_text(encoding='utf-8'),encoding='utf-8')
    make_viewing_instructions(cfg,out)
    return out/'plan.json'


def make_viewing_instructions(cfg,out):
    p=cfg['patch'];o=cfg['optics']
    txt=f'''BDColorLab fixed-view setup (ALL VALUES IN MILLIMETRES)
Data side faces viewer. Disc centre = (0,0,0); X right, Y up, Z toward viewer.
Patch centre: {p['centerMm']}; size: {p['widthMm']} x {p['heightMm']}
Camera/viewer centre: {o['viewerPositionMm']}
Small white light centre: {o['lightPositionMm']}
The model assumes equal-energy white illumination, not your actual LED spectrum.
Fix lamp and disc. Move the CAMERA ONLY to characterize angle response.
Lock exposure/white balance; RAW is preferable. A phone flashlight moving with
camera changes TWO variables and is not a fixed-light measurement.

The disc's absolute angular position relative to its printed label is not controlled.
Rotate the entire disc so the actual rectangular patch is at 12 o'clock before
measuring these positions. Do not chase a nice angle separately for each color.

No laser is needed. Do not modify the burner or peel/scratch the recording layer.
'''
    (Path(out)/'VIEWING_SETUP.txt').write_text(txt,encoding='utf-8')


def calibration_plan(config_path,spectra_path,out,log=print):
    cfg=load_config(config_path);out=check_output_dir(out)
    textures,freq,spec=load_spectra(spectra_path);r=Response(cfg,textures,freq,spec)
    p=cfg['patch'];cx,cy=p['centerMm'];ids,xyz=r.at(cx,cy)
    chrom=xyz/np.maximum(xyz.sum(1,keepdims=True),1e-10)
    chosen=[0,0xAA+1,0x44+1,0x09+1,0x11+1,0x9e+1]
    # Farthest chromaticity sampling; include fixed diagnostic baseline Q values.
    while len(chosen)<64:
        distances=np.min(np.sum((chrom[:,None]-chrom[np.array(chosen)][None,:])**2,axis=2),axis=1)
        distances[chosen]=-1
        # Prefer patches with enough modeled signal to be measurable.
        distances*=np.minimum(xyz[:,1]/.03,1)
        k=int(np.argmax(distances));chosen.append(k)
    W=round(p['widthMm']/p['microcellMm']);H=round(p['heightMm']/p['microcellMm'])
    grid=np.zeros((H,W),np.uint16);manifest=[]
    for row in range(8):
        for col in range(8):
            n=row*8+col;tid=chosen[n]
            y0,y1=round(row*H/8),round((row+1)*H/8);x0,x1=round(col*W/8),round((col+1)*W/8)
            grid[y0:y1,x0:x1]=tid
            manifest.append({'patch':n+1,'textureId':textures[tid]['id'],'qHex':textures[tid]['qHex'],
                'row':row,'column':col,'rectangleGrid':[x0,y0,x1,y1],
                'centerMm':[cx-p['widthMm']/2+(col+.5)*p['widthMm']/8,cy+p['heightMm']/2-(row+.5)*p['heightMm']/8]})
    np.save(out/'texture_ids.npy',grid)
    plan={'schema':'bdcolor-plan-v1','kind':'calibration','configuration':cfg,'textures':textures,
          'textureDigest':texture_digest(textures),'opticalSignature':optical_signature(cfg),
          'responseMode':'calibration-target-not-color-image','gridFile':'texture_ids.npy',
          'gridSha256':hashlib.sha256((out/'texture_ids.npy').read_bytes()).hexdigest(),
          'gridWidth':W,'gridHeight':H,'patches':manifest}
    save_json(out/'plan.json',plan);save_json(out/'patch_manifest.json',manifest)
    template={'schema':'bdcolor-measurements-v1','opticalSignature':optical_signature(cfg),
       'textureDigest':texture_digest(textures),'measurementConditions':'FILL IN actual illumination, camera/spectrometer, exposure and common XYZ normalization.',
       'warning':'One 8x8 grid gives one LOCAL approximate palette. Each tile is at a different position; repeat the palette at more positions for a spatial profile. No automatic JPEG-to-absolute-XYZ claim.',
       'zones':[{'centerMm':[cx,cy],'XYZ':{textures[i]['id']:None for i in chosen}}]}
    save_json(out/'measurements_template.json',template)
    preview=Image.new('RGB',(960,640),'white');d=ImageDraw.Draw(preview)
    for a in manifest:
        row,col=a['row'],a['column'];tid=chosen[row*8+col]
        color=tuple(np.uint8(to_srgb((xyz[tid]/max(cfg['optics']['targetExposure'],1e-9))@XYZ_TO_RGB.T)*255))
        box=(col*120,row*80,(col+1)*120-1,(row+1)*80-1);d.rectangle(box,fill=color,outline='white')
        d.rectangle((box[0]+2,box[1]+2,box[0]+118,box[1]+25),fill='white')
        d.text((box[0]+5,box[1]+5),f"{a['patch']:02d} {a['textureId']}",fill='black',font_size=12)
    captioned_preview(preview,'CALIBRATION KEY - MODEL COLORS; LABELS NOT BURNED',out/'patch_key.png')
    make_viewing_instructions(cfg,out)
    return out/'plan.json'
