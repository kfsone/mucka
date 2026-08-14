#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# ///
"""Ingest live combat clogs into the combat-analysis SQLite database.

Usage:
  uv run tools/combat/ingest_clogs.py
  uv run tools/combat/ingest_clogs.py --db path\to\combat.db
  uv run tools/combat/ingest_clogs.py --clog-dir path\to\clogs --force
"""

from __future__ import annotations

import argparse
import json
import sqlite3
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from init_db import DEFAULT_DB, ensure_schema
from reduce_combat import KILL_GRACE_MS, make_event_id, midpoint, normalize_npc_group, stable_id

DEFAULT_CLOG_DIR = Path.home() / ".mucka" / "clogs"


@dataclass
class FightState:
    npc_name: str
    npc_group: str
    initiator: str | None
    start_event_id: str
    start_timestamp_ms: int
    start_weapon: str | None
    weapon_used: str | None
    end_event_id: str | None = None
    end_timestamp_ms: int | None = None
    outcome: str | None = None
    resolution_text: str | None = None
    you_hits: int = 0
    you_misses: int = 0
    they_hits: int = 0
    they_misses: int = 0
    approx_damage_done: float = 0.0
    approx_damage_taken: float = 0.0
    notes: list[str] = field(default_factory=list)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--db", type=Path, default=DEFAULT_DB, help="SQLite DB path.")
    parser.add_argument("--clog-dir", type=Path, default=DEFAULT_CLOG_DIR, help="Directory containing clog JSONL files.")
    parser.add_argument("--force", action="store_true", help="Reingest files already present in captures.")
    parser.add_argument(
        "--fights-file",
        type=Path,
        default=None,
        help="Per-fight rollup index written by the client (defaults to <clog-dir>/fights.jsonl).",
    )
    return parser.parse_args()


def slugify_effect_name(name: str) -> str:
    chars: list[str] = []
    for ch in name:
        if ch.isupper() and chars:
            chars.append("-")
        chars.append(ch.lower())
    return "".join(chars)


def load_json_lines(path: Path) -> tuple[list[str], list[dict[str, Any]]]:
    raw_lines: list[str] = []
    rows: list[dict[str, Any]] = []
    for line in path.read_text(encoding="utf-8").splitlines():
        text = line.strip()
        if not text:
            continue
        raw_lines.append(text)
        rows.append(json.loads(text))
    return raw_lines, rows


def insert_raw_event(
    con: sqlite3.Connection,
    *,
    capture_id: str,
    seq_index: int,
    timestamp_ms: int | None,
    tag_code: str | None,
    category: str,
    event_type: str,
    actor: str | None,
    subject_name: str | None,
    weapon_name: str | None,
    decoded_text: str,
    record_json: str,
) -> str:
    raw_event_id = make_event_id(capture_id, seq_index, 0)
    con.execute(
        """
        INSERT INTO raw_events (
            id, capture_id, seq_index, event_ordinal, timestamp_ms, direction, tag_code,
            category, event_type, actor, subject_name, weapon_name, decoded_text,
            snippet_text, record_json, is_client_probe
        ) VALUES (?, ?, ?, 0, ?, 'rx', ?, ?, ?, ?, ?, ?, ?, ?, ?, 0)
        """,
        (
            raw_event_id,
            capture_id,
            seq_index,
            timestamp_ms,
            tag_code,
            category,
            event_type,
            actor,
            subject_name,
            weapon_name,
            decoded_text,
            decoded_text[:240],
            record_json,
        ),
    )
    return raw_event_id


def coerce_int(value: Any) -> int | None:
    return value if isinstance(value, int) else None


def ensure_fight(
    fights: dict[str, FightState],
    participants: list[str],
    participant_set: set[str],
    *,
    npc_name: str,
    raw_event_id: str,
    timestamp_ms: int,
    current_weapon: str | None,
    initiator: str | None,
    event_type: str,
) -> FightState:
    fight = fights.get(npc_name)
    if fight is None or fight.outcome is not None:
        fight = FightState(
            npc_name=npc_name,
            npc_group=normalize_npc_group(npc_name),
            initiator=initiator,
            start_event_id=raw_event_id,
            start_timestamp_ms=timestamp_ms,
            start_weapon=current_weapon,
            weapon_used=current_weapon,
        )
        if event_type != "fight-start":
            fight.notes.append(f"fight opened implicitly from {event_type}")
        fights[npc_name] = fight
    if npc_name not in participant_set:
        participant_set.add(npc_name)
        participants.append(npc_name)
    return fight


def close_fight(fight: FightState, raw_event_id: str, timestamp_ms: int, outcome: str, resolution_text: str) -> None:
    fight.end_event_id = raw_event_id
    fight.end_timestamp_ms = timestamp_ms
    fight.outcome = outcome
    fight.resolution_text = resolution_text


def ingest_one(con: sqlite3.Connection, clog_path: Path, force: bool) -> dict[str, Any]:
    source_file = str(clog_path.resolve())
    existing = con.execute("SELECT id FROM captures WHERE source_file = ?", (source_file,)).fetchone()
    if existing is not None:
        if not force:
            return {"file": clog_path.name, "status": "skipped", "reason": "already ingested"}
        con.execute("DELETE FROM captures WHERE id = ?", (existing[0],))

    raw_lines, rows = load_json_lines(clog_path)
    if not rows:
        return {"file": clog_path.name, "status": "skipped", "reason": "empty file"}

    header = rows[0]
    footer = rows[-1]
    event_rows = [(idx, row, raw_lines[idx]) for idx, row in enumerate(rows) if row.get("type") == "event"]
    if header.get("type") != "encounter_start" or not event_rows:
        return {"file": clog_path.name, "status": "skipped", "reason": "missing encounter_start or event rows"}

    capture_id = stable_id(source_file)
    first_event = event_rows[0][1]
    last_event = event_rows[-1][1]
    started_at_ms = coerce_int(first_event.get("ts"))
    stopped_at_ms = coerce_int(footer.get("ts")) if footer.get("type") == "encounter_end" else coerce_int(last_event.get("ts"))
    loaded_at_ms = int(time.time() * 1000)

    con.execute(
        """
        INSERT INTO captures (id, source_file, started_at_ms, stopped_at_ms, loaded_at_ms)
        VALUES (?, ?, ?, ?, ?)
        """,
        (capture_id, source_file, started_at_ms, stopped_at_ms, loaded_at_ms),
    )

    header_json = raw_lines[0]
    header_event_id = insert_raw_event(
        con,
        capture_id=capture_id,
        seq_index=0,
        timestamp_ms=coerce_int(header.get("ts")),
        tag_code="clog.encounter-start",
        category="clog",
        event_type="clog-encounter-start",
        actor=None,
        subject_name=header.get("room"),
        weapon_name=None,
        decoded_text="encounter_start",
        record_json=header_json,
    )

    room_snapshot_id: str | None = None
    if header.get("room") is not None or header.get("weather") is not None:
        room_snapshot_id = stable_id(header_event_id, "room")
        con.execute(
            """
            INSERT INTO room_snapshots (
                id, capture_id, source_event_id, timestamp_ms, seq_index, ambient_code, ambient_name,
                room_short, room_long, exits_text, raw_text, note
            ) VALUES (?, ?, ?, ?, 0, NULL, NULL, ?, NULL, NULL, ?, ?)
            """,
            (
                room_snapshot_id,
                capture_id,
                header_event_id,
                coerce_int(header.get("ts")) or started_at_ms or 0,
                header.get("room"),
                header.get("room") or "",
                f"clog header weather={header.get('weather')!r}",
            ),
        )

    stats = header.get("stats") or {}
    stats_snapshot_id: str | None = None
    if isinstance(stats, dict) and stats:
        stats_snapshot_id = stable_id(header_event_id, "stats")
        con.execute(
            """
            INSERT INTO stats_snapshots (
                id, capture_id, source_event_id, timestamp_ms, seq_index,
                stamina, max_stamina, strength, raw_strength, max_strength,
                dexterity, raw_dexterity, max_dexterity, current_magic, max_magic, score,
                weight_carried_grams, max_weight_grams, objects_carried, max_objects_carried,
                level, games_played, is_blind, is_deaf, is_crippled, is_dumb,
                reset_minutes, weather, raw_text
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                stats_snapshot_id,
                capture_id,
                header_event_id,
                coerce_int(header.get("ts")) or started_at_ms or 0,
                0,
                coerce_int(stats.get("stamina")),
                coerce_int(stats.get("maxStamina")),
                coerce_int(stats.get("strength")),
                coerce_int(stats.get("rawStrength")),
                coerce_int(stats.get("maxStrength")),
                coerce_int(stats.get("dexterity")),
                coerce_int(stats.get("rawDexterity")),
                coerce_int(stats.get("maxDexterity")),
                coerce_int(stats.get("magic")),
                coerce_int(stats.get("maxMagic")),
                coerce_int(stats.get("score")),
                None,   # weight carried: deliberately not captured - see README
                coerce_int(stats.get("maxWeightGrams")),
                coerce_int(stats.get("objectsCarried")),
                coerce_int(stats.get("maxObjectsCarried")),
                coerce_int(stats.get("level")),
                coerce_int(stats.get("gamesPlayed")),
                1 if stats.get("isBlind") else 0,
                1 if stats.get("isDeaf") else 0,
                1 if stats.get("isCrippled") else 0,
                1 if stats.get("isDumb") else 0,
                None,
                (header.get("weather") or " ")[:1],
                json.dumps(stats, separators=(",", ":"), sort_keys=True),
            ),
        )

    current_weapon = None
    participants: list[str] = []
    participant_set: set[str] = set()
    fights: dict[str, FightState] = {}
    combat_event_rows: list[dict[str, Any]] = []
    you_hits = you_misses = they_hits = they_misses = withdraw_offers = kills_by_you = kills_by_them = join_events = 0
    last_known_stamina = coerce_int(stats.get("stamina")) if isinstance(stats, dict) else None
    end_reason = "ambiguous"
    end_detail = "clog ended without a terminal combat line"

    event_type_map = {
        "FightStart": ("08.00", "fight-start"),
        "Hit": ("08.01", "you-hit"),
        "Miss": ("08.02", "you-miss"),
        "HitByNpc": ("08.03", "they-hit"),
        "MissByNpc": ("08.04", "they-miss"),
        "WithdrawOffer": ("08.07", "offer-withdraw"),
        "Kill": ("08.08", "you-killed"),
        "KilledByNpc": ("08.09", "they-killed"),
        "Withdrawn": ("08.10", "fight-end-withdraw"),
        "NpcFled": ("08.11", "fight-end-flee"),
        "YouFled": ("08.11", "fight-end-flee"),
        "FightEndOther": ("08.12", "fight-end-other"),
        "WeaponEquip": ("plain.weapon-equip", "weapon-change"),
        "WeaponBroke": ("plain.weapon-broke", "weapon-broke"),
        "DroppedGuard": ("plain.guard-drop", "dropped-guard"),
    }
    actor_map = {"Player": "you", "Npc": "them"}

    raw_event_ids: list[str] = []
    for seq_index, row, record_json in event_rows:
        kind = row.get("kind")
        if kind not in event_type_map:
            continue
        tag_code, event_type = event_type_map[kind]
        raw_event_id = insert_raw_event(
            con,
            capture_id=capture_id,
            seq_index=seq_index,
            timestamp_ms=coerce_int(row.get("ts")),
            tag_code=tag_code,
            category="combat",
            event_type=event_type,
            actor=actor_map.get(row.get("actor")),
            subject_name=row.get("npc"),
            weapon_name=row.get("weapon"),
            decoded_text=row.get("raw") or kind,
            record_json=record_json,
        )
        raw_event_ids.append(raw_event_id)

        timestamp_ms = coerce_int(row.get("ts")) or 0
        npc_name = row.get("npc")
        weapon = row.get("weapon")
        if weapon and event_type in {"fight-start", "weapon-change"}:
            current_weapon = weapon

        approx_damage_taken = None
        if event_type == "fight-start":
            initiator = "player" if row.get("actor") == "Player" else "npc"
            existing_open = sum(1 for fight in fights.values() if fight.outcome is None)
            fight = ensure_fight(
                fights,
                participants,
                participant_set,
                npc_name=npc_name or "unknown",
                raw_event_id=raw_event_id,
                timestamp_ms=timestamp_ms,
                current_weapon=weapon or current_weapon,
                initiator=initiator,
                event_type=event_type,
            )
            fight.start_weapon = weapon or fight.start_weapon
            fight.weapon_used = weapon or fight.weapon_used
            if existing_open > 0:
                join_events += 1
        elif event_type == "you-hit" and npc_name:
            fight = ensure_fight(
                fights,
                participants,
                participant_set,
                npc_name=npc_name,
                raw_event_id=raw_event_id,
                timestamp_ms=timestamp_ms,
                current_weapon=current_weapon,
                initiator="player",
                event_type=event_type,
            )
            you_hits += 1
            fight.you_hits += 1
            damage = midpoint(coerce_int(row.get("rangeLow")), coerce_int(row.get("rangeHigh")))
            if damage is not None:
                fight.approx_damage_done += damage
        elif event_type == "you-miss" and npc_name:
            fight = ensure_fight(
                fights,
                participants,
                participant_set,
                npc_name=npc_name,
                raw_event_id=raw_event_id,
                timestamp_ms=timestamp_ms,
                current_weapon=current_weapon,
                initiator="player",
                event_type=event_type,
            )
            you_misses += 1
            fight.you_misses += 1
        elif event_type == "they-hit" and npc_name:
            fight = ensure_fight(
                fights,
                participants,
                participant_set,
                npc_name=npc_name,
                raw_event_id=raw_event_id,
                timestamp_ms=timestamp_ms,
                current_weapon=current_weapon,
                initiator="npc",
                event_type=event_type,
            )
            they_hits += 1
            fight.they_hits += 1
            stamina_after = coerce_int(row.get("rangeLow"))
            if last_known_stamina is not None and stamina_after is not None:
                damage_taken = last_known_stamina - stamina_after
                if damage_taken >= 0:
                    approx_damage_taken = float(damage_taken)
                    fight.approx_damage_taken += approx_damage_taken
            last_known_stamina = stamina_after
        elif event_type == "they-miss" and npc_name:
            fight = ensure_fight(
                fights,
                participants,
                participant_set,
                npc_name=npc_name,
                raw_event_id=raw_event_id,
                timestamp_ms=timestamp_ms,
                current_weapon=current_weapon,
                initiator="npc",
                event_type=event_type,
            )
            they_misses += 1
            fight.they_misses += 1
        elif event_type == "offer-withdraw" and npc_name:
            ensure_fight(
                fights,
                participants,
                participant_set,
                npc_name=npc_name,
                raw_event_id=raw_event_id,
                timestamp_ms=timestamp_ms,
                current_weapon=current_weapon,
                initiator="player",
                event_type=event_type,
            )
            withdraw_offers += 1
        elif event_type == "you-killed" and npc_name:
            fight = ensure_fight(
                fights,
                participants,
                participant_set,
                npc_name=npc_name,
                raw_event_id=raw_event_id,
                timestamp_ms=timestamp_ms,
                current_weapon=current_weapon,
                initiator="player",
                event_type=event_type,
            )
            kills_by_you += 1
            close_fight(fight, raw_event_id, timestamp_ms, "killed", row.get("raw") or kind)
            if all(open_fight.outcome is not None for open_fight in fights.values()):
                end_reason = "you-killed-them"
                end_detail = row.get("raw") or kind
        elif event_type == "they-killed" and npc_name:
            fight = ensure_fight(
                fights,
                participants,
                participant_set,
                npc_name=npc_name,
                raw_event_id=raw_event_id,
                timestamp_ms=timestamp_ms,
                current_weapon=current_weapon,
                initiator="npc",
                event_type=event_type,
            )
            kills_by_them += 1
            close_fight(fight, raw_event_id, timestamp_ms, "pass/unresolved", row.get("raw") or kind)
            for other in fights.values():
                if other.outcome is None:
                    close_fight(other, raw_event_id, timestamp_ms, "pass/unresolved", "player died before individual resolutions were observed")
            end_reason = "they-killed-you"
            end_detail = row.get("raw") or kind
        elif event_type == "fight-end-withdraw":
            if npc_name:
                fight = ensure_fight(
                    fights,
                    participants,
                    participant_set,
                    npc_name=npc_name,
                    raw_event_id=raw_event_id,
                    timestamp_ms=timestamp_ms,
                    current_weapon=current_weapon,
                    initiator="npc",
                    event_type=event_type,
                )
                close_fight(fight, raw_event_id, timestamp_ms, "withdrawn", row.get("raw") or kind)
            for other in fights.values():
                if other.outcome is None:
                    close_fight(other, raw_event_id, timestamp_ms, "withdrawn", row.get("raw") or kind)
            end_reason = "withdraw"
            end_detail = row.get("raw") or kind
        elif event_type == "fight-end-flee":
            if kind == "YouFled":
                for other in fights.values():
                    if other.outcome is None:
                        close_fight(other, raw_event_id, timestamp_ms, "you-fled", row.get("raw") or kind)
                end_reason = "flee"
                end_detail = row.get("raw") or kind
            elif npc_name:
                fight = ensure_fight(
                    fights,
                    participants,
                    participant_set,
                    npc_name=npc_name,
                    raw_event_id=raw_event_id,
                    timestamp_ms=timestamp_ms,
                    current_weapon=current_weapon,
                    initiator="npc",
                    event_type=event_type,
                )
                close_fight(fight, raw_event_id, timestamp_ms, "npc-fled", row.get("raw") or kind)
                if all(open_fight.outcome is not None for open_fight in fights.values()):
                    end_reason = "flee"
                    end_detail = row.get("raw") or kind
        elif event_type == "fight-end-other":
            if all(open_fight.outcome is not None for open_fight in fights.values()):
                end_reason = "other"
                end_detail = row.get("raw") or kind
        elif event_type == "weapon-change":
            current_weapon = weapon or current_weapon
            for fight in fights.values():
                if fight.outcome is None:
                    if fight.start_weapon is None:
                        fight.start_weapon = current_weapon
                    fight.weapon_used = current_weapon
        elif event_type == "weapon-broke" and weapon and current_weapon and weapon.lower() == current_weapon.lower():
            current_weapon = None

        combat_event_rows.append(
            {
                "raw_event_id": raw_event_id,
                "timestamp_ms": timestamp_ms,
                "seq_index": seq_index,
                "tag_code": tag_code,
                "event_type": event_type,
                "actor": actor_map.get(row.get("actor")),
                "participant_name": npc_name,
                "fight_npc_name": npc_name if npc_name in fights else None,
                "weapon_name": weapon or current_weapon,
                "approx_damage_done": midpoint(coerce_int(row.get("rangeLow")), coerce_int(row.get("rangeHigh"))) if event_type == "you-hit" else None,
                "approx_damage_taken": approx_damage_taken,
                "plain_text": row.get("raw") or kind,
            }
        )

    footer_event_id = None
    if footer.get("type") == "encounter_end":
        footer_event_id = insert_raw_event(
            con,
            capture_id=capture_id,
            seq_index=len(rows) - 1,
            timestamp_ms=coerce_int(footer.get("ts")),
            tag_code="clog.encounter-end",
            category="clog",
            event_type="clog-encounter-end",
            actor=None,
            subject_name=None,
            weapon_name=None,
            decoded_text="encounter_end",
            record_json=raw_lines[-1],
        )

    for fight in fights.values():
        if fight.outcome is None:
            close_fight(
                fight,
                footer_event_id or fight.end_event_id or fight.start_event_id,
                stopped_at_ms or fight.start_timestamp_ms + KILL_GRACE_MS,
                "pass/unresolved",
                "clog ended before this fight resolved explicitly",
            )

    session_start_event_id = raw_event_ids[0]
    session_end_event_id = footer_event_id or raw_event_ids[-1]
    session_end_timestamp_ms = stopped_at_ms or (coerce_int(last_event.get("ts")) or started_at_ms or 0)
    duration_ms = None if started_at_ms is None or session_end_timestamp_ms is None else session_end_timestamp_ms - started_at_ms

    con.execute(
        """
        INSERT INTO combat_sessions (
            capture_id, session_index, initiator, start_event_id, end_event_id, start_timestamp_ms,
            end_timestamp_ms, duration_ms, end_reason, end_detail, primary_target, participant_names_json,
            participant_confidence, start_weapon, last_explicit_weapon, start_room_snapshot_id,
            end_room_snapshot_id, start_stats_snapshot_id, end_stats_snapshot_id, you_hits, you_misses,
            they_hits, they_misses, withdraw_offers, kills_by_you, kills_by_them, join_events, notes
        ) VALUES (?, 1, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, NULL, ?, NULL, ?, ?, ?, ?, ?, ?, ?, ?, ?)
        """,
        (
            capture_id,
            "player" if first_event.get("actor") == "Player" else "npc",
            session_start_event_id,
            session_end_event_id,
            started_at_ms,
            session_end_timestamp_ms,
            duration_ms,
            end_reason,
            end_detail,
            participants[0] if participants else None,
            json.dumps(participants),
            "high" if participants else "low",
            first_event.get("weapon"),
            current_weapon or first_event.get("weapon"),
            room_snapshot_id,
            stats_snapshot_id,
            you_hits,
            you_misses,
            they_hits,
            they_misses,
            withdraw_offers,
            kills_by_you,
            kills_by_them,
            join_events,
            None,
        ),
    )
    session_id = int(con.execute("SELECT last_insert_rowid()").fetchone()[0])

    if stats_snapshot_id:
        con.execute(
            "INSERT OR IGNORE INTO combat_session_stats (session_id, snapshot_id, relation) VALUES (?, ?, 'start')",
            (session_id, stats_snapshot_id),
        )

    effects = header.get("effects") or {}
    if isinstance(effects, dict):
        for key, value in effects.items():
            if key.endswith("Msg") or key == "AnyActive" or not value:
                continue
            effect_name = slugify_effect_name(key)
            window_id = stable_id(capture_id, "clog-effect", effect_name)
            con.execute(
                """
                INSERT INTO status_effect_windows (
                    id, capture_id, effect_name, start_event_id, end_event_id,
                    start_timestamp_ms, end_timestamp_ms, confidence, note
                ) VALUES (?, ?, ?, ?, ?, ?, ?, 'medium', ?)
                """,
                (
                    window_id,
                    capture_id,
                    effect_name,
                    None,
                    None,
                    started_at_ms,
                    session_end_timestamp_ms,
                    "effect was active in encounter_start snapshot; exact start/end unknown",
                ),
            )
            con.execute(
                """
                INSERT OR IGNORE INTO combat_session_status_effects (
                    session_id, status_window_id, overlap_start_ms, overlap_end_ms
                ) VALUES (?, ?, ?, ?)
                """,
                (session_id, window_id, started_at_ms, session_end_timestamp_ms),
            )

    fight_ids: dict[str, int] = {}
    for fight in fights.values():
        duration = None if fight.end_timestamp_ms is None else fight.end_timestamp_ms - fight.start_timestamp_ms
        con.execute(
            """
            INSERT INTO combat_fights (
                capture_id, session_id, npc_name, npc_group, initiator, start_event_id, end_event_id,
                start_timestamp_ms, end_timestamp_ms, duration_ms, start_weapon, weapon_used, outcome,
                resolution_text, you_hits, you_misses, they_hits, they_misses, approx_damage_done,
                approx_damage_taken, notes
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                capture_id,
                session_id,
                fight.npc_name,
                fight.npc_group,
                fight.initiator,
                fight.start_event_id,
                fight.end_event_id,
                fight.start_timestamp_ms,
                fight.end_timestamp_ms,
                duration,
                fight.start_weapon,
                fight.weapon_used,
                fight.outcome,
                fight.resolution_text,
                fight.you_hits,
                fight.you_misses,
                fight.they_hits,
                fight.they_misses,
                round(fight.approx_damage_done, 3),
                round(fight.approx_damage_taken, 3),
                "; ".join(fight.notes) if fight.notes else None,
            ),
        )
        fight_ids[fight.npc_name] = int(con.execute("SELECT last_insert_rowid()").fetchone()[0])

    for row in combat_event_rows:
        con.execute(
            """
            INSERT INTO combat_events (
                capture_id, session_id, fight_id, raw_event_id, timestamp_ms, seq_index, tag_code,
                event_type, actor, participant_name, weapon_name, approx_damage_done, approx_damage_taken, plain_text
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                capture_id,
                session_id,
                fight_ids.get(row["fight_npc_name"]) if row["fight_npc_name"] else None,
                row["raw_event_id"],
                row["timestamp_ms"],
                row["seq_index"],
                row["tag_code"],
                row["event_type"],
                row["actor"],
                row["participant_name"],
                row["weapon_name"],
                row["approx_damage_done"],
                row["approx_damage_taken"],
                row["plain_text"],
            ),
        )

    return {
        "file": clog_path.name,
        "status": "ingested",
        "capture_id": capture_id,
        "fights": len(fights),
        "participants": len(participants),
        "events": len(combat_event_rows),
    }


LIVE_FIGHT_COLUMNS = (
    "source_file", "format_version", "character_name", "encounter_started_at_ms", "started_at_ms",
    "ended_at_ms", "duration_ms", "npc_name", "npc_group",
    "weapon_used", "outcome", "you_hits", "you_misses", "they_hits", "they_misses",
    "approx_damage_done", "approx_damage_taken", "narrative_mode", "room", "weather", "strength",
    "raw_strength", "dexterity", "raw_dexterity", "stamina_at_start", "max_stamina",
    "min_stamina", "stamina_at_end", "score_at_start", "score_at_end",
    "weight_carried_grams", "objects_carried", "level", "is_blind", "is_deaf", "is_crippled",
    "is_dumb", "effects_json",
)


def ingest_fights(con: sqlite3.Connection, fights_path: Path) -> dict[str, Any]:
    """Load the client's per-fight rollup index (fights.jsonl) into live_fights.

    The client writes these rows itself, already keyed with the same npc_group the offline pipeline
    computes (mudsharp/Combat/NpcGroups.cs is pinned to normalize_npc_group by
    npc_group_fixture.txt), so no re-derivation happens here -- re-deriving would be a second place
    for the two to drift.

    Idempotent: the UNIQUE (started_at_ms, npc_name) key means re-running over the append-only file
    replaces rather than duplicates, so there is no --force equivalent to worry about.
    """
    if not fights_path.exists():
        return {"file": fights_path.name, "status": "skipped", "reason": "no fights.jsonl", "rows": 0}

    source_file = str(fights_path.resolve())
    inserted = 0
    malformed = 0

    for line in fights_path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            row = json.loads(line)
        except json.JSONDecodeError:
            # A row truncated by a crash mid-write costs that row, not the whole file.
            malformed += 1
            continue

        values = (
            source_file,
            # Rows this old (pre-versioning entirely) would already have been discarded/renamed
            # aside client-side before reaching fights.jsonl (see FightHistoryStore.LoadAsync) - the
            # "or 1" fallback here only matters for a hand-edited/older test fixture line.
            coerce_int(row.get("format_version")) or 1,
            row.get("character_name"),
            coerce_int(row.get("encounter_started_at_ms")),
            coerce_int(row.get("started_at_ms")),
            coerce_int(row.get("ended_at_ms")),
            coerce_int(row.get("duration_ms")),
            row.get("npc_name") or "",
            row.get("npc_group") or "",
            row.get("weapon_used"),
            row.get("outcome") or "Unresolved",
            coerce_int(row.get("you_hits")) or 0,
            coerce_int(row.get("you_misses")) or 0,
            coerce_int(row.get("they_hits")) or 0,
            coerce_int(row.get("they_misses")) or 0,
            float(row.get("approx_damage_done") or 0.0),
            float(row.get("approx_damage_taken") or 0.0),
            1 if row.get("narrative_mode") else 0,
            row.get("room"),
            row.get("weather"),
            coerce_int(row.get("strength")),
            coerce_int(row.get("raw_strength")),
            coerce_int(row.get("dexterity")),
            coerce_int(row.get("raw_dexterity")),
            coerce_int(row.get("stamina_at_start")),
            coerce_int(row.get("max_stamina")),
            coerce_int(row.get("min_stamina")),
            coerce_int(row.get("stamina_at_end")),
            coerce_int(row.get("score_at_start")),
            coerce_int(row.get("score_at_end")),
            None,   # weight carried: deliberately not captured - see README
            coerce_int(row.get("objects_carried")),
            coerce_int(row.get("level")),
            1 if row.get("is_blind") else 0,
            1 if row.get("is_deaf") else 0,
            1 if row.get("is_crippled") else 0,
            1 if row.get("is_dumb") else 0,
            json.dumps(row.get("effects") or []),
        )

        placeholders = ", ".join("?" for _ in LIVE_FIGHT_COLUMNS)
        con.execute(
            f"INSERT OR REPLACE INTO live_fights ({', '.join(LIVE_FIGHT_COLUMNS)}) VALUES ({placeholders})",
            values,
        )
        inserted += 1

    return {
        "file": fights_path.name,
        "status": "ingested",
        "rows": inserted,
        "malformed": malformed,
    }


def main() -> int:
    args = parse_args()
    args.db.parent.mkdir(parents=True, exist_ok=True)
    con = sqlite3.connect(args.db)
    try:
        ensure_schema(con)
        # "clog.*.jsonl" rather than "*.jsonl": the same directory also holds items.jsonl (from
        # "$clog eval") and fights.jsonl (the per-fight rollup index, ingested separately below),
        # neither of which is an encounter clog.
        clog_paths = sorted(args.clog_dir.glob("clog.*.jsonl"))
        results: list[dict[str, Any]] = []
        for clog_path in clog_paths:
            try:
                result = ingest_one(con, clog_path, args.force)
                results.append(result)
                if result["status"] == "ingested":
                    con.commit()
                else:
                    con.rollback()
            except Exception as exc:  # pragma: no cover - operational path
                con.rollback()
                results.append({"file": clog_path.name, "status": "error", "reason": str(exc)})

        ingested = [r for r in results if r["status"] == "ingested"]
        skipped = [r for r in results if r["status"] == "skipped"]
        errors = [r for r in results if r["status"] == "error"]
        print(f"Scanned {len(clog_paths)} clog files from {args.clog_dir}")
        print(f"Ingested: {len(ingested)}  Skipped: {len(skipped)}  Errors: {len(errors)}")
        for row in results:
            if row["status"] == "ingested":
                print(f"  + {row['file']}: {row['events']} events, {row['fights']} fights")
            else:
                print(f"  - {row['file']}: {row['reason']}")

        try:
            fights_result = ingest_fights(con, args.fights_file or (args.clog_dir / "fights.jsonl"))
            con.commit()
        except Exception as exc:  # pragma: no cover - operational path
            con.rollback()
            fights_result = {"file": "fights.jsonl", "status": "error", "reason": str(exc)}
            errors.append(fights_result)

        if fights_result["status"] == "ingested":
            note = f"  + {fights_result['file']}: {fights_result['rows']} live fight rows"
            if fights_result.get("malformed"):
                note += f" ({fights_result['malformed']} malformed lines skipped)"
            print(note)
        else:
            print(f"  - {fights_result['file']}: {fights_result['reason']}")

        return 1 if errors else 0
    finally:
        con.close()


if __name__ == "__main__":
    raise SystemExit(main())
