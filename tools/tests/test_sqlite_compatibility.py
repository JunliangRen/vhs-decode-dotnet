from contextlib import closing
import importlib.util
import math
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import unittest


spec = importlib.util.spec_from_file_location(
    "sqlite_compatibility", Path(__file__).resolve().parents[1] / "sqlite_compatibility.py")
compatibility = importlib.util.module_from_spec(spec)
spec.loader.exec_module(compatibility)


class SqliteCompatibilityTests(unittest.TestCase):
    def create(self, path, rows=((1, 42.0, "first"), (2, 4.387, "second"))):
        with closing(sqlite3.connect(path)) as connection, connection:
            connection.execute("CREATE TABLE sample(id INTEGER PRIMARY KEY, value REAL, note TEXT)")
            connection.executemany("INSERT INTO sample VALUES(?,?,?)", rows)

    def test_physical_bytes_and_row_layout_do_not_change_semantic_contract(self):
        with tempfile.TemporaryDirectory() as directory:
            first, second = Path(directory) / "a.db", Path(directory) / "b.db"
            self.create(first)
            self.create(second, ((2, 4.387, "second"), (1, 42.0, "first")))
            data = bytearray(second.read_bytes())
            data[96:100] = (3_054_000).to_bytes(4, "big")
            second.write_bytes(data)
            self.assertNotEqual(first.read_bytes(), second.read_bytes())
            result = compatibility.compare_databases(first, second)
            self.assertEqual(64, len(result["schema_sha256"]))
            self.assertEqual(64, len(result["sql_dump_sha256"]))

    def test_value_changes_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            first, second = Path(directory) / "a.db", Path(directory) / "b.db"
            self.create(first)
            self.create(second, ((1, 42.0, "changed"), (2, 4.387, "second")))
            with self.assertRaisesRegex(ValueError, "SQL dump differs"):
                compatibility.compare_databases(first, second)

    def test_schema_changes_are_rejected_even_when_rows_match(self):
        with tempfile.TemporaryDirectory() as directory:
            first, second = Path(directory) / "a.db", Path(directory) / "b.db"
            self.create(first)
            self.create(second)
            with closing(sqlite3.connect(second)) as connection, connection:
                connection.execute("CREATE INDEX changed ON sample(note)")
            with self.assertRaisesRegex(ValueError, "schema differs"):
                compatibility.compare_databases(first, second)

    def test_schema_version_changes_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            first, second = Path(directory) / "a.db", Path(directory) / "b.db"
            self.create(first)
            self.create(second)
            with closing(sqlite3.connect(second)) as connection, connection:
                connection.execute("PRAGMA user_version=2")
            with self.assertRaisesRegex(ValueError, "schema differs"):
                compatibility.compare_databases(first, second)

    def test_export_preserves_storage_types_and_ignores_row_order(self):
        with tempfile.TemporaryDirectory() as directory:
            first, second = Path(directory) / "a.db", Path(directory) / "b.db"
            for path, values in ((first, (1, "same", b"same")), (second, (b"same", "same", 1))):
                with closing(sqlite3.connect(path)) as connection, connection:
                    connection.execute("CREATE TABLE sample(value)")
                    connection.executemany("INSERT INTO sample VALUES(?)", [(v,) for v in values])
            compatibility.compare_databases(first, second)
            with closing(sqlite3.connect(second)) as connection, connection:
                connection.execute("UPDATE sample SET value=1.0 WHERE typeof(value)='integer'")
            with self.assertRaisesRegex(ValueError, "SQL dump differs"):
                compatibility.compare_databases(first, second)

    def test_sql_export_round_trips_types_and_escaped_text(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'file # with spaces.db'
            values = (None, 17, 17.0, -0.125, "a'b\n日本語\0tail", b"\x00\xff'", math.inf, -math.inf)
            with closing(sqlite3.connect(path)) as connection, connection:
                connection.execute('CREATE TABLE "odd""table"(a,b,c,d,e,f,g,h)')
                connection.execute('INSERT INTO "odd""table" VALUES(?,?,?,?,?,?,?,?)', values)
            before = path.read_bytes()
            snapshot = compatibility.snapshot(path)
            with closing(sqlite3.connect(":memory:")) as connection, connection:
                connection.executescript(snapshot["sql_dump"])
                self.assertEqual(values, connection.execute('SELECT * FROM "odd""table"').fetchone())
            self.assertIn('INSERT INTO "odd""table" VALUES(NULL,17,17.0,-0.125,', snapshot["sql_dump"])
            self.assertEqual(before, path.read_bytes())

    def test_duplicates_and_changes_after_nul_are_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            first, second = Path(directory) / "a.db", Path(directory) / "b.db"
            for path, values in ((first, ("a\0one", "a\0one")), (second, ("a\0one", "a\0two"))):
                with closing(sqlite3.connect(path)) as connection, connection:
                    connection.execute("CREATE TABLE sample(value TEXT)")
                    connection.executemany("INSERT INTO sample VALUES(?)", [(v,) for v in values])
            dump = compatibility.snapshot(first)["sql_dump"]
            self.assertEqual(2, dump.count("INSERT INTO"))
            with self.assertRaisesRegex(ValueError, "SQL dump differs"):
                compatibility.compare_databases(first, second)

    def test_missing_and_corrupt_databases_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "missing.db"
            with self.assertRaises(sqlite3.Error):
                compatibility.snapshot(path)
            self.assertFalse(path.exists())
            path.write_bytes(b"not a SQLite database")
            with self.assertRaises(sqlite3.Error):
                compatibility.snapshot(path)

    def test_cli_fails_for_invalid_input_without_writing_success_report(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            output = root / "snapshot.json"
            result = subprocess.run([sys.executable, str(Path(compatibility.__file__)),
                                     str(root / "missing.db"), "--output", str(output)],
                                    capture_output=True, text=True)
            self.assertEqual(1, result.returncode)
            self.assertIn("SQLite compatibility check failed", result.stderr)
            self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
