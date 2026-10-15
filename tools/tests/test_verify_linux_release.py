from array import array
from contextlib import closing
import importlib.util
import os
from pathlib import Path
import struct
import sqlite3
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("linux_release", Path(__file__).resolve().parents[1] / "verify-linux-release.py")
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class LinuxReleaseSmokeTests(unittest.TestCase):
    def test_decode_modes_use_their_supported_cli_options(self):
        root = Path("package")
        for mode in ("vhs", "ld"):
            arguments = release.decode_arguments(root, mode, "ipp-fast", "input.s16", "out")
            self.assertEqual(root / f"{mode}-decode", arguments[0])
            self.assertEqual(mode == "vhs", "--write_db" in arguments)
            self.assertEqual(mode == "vhs", "--overwrite" in arguments)
            self.assertEqual("ipp-fast", arguments[arguments.index("--dsp-backend") + 1])

    def test_sqlite_output_uses_production_tbc_db_suffix_for_both_modes(self):
        with tempfile.TemporaryDirectory() as directory:
            prefix = Path(directory) / "decode"
            metadata = {"videoParameters": {"numberOfSequentialFields": 2, "fieldWidth": 2, "fieldHeight": 1}, "fields": [{}, {}]}
            for suffix in (".tbc", ".log"):
                Path(str(prefix) + suffix).write_bytes(b"nonempty")
            with closing(sqlite3.connect(Path(str(prefix) + ".tbc.db"))) as connection, connection:
                connection.execute("CREATE TABLE metadata(field_id INTEGER PRIMARY KEY)")
            Path(str(prefix) + ".tbc.json").write_text(__import__("json").dumps(metadata))
            Path(str(prefix) + "_chroma.tbc").write_bytes(b"\0" * 8)
            for mode in ("vhs", "ld"):
                self.assertEqual(metadata, release.read_result(prefix, "exact", mode=mode))
            Path(str(prefix) + ".tbc.db").unlink()
            Path(str(prefix) + ".db").write_bytes(b"wrong suffix")
            with self.assertRaisesRegex(ValueError, "tbc.db"):
                release.read_result(prefix, "exact")

    def test_chroma_requires_complete_nonempty_field_geometry(self):
        video = {"fieldWidth": 2, "fieldHeight": 3, "numberOfSequentialFields": 2}
        with tempfile.TemporaryDirectory() as directory:
            prefix = Path(directory) / "vhs"
            path = Path(str(prefix) + "_chroma.tbc")
            with self.assertRaisesRegex(ValueError, "chroma output"):
                release.verify_chroma(prefix, video)
            for size in (0, 23, 25):
                path.write_bytes(b"\0" * size)
                with self.assertRaisesRegex(ValueError, "chroma output"):
                    release.verify_chroma(prefix, video)
            path.write_bytes(b"\0" * 24)
            self.assertEqual(release.sha256(path), release.verify_chroma(prefix, video))

    def test_ire_gate_rejects_active_picture_change_and_incorrect_geometry(self):
        video = {"fieldWidth": 4, "fieldHeight": 1, "numberOfSequentialFields": 1,
                 "activeVideoStart": 1, "activeVideoEnd": 3, "black16bIre": 1000, "white16bIre": 11000}
        with tempfile.TemporaryDirectory() as directory:
            first, second = Path(directory) / "a", Path(directory) / "b"
            Path(str(first) + ".tbc").write_bytes(struct.pack("<4H", 1000, 2000, 2000, 1000))
            Path(str(second) + ".tbc").write_bytes(struct.pack("<4H", 1000, 3000, 2000, 1000))
            with self.assertRaisesRegex(ValueError, "compatibility gate failed"):
                release.compare_tbc(first, second, video)
            video["fieldWidth"] = 5
            with self.assertRaisesRegex(ValueError, "metadata field geometry"):
                release.compare_tbc(first, second, video)

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
