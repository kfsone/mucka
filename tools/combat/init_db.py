#!/usr/bin/env python3
"""
Initialize (or verify) the MUD2 combat-analysis SQLite database.

Usage:
    uv run tools/combat/init_db.py [path/to/combat.db]

Default location: ~/.mucka/combat/combat.db
"""

import sqlite3
import sys
from pathlib import Path

SCHEMA_FILE = Path(__file__).parent / "schema.sql"
DEFAULT_DB = Path.home() / ".mucka" / "combat" / "combat.db"


def _column_names(con: sqlite3.Connection, table: str) -> set[str]:
    return {row[1] for row in con.execute(f"PRAGMA table_info({table})")}


def ensure_schema(con: sqlite3.Connection) -> None:
    con.executescript(SCHEMA_FILE.read_text(encoding="utf-8"))

    stats_columns = _column_names(con, "stats_snapshots")
    wanted = {
        "raw_strength": "INTEGER",
        "raw_dexterity": "INTEGER",
        "weight_carried_grams": "INTEGER",
        "max_weight_grams": "INTEGER",
        "objects_carried": "INTEGER",
        "max_objects_carried": "INTEGER",
        "level": "INTEGER",
        "games_played": "INTEGER",
    }
    for name, type_name in wanted.items():
        if name not in stats_columns:
            con.execute(f"ALTER TABLE stats_snapshots ADD COLUMN {name} {type_name}")


def init_db(db_path: Path) -> None:
    db_path.parent.mkdir(parents=True, exist_ok=True)
    con = sqlite3.connect(db_path)
    try:
        ensure_schema(con)
        con.commit()
        tables = [r[0] for r in con.execute(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name"
        ).fetchall()]
        print(f"DB ready: {db_path}")
        for table in tables:
            count = con.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0]
            print(f"  {table}: {count} rows")
    finally:
        con.close()


if __name__ == "__main__":
    path = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_DB
    init_db(path)
