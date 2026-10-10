from array import array
import importlib.util
import os
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("linux_release", Path(__file__).resolve().parents[1] / "verify-linux-release.py")
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class LinuxReleaseSmokeTests(unittest.TestCase):
    def test_regular_line_has_pal_sync_and_active_video(self):
        line = release.pal_line(0)
        self.assertEqual(2560, len(line))
        self.assertEqual([2000] * 188, line[:188])
        self.assertEqual([10000] * 212, line[188:400])
        self.assertTrue(all(value > 10000 for value in line[400:]))

    def test_vertical_lines_preserve_short_and_broad_half_line_pulses(self):
        for field_line, widths in ((0, (94, 94)), (3, (1092, 1092)), (5, (1092, 94))):
            with self.subTest(field_line=field_line):
                line = release.pal_line(10 + field_line)
                for start, width in zip((0, 1280), widths):
                    self.assertEqual([2000] * width, line[start:start + width])
                    self.assertEqual(10000, line[start + width])

    def test_rf_bytes_are_deterministic_and_modes_have_distinct_carriers(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(release, "TOTAL_LINES", 2):
            root = Path(directory)
            for name, mode in (("first", "vhs"), ("repeat", "vhs"), ("ld", "ld")):
                release.generate_pal_rf(root / name, mode)
            self.assertEqual(2 * 2560 * 2, (root / "first").stat().st_size)
            self.assertEqual(release.sha256(root / "first"), release.sha256(root / "repeat"))
            self.assertNotEqual(release.sha256(root / "first"), release.sha256(root / "ld"))
            samples = struct.unpack("<5120h", (root / "first").read_bytes())
            self.assertTrue(all(-12000 <= sample <= 12000 for sample in samples))

    def test_smoke_environment_does_not_use_sdk_or_oneapi_loader_overrides(self):
        with patch.dict(os.environ, {"DOTNET_ROOT": "/sdk", "LD_LIBRARY_PATH": "/oneapi", "LD_PRELOAD": "/test.so", "IPP_ROOT": "/ipp", "TBBROOT": "/tbb", "KEEP_ME": "yes"}):
            environment = release.clean_environment()
            for name in ("DOTNET_ROOT", "LD_LIBRARY_PATH", "LD_PRELOAD", "IPP_ROOT", "TBBROOT"):
                self.assertNotIn(name, environment)
            self.assertEqual("yes", environment["KEEP_ME"])

    def test_tbc_comparison_accepts_rounding_and_reports_metrics(self):
        with tempfile.TemporaryDirectory() as directory:
            first, second = Path(directory) / "a", Path(directory) / "b"
            Path(str(first) + ".tbc").write_bytes(struct.pack("<3H", 100, 200, 300))
            Path(str(second) + ".tbc").write_bytes(struct.pack("<3H", 100, 201, 300))
            result = release.compare_tbc(first, second)
            self.assertEqual(3, result["samples"])
            self.assertEqual(1, result["maximum_delta"])

    def test_tbc_comparison_rejects_geometry_and_material_pixel_changes(self):
        with tempfile.TemporaryDirectory() as directory:
            first, second = Path(directory) / "a", Path(directory) / "b"
            Path(str(first) + ".tbc").write_bytes(struct.pack("<2H", 100, 200))
            for samples in ((100,), (100, 210)):
                Path(str(second) + ".tbc").write_bytes(struct.pack("<" + "H" * len(samples), *samples))
                with self.assertRaises(ValueError):
                    release.compare_tbc(first, second)


if __name__ == "__main__":
    unittest.main()
