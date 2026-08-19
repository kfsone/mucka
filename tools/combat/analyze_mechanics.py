#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# ///
"""Query the combat database for weapon/NPC effectiveness and stat correlations."""

from __future__ import annotations

import argparse
import json
import re
import sqlite3
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path
from statistics import mean

from init_db import DEFAULT_DB, ensure_schema

HIT_RE = re.compile(r"You hit the .*? \((?P<low>\d+)-(?P<high>\d+)\)\.")
DEFAULT_NOTES = Path(__file__).with_name("MECHANICS_NOTES.md")


@dataclass
class MatrixRow:
    weapon_used: str
    npc_group: str
    fight_count: int
    hits: int
    misses: int
    hit_rate: float | None
    min_hit_low: int | None
    max_hit_high: int | None
    mean_hit_midpoint: float | None
    approx_dps: float | None
    kills: int
    deaths: int
    npc_flees: int
    your_flees: int
    withdrawn: int


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--db", type=Path, default=DEFAULT_DB, help="SQLite DB path.")
    parser.add_argument("--notes-out", type=Path, default=DEFAULT_NOTES, help="Methodology notes path.")
    # Off by default: write_notes()'s template is a fixed skeleton that only covers the
    # methodology sections, while MECHANICS_NOTES.md has accumulated ~370 lines of hand-written
    # live-session research findings on top of it. Overwriting unconditionally silently destroyed
    # that. Requiring an explicit opt-in means running this tool for its normal purpose (the
    # console report) can never lose notes by accident.
    parser.add_argument(
        "--write-notes",
        action="store_true",
        help="Overwrite --notes-out with the methodology template. Off by default: this tool's "
        "normal use is the console report; refreshing the notes file is a separate, deliberate "
        "action. Refuses to shrink an existing file unless --force-notes-overwrite is also given.",
    )
    parser.add_argument(
        "--force-notes-overwrite",
        action="store_true",
        help="With --write-notes, allow overwriting --notes-out even if the existing file is "
        "larger than the template about to be written (i.e. it likely has hand-written content "
        "the template does not reproduce). Without this, a larger existing file is left untouched.",
    )
    return parser.parse_args()


def fmt(value: object) -> str:
    if value is None:
        return ""
    if isinstance(value, float):
        return f"{value:.3f}".rstrip("0").rstrip(".")
    return str(value)


def write_markdown_table(headers: list[str], rows: list[dict[str, object]]) -> str:
    lines = ["| " + " | ".join(headers) + " |", "| " + " | ".join("---" for _ in headers) + " |"]
    for row in rows:
        lines.append("| " + " | ".join(fmt(row.get(header)) for header in headers) + " |")
    return "\n".join(lines)


def build_matrix(con: sqlite3.Connection) -> list[MatrixRow]:
    fight_rows = con.execute(
        """
        SELECT
            f.id,
            COALESCE(NULLIF(f.weapon_used, ''), '(unknown)') AS weapon_used,
            f.npc_group,
            f.outcome,
            f.duration_ms
        FROM combat_fights f
        ORDER BY f.id
        """
    ).fetchall()
    hit_rows = con.execute(
        """
        SELECT
            f.id AS fight_id,
            COALESCE(NULLIF(f.weapon_used, ''), '(unknown)') AS weapon_used,
            f.npc_group,
            e.event_type,
            e.plain_text
        FROM combat_events e
        JOIN combat_fights f ON f.id = e.fight_id
        WHERE e.event_type IN ('you-hit', 'you-miss', 'they-killed')
        ORDER BY f.id, e.id
        """
    ).fetchall()

    grouped: dict[tuple[str, str], dict[str, object]] = defaultdict(
        lambda: {
            "fight_ids": set(),
            "hits": 0,
            "misses": 0,
            "min_low": None,
            "max_high": None,
            "midpoints": [],
            "kills": 0,
            "deaths": 0,
            "npc_flees": 0,
            "your_flees": 0,
            "withdrawn": 0,
            "damage_done": 0.0,
            "duration_ms": 0,
        }
    )

    for row in fight_rows:
        key = (row["weapon_used"], row["npc_group"])
        bucket = grouped[key]
        bucket["fight_ids"].add(row["id"])
        bucket["duration_ms"] += row["duration_ms"] or 0
        if row["outcome"] == "Kill":
            bucket["kills"] += 1
        elif row["outcome"] == "CFled":
            bucket["npc_flees"] += 1
        elif row["outcome"] == "UFled":
            bucket["your_flees"] += 1
        elif row["outcome"] == "Withdraw":
            bucket["withdrawn"] += 1

    for row in hit_rows:
        key = (row["weapon_used"], row["npc_group"])
        bucket = grouped[key]
        if row["event_type"] == "you-miss":
            bucket["misses"] += 1
            continue
        if row["event_type"] == "they-killed":
            bucket["deaths"] += 1
            continue
        match = HIT_RE.search(row["plain_text"])
        if not match:
            continue
        low = int(match.group("low"))
        high = int(match.group("high"))
        mid = (low + high) / 2.0
        bucket["hits"] += 1
        bucket["midpoints"].append(mid)
        bucket["damage_done"] += mid
        bucket["min_low"] = low if bucket["min_low"] is None else min(bucket["min_low"], low)
        bucket["max_high"] = high if bucket["max_high"] is None else max(bucket["max_high"], high)

    rows: list[MatrixRow] = []
    for (weapon_used, npc_group), bucket in grouped.items():
        attempts = bucket["hits"] + bucket["misses"]
        duration_ms = bucket["duration_ms"]
        rows.append(
            MatrixRow(
                weapon_used=weapon_used,
                npc_group=npc_group,
                fight_count=len(bucket["fight_ids"]),
                hits=bucket["hits"],
                misses=bucket["misses"],
                hit_rate=(bucket["hits"] / attempts) if attempts else None,
                min_hit_low=bucket["min_low"],
                max_hit_high=bucket["max_high"],
                mean_hit_midpoint=mean(bucket["midpoints"]) if bucket["midpoints"] else None,
                approx_dps=(bucket["damage_done"] / (duration_ms / 1000.0)) if duration_ms else None,
                kills=bucket["kills"],
                deaths=bucket["deaths"],
                npc_flees=bucket["npc_flees"],
                your_flees=bucket["your_flees"],
                withdrawn=bucket["withdrawn"],
            )
        )
    rows.sort(key=lambda row: (-row.fight_count, row.weapon_used, row.npc_group))
    return rows


def find_nearest_stats(con: sqlite3.Connection, window_ms: int = 30000) -> list[sqlite3.Row]:
    return con.execute(
        """
        WITH ranked AS (
            SELECT
                f.id AS fight_id,
                COALESCE(NULLIF(f.weapon_used, ''), '(unknown)') AS weapon_used,
                f.npc_group,
                f.start_timestamp_ms,
                f.you_hits,
                f.you_misses,
                f.approx_damage_done,
                ss.timestamp_ms AS stats_timestamp_ms,
                ss.weight_carried_grams,
                ss.objects_carried,
                ss.raw_strength,
                ss.strength,
                ss.raw_dexterity,
                ss.dexterity,
                ABS(ss.timestamp_ms - f.start_timestamp_ms) AS delta_ms,
                ROW_NUMBER() OVER (
                    PARTITION BY f.id
                    ORDER BY ABS(ss.timestamp_ms - f.start_timestamp_ms), ss.timestamp_ms
                ) AS rn
            FROM combat_fights f
            JOIN stats_snapshots ss
              ON ss.capture_id = f.capture_id
             AND ss.timestamp_ms BETWEEN f.start_timestamp_ms - ? AND f.start_timestamp_ms + ?
        )
        SELECT *
        FROM ranked
        WHERE rn = 1
        ORDER BY fight_id
        """,
        (window_ms, window_ms),
    ).fetchall()


def build_stat_bucket_rows(nearest_rows: list[sqlite3.Row]) -> tuple[list[dict[str, object]], dict[str, int]]:
    coverage = {
        "fights_with_near_stats": len(nearest_rows),
        "with_weight": 0,
        "with_objects": 0,
        "with_raw_strength": 0,
        "with_raw_dexterity": 0,
    }
    buckets: dict[tuple[object, ...], dict[str, object]] = defaultdict(
        lambda: {"fights": 0, "hits": 0, "misses": 0, "approx_damage_done": 0.0, "delta_ms": []}
    )
    for row in nearest_rows:
        if row["weight_carried_grams"] is not None:
            coverage["with_weight"] += 1
        if row["objects_carried"] is not None:
            coverage["with_objects"] += 1
        if row["raw_strength"] is not None:
            coverage["with_raw_strength"] += 1
        if row["raw_dexterity"] is not None:
            coverage["with_raw_dexterity"] += 1
        key = (
            row["weapon_used"],
            row["npc_group"],
            row["weight_carried_grams"],
            row["objects_carried"],
            row["raw_strength"],
            row["strength"],
            row["raw_dexterity"],
            row["dexterity"],
        )
        bucket = buckets[key]
        bucket["fights"] += 1
        bucket["hits"] += row["you_hits"] or 0
        bucket["misses"] += row["you_misses"] or 0
        bucket["approx_damage_done"] += row["approx_damage_done"] or 0.0
        bucket["delta_ms"].append(row["delta_ms"])

    rows: list[dict[str, object]] = []
    for key, bucket in buckets.items():
        hits = bucket["hits"]
        misses = bucket["misses"]
        rows.append(
            {
                "weapon_used": key[0],
                "npc_group": key[1],
                "weight_carried_grams": key[2],
                "objects_carried": key[3],
                "raw_strength": key[4],
                "effective_strength": key[5],
                "raw_dexterity": key[6],
                "effective_dexterity": key[7],
                "fights": bucket["fights"],
                "swings": hits + misses,
                "hit_rate": (hits / (hits + misses)) if (hits + misses) else None,
                "avg_damage_per_hit_midpoint": (bucket["approx_damage_done"] / hits) if hits else None,
                "nearest_stats_delta_ms_avg": mean(bucket["delta_ms"]) if bucket["delta_ms"] else None,
            }
        )
    rows.sort(key=lambda row: (-int(row["swings"]), str(row["weapon_used"]), str(row["npc_group"])))
    return rows, coverage


def write_notes(path: Path, force: bool = False) -> str | None:
    """Overwrite path with the fixed methodology template. Returns None on success, or a
    human-readable reason the write was refused (path is left untouched in that case).

    This template only ever reproduces the methodology sections below - MECHANICS_NOTES.md is
    expected to accumulate hand-written live-session research findings on top of it, so writing
    over an existing file that is already bigger than the template would silently discard that
    research. Refuse unless the caller passes force=True (wired to --force-notes-overwrite).
    """
    text = """# Combat mechanics notes

## Current observables in the merged database

- Per-fight outcome, duration, weapon, npc instance, and npc group.
- Per-hit player damage ranges from combat prose, stored as replayable event text.
- Approximate damage taken inferred from stamina-before minus stamina-after when a hit line reports it.
- Encounter-start room, weather, and status/effect snapshot from live clogs.
- Effective strength/dexterity from the older research capture, plus new live-capture fields for raw strength, raw dexterity, carried weight, carried object count, level, and games played.

## Hidden weapon modifier methodology

The cleanest way to isolate a hidden per-weapon damage modifier is controlled A/B sampling:

1. Hold the target constant: same npc_group, ideally same room/light/weather where possible.
2. Hold the player state constant: same raw/effective strength, raw/effective dexterity, similar stamina, same afflictions, same carried weight, same carried object count.
3. Vary only the weapon.
4. Collect enough swings per condition to compare both hit rate and damage-per-hit distribution, not just one kill time.
5. Prefer repeated single-target fights over pack fights, since joins and retargets muddy fight duration and weapon provenance.

Suggested analysis sequence:

- First compare average hit midpoint and hit rate for the same weapon against the same npc_group.
- Then compare two weapons against that same npc_group under matching raw/effective stats buckets.
- If a weapon shows consistently higher damage at the same raw strength and same target, the residual is a candidate hidden modifier.
- If hit rate changes but damage-per-hit does not, the hidden property may be accuracy or timing rather than raw damage.

## What is still missing for rigorous proof

- Most existing rows do not yet have raw_strength, raw_dexterity, weight_carried_grams, or objects_carried because the older captures predate the new scorecard parsing.
- We still do not know the exact in-game formula mapping strength, weight, and dexterity to hit chance or damage.
- We do not have direct npc stats; we only see outcomes.
- We do not persist explicit room lighting state, only room prose and weather.
- Current live clogs snapshot stats at encounter start, not every weapon switch or every joiner start inside a long encounter.

## Highest-value next data improvements

- Keep collecting live clogs after the new scorecard fields land so raw/effective stat deltas become queryable.
- Add inventory parsing so carried item identities can be correlated with weight and dex penalties.
- Capture nearest scorecard snapshot after weapon-equip or weapon-break events when practical.
- If any command or prose reveals weapon weight directly, record that verbatim alongside the equipped weapon.
- Consider an explicit light/darkness flag if the protocol exposes one; some user hypotheses depend on visibility.

## Search result: direct weapon-weight evidence

A repository search over the research capture and current clogs found scorecard "weight carried" lines, but no direct prose reporting a weapon's own weight next to its weapon name. That means the current best path is still indirect inference: use controlled same-target comparisons while holding carried weight and effective stats as constant as possible.
"""
    new_size = len(text.encode("ascii"))
    if path.exists() and not force:
        existing_size = path.stat().st_size
        if existing_size > new_size:
            return (
                f"refusing to overwrite {path} ({existing_size} bytes) with the smaller "
                f"template ({new_size} bytes) - it likely holds hand-written notes the template "
                "does not reproduce; pass --force-notes-overwrite to override"
            )
    path.write_text(text, encoding="ascii")
    return None


def main() -> int:
    args = parse_args()
    con = sqlite3.connect(args.db)
    con.row_factory = sqlite3.Row
    try:
        ensure_schema(con)
        counts = con.execute(
            """
            SELECT
                COUNT(*) AS total_captures,
                SUM(CASE WHEN INSTR(LOWER(source_file), '.mucka\\clogs\\') > 0 THEN 1 ELSE 0 END) AS clog_captures
            FROM captures
            """
        ).fetchone()
        matrix = build_matrix(con)
        nearest_rows = find_nearest_stats(con)
        bucket_rows, coverage = build_stat_bucket_rows(nearest_rows)
    finally:
        con.close()

    # Opt-in only (--write-notes) - see write_notes()'s docstring and --write-notes' help text:
    # the notes file accumulates hand-written research on top of this tool's fixed template, so
    # the default run (the console report below) must never touch it.
    notes_status: str
    if not args.write_notes:
        notes_status = f"not written (pass --write-notes to refresh `{args.notes_out}`)"
    else:
        refusal = write_notes(args.notes_out, force=args.force_notes_overwrite)
        notes_status = refusal if refusal else f"written to `{args.notes_out}`"

    top_matrix = [
        {
            "weapon_used": row.weapon_used,
            "npc_group": row.npc_group,
            "fight_count": row.fight_count,
            "hits": row.hits,
            "misses": row.misses,
            "hit_rate": row.hit_rate,
            "min_hit_low": row.min_hit_low,
            "max_hit_high": row.max_hit_high,
            "mean_hit_midpoint": row.mean_hit_midpoint,
            "approx_dps": row.approx_dps,
            "kills": row.kills,
            "deaths": row.deaths,
            "npc_flees": row.npc_flees,
            "your_flees": row.your_flees,
            "withdrawn": row.withdrawn,
        }
        for row in matrix[:12]
    ]

    lines: list[str] = []
    lines.append("# Mechanics analysis")
    lines.append("")
    lines.append(f"Database: `{args.db}`")
    lines.append("")
    lines.append("## Coverage")
    lines.append("")
    lines.append(f"- Captures in DB: {counts['total_captures']}")
    lines.append(f"- Clog captures in DB: {counts['clog_captures'] or 0}")
    lines.append(f"- Fights with a stats snapshot within 30s of fight start: {coverage['fights_with_near_stats']}")
    lines.append(f"- Of those, rows with weight carried: {coverage['with_weight']}")
    lines.append(f"- Of those, rows with objects carried: {coverage['with_objects']}")
    lines.append(f"- Of those, rows with raw strength: {coverage['with_raw_strength']}")
    lines.append(f"- Of those, rows with raw dexterity: {coverage['with_raw_dexterity']}")
    lines.append("")
    lines.append("## Weapon x npc_group effectiveness matrix (top rows by fight count)")
    lines.append("")
    lines.append(
        write_markdown_table(
            [
                "weapon_used",
                "npc_group",
                "fight_count",
                "hits",
                "misses",
                "hit_rate",
                "min_hit_low",
                "max_hit_high",
                "mean_hit_midpoint",
                "approx_dps",
                "kills",
                "deaths",
                "npc_flees",
                "your_flees",
                "withdrawn",
            ],
            top_matrix,
        )
    )
    lines.append("")
    lines.append("## Stat buckets near fight start")
    lines.append("")
    if bucket_rows:
        lines.append(
            write_markdown_table(
                [
                    "weapon_used",
                    "npc_group",
                    "weight_carried_grams",
                    "objects_carried",
                    "raw_strength",
                    "effective_strength",
                    "raw_dexterity",
                    "effective_dexterity",
                    "fights",
                    "swings",
                    "hit_rate",
                    "avg_damage_per_hit_midpoint",
                    "nearest_stats_delta_ms_avg",
                ],
                bucket_rows[:12],
            )
        )
    else:
        lines.append("No fights had a stats snapshot within 30s of fight start.")
    lines.append("")
    lines.append(f"Methodology notes: {notes_status}")
    print("\n".join(lines))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
