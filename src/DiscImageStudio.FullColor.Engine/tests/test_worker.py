import contextlib
import copy
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
import worker
from bdcolor.writer import export, verify_export, _initialize_output, Renderer
from bdcolor.dis_dither import phase, allocate
from bdcolor.core import CHANNEL_BITS_PER_RUB


class WorkflowTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        cls.root = Path(cls.temp.name)
        cls.cfg = json.loads((ROOT / 'configs/verbatim_64099.json').read_text(encoding='utf-8'))
        cls.cfg['patch'].update(widthMm=1., heightMm=1.)
        cls.source = cls.root / '中文 色卡.png'
        Image.new('RGB', (24, 24), (220, 40, 90)).save(cls.source)

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def request(self, name):
        return {'operation': 'preview', 'directory': str(self.root / name),
                'spectra': str(self.root / 'spectra.npz'), 'configuration': copy.deepcopy(self.cfg),
                'inputPath': str(self.source), 'measurementsPath': '', 'inputLayout': 'disc-composite',
                'canvasOuterRadiusMm': self.cfg['geometry']['outerRadiusMm'], 'designFingerprint': 'fixture-design'}

    def test_01_preview_and_encoded_fragment(self):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            plan = worker.execute(self.request('preview'))
        messages = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertEqual(messages[-1]['fraction'], 1.)
        self.assertTrue((Path(plan).parent / 'predicted.png').is_file())
        self.assertEqual(json.loads(Path(plan).read_text(encoding='utf-8'))['designSource']['fingerprint'], 'fixture-design')
        with self.assertRaises(ValueError):
            export(plan, self.root / 'blocked.iso')
        fragment = export(plan, self.root / 'diagnostic.bin', allow_unmeasured=True, fragment_clusters=4, log=lambda _: None)
        self.assertGreater(verify_export(plan, fragment, log=lambda _: None), 0)

    def test_02_invalid_optics_before_output(self):
        request = self.request('invalid')
        request['configuration']['optics']['targetExposure'] = 0
        with self.assertRaises(ValueError):
            worker.execute(request)
        self.assertFalse(Path(request['directory']).exists())

    def test_03_calibration_and_no_overwrite(self):
        request = self.request('palette')
        request['operation'] = 'calibration'
        with contextlib.redirect_stdout(io.StringIO()):
            plan = worker.execute(request)
        self.assertEqual(len(json.loads(Path(plan).read_text(encoding='utf-8'))['patches']), 64)
        with self.assertRaises(FileExistsError):
            worker.execute(request)

    def test_04_design_position_is_cropped_without_refitting(self):
        from PIL import ImageDraw
        request = self.request('crop')
        request['canvasOuterRadiusMm'] = 50.
        request['configuration']['patch'].update(centerMm=[10., 30.], widthMm=4., heightMm=4., macrocellMm=.25)
        composite = Image.new('RGB', (1000, 1000), 'blue')
        # Design coordinates (+10, +30) map to image coordinates (600, 200).
        ImageDraw.Draw(composite).rectangle((570, 170, 630, 230), fill='red')
        source = self.root / 'composed-layout.png'
        composite.save(source)
        request['inputPath'] = str(source)
        destination = self.root / 'physical-roi'
        destination.mkdir()
        sampled = worker.sample_design_composite(request, destination)
        with Image.open(sampled) as image:
            self.assertEqual(image.size, (16, 16))
            self.assertEqual(image.getpixel((8, 8)), (255, 0, 0))

    def test_05_independent_image_input_is_rejected(self):
        request = self.request('no-independent-image')
        request['inputLayout'] = 'separate-image'
        with contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaises(ValueError):
                worker.execute(request)

    def test_06_full_length_zero_initialization_and_sparse_allocation(self):
        path = self.root / 'fresh-output.bin'
        length = 8 * 1024 * 1024 + 37
        with path.open('xb') as stream:
            sparse = _initialize_output(stream, length)
        self.assertEqual(path.stat().st_size, length)
        with path.open('rb') as stream:
            for offset in (0, length // 2, length - 4096):
                stream.seek(offset)
                self.assertEqual(stream.read(4096), bytes(4096))
        if os.name == 'nt' and sparse:
            import ctypes
            from ctypes import wintypes
            kernel = ctypes.WinDLL('kernel32', use_last_error=True)
            allocated = kernel.GetCompressedFileSizeW
            allocated.argtypes = [wintypes.LPCWSTR, ctypes.POINTER(wintypes.DWORD)]
            allocated.restype = wintypes.DWORD
            high = wintypes.DWORD()
            low = allocated(str(path), ctypes.byref(high))
            self.assertLess((high.value << 32) | low, 1024 * 1024)
        with path.open('r+b') as stream:
            stream.seek(length // 2)
            stream.write(b'PAYLOAD')
        with path.open('rb') as stream:
            stream.seek(length // 2 - 1)
            self.assertEqual(stream.read(9), b'\0PAYLOAD\0')

    def test_07_full_disc_keeps_lower_design_and_annulus_mask(self):
        from PIL import ImageDraw
        request = self.request('full-disc')
        request['configuration']['imageLayout']='disc-annulus'
        request['configuration']['patch'].update(macrocellMm=2.,microcellMm=.1)
        request['configuration']['tone']={'ditherStrength':1.,'brightness':0.,'srgbLinearization':False}
        image=Image.new('RGB',(464,464),'black')
        # Bottom of disc, far outside the former (0,+43) 20x12 mm crop.
        ImageDraw.Draw(image).rectangle((222,390,242,410),fill='red')
        source=self.root/'lower-design.png';image.save(source);request['inputPath']=str(source)
        with contextlib.redirect_stdout(io.StringIO()): plan=worker.execute(request)
        data=json.loads(Path(plan).read_text(encoding='utf-8'))
        self.assertGreater(data['metrics']['coloredMacrocellCount'],0)
        with Image.open(Path(plan).parent/'target.png') as target:
            pixels=np.asarray(target)
            self.assertEqual(pixels[target.height//2,target.width//2,3],0)
            self.assertEqual(pixels[0,0,3],0)
            self.assertGreater(pixels[50,29,0],100)
        renderer=Renderer(plan)
        self.assertLess(len(renderer.candidates()),renderer.geometry.n//32//4)
        # Every active cell's nearest spiral turn must survive polar culling,
        # including isolated marks on both sides of the angular wrap.
        gy,gx=np.nonzero(renderer.grid)
        x=renderer.xmin+(gx+.5)*renderer.micro;y=renderer.ymax-(gy+.5)*renderer.micro
        g=renderer.geometry;rr=np.hypot(x,y);theta=np.arctan2(y,x)
        turns=np.round((g.angle0-(rr-g.ri)/g.b-theta)/(2*np.pi))
        nearest=g.ri+g.b*(g.angle0-theta-2*np.pi*turns)
        good=(nearest>=g.ri)&(nearest<g.ro)
        clusters=np.floor(g.bits_at_radius(nearest[good])/CHANNEL_BITS_PER_RUB).astype(int)
        self.assertTrue(np.isin(clusters,renderer.candidates()).all())
        # Compare angular culling with brute-force nominal positions on actual
        # clusters. Export must not omit any written dots in the bottom design.
        for cluster in renderer.candidates()[::max(1,len(renderer.candidates())//5)]:
            x,y=renderer.geometry.xy_at_bits(int(cluster)*CHANNEL_BITS_PER_RUB+renderer.offsets)
            ix=np.floor((x-renderer.xmin)/renderer.micro).astype(int).clip(0,renderer.grid.shape[1]-1)
            iy=np.floor((renderer.ymax-y)/renderer.micro).astype(int).clip(0,renderer.grid.shape[0]-1)
            expected=np.flatnonzero(renderer.grid[iy,ix]!=0)
            indices,ids=renderer.patch_payload_indices(int(cluster))
            np.testing.assert_array_equal(np.sort(indices[ids!=0]),expected)

    def test_08_native_dither_vectors_and_density(self):
        indices=[0,1,2,255,65536,2147483647,4294967301,-1]
        expected=[0,1834104592,2889141741,3967754812,3153350005,4259742747,1557653252,2175737881]
        np.testing.assert_array_equal(phase(indices)*4294967296.,expected)
        samples=np.arange(100000,dtype=np.int64)
        selected=allocate(np.array([0,9,42]),np.array([.2,.3,.5]),samples,1.)
        np.testing.assert_allclose([(selected==i).mean() for i in (0,9,42)],[.2,.3,.5],atol=.005)
        np.testing.assert_array_equal(allocate(np.array([0,9]),np.array([.25,.75]),samples,0.),9)
        for gray in (0,64,128,255):
            for strength in (0.,.75,1.):
                actual=allocate(np.array([0,1]),np.array([1-gray/255.,gray/255.]),samples,strength)
                native=gray >= 128 + strength*255*(phase(samples)-.5)
                np.testing.assert_array_equal(actual,native.astype(int))

    def test_09_native_tone_density_is_not_linearized_again(self):
        # These bytes are the native AdjustedGray outputs for source gray 128:
        # 128 without gamma, 55 with gamma. The latter must stay darker.
        values=[]
        for gamma,gray in ((False,128),(True,55)):
            source=self.root/f'prepared-tone-{gamma}.png'
            Image.new('RGB',(24,24),(gray,gray,gray)).save(source)
            request=self.request(f'tone-{gamma}')
            request['inputPath']=str(source)
            request['configuration']['tone']={'ditherStrength':1.,'brightness':0.,'srgbLinearization':gamma}
            with contextlib.redirect_stdout(io.StringIO()):plan=worker.execute(request)
            with Image.open(Path(plan).parent/'target.png') as im:values.append(im.getpixel((2,2))[0])
        self.assertEqual(values,[188,128])

    def test_10_parallel_export_and_resume_are_byte_identical(self):
        with contextlib.redirect_stdout(io.StringIO()):plan=worker.execute(self.request('parallel-export'))
        renderer=Renderer(plan);start=int(renderer.candidates()[len(renderer.candidates())//2])
        options=dict(allow_unmeasured=True,fragment_clusters=96,start_cluster=start,log=lambda _:None)
        reference=export(plan,self.root/'serial.bin',workers=1,**options)
        parallel=export(plan,self.root/'parallel.bin',workers=4,**options)
        self.assertEqual(reference.read_bytes(),parallel.read_bytes())
        self.assertTrue(any(reference.read_bytes()))
        resumed=self.root/'resumed.bin'
        self.assertIsNone(export(plan,resumed,workers=4,work_limit=35,**options))
        self.assertFalse(resumed.exists())
        state=json.loads(Path(str(resumed)+'.partial.resume.json').read_text())
        self.assertEqual(state['nextCandidate'],35)
        # Worker count is scheduling only; the same checkpoint can resume with
        # a different thread count, without changing bytes or sample hashes.
        export(plan,resumed,workers=2,resume=True,**options)
        self.assertEqual(reference.read_bytes(),resumed.read_bytes())
        self.assertGreater(verify_export(plan,resumed,log=lambda _:None),0)

    def test_11_parallel_failure_keeps_a_safe_checkpoint(self):
        with contextlib.redirect_stdout(io.StringIO()):plan=worker.execute(self.request('failed-parallel-export'))
        r=Renderer(plan);start=int(r.candidates()[len(r.candidates())//2])
        options=dict(allow_unmeasured=True,fragment_clusters=96,start_cluster=start,log=lambda _:None)
        original=Renderer.render_cluster
        def failing(renderer,cluster):
            if cluster==start+40:raise OSError('injected render failure')
            return original(renderer,cluster)
        output=self.root/'failed.bin'
        with patch.object(Renderer,'render_cluster',failing):
            with self.assertRaisesRegex(OSError,'injected render failure'):
                export(plan,output,workers=4,**options)
        self.assertFalse(output.exists())
        self.assertTrue(Path(str(output)+'.partial.resume.json').exists())
        export(plan,output,workers=2,resume=True,**options)
        reference=export(plan,self.root/'failure-reference.bin',workers=1,**options)
        self.assertEqual(reference.read_bytes(),output.read_bytes())

    def test_12_parallel_color_preserves_grid_and_predictions(self):
        plans=[]
        for count in (1,2):
            request=self.request(f'color-workers-{count}')
            request['workers']=count
            request['configuration']['patch'].update(widthMm=6.,heightMm=6.)
            request['configuration']['tone']={'ditherStrength':.95,'brightness':0.,'srgbLinearization':True}
            with contextlib.redirect_stdout(io.StringIO()):plans.append(Path(worker.execute(request)))
        data=[json.loads(p.read_text(encoding='utf-8')) for p in plans]
        self.assertEqual(data[1]['execution']['colorWorkers'],2)
        for name in ('texture_ids.npy','target.png','predicted.png','gamut_error.png'):
            self.assertEqual((plans[0].parent/name).read_bytes(),(plans[1].parent/name).read_bytes(),name)
        self.assertEqual(data[0]['metrics'],data[1]['metrics'])

    def test_13_automatic_workers_see_all_available_processors(self):
        from bdcolor.parallel import worker_count
        with patch('bdcolor.parallel.available_cpus',return_value=24):
            self.assertEqual(worker_count(0),24)
            self.assertEqual(worker_count(1),1)
            self.assertEqual(worker_count(128),24)
        for value in (-1,1.5,'NaN'):
            with self.assertRaises(ValueError):worker_count(value)

    def test_14_legacy_random_dither_is_parallel_schedule_independent(self):
        plans=[]
        for count in (1,2):
            request=self.request(f'legacy-color-workers-{count}');request['workers']=count
            request['configuration']['patch'].update(widthMm=6.,heightMm=6.)
            with contextlib.redirect_stdout(io.StringIO()):plans.append(Path(worker.execute(request)))
        self.assertEqual((plans[0].parent/'texture_ids.npy').read_bytes(),(plans[1].parent/'texture_ids.npy').read_bytes())
        self.assertEqual((plans[0].parent/'predicted.png').read_bytes(),(plans[1].parent/'predicted.png').read_bytes())


if __name__ == '__main__':
    unittest.main()
