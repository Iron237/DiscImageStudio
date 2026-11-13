import contextlib
import copy
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
import worker
from bdcolor.writer import export, verify_export


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


if __name__ == '__main__':
    unittest.main()
