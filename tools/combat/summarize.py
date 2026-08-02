#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# ///
"""Generate SUMMARY.md from a reduced MUD2 combat database.

Usage:
  uv run tools/combat/summarize.py
  uv run tools/combat/summarize.py --db path\to\combat.db --out tools/combat/SUMMARY.md
"""

from __future__ import annotations

import argparse
import sqlite3
from pathlib import Path

DEFAULT_DB = Path.home() / ".mucka" / "combat" / "combat.db"
DEFAULT_OUT = Path(__file__).with_name("SUMMARY.md")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--db", type=Path, default=DEFAULT_DB, help="SQLite DB path.")
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT, help="Markdown output path.")
    return parser.parse_args()


def fmt_value(value: object) -> str:
    if value is None:
        return ""
    if isinstance(value, float):
        return f"{value:.3f}".rstrip("0").rstrip(".")
    return str(value)


def write_table(lines: list[str], headers: list[str], rows: list[sqlite3.Row]) -> None:
    lines.append("| " + " | ".join(headers) + " |")
    lines.append("| " + " | ".join("---" for _ in headers) + " |")
    for row in rows:
        lines.append("| " + " | ".join(fmt_value(row[h]) for h in headers) + " |")
    lines.append("")


def main() -> int:
    args = parse_args()
    con = sqlite3.connect(args.db)
    con.row_factory = sqlite3.Row
    try:
        total = con.execute("SELECT * FROM v_summary_total").fetchone()
        by_weapon = con.execute("SELECT * FROM v_summary_by_weapon").fetchall()
        by_npc = con.execute("SELECT * FROM v_summary_by_npc").fetchall()
        by_group = con.execute("SELECT * FROM v_summary_by_npc_group").fetchall()
    finally:
        con.close()

    if total is None:
        raise SystemExit("No summary data found. Run reduce_combat.py first.")

    unique_npcs = total["unique_npcs_csv"] or ""
    lines: list[str] = []
    lines.append("# Combat summary")
    lines.append("")
    lines.append(f"Database: `{args.db}`")
    lines.append("")
    lines.append("## Totals")
    lines.append("")
    lines.append(f"Unique NPCs fought: {unique_npcs}")
    lines.append("")
    write_table(
        lines,
        [
            "encounter_count",
            "fight_count",
            "kills",
            "passes",
            "npc_flees",
            "your_flees",
            "withdrawn",
            "approx_damage_done",
            "approx_health_lost",
            "fight_duration_seconds",
            "approx_dps",
        ],
        [total],
    )

    lines.append("## By Weapon")
    lines.append("")
    write_table(
        lines,
        [
            "weapon_used",
            "encounter_count",
            "fight_count",
            "kills",
            "passes",
            "npc_flees",
            "your_flees",
            "withdrawn",
            "approx_damage_done",
            "approx_health_lost",
            "fight_duration_seconds",
            "approx_dps",
            "unique_npcs_csv",
        ],
        by_weapon,
    )

    lines.append("## By NPC unique-instance")
    lines.append("")
    write_table(
        lines,
        [
            "npc_name",
            "fight_count",
            "kills",
            "passes",
            "npc_flees",
            "your_flees",
            "withdrawn",
            "approx_damage_done",
            "approx_health_lost",
            "fight_duration_seconds",
            "approx_dps",
        ],
        by_npc,
    )

    lines.append("## By NPC species-group")
    lines.append("")
    write_table(
        lines,
        [
            "npc_group",
            "fight_count",
            "kills",
            "passes",
            "npc_flees",
            "your_flees",
            "withdrawn",
            "approx_damage_done",
            "approx_health_lost",
            "fight_duration_seconds",
            "approx_dps",
            "unique_npcs_csv",
        ],
        by_group,
    )

    args.out.write_text("\n".join(lines) + "\n", encoding="ascii")
    print(f"Wrote {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
