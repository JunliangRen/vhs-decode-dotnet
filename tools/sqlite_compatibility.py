"""Compare completed metadata databases by schema and stable SQL contents."""

import argparse
import hashlib
import json
import math
from pathlib import Path
import sqlite3
import sys


def identifier(value):
    return '"' + value.replace('"', '""') + '"'


def sql_literal(value):
    if value is None:
        return "NULL"
    if isinstance(value, int):
        return str(value)
    if isinstance(value, float):
        if math.isnan(value):
            raise ValueError("SQLite returned an unsupported NaN value")
        if math.isinf(value):
            return "9.0e999" if value > 0 else "-9.0e999"
        return repr(value)
    if isinstance(value, bytes):
        return "X'" + value.hex() + "'"
    if isinstance(value, str):
        # SQL string literals cannot carry NUL; a UTF-8 blob cast preserves it.
        if "\0" in value:
            return "CAST(X'" + value.encode("utf-8").hex() + "' AS TEXT)"
        return "'" + value.replace("'", "''") + "'"
    raise ValueError(f"Unsupported SQLite value type: {type(value).__name__}")


def snapshot(database):
    database = Path(database).resolve()
    # mode=ro refuses missing databases and never silently creates an empty file.
    connection = sqlite3.connect(database.as_uri() + "?mode=ro", uri=True)
    try:
        connection.execute("BEGIN")
        if connection.execute("PRAGMA integrity_check").fetchall() != [("ok",)]:
            raise ValueError(f"SQLite integrity check failed: {database}")
        objects = [list(row) for row in connection.execute(
            "SELECT type, name, tbl_name, sql FROM sqlite_master ORDER BY type, name, tbl_name")]
        user_version = connection.execute("PRAGMA user_version").fetchone()[0]
        schema = {"user_version": user_version, "objects": objects}
        statements = ["BEGIN TRANSACTION;", f"PRAGMA user_version={user_version};"]
        for kind, name, _, sql in objects:
            if kind != "table":
                continue
            columns = connection.execute(f"PRAGMA table_xinfo({identifier(name)})").fetchall()
            if any(column[6] != 0 for column in columns):
                raise ValueError(f"Unsupported hidden or generated SQLite columns: {name}")
            if sql is not None:
                statements.append(sql.rstrip(";") + ";")
            inserts = [f"INSERT INTO {identifier(name)} VALUES(" +
                       ",".join(sql_literal(value) for value in row) + ");"
                       for row in connection.execute(f"SELECT * FROM {identifier(name)}")]
            # Physical row/page order is not a data contract; preserve duplicates.
            statements.extend(sorted(inserts))
        for kind, _, _, sql in objects:
            if kind != "table" and sql is not None:
                statements.append(sql.rstrip(";") + ";")
        statements.append("COMMIT;")
        sql_dump = "\n".join(statements) + "\n"
        schema_text = json.dumps(schema, ensure_ascii=False, separators=(",", ":"))
        return {"schema": schema, "sql_dump": sql_dump,
                "schema_sha256": hashlib.sha256(schema_text.encode("utf-8")).hexdigest(),
                "sql_dump_sha256": hashlib.sha256(sql_dump.encode("utf-8")).hexdigest()}
    finally:
        connection.close()


def compare_databases(reference, candidate):
    left, right = snapshot(reference), snapshot(candidate)
    if left["schema"] != right["schema"]:
        raise ValueError("SQLite schema differs")
    if left["sql_dump"] != right["sql_dump"]:
        raise ValueError("SQLite SQL dump differs")
    return {key: left[key] for key in ("schema_sha256", "sql_dump_sha256")}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("database", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    options = parser.parse_args()
    try:
        result = snapshot(options.database)
        # Escape embedded newlines so CRLF conversion only changes JSON layout.
        text = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
        options.output.write_bytes(text.replace("\n", "\r\n").encode("utf-8"))
        print(json.dumps({key: result[key] for key in ("schema_sha256", "sql_dump_sha256")}))
    except (ValueError, OSError, sqlite3.Error) as error:
        print(f"SQLite compatibility check failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
