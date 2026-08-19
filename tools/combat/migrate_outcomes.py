#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# ///
"""Migrate stored fight outcomes to the unified FightOutcome vocabulary (2026-08-19).

MUD2 ends a fight in exactly seven ways (see MudSharp.Combat.FightOutcome). Two of them -- the
creature's failed flee and the player's -- were not recognised by this project at all until
2026-08-19, and the five that were recognised had drifted into two spellings: the live client wrote
PascalCase enum names into ~/.mucka/combat/mucka.db, while the offline reducer wrote lowercase
hyphenated names into ~/.mucka/combat/combat.db, despite schema.sql claiming the two were "directly
comparable". They now share one spelling.

    live (mucka.db)        offline (combat.db)      new
    Killed                 killed                   Kill
    KilledByNpc            (n/a)                    Died
    NpcFled                npc-fled                 CFled
    YouFled                you-fled                 UFled
    Withdrawn              withdrawn                Withdraw
    Unresolved             pass/unresolved          Unresolved
    (never recorded)       (never recorded)         CFledFail   <- new, creature broke off
    (never recorded)       (never recorded)         UFledFail   <- new, player's flee failed

The two new outcomes are deliberately NOT back-filled. No stored row can be reclassified into them:
the rows that should have been CFledFail were written as Unresolved (the live recorder never resolved
a failed flee at all) or were never written, and nothing in a rollup row records which line ended the
fight. Re-reducing the original captures with the current reduce_combat.py is the only honest way to
recover them, and that is a separate operation on the raw jsonl -- not something this script can
invent from a summary.

Usage:
  uv run tools/combat/migrate_outcomes.py                 # migrate both default databases
  uv run tools/combat/migrate_outcomes.py --dry-run       # report what would change, touch nothing
  uv run tools/combat/migrate_outcomes.py --db path.db    # one specific database
  uv run tools/combat/migrate_outcomes.py --no-backup     # skip the .bak copy (not recommended)

Idempotent: rows already carrying the new spellings are left alone, so re-running is safe.
"""

import argparse
import shutil
import sqlite3
import sys
from pathlib import Path

HOME_COMBAT = Path.home() / ".mucka" / "combat"
DEFAULT_DBS = [HOME_COMBAT / "mucka.db", HOME_COMBAT / "combat.db"]

# (table, column) pairs that hold an outcome, checked for existence before use.
OUTCOME_COLUMNS = [
    ("fights", "outcome"),          # mucka.db  - written by FightHistoryStore
    ("combat_fights", "outcome"),   # combat.db - written by reduce_combat.py
    ("live_fights", "outcome"),     # combat.db - fights.jsonl imported by ingest_clogs.py
]

# Old spelling -> new. Both vocabularies in one map; they never collide.
RENAMES = {
    # live / PascalCase
    "Killed": "Kill",
    "KilledByNpc": "Died",
    "NpcFled": "CFled",
    "YouFled": "UFled",
    "Withdrawn": "Withdraw",
    # offline / lowercase-hyphenated
    "killed": "Kill",
    "npc-fled": "CFled",
    "you-fled": "UFled",
    "withdrawn": "Withdraw",
    "pass/unresolved": "Unresolved",
}

VALID_NEW = {"Kill", "CFled", "CFledFail", "UFled", "UFledFail", "Withdraw", "Died", "Unresolved"}


def tables(con: sqlite3.Connection) -> set[str]:
    return {r[0] for r in con.execute("SELECT name FROM sqlite_master WHERE type='table'")}


def migrate_db(path: Path, dry_run: bool, backup: bool) -> int:
    if not path.exists():
        print(f"  {path}: not present, skipped")
        return 0

    con = sqlite3.connect(path)
    present = tables(con)
    targets = [(t, c) for t, c in OUTCOME_COLUMNS if t in present]
    if not targets:
        print(f"  {path}: no outcome tables, skipped")
        con.close()
        return 0

    # Survey first so --dry-run and the real run report identically.
    planned = 0
    for table, col in targets:
        counts = dict(con.execute(f"SELECT {col}, COUNT(*) FROM {table} GROUP BY {col}"))
        for value, n in sorted(counts.items(), key=lambda kv: -kv[1]):
            if value in RENAMES:
                print(f"  {path.name}/{table}: {value!r} -> {RENAMES[value]!r}  ({n} rows)")
                planned += n
            elif value in VALID_NEW:
                print(f"  {path.name}/{table}: {value!r} already current  ({n} rows)")
            else:
                # Loud rather than silent: an unrecognised outcome means either a vocabulary this
                # script has not been told about, or corruption. Either way it must not be rewritten.
                print(f"  {path.name}/{table}: UNKNOWN outcome {value!r} ({n} rows) - LEFT UNTOUCHED",
                      file=sys.stderr)

    if planned == 0:
        print(f"  {path.name}: nothing to do")
        con.close()
        return 0

    if dry_run:
        con.close()
        return planned

    con.close()
    if backup:
        bak = path.with_suffix(path.suffix + ".pre-outcome-rename.bak")
        shutil.copy2(path, bak)
        print(f"  backup: {bak}")

    con = sqlite3.connect(path)
    changed = 0
    with con:
        for table, col in targets:
            for old, new in RENAMES.items():
                cur = con.execute(f"UPDATE {table} SET {col} = ? WHERE {col} = ?", (new, old))
                changed += cur.rowcount
    con.close()
    print(f"  {path.name}: {changed} rows rewritten")
    return changed


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--db", type=Path, action="append",
                    help="database to migrate (repeatable); defaults to mucka.db and combat.db")
    ap.add_argument("--dry-run", action="store_true", help="report only, change nothing")
    ap.add_argument("--no-backup", action="store_true", help="do not write a .bak copy first")
    args = ap.parse_args()

    dbs = args.db or DEFAULT_DBS
    print("dry run - nothing will be written" if args.dry_run else "migrating")
    total = 0
    for db in dbs:
        total += migrate_db(db, args.dry_run, not args.no_backup)
    print(f"total rows {'to rewrite' if args.dry_run else 'rewritten'}: {total}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
