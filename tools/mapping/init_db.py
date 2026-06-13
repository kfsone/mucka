#!/usr/bin/env python3
"""
Initialize (or verify) the MUD2 mapping SQLite database.

Usage:
    uv run tools/mapping/init_db.py [path/to/mapdb.sqlite]

Default location: ~/.mucka/mapping/mapdb.sqlite
"""

import sqlite3
import sys
from pathlib import Path

SCHEMA_FILE = Path(__file__).parent / "schema.sql"
DEFAULT_DB = Path.home() / ".mucka" / "mapping" / "mapdb.sqlite"


def init_db(db_path: Path) -> None:
    db_path.parent.mkdir(parents=True, exist_ok=True)
    con = sqlite3.connect(db_path)
    try:
        con.executescript(SCHEMA_FILE.read_text(encoding="utf-8"))
        con.commit()
        # Report table row counts as a quick sanity check
        tables = [r[0] for r in con.execute(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name"
        ).fetchall()]
        print(f"DB ready: {db_path}")
        for t in tables:
            n = con.execute(f"SELECT COUNT(*) FROM {t}").fetchone()[0]
            print(f"  {t}: {n} rows")
    finally:
        con.close()


if __name__ == "__main__":
    path = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_DB
    init_db(path)
