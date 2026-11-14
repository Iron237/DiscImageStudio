"""Synthetic optical scenes test registration/sampling/fit, not physical color claims."""
import math
import os
from pathlib import Path
import tempfile
import unittest
import sys
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import numpy as np
import cv2
from PIL import Image, TiffImagePlugin
from dvdcal.chart import create_chart
from dvdcal.common import load_chart, read_json, save_json, sha, srgb
from dvdcal.photos import import_photos, register_photo, project, marker_corners, fixed_white_balance, decode
from dvdcal.fit import build_profile, project_recipes, response


GEOMETRY = dict(innerRadiusMm=23.991460627731737, outerRadiusMm=57.964893657925366, sectors=2297888,
                pitchLinear=-3.9256854319227684e-5, pitchQuadratic=6.964795915429256e-5, pitchCubic=-4.670918950137523e-5)
SOURCE = Path(__file__).resolve().parents[3] / 'third_party/DiscColorStudio/src/DiscColorStudio/data/continuous-color-map.json'


def synthetic_photo(chart, angle, path, pixels=1800, markers=True):
    """Independent polygon projection of known linear colors, then sRGB encoding."""
    radians = math.radians(angle)
    rotation = np.array([[math.cos(radians), -math.sin(radians), 0],
                         [math.sin(radians), math.cos(radians), 0], [0, 0, 1]])
    scale = pixels / 135
    reference = np.array([[scale, .03 * scale, pixels / 2], [-.02 * scale, .98 * scale, pixels / 2], [.0003, -.0002, 1]])
    matrix = reference @ rotation
    image = np.full((pixels, pixels, 3), 30, np.uint8)
    palette = np.random.default_rng(55).uniform(.035, .28, (1154, 3))
    palette[1152], palette[1153] = [.012, .015, .013], [.35, .35, .35]
    half = chart['patchSizeMm'] / 2
    def polygon(points, color):
        projected = np.round(project(matrix, points)).astype(np.int32)
        cv2.fillConvexPoly(image, projected, tuple(int(v) for v in color))
    for patch in chart['patches']:
        x, y = patch['x'], patch['y']
        beta = math.atan2(y, x) + radians
        gain = .85 + .1 * math.cos(beta)
        color = sum(palette[c['texture']] * c['weight'] for c in patch['components']) * gain
        encoded = np.clip(srgb(color) * 255 + .5, 0, 255)
        polygon([[x - half, y - half], [x + half, y - half], [x + half, y + half], [x - half, y + half]], encoded)
    if markers:
        for marker in chart['markers']:
            x, y, size, quiet = marker['x'], marker['y'], marker['size'], marker['quiet']
            h = size / 2 + quiet
            polygon([[x-h, y-h], [x+h, y-h], [x+h, y+h], [x-h, y+h]], [255] * 3)
            for row, line in enumerate(marker['bits']):
                for col, bit in enumerate(line):
                    a, b = x + (col / 6 - .5) * size, y + (row / 6 - .5) * size
                    d = size / 6
                    polygon([[a, b], [a+d, b], [a+d, b+d], [a, b+d]], [255 if bit == '1' else 0] * 3)
            for row, line in enumerate(marker.get('labelBits', [])):
                for col, bit in enumerate(line):
                    cell = marker['labelCellMm']
                    a, b = marker['labelX'] + (col - 2.5) * cell, marker['labelY'] + (row - 3.5) * cell
                    polygon([[a, b], [a+cell, b], [a+cell, b+cell], [a, b+cell]], [0 if bit == '1' else 255] * 3)
    Image.fromarray(image).save(path)
    centers = project(matrix, [[m['x'], m['y']] for m in chart['markers']]) / pixels
    return centers.tolist()


class DvdCalibrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        fixture = os.environ.get('DIS_DVD_CAL_FIXTURE_ROOT')
        cls.temp = None if fixture else tempfile.TemporaryDirectory(prefix='dis-dvd-cal-tests-')
        cls.root = Path(fixture).resolve() if fixture else Path(cls.temp.name)
        if fixture:
            cls.root.mkdir(parents=True, exist_ok=False)
            (cls.root / 'SYNTHETIC-TEST-DATA.txt').write_text('Synthetic software fixture. Not measured disc colors. Physical ISO export is forbidden.', encoding='utf-8')
        cls.chart_path = Path(create_chart(dict(directory=str(cls.root / 'chart'), geometry=GEOMETRY, sourceProfilePath=str(SOURCE))))
        cls.chart = load_chart(cls.chart_path)

    @classmethod
    def tearDownClass(cls):
        if cls.temp is not None:
            cls.temp.cleanup()

    def test_chart_contains_exact_motifs_and_held_out_mixtures(self):
        chart = self.chart
        self.assertEqual(len(chart['patches']), 1330)
        self.assertEqual(sum(p['kind'] == 'pure' for p in chart['patches']), 1154)
        self.assertEqual(sum(p['kind'] == 'repeat' for p in chart['patches']), 128)
        self.assertEqual(sum(p['kind'] == 'holdout-mixture' for p in chart['patches']), 48)
        self.assertEqual(chart['textures'][1152]['motif'], 'A5')
        self.assertEqual(len({(p['gridX'], p['gridY']) for p in chart['patches']}), 1330)
        for patch in chart['patches']:
            for dx in (-.9, .9):
                for dy in (-.9, .9):
                    radius = math.hypot(patch['x'] + dx, patch['y'] + dy)
                    self.assertGreater(radius, GEOMETRY['innerRadiusMm'])
                    self.assertLess(radius, GEOMETRY['outerRadiusMm'])

    def test_automatic_perspective_registration_and_manual_repair(self):
        folder = self.root / 'registration'
        folder.mkdir()
        first, second, missing = [folder / name for name in ('000.png', '025.png', '050-no-markers.png')]
        synthetic_photo(self.chart, 0, first)
        synthetic_photo(self.chart, 25, second)
        points = synthetic_photo(self.chart, 50, missing, markers=False)
        before = [sha(p) for p in (first, second, missing)]
        path = import_photos(dict(directory=str(folder / 'session'), chartPath=str(self.chart_path),
                                  photos=[str(first), str(second), str(missing)], synthetic=True))
        session = read_json(path)
        self.assertEqual(session['frames'][0]['registration'], 'automatic-aruco')
        self.assertAlmostEqual(session['frames'][1]['rotationDeg'], 25, delta=.3)
        self.assertFalse(session['frames'][2]['poseValid'])
        register_photo(dict(sessionPath=path, frameId=session['frames'][2]['id'], points=points))
        repaired = read_json(path)
        self.assertTrue(repaired['frames'][2]['poseValid'])
        self.assertAlmostEqual(repaired['frames'][2]['rotationDeg'], 50, delta=.3)
        self.assertGreater(repaired['frames'][0]['validPatches'], 1300)
        self.assertEqual(before, [sha(p) for p in (first, second, missing)])
        with self.assertRaises(ValueError):
            build_profile(dict(sessionPath=path, directory=str(folder / 'insufficient-profile'), windowWidthDeg=5))
        self.assertFalse((folder / 'insufficient-profile/profile.json').exists())
        self.assertTrue((folder / 'insufficient-profile/quality.json').exists())

    def test_convex_recipes_are_nonnegative_and_reproduce_inside_gamut(self):
        colors = np.array([[r, g, b] for r in (.02, .4) for g in (.02, .4) for b in (.02, .4)])
        targets = np.array([[.1, .2, .3], [.02, .02, .02], [1, 0, 0]])
        ids, weights, reachable = project_recipes(colors, np.arange(8), targets)
        self.assertTrue(np.all(weights >= 0))
        np.testing.assert_allclose(weights.sum(axis=1), 1, atol=2e-6)
        predicted = np.sum(colors[ids] * weights[:, :, None], axis=1)
        np.testing.assert_allclose(predicted[:2], targets[:2], atol=1e-6)
        self.assertTrue(np.all(predicted[2] <= .400001))
        self.assertLess(reachable, 1)

    def test_missing_angles_never_borrow_response(self):
        curve = (np.array([0., 5., 25.]), np.array([[.1]*3, [.2]*3, [.3]*3]))
        actual = response(curve, [2.5, 10, 30])
        np.testing.assert_allclose(actual[0], [.15]*3)
        self.assertTrue(np.isnan(actual[1:]).all())

    def test_raw_rgb_second_green_can_be_unspecified(self):
        class Raw:
            camera_whitebalance = [2., 1., 1.5, 0.]
            daylight_whitebalance = [2., 1., 1.5, 0.]
        self.assertEqual(fixed_white_balance(Raw()), [2., 1., 1.5, 1.])

    def test_raw_dng_linear_exposure_is_not_automatically_stretched(self):
        base = np.tile(np.array([[900, 1400], [1400, 1800]], dtype=np.uint16), (64, 64))
        outputs = []
        for index, values in enumerate((base, ((base - 64) // 2 + 64).astype(np.uint16))):
            tags = TiffImagePlugin.ImageFileDirectory_v2()
            values_by_tag = {271: 'DIS Test', 272: 'Synthetic CFA', 262: 32803, 33421: (2, 2),
                33422: bytes([0, 1, 1, 2]), 50706: bytes([1, 4, 0, 0]), 50707: bytes([1, 1, 0, 0]),
                50708: 'DIS synthetic sensor', 50710: bytes([0, 1, 2]), 50711: 1, 50714: 64, 50717: 4095,
                50721: tuple(TiffImagePlugin.IFDRational(v) for v in (1, 0, 0, 0, 1, 0, 0, 0, 1)),
                50728: tuple(TiffImagePlugin.IFDRational(1) for _ in range(3)), 50778: 21}
            for tag, value in values_by_tag.items():
                tags[tag] = value
            file = self.root / f'synthetic-raw-{index}.dng'
            Image.fromarray(values).save(file, format='TIFF', tiffinfo=tags)
            rgb, processing = decode(file)
            self.assertEqual(processing['mode'], 'RAW-linear-sRGB')
            self.assertEqual(rgb.shape, (128, 128, 3))
            outputs.append(float(np.mean(rgb[8:-8, 8:-8])))
        self.assertAlmostEqual(outputs[1] / outputs[0], .5, delta=.005)

    def test_full_photo_to_profile_pipeline(self):
        folder = self.root / 'full'
        folder.mkdir()
        files = []
        for angle in range(0, 360, 5):
            image = folder / f'{angle:03}deg.png'
            synthetic_photo(self.chart, angle, image)
            files.append(str(image))
        session = import_photos(dict(directory=str(folder / 'session'), chartPath=str(self.chart_path),
                                     photos=files, synthetic=True))
        data = read_json(session)
        self.assertGreaterEqual(sum(f.get('poseValid', False) for f in data['frames']), 70)
        profile = build_profile(dict(sessionPath=session, directory=str(folder / 'session/profiles/test-profile'),
                                     windowWidthDeg=5, minimumCoverage=.9))
        result = read_json(profile)
        self.assertTrue(result['disCalibration']['synthetic'])
        self.assertFalse(result['disCalibration']['physicalColorValidated'])
        self.assertEqual(result['format'], 'dvd-continuous-color-map-v3')
        self.assertEqual(len(result['textures']), 1154)
        self.assertEqual(len(result['nodes']), 6)
        report = read_json(Path(profile).parent / 'quality.json')
        self.assertEqual(report['status'], 'software-checks-passed')
        self.assertGreater(report['holdout-mixture']['count'], 24)
        self.assertLess(report['repeat']['median'], .03)
        self.assertLess(report['holdout-mixture']['median'], .03)
        invalid = read_json(session)
        mixed_ids = {p['id'] for p in self.chart['patches'] if p['kind'] == 'holdout-mixture'}
        for frame in invalid['frames']:
            for observation in frame['observations']:
                if observation['patch'] in mixed_ids:
                    observation['linear'] = [.95, .01, .02]
        bad_session = folder / 'bad-mixtures-session.json'
        save_json(bad_session, invalid)
        with self.assertRaises(ValueError):
            build_profile(dict(sessionPath=str(bad_session), directory=str(folder / 'rejected-profile'),
                               windowWidthDeg=5, minimumCoverage=.9))
        self.assertEqual(read_json(folder / 'rejected-profile/quality.json')['status'], 'review-required')
        self.assertFalse((folder / 'rejected-profile/profile.json').exists())


if __name__ == '__main__':
    unittest.main()
