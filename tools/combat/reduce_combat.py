#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# ///
"""Reduce MUD2 combat captures (.jsonl) into a SQLite combat-analysis database.

The capture format is the same JSONL session log used elsewhere in mucka:
  [timestamp_ms, "tx"|"rx"|"an", data]
or a one-element ["...elided..."] record which is skipped safely.

Combat detection is protocol-driven, not text-driven, but this reducer also scans
plain decoded text for important in-combat weapon/guard transitions that are not
wrapped in C08 tags in the supplied research capture.

Usage:
  uv run tools/combat/reduce_combat.py <capture.jsonl> [more.jsonl ...]
  uv run tools/combat/reduce_combat.py --db path\to\combat.db <capture.jsonl>

Default database: ~/.mucka/combat/combat.db
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sqlite3
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "mapping"))
from decode_probe import decode_rx  # noqa: E402

SCHEMA_FILE = Path(__file__).with_name("schema.sql")
DEFAULT_DB = Path.home() / ".mucka" / "combat" / "combat.db"
KILL_GRACE_MS = 5000
COMMAND_ASSOC_PRE_MS = 2500
COMMAND_ASSOC_POST_MS = 1000

TAG_RE = re.compile(r"\{/?c[0-9.?]*\}")
RELEVANT_TAG_RE = re.compile(
    r"\{c("
    r"05\.00\.10|05\.01\.10|"
    r"06\.(?:03|04|05|12\.00|12\.01)|"
    r"07(?:\.\d{2}){0,2}|"
    r"08(?:\.\d{2})?|"
    r"11(?:\.\d{2})?|"
    r"12\.08\.(?:01|02|03)"
    r")\}(.*?)\{/c\1\}",
    re.DOTALL,
)
ROOM_SHORT_RE = re.compile(r"\{c02\.01\}(.*?)\{/c02\.01\}", re.DOTALL)
ROOM_LONG_RE = re.compile(r"\{c02\.02\}(.*?)\{/c02\.02\}", re.DOTALL)
AMBIENT_RE = re.compile(r"\{c20\.(\d{2})\}(.*?)\{/c20\.\1\}", re.DOTALL)
FEX_RE = re.compile(r"\{c12\.08\.02\}(.*?)\{/c12\.08\.02\}", re.DOTALL)
PLAIN_LOGIN_RE = re.compile(r"Please enter your account id|Account ID:", re.IGNORECASE)
CAPTURE_STARTED_RE = re.compile(r"capture started:", re.IGNORECASE)
PLAYER_ATTACK_RE = re.compile(r"You attack the (?P<npc>.*?), using the (?P<weapon>.*?) as a weapon\.")
NPC_START_RE = re.compile(r"The (?P<npc>.*?) is ")
YOU_HIT_RE = re.compile(r"You hit the (?P<npc>.*?) \((?P<low>\d+)-(?P<high>\d+)\)\.")
YOU_MISS_RE = re.compile(r"You miss the (?P<npc>.*?)\.")
THEY_HIT_RE = re.compile(r"The (?P<npc>.*?) hits you \((?P<cur>\d+)/(?P<max>\d+)\)\.")
THEY_MISS_RE = re.compile(r"The (?P<npc>.*?) misses you\.")
OFFER_WITHDRAW_RE = re.compile(r"You offer to withdraw from your fight with the (?P<npc>.*?)\.")
YOU_KILLED_RE = re.compile(r"You have killed the (?P<npc>.*?)\.")
THEY_KILLED_RE = re.compile(r"The (?P<npc>.*?) has killed you\.")
NPC_FLED_RE = re.compile(r"The (?P<npc>.*?) has fled by going (?P<dir>.*?)\.")
YOU_FLED_RE = re.compile(r"You have fled by going (?P<dir>.*?)\.")
WITHDRAW_END_RE = re.compile(r"The (?P<npc>.*?) withdraws from your fight, and so do you\.")
WEAPON_SWITCH_RE = re.compile(r"You drop your guard as you switch from using the (?P<old>.*?) to the (?P<new>.*?)\.")
WEAPON_EQUIP_RE = re.compile(r"You are now using the (?P<weapon>.*?) to fight!")
WEAPON_BROKE_RE = re.compile(r"The (?P<weapon>.*?) breaks to bits\.")
WEAPON_CANNOT_USE_RE = re.compile(r"You cannot use the (?P<weapon>.*?) to fight now!")
GUARD_DROP_RE = re.compile(r"Your guard drops momentarily in your confusion\.")
USING_ANYWAY_RE = re.compile(r"You're using the (?P<weapon>.*?) anyway\.\.\.")
WEAPON_IN_USE_RE = re.compile(r"weapon in use:\s+(?P<weapon>\S+)", re.IGNORECASE)
ITEM_DROPPED_RE = re.compile(r"(?P<item>[A-Za-z0-9-]+) dropped\.")

AMBIENT_NAMES = {
    "00": "silence",
    "01": "tea-room",
    "02": "sea",
    "03": "rivers",
    "04": "forests",
    "05": "meadows",
    "06": "evil wood",
    "07": "monastery",
    "08": "scriptorium",
    "09": "rain",
    "10": "graveyard",
    "11": "beaches",
    "12": "outside",
    "13": "storm",
    "14": "wind",
}
IRREGULAR_GROUPS = {
    "dwarf": "dwarves",
    "mouse": "mice",
    "thief": "thieves",
    "wolf": "wolves",
}


def strip_tags(text: str) -> str:
    return TAG_RE.sub("", text).replace("\r", "")


def collapse_ws(text: str) -> str:
    return " ".join(strip_tags(text).split())


def maybe_int(value: str | None) -> int | None:
    if value is None:
        return None
    value = value.replace(",", "").strip()
    return int(value) if value and re.fullmatch(r"-?\d+", value) else None


def stable_id(*parts: Any) -> str:
    payload = "|".join("" if p is None else str(p) for p in parts)
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()[:24]


def make_event_id(capture_id: str, seq_index: int, ordinal: int) -> str:
    return f"{capture_id}:{seq_index}:{ordinal}"


def midpoint(low: int | None, high: int | None) -> float | None:
    if low is None or high is None:
        return None
    return (low + high) / 2.0


def normalize_npc_group(name: str) -> str:
    base = re.sub(r"\d+$", "", name.strip().lower())
    tokens = [token for token in re.split(r"[\s-]+", base) if token]
    leaf = tokens[-1] if tokens else base
    if leaf in IRREGULAR_GROUPS:
        return IRREGULAR_GROUPS[leaf]
    if re.search(r"(?:s|x|ch|sh)$", leaf):
        return leaf + "es"
    if len(leaf) > 1 and leaf.endswith("y") and leaf[-2] not in "aeiou":
        return leaf[:-1] + "ies"
    return leaf + "s"


def parse_fes_snapshot(plain_text: str) -> dict[str, Any] | None:
    fields = plain_text.split()
    if len(fields) < 15:
        return None
    return {
        "stamina": maybe_int(fields[0]),
        "max_stamina": maybe_int(fields[1]),
        "strength": maybe_int(fields[2]),
        "max_strength": maybe_int(fields[3]),
        "dexterity": maybe_int(fields[4]),
        "max_dexterity": maybe_int(fields[5]),
        "current_magic": maybe_int(fields[6]),
        "max_magic": maybe_int(fields[7]),
        "score": maybe_int(fields[8]),
        "is_blind": 1 if fields[9] == "Y" else 0,
        "is_deaf": 1 if fields[10] == "Y" else 0,
        "is_crippled": 1 if fields[11] == "Y" else 0,
        "is_dumb": 1 if fields[12] == "Y" else 0,
        "reset_minutes": maybe_int(fields[13]),
        "weather": fields[14][:1] if fields[14] else None,
        "raw_text": plain_text,
    }


def parse_fei_snapshot(plain_text: str) -> dict[str, Any]:
    room_items: list[str] = []
    carried_items: list[str] = []
    current = room_items
    for line in plain_text.splitlines():
        item = line.strip()
        if not item:
            continue
        if item == "========":
            current = carried_items
            continue
        current.append(item)
    return {
        "room_items_json": json.dumps(room_items),
        "carried_items_json": json.dumps(carried_items),
        "raw_text": plain_text,
    }


def parse_room_snapshot(decoded_text: str) -> dict[str, Any] | None:
    short_match = ROOM_SHORT_RE.search(decoded_text)
    long_match = ROOM_LONG_RE.search(decoded_text)
    ambient_match = AMBIENT_RE.search(decoded_text)
    exits_match = FEX_RE.search(decoded_text)
    if not short_match and not long_match and not ambient_match and not exits_match:
        return None
    ambient_code = ambient_match.group(1) if ambient_match else None
    raw_text = collapse_ws(decoded_text)
    if len(raw_text) > 400:
        raw_text = raw_text[:397] + "..."
    return {
        "ambient_code": ambient_code,
        "ambient_name": AMBIENT_NAMES.get(ambient_code) if ambient_code else None,
        "room_short": collapse_ws(short_match.group(1)) if short_match else None,
        "room_long": collapse_ws(long_match.group(1)) if long_match else None,
        "exits_text": collapse_ws(exits_match.group(1)) if exits_match else None,
        "raw_text": raw_text,
        "note": None,
    }


def infer_effect_name(tag_code: str, plain_text: str) -> tuple[str, str]:
    lowered = plain_text.lower()
    if "glowing" in lowered:
        return "glowing", "high"
    if "blind" in lowered:
        return "blind", "high"
    if "hearing" in lowered or "deaf" in lowered:
        return "deaf", "medium"
    if "speech" in lowered or "dumb" in lowered:
        return "dumb", "medium"
    if "cripple" in lowered or "limp" in lowered:
        return "crippled", "medium"
    if tag_code == "11.02" and "strength" in lowered:
        return "strength-buff", "medium"
    if tag_code == "11.03" and "strength" in lowered:
        return "strength-buff", "medium"
    return f"tag-{tag_code}", "low"


@dataclass
class ParsedEvent:
    position: int
    order: int
    tag_code: str | None
    category: str
    event_type: str
    actor: str | None
    npc_name: str | None
    weapon_name: str | None
    plain_text: str
    initiator: str | None = None
    approx_damage_done: float | None = None
    stamina_after: int | None = None
    max_stamina: int | None = None


@dataclass
class RawEvent:
    id: str
    capture_id: str
    seq_index: int
    event_ordinal: int
    timestamp_ms: int | None
    direction: str
    tag_code: str | None
    category: str
    event_type: str
    actor: str | None
    subject_name: str | None
    weapon_name: str | None
    decoded_text: str
    snippet_text: str
    record_json: str
    is_client_probe: int = 0


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


@dataclass
class SessionState:
    db_id: int
    capture_id: str
    session_index: int
    initiator: str | None
    start_event_id: str
    start_timestamp_ms: int
    start_room_snapshot_id: str | None
    start_stats_snapshot_id: str | None
    start_inventory_snapshot_id: str | None
    start_weapon: str | None
    primary_target: str | None = None
    participants: list[str] = field(default_factory=list)
    participant_set: set[str] = field(default_factory=set)
    participant_confidence: str = "low"
    last_explicit_weapon: str | None = None
    end_event_id: str | None = None
    end_timestamp_ms: int | None = None
    end_reason: str | None = None
    end_detail: str | None = None
    end_room_snapshot_id: str | None = None
    end_stats_snapshot_id: str | None = None
    end_inventory_snapshot_id: str | None = None
    you_hits: int = 0
    you_misses: int = 0
    they_hits: int = 0
    they_misses: int = 0
    withdraw_offers: int = 0
    kills_by_you: int = 0
    kills_by_them: int = 0
    join_events: int = 0
    notes: list[str] = field(default_factory=list)
    pending_end_reason: str | None = None
    pending_end_event_id: str | None = None
    pending_end_ts: int | None = None
    pending_end_detail: str | None = None
    pending_end_mode: str | None = None
    open_fights: dict[str, FightState] = field(default_factory=dict)
    fights: dict[str, FightState] = field(default_factory=dict)
    combat_event_rows: list[dict[str, Any]] = field(default_factory=list)


class Reducer:
    def __init__(self, con: sqlite3.Connection):
        self.con = con
        self.current_room_snapshot_id: str | None = None
        self.current_stats_snapshot_id: str | None = None
        self.current_inventory_snapshot_id: str | None = None
        self.current_weapon: str | None = None
        self.last_known_stamina: int | None = None
        self.last_known_max_stamina: int | None = None
        self.last_use_command: tuple[int, str] | None = None
        self.open_effects: dict[str, str] = {}
        self.open_session: SessionState | None = None
        self.completed_sessions: list[SessionState] = []
        self.tx_events: list[RawEvent] = []
        self.session_index = 0

    def reload_capture(self, capture_path: Path) -> None:
        capture_id = stable_id(capture_path.resolve())
        now_ms = int(time.time() * 1000)
        self.con.execute("PRAGMA foreign_keys = ON")
        self.con.execute("DELETE FROM captures WHERE id = ?", (capture_id,))
        self.con.execute(
            "INSERT INTO captures (id, source_file, loaded_at_ms) VALUES (?, ?, ?)",
            (capture_id, str(capture_path.resolve()), now_ms),
        )
        self._reset_state()

        start_ts: int | None = None
        stop_ts: int | None = None
        lines = capture_path.read_text(encoding="utf-8-sig").splitlines()
        for seq_index, line in enumerate(lines):
            if not line.strip():
                continue
            record = json.loads(line)
            if isinstance(record, list) and len(record) == 1:
                continue
            if not isinstance(record, list) or len(record) != 3:
                continue
            timestamp_ms, direction, data = record
            if start_ts is None and isinstance(timestamp_ms, int):
                start_ts = timestamp_ms
            if isinstance(timestamp_ms, int):
                stop_ts = timestamp_ms
            self._expire_pending_session(timestamp_ms)
            record_json = json.dumps(record, ensure_ascii=True)

            if direction == "tx":
                self._handle_tx(capture_id, seq_index, timestamp_ms, data, record_json)
                continue
            if direction == "an":
                self._handle_annotation(capture_id, seq_index, timestamp_ms, data, record_json)
                continue
            if direction != "rx":
                continue

            decoded = decode_rx(data, plain=False)
            plain_record = collapse_ws(decoded)
            if PLAIN_LOGIN_RE.search(plain_record):
                self._force_close_session(
                    timestamp_ms,
                    None,
                    "ambiguous-login",
                    "login prompt appeared mid-session",
                    fight_outcome="pass/unresolved",
                )

            weapon_hint = WEAPON_IN_USE_RE.search(decoded)
            if weapon_hint:
                self.current_weapon = weapon_hint.group("weapon")

            for dropped in ITEM_DROPPED_RE.finditer(decoded):
                item = dropped.group("item")
                if self.current_weapon and item.lower() == self.current_weapon.lower():
                    self.current_weapon = None

            room_snapshot_id = self._maybe_insert_room_snapshot(
                capture_id, seq_index, timestamp_ms, decoded, record_json
            )
            events = self._extract_ordered_events(decoded)
            self._process_events(
                capture_id,
                seq_index,
                timestamp_ms,
                record_json,
                room_snapshot_id,
                events,
            )
            if self.open_session and self.open_session.pending_end_mode == "explicit" and not self.open_session.open_fights:
                self._finalize_session(self.open_session)

        self._expire_pending_session((stop_ts or 0) + KILL_GRACE_MS + 1)
        self._close_at_capture_end(stop_ts)
        self._finalize_effect_windows(capture_id, stop_ts)
        self._associate_commands()
        self._associate_snapshots(capture_id)
        self.con.execute(
            "UPDATE captures SET started_at_ms = ?, stopped_at_ms = ? WHERE id = ?",
            (start_ts, stop_ts, capture_id),
        )
        self.con.commit()

    def _reset_state(self) -> None:
        self.current_room_snapshot_id = None
        self.current_stats_snapshot_id = None
        self.current_inventory_snapshot_id = None
        self.current_weapon = None
        self.last_known_stamina = None
        self.last_known_max_stamina = None
        self.last_use_command = None
        self.open_effects = {}
        self.open_session = None
        self.completed_sessions = []
        self.tx_events = []
        self.session_index = 0

    def _handle_tx(self, capture_id: str, seq_index: int, timestamp_ms: int, data: str, record_json: str) -> None:
        text = data.replace("\r", "").replace("\n", "")
        is_probe = 1 if text.startswith("\x1b-[") else 0
        event = RawEvent(
            id=make_event_id(capture_id, seq_index, 0),
            capture_id=capture_id,
            seq_index=seq_index,
            event_ordinal=0,
            timestamp_ms=timestamp_ms,
            direction="tx",
            tag_code=None,
            category="client-probe" if is_probe else "command",
            event_type="tx-command",
            actor=None,
            subject_name=None,
            weapon_name=None,
            decoded_text=text,
            snippet_text=text[:240],
            record_json=record_json,
            is_client_probe=is_probe,
        )
        self._insert_raw_event(event)
        self.tx_events.append(event)
        if not is_probe:
            weapon_cmd = re.search(r"^(?:use|wield)\s+(.+)$", text.strip(), re.IGNORECASE)
            if weapon_cmd:
                self.last_use_command = (timestamp_ms, weapon_cmd.group(1).strip())

    def _handle_annotation(self, capture_id: str, seq_index: int, timestamp_ms: int, data: str, record_json: str) -> None:
        event = RawEvent(
            id=make_event_id(capture_id, seq_index, 0),
            capture_id=capture_id,
            seq_index=seq_index,
            event_ordinal=0,
            timestamp_ms=timestamp_ms,
            direction="an",
            tag_code=None,
            category="annotation",
            event_type="annotation",
            actor=None,
            subject_name=None,
            weapon_name=None,
            decoded_text=data,
            snippet_text=data[:240],
            record_json=record_json,
            is_client_probe=0,
        )
        self._insert_raw_event(event)
        if CAPTURE_STARTED_RE.search(data):
            self._force_close_session(timestamp_ms, event.id, "ambiguous-capture-restart", data, "pass/unresolved")

    def _extract_ordered_events(self, decoded: str) -> list[ParsedEvent]:
        events: list[ParsedEvent] = []
        for match in RELEVANT_TAG_RE.finditer(decoded):
            tag_code = match.group(1)
            inner = strip_tags(match.group(2)).replace("\n\n", "\n").strip("\n")
            event = self._parse_tag_event(tag_code, inner, match.start())
            if event is not None:
                events.append(event)
        events.extend(self._extract_plain_events(decoded))
        events.sort(key=lambda event: (event.position, event.order, event.event_type, event.plain_text))
        return events

    def _parse_tag_event(self, tag_code: str, plain_text: str, position: int) -> ParsedEvent | None:
        category = "other"
        event_type = "other"
        actor: str | None = None
        npc_name: str | None = None
        weapon_name: str | None = None
        initiator: str | None = None
        approx_damage_done: float | None = None
        stamina_after: int | None = None
        max_stamina: int | None = None
        text = collapse_ws(plain_text)

        if tag_code.startswith("08"):
            category = "combat"
            mapping = {
                "08.00": "fight-start",
                "08.01": "you-hit",
                "08.02": "you-miss",
                "08.03": "they-hit",
                "08.04": "they-miss",
                "08.07": "offer-withdraw",
                "08.08": "you-killed",
                "08.09": "they-killed",
                "08.10": "fight-end-withdraw",
                "08.11": "fight-end-flee",
                "08.12": "fight-end-other",
                "08.13": "persona-not-updated",
            }
            event_type = mapping.get(tag_code, "combat")
        elif tag_code.startswith("07"):
            category = "isolated-hit"
            event_type = "isolated-hit"
        elif tag_code.startswith("11"):
            category = "status-effect"
            event_type = "status-effect"
        elif tag_code == "12.08.01":
            category = "stats"
            event_type = "fes"
        elif tag_code == "12.08.02":
            category = "room"
            event_type = "fex"
        elif tag_code == "12.08.03":
            category = "inventory"
            event_type = "fei"
        elif tag_code in {"06.03", "06.04"}:
            category = "reset"
            event_type = "reset"
        elif tag_code.startswith("06.12"):
            category = "rules"
            event_type = "fighting-toggle"
        elif tag_code in {"05.00.10", "05.01.10"}:
            category = "flee"
            event_type = "seen-fleeing"

        if tag_code == "08.00":
            player = PLAYER_ATTACK_RE.search(text)
            if player:
                npc_name = player.group("npc")
                weapon_name = player.group("weapon")
                actor = "you"
                initiator = "player"
            else:
                npc = NPC_START_RE.search(text)
                if npc:
                    npc_name = npc.group("npc")
                    actor = "them"
                    initiator = "npc"
        elif tag_code == "08.01":
            actor = "you"
            hit = YOU_HIT_RE.search(text)
            if hit:
                npc_name = hit.group("npc")
                approx_damage_done = midpoint(int(hit.group("low")), int(hit.group("high")))
        elif tag_code == "08.02":
            actor = "you"
            miss = YOU_MISS_RE.search(text)
            if miss:
                npc_name = miss.group("npc")
        elif tag_code == "08.03":
            actor = "them"
            hit = THEY_HIT_RE.search(text)
            if hit:
                npc_name = hit.group("npc")
                stamina_after = maybe_int(hit.group("cur"))
                max_stamina = maybe_int(hit.group("max"))
        elif tag_code == "08.04":
            actor = "them"
            miss = THEY_MISS_RE.search(text)
            if miss:
                npc_name = miss.group("npc")
        elif tag_code == "08.07":
            actor = "you"
            offer = OFFER_WITHDRAW_RE.search(text)
            if offer:
                npc_name = offer.group("npc")
        elif tag_code == "08.08":
            actor = "you"
            kill = YOU_KILLED_RE.search(text)
            if kill:
                npc_name = kill.group("npc")
        elif tag_code == "08.09":
            actor = "them"
            death = THEY_KILLED_RE.search(text)
            if death:
                npc_name = death.group("npc")
        elif tag_code == "08.10":
            actor = "them"
            ended = WITHDRAW_END_RE.search(text)
            if ended:
                npc_name = ended.group("npc")
        elif tag_code == "08.11":
            if YOU_FLED_RE.search(text):
                actor = "you"
            else:
                actor = "them"
                fled = NPC_FLED_RE.search(text)
                if fled:
                    npc_name = fled.group("npc")
        elif tag_code == "08.12":
            actor = "them"
        elif tag_code == "08.13":
            actor = "you"

        return ParsedEvent(
            position=position,
            order=0,
            tag_code=tag_code,
            category=category,
            event_type=event_type,
            actor=actor,
            npc_name=npc_name,
            weapon_name=weapon_name,
            plain_text=text,
            initiator=initiator,
            approx_damage_done=approx_damage_done,
            stamina_after=stamina_after,
            max_stamina=max_stamina,
        )

    def _extract_plain_events(self, decoded: str) -> list[ParsedEvent]:
        events: list[ParsedEvent] = []
        for match in WEAPON_SWITCH_RE.finditer(decoded):
            text = collapse_ws(match.group(0))
            new_weapon = match.group("new")
            events.append(ParsedEvent(match.start(), 0, "plain.weapon-switch", "combat", "weapon-change", "you", None, new_weapon, text))
            events.append(ParsedEvent(match.start(), 1, "plain.guard-drop", "combat", "dropped-guard", "you", None, new_weapon, text))
        for match in WEAPON_EQUIP_RE.finditer(decoded):
            text = collapse_ws(match.group(0))
            weapon = match.group("weapon")
            events.append(ParsedEvent(match.start(), 0, "plain.weapon-equip", "combat", "weapon-change", "you", None, weapon, text))
        for match in WEAPON_BROKE_RE.finditer(decoded):
            text = collapse_ws(match.group(0))
            weapon = match.group("weapon")
            events.append(ParsedEvent(match.start(), 0, "plain.weapon-broke", "combat", "weapon-broke", "you", None, weapon, text))
        for match in GUARD_DROP_RE.finditer(decoded):
            text = collapse_ws(match.group(0))
            events.append(ParsedEvent(match.start(), 0, "plain.guard-drop", "combat", "dropped-guard", "you", None, None, text))
        return events

    def _maybe_insert_room_snapshot(self, capture_id: str, seq_index: int, timestamp_ms: int, decoded: str, record_json: str) -> str | None:
        snapshot = parse_room_snapshot(decoded)
        if snapshot is None:
            return None
        source_event_id = make_event_id(capture_id, seq_index, -1)
        raw = RawEvent(
            id=source_event_id,
            capture_id=capture_id,
            seq_index=seq_index,
            event_ordinal=-1,
            timestamp_ms=timestamp_ms,
            direction="rx",
            tag_code="room.snapshot",
            category="room",
            event_type="room-snapshot",
            actor=None,
            subject_name=snapshot.get("room_short"),
            weapon_name=None,
            decoded_text=snapshot["raw_text"],
            snippet_text=snapshot["raw_text"][:240],
            record_json=record_json,
            is_client_probe=0,
        )
        self._insert_raw_event(raw)
        room_id = stable_id(source_event_id, "room")
        self.con.execute(
            """
            INSERT INTO room_snapshots (
                id, capture_id, source_event_id, timestamp_ms, seq_index,
                ambient_code, ambient_name, room_short, room_long, exits_text, raw_text, note
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                room_id,
                capture_id,
                source_event_id,
                timestamp_ms,
                seq_index,
                snapshot["ambient_code"],
                snapshot["ambient_name"],
                snapshot["room_short"],
                snapshot["room_long"],
                snapshot["exits_text"],
                snapshot["raw_text"],
                snapshot["note"],
            ),
        )
        self.current_room_snapshot_id = room_id
        return room_id

    def _process_events(
        self,
        capture_id: str,
        seq_index: int,
        timestamp_ms: int,
        record_json: str,
        room_snapshot_id: str | None,
        events: list[ParsedEvent],
    ) -> None:
        for ordinal, parsed in enumerate(events):
            self._maybe_finalize_pending_session_before_event(timestamp_ms, parsed)
            raw_event = RawEvent(
                id=make_event_id(capture_id, seq_index, ordinal),
                capture_id=capture_id,
                seq_index=seq_index,
                event_ordinal=ordinal,
                timestamp_ms=timestamp_ms,
                direction="rx",
                tag_code=parsed.tag_code,
                category=parsed.category,
                event_type=parsed.event_type,
                actor=parsed.actor,
                subject_name=parsed.npc_name,
                weapon_name=parsed.weapon_name,
                decoded_text=parsed.plain_text,
                snippet_text=parsed.plain_text[:240],
                record_json=record_json,
                is_client_probe=0,
            )
            self._insert_raw_event(raw_event)
            self._dispatch_event(raw_event, parsed, room_snapshot_id)

    def _dispatch_event(self, raw_event: RawEvent, parsed: ParsedEvent, room_snapshot_id: str | None) -> None:
        if parsed.event_type == "fes":
            self._handle_stats_event(raw_event)
            return
        if parsed.event_type == "fei":
            self._handle_inventory_event(raw_event)
            return
        if parsed.event_type == "status-effect":
            self._handle_status_effect_event(raw_event)
            return
        if parsed.event_type == "reset":
            self._force_close_session(raw_event.timestamp_ms, raw_event.id, "reset", raw_event.decoded_text, "pass/unresolved")
            return
        if parsed.tag_code and parsed.tag_code.startswith("08"):
            self._handle_combat_event(raw_event, parsed, room_snapshot_id)
            return
        if parsed.event_type in {"weapon-change", "weapon-broke", "dropped-guard"}:
            self._handle_plain_combat_event(raw_event, parsed)

    def _handle_stats_event(self, raw_event: RawEvent) -> None:
        parsed = parse_fes_snapshot(collapse_ws(raw_event.decoded_text))
        if parsed is None:
            return
        snapshot_id = stable_id(raw_event.id, "stats")
        self.con.execute(
            """
            INSERT INTO stats_snapshots (
                id, capture_id, source_event_id, timestamp_ms, seq_index,
                stamina, max_stamina, strength, max_strength, dexterity, max_dexterity,
                current_magic, max_magic, score, is_blind, is_deaf, is_crippled, is_dumb,
                reset_minutes, weather, raw_text
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                snapshot_id,
                raw_event.capture_id,
                raw_event.id,
                raw_event.timestamp_ms,
                raw_event.seq_index,
                parsed["stamina"],
                parsed["max_stamina"],
                parsed["strength"],
                parsed["max_strength"],
                parsed["dexterity"],
                parsed["max_dexterity"],
                parsed["current_magic"],
                parsed["max_magic"],
                parsed["score"],
                parsed["is_blind"],
                parsed["is_deaf"],
                parsed["is_crippled"],
                parsed["is_dumb"],
                parsed["reset_minutes"],
                parsed["weather"],
                parsed["raw_text"],
            ),
        )
        self.current_stats_snapshot_id = snapshot_id
        if parsed["stamina"] is not None:
            self.last_known_stamina = parsed["stamina"]
            self.last_known_max_stamina = parsed["max_stamina"]

    def _handle_inventory_event(self, raw_event: RawEvent) -> None:
        parsed = parse_fei_snapshot(raw_event.decoded_text)
        snapshot_id = stable_id(raw_event.id, "inventory")
        self.con.execute(
            """
            INSERT INTO inventory_snapshots (
                id, capture_id, source_event_id, timestamp_ms, seq_index,
                room_items_json, carried_items_json, raw_text
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                snapshot_id,
                raw_event.capture_id,
                raw_event.id,
                raw_event.timestamp_ms,
                raw_event.seq_index,
                parsed["room_items_json"],
                parsed["carried_items_json"],
                parsed["raw_text"],
            ),
        )
        self.current_inventory_snapshot_id = snapshot_id

    def _handle_status_effect_event(self, raw_event: RawEvent) -> None:
        phase = "end" if raw_event.tag_code in {"11.01", "11.03", "11.21"} else "start"
        effect_name, confidence = infer_effect_name(raw_event.tag_code or "", collapse_ws(raw_event.decoded_text))
        effect_event_id = stable_id(raw_event.id, effect_name, phase)
        event_ts = raw_event.timestamp_ms
        self.con.execute(
            """
            INSERT INTO status_effect_events (
                id, capture_id, raw_event_id, timestamp_ms, effect_name, phase, confidence, detail_text
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                effect_event_id,
                raw_event.capture_id,
                raw_event.id,
                raw_event.timestamp_ms,
                effect_name,
                phase,
                confidence,
                collapse_ws(raw_event.decoded_text),
            ),
        )
        if phase == "start":
            self.open_effects[effect_name] = effect_event_id
        else:
            start_event_id = self.open_effects.pop(effect_name, None)
            start_ts = self._status_event_timestamp(start_event_id) if start_event_id else None
            self.con.execute(
                """
                INSERT INTO status_effect_windows (
                    id, capture_id, effect_name, start_event_id, end_event_id,
                    start_timestamp_ms, end_timestamp_ms, confidence, note
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
                """,
                (
                    stable_id(effect_name, start_event_id, effect_event_id, "closed"),
                    raw_event.capture_id,
                    effect_name,
                    start_event_id,
                    effect_event_id,
                    start_ts,
                    event_ts,
                    confidence,
                    None if start_event_id else "observed end without matching start in this capture",
                ),
            )

    def _handle_plain_combat_event(self, raw_event: RawEvent, parsed: ParsedEvent) -> None:
        same_weapon_hint = USING_ANYWAY_RE.search(raw_event.record_json)
        if parsed.event_type == "weapon-change" and parsed.weapon_name:
            self.current_weapon = parsed.weapon_name
            if self.open_session:
                self.open_session.last_explicit_weapon = parsed.weapon_name
                for fight in self.open_session.open_fights.values():
                    if fight.start_weapon is None:
                        fight.start_weapon = parsed.weapon_name
                    fight.weapon_used = parsed.weapon_name
        elif parsed.event_type == "weapon-broke" and parsed.weapon_name:
            if self.current_weapon and self.current_weapon.lower() == parsed.weapon_name.lower():
                self.current_weapon = None
        elif same_weapon_hint:
            self.current_weapon = same_weapon_hint.group("weapon")
        if self.open_session is None:
            return
        session = self.open_session
        session.end_room_snapshot_id = self.current_room_snapshot_id
        session.end_stats_snapshot_id = self.current_stats_snapshot_id
        session.end_inventory_snapshot_id = self.current_inventory_snapshot_id
        session.combat_event_rows.append(
            {
                "raw_event_id": raw_event.id,
                "timestamp_ms": raw_event.timestamp_ms,
                "seq_index": raw_event.seq_index,
                "tag_code": raw_event.tag_code,
                "event_type": parsed.event_type,
                "actor": raw_event.actor,
                "participant_name": None,
                "fight_npc_name": None,
                "weapon_name": parsed.weapon_name or self.current_weapon,
                "approx_damage_done": None,
                "approx_damage_taken": None,
                "plain_text": raw_event.decoded_text,
            }
        )

    def _handle_combat_event(self, raw_event: RawEvent, parsed: ParsedEvent, room_snapshot_id: str | None) -> None:
        if parsed.event_type == "fight-start":
            self._handle_fight_start(raw_event, parsed, room_snapshot_id)
            return

        session = self.open_session
        if session is None:
            initiator = "npc" if parsed.actor == "them" else "player"
            self._open_session(raw_event, initiator, room_snapshot_id)
            session = self.open_session
        assert session is not None
        needs_fight = parsed.event_type in {
            "you-hit",
            "you-miss",
            "they-hit",
            "they-miss",
            "offer-withdraw",
            "you-killed",
            "they-killed",
        } or (parsed.event_type == "fight-end-flee" and parsed.npc_name is not None) or (
            parsed.event_type == "fight-end-withdraw" and parsed.npc_name is not None
        )
        fight = self._ensure_fight_for_event(session, raw_event, parsed) if needs_fight else None

        approx_damage_taken: float | None = None
        if parsed.event_type == "you-hit":
            assert fight is not None
            session.you_hits += 1
            fight.you_hits += 1
            if parsed.approx_damage_done is not None:
                fight.approx_damage_done += parsed.approx_damage_done
        elif parsed.event_type == "you-miss":
            assert fight is not None
            session.you_misses += 1
            fight.you_misses += 1
        elif parsed.event_type == "they-hit":
            assert fight is not None
            session.they_hits += 1
            fight.they_hits += 1
            approx_damage_taken = self._compute_damage_taken(parsed.stamina_after, parsed.max_stamina)
            if approx_damage_taken is not None:
                fight.approx_damage_taken += approx_damage_taken
        elif parsed.event_type == "they-miss":
            assert fight is not None
            session.they_misses += 1
            fight.they_misses += 1
        elif parsed.event_type == "offer-withdraw":
            assert fight is not None
            session.withdraw_offers += 1
        elif parsed.event_type == "you-killed":
            assert fight is not None
            session.kills_by_you += 1
            self._close_fight(session, fight.npc_name, raw_event, "killed", raw_event.decoded_text)
            if not session.open_fights:
                self._set_pending_end(session, "you-killed-them", raw_event)
        elif parsed.event_type == "they-killed":
            assert fight is not None
            session.kills_by_them += 1
            self._resolve_all_open_fights(session, raw_event, "pass/unresolved", "player died before individual resolutions were observed")
            self._set_pending_end(session, "they-killed-you", raw_event)
        elif parsed.event_type == "fight-end-flee":
            self._handle_flee_event(session, raw_event, parsed)
        elif parsed.event_type == "fight-end-withdraw":
            named = parsed.npc_name
            if named:
                self._close_fight(session, named, raw_event, "withdrawn", raw_event.decoded_text)
            for npc_name in list(session.open_fights):
                self._close_fight(session, npc_name, raw_event, "withdrawn", f"Encounter ended on withdraw event naming {named or 'another foe'}.")
            self._set_pending_end(session, "withdraw", raw_event)
        elif parsed.event_type == "fight-end-other":
            if not session.open_fights:
                self._set_pending_end(session, session.end_reason or "other", raw_event)
            else:
                session.end_reason = session.end_reason or "other"
                session.end_detail = raw_event.decoded_text if not session.end_detail else f"{session.end_detail}; {raw_event.decoded_text}"
        elif parsed.event_type == "persona-not-updated":
            session.notes.append("persona not updated; local stats should be treated as zeroed")

        if room_snapshot_id:
            session.end_room_snapshot_id = room_snapshot_id
        if self.current_stats_snapshot_id:
            session.end_stats_snapshot_id = self.current_stats_snapshot_id
        if self.current_inventory_snapshot_id:
            session.end_inventory_snapshot_id = self.current_inventory_snapshot_id

        session.combat_event_rows.append(
            {
                "raw_event_id": raw_event.id,
                "timestamp_ms": raw_event.timestamp_ms,
                "seq_index": raw_event.seq_index,
                "tag_code": raw_event.tag_code,
                "event_type": parsed.event_type,
                "actor": raw_event.actor,
                "participant_name": parsed.npc_name,
                "fight_npc_name": parsed.npc_name,
                "weapon_name": parsed.weapon_name or (fight.weapon_used if fight is not None else None) or self.current_weapon,
                "approx_damage_done": parsed.approx_damage_done,
                "approx_damage_taken": approx_damage_taken,
                "plain_text": raw_event.decoded_text,
            }
        )

    def _handle_fight_start(self, raw_event: RawEvent, parsed: ParsedEvent, room_snapshot_id: str | None) -> None:
        self._maybe_finalize_pending_session_before_event(raw_event.timestamp_ms or 0, parsed)
        if self.open_session is None:
            self._open_session(raw_event, parsed.initiator, room_snapshot_id)
        session = self.open_session
        assert session is not None
        npc_name = parsed.npc_name or "unknown"
        if npc_name in session.open_fights:
            fight = session.open_fights[npc_name]
        else:
            start_weapon = parsed.weapon_name or self.current_weapon
            fight = FightState(
                npc_name=npc_name,
                npc_group=normalize_npc_group(npc_name),
                initiator=parsed.initiator,
                start_event_id=raw_event.id,
                start_timestamp_ms=raw_event.timestamp_ms or 0,
                start_weapon=start_weapon,
                weapon_used=start_weapon,
            )
            if start_weapon is None:
                fight.notes.append("weapon unknown at fight start")
            session.open_fights[npc_name] = fight
            session.fights[npc_name] = fight
            self._add_participant(session, npc_name)
            if parsed.weapon_name:
                self.current_weapon = parsed.weapon_name
                session.last_explicit_weapon = parsed.weapon_name
        if parsed.initiator and session.initiator is None:
            session.initiator = parsed.initiator
        if room_snapshot_id:
            session.end_room_snapshot_id = room_snapshot_id
        session.end_stats_snapshot_id = self.current_stats_snapshot_id
        session.end_inventory_snapshot_id = self.current_inventory_snapshot_id
        session.combat_event_rows.append(
            {
                "raw_event_id": raw_event.id,
                "timestamp_ms": raw_event.timestamp_ms,
                "seq_index": raw_event.seq_index,
                "tag_code": raw_event.tag_code,
                "event_type": "combatant-joins" if session.start_event_id != raw_event.id else "fight-start",
                "actor": raw_event.actor,
                "participant_name": npc_name,
                "fight_npc_name": npc_name,
                "weapon_name": parsed.weapon_name or fight.weapon_used,
                "approx_damage_done": None,
                "approx_damage_taken": None,
                "plain_text": raw_event.decoded_text,
            }
        )
        if session.start_event_id != raw_event.id:
            session.join_events += 1

    def _open_session(self, raw_event: RawEvent, initiator: str | None, room_snapshot_id: str | None) -> None:
        self.session_index += 1
        start_weapon = raw_event.weapon_name or self.current_weapon
        self.con.execute(
            """
            INSERT INTO combat_sessions (
                capture_id, session_index, initiator, start_event_id, start_timestamp_ms,
                start_room_snapshot_id, start_stats_snapshot_id, start_inventory_snapshot_id,
                start_weapon, last_explicit_weapon
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                raw_event.capture_id,
                self.session_index,
                initiator,
                raw_event.id,
                raw_event.timestamp_ms,
                room_snapshot_id or self.current_room_snapshot_id,
                self.current_stats_snapshot_id,
                self.current_inventory_snapshot_id,
                start_weapon,
                start_weapon,
            ),
        )
        db_id = int(self.con.execute("SELECT last_insert_rowid()").fetchone()[0])
        self.open_session = SessionState(
            db_id=db_id,
            capture_id=raw_event.capture_id,
            session_index=self.session_index,
            initiator=initiator,
            start_event_id=raw_event.id,
            start_timestamp_ms=raw_event.timestamp_ms or 0,
            start_room_snapshot_id=room_snapshot_id or self.current_room_snapshot_id,
            start_stats_snapshot_id=self.current_stats_snapshot_id,
            start_inventory_snapshot_id=self.current_inventory_snapshot_id,
            start_weapon=start_weapon,
            last_explicit_weapon=start_weapon,
        )
        if start_weapon is None:
            self.open_session.notes.append("encounter started with unknown weapon state")

    def _ensure_fight_for_event(self, session: SessionState, raw_event: RawEvent, parsed: ParsedEvent) -> FightState:
        npc_name = parsed.npc_name or session.primary_target or "unknown"
        fight = session.open_fights.get(npc_name)
        if fight is None:
            initiator = parsed.initiator or ("npc" if parsed.actor == "them" else "player")
            start_weapon = self.current_weapon
            fight = FightState(
                npc_name=npc_name,
                npc_group=normalize_npc_group(npc_name),
                initiator=initiator,
                start_event_id=raw_event.id,
                start_timestamp_ms=raw_event.timestamp_ms or 0,
                start_weapon=start_weapon,
                weapon_used=start_weapon,
                notes=[f"fight opened implicitly from {parsed.event_type}"],
            )
            if start_weapon is None:
                fight.notes.append("weapon unknown at fight start")
            session.open_fights[npc_name] = fight
            session.fights[npc_name] = fight
            self._add_participant(session, npc_name)
        return fight

    def _handle_flee_event(self, session: SessionState, raw_event: RawEvent, parsed: ParsedEvent) -> None:
        if YOU_FLED_RE.search(raw_event.decoded_text):
            self._resolve_all_open_fights(session, raw_event, "you-fled", raw_event.decoded_text)
            self._set_pending_end(session, "flee", raw_event)
            return
        npc_name = parsed.npc_name
        if npc_name:
            self._close_fight(session, npc_name, raw_event, "npc-fled", raw_event.decoded_text)
        if not session.open_fights:
            self._set_pending_end(session, "flee", raw_event)

    def _close_fight(self, session: SessionState, npc_name: str, raw_event: RawEvent, outcome: str, resolution_text: str) -> None:
        fight = session.open_fights.pop(npc_name, None)
        if fight is None:
            fight = session.fights.get(npc_name)
            if fight is None or fight.outcome is not None:
                return
        fight.end_event_id = raw_event.id
        fight.end_timestamp_ms = raw_event.timestamp_ms
        fight.outcome = outcome
        fight.resolution_text = resolution_text

    def _resolve_all_open_fights(self, session: SessionState, raw_event: RawEvent, outcome: str, resolution_text: str) -> None:
        for npc_name in list(session.open_fights):
            self._close_fight(session, npc_name, raw_event, outcome, resolution_text)

    def _set_pending_end(self, session: SessionState, reason: str, raw_event: RawEvent) -> None:
        session.end_reason = reason
        session.end_detail = raw_event.decoded_text if not session.end_detail else f"{session.end_detail}; {raw_event.decoded_text}"
        session.end_event_id = raw_event.id
        session.end_timestamp_ms = raw_event.timestamp_ms
        session.pending_end_reason = reason
        session.pending_end_event_id = raw_event.id
        session.pending_end_ts = raw_event.timestamp_ms
        session.pending_end_detail = raw_event.decoded_text
        session.pending_end_mode = "explicit" if reason in {"flee", "withdraw", "other"} else "kill"

    def _compute_damage_taken(self, stamina_after: int | None, max_stamina: int | None) -> float | None:
        if stamina_after is None:
            return None
        damage: float | None = None
        if self.last_known_stamina is not None:
            delta = self.last_known_stamina - stamina_after
            if delta >= 0:
                damage = float(delta)
        self.last_known_stamina = stamina_after
        self.last_known_max_stamina = max_stamina
        return damage

    def _add_participant(self, session: SessionState, participant_name: str) -> None:
        if participant_name not in session.participant_set:
            session.participant_set.add(participant_name)
            session.participants.append(participant_name)
        if session.primary_target is None:
            session.primary_target = participant_name

    def _maybe_finalize_pending_session_before_event(self, timestamp_ms: int, parsed: ParsedEvent, force_any: bool = False) -> None:
        session = self.open_session
        if session is None or session.pending_end_mode is None or session.open_fights:
            return
        if session.pending_end_mode == "explicit" and not force_any:
            return
        if not force_any and session.pending_end_ts is not None and timestamp_ms - session.pending_end_ts <= KILL_GRACE_MS:
            return
        self._finalize_session(session)

    def _expire_pending_session(self, timestamp_ms: int | None) -> None:
        session = self.open_session
        if session is None or session.pending_end_mode != "kill" or session.open_fights:
            return
        if timestamp_ms is None or session.pending_end_ts is None:
            return
        if timestamp_ms - session.pending_end_ts <= KILL_GRACE_MS:
            return
        self._finalize_session(session)

    def _force_close_session(self, timestamp_ms: int | None, end_event_id: str | None, reason: str, detail: str, fight_outcome: str) -> None:
        session = self.open_session
        if session is None:
            return
        placeholder = RawEvent(
            id=end_event_id or stable_id(session.capture_id, timestamp_ms, reason, detail),
            capture_id=session.capture_id,
            seq_index=-1,
            event_ordinal=-1,
            timestamp_ms=timestamp_ms,
            direction="rx",
            tag_code=None,
            category="forced-end",
            event_type=reason,
            actor=None,
            subject_name=None,
            weapon_name=None,
            decoded_text=detail,
            snippet_text=detail[:240],
            record_json="[]",
            is_client_probe=0,
        )
        self._resolve_all_open_fights(session, placeholder, fight_outcome, detail)
        session.end_event_id = end_event_id
        session.end_timestamp_ms = timestamp_ms
        session.end_reason = reason
        session.end_detail = detail
        session.pending_end_mode = "explicit"
        if self.current_room_snapshot_id:
            session.end_room_snapshot_id = self.current_room_snapshot_id
        if self.current_stats_snapshot_id:
            session.end_stats_snapshot_id = self.current_stats_snapshot_id
        if self.current_inventory_snapshot_id:
            session.end_inventory_snapshot_id = self.current_inventory_snapshot_id
        self._finalize_session(session)

    def _close_at_capture_end(self, stop_ts: int | None) -> None:
        session = self.open_session
        if session is None:
            return
        if session.open_fights:
            placeholder = RawEvent(
                id=stable_id(session.capture_id, stop_ts, "capture-stop"),
                capture_id=session.capture_id,
                seq_index=-1,
                event_ordinal=-1,
                timestamp_ms=stop_ts,
                direction="rx",
                tag_code=None,
                category="forced-end",
                event_type="capture-stop",
                actor=None,
                subject_name=None,
                weapon_name=None,
                decoded_text="capture ended while combat was still open",
                snippet_text="capture ended while combat was still open",
                record_json="[]",
                is_client_probe=0,
            )
            self._resolve_all_open_fights(session, placeholder, "pass/unresolved", placeholder.decoded_text)
            session.end_reason = session.end_reason or "ambiguous-capture-stop"
            session.end_detail = session.end_detail or placeholder.decoded_text
            session.end_timestamp_ms = stop_ts
        self._finalize_session(session)

    def _finalize_session(self, session: SessionState) -> None:
        if session.end_timestamp_ms is None:
            session.end_timestamp_ms = session.pending_end_ts or session.start_timestamp_ms
        if session.end_event_id is None:
            session.end_event_id = session.pending_end_event_id
        if session.end_reason is None:
            session.end_reason = session.pending_end_reason or "ambiguous"
        if session.end_detail is None:
            session.end_detail = session.pending_end_detail
        participant_confidence = "high" if session.primary_target else ("medium" if session.participants else "low")
        duration_ms = (session.end_timestamp_ms - session.start_timestamp_ms) if session.end_timestamp_ms is not None else None
        notes = "; ".join(dict.fromkeys(note for note in session.notes if note))
        self.con.execute(
            """
            UPDATE combat_sessions
            SET
                initiator = ?,
                end_event_id = ?,
                end_timestamp_ms = ?,
                duration_ms = ?,
                end_reason = ?,
                end_detail = ?,
                primary_target = ?,
                participant_names_json = ?,
                participant_confidence = ?,
                last_explicit_weapon = ?,
                end_room_snapshot_id = ?,
                end_stats_snapshot_id = ?,
                end_inventory_snapshot_id = ?,
                you_hits = ?,
                you_misses = ?,
                they_hits = ?,
                they_misses = ?,
                withdraw_offers = ?,
                kills_by_you = ?,
                kills_by_them = ?,
                join_events = ?,
                notes = ?
            WHERE id = ?
            """,
            (
                session.initiator,
                session.end_event_id,
                session.end_timestamp_ms,
                duration_ms,
                session.end_reason,
                session.end_detail,
                session.primary_target,
                json.dumps(session.participants),
                participant_confidence,
                session.last_explicit_weapon,
                session.end_room_snapshot_id,
                session.end_stats_snapshot_id,
                session.end_inventory_snapshot_id,
                session.you_hits,
                session.you_misses,
                session.they_hits,
                session.they_misses,
                session.withdraw_offers,
                session.kills_by_you,
                session.kills_by_them,
                session.join_events,
                notes or None,
                session.db_id,
            ),
        )
        fight_ids: dict[str, int] = {}
        for fight in session.fights.values():
            if fight.outcome is None:
                fight.outcome = "pass/unresolved"
                fight.resolution_text = fight.resolution_text or f"Encounter ended as {session.end_reason or 'unknown'} before this fight resolved."
                fight.end_event_id = fight.end_event_id or session.end_event_id
                fight.end_timestamp_ms = fight.end_timestamp_ms or session.end_timestamp_ms
            duration = None
            if fight.end_timestamp_ms is not None:
                duration = fight.end_timestamp_ms - fight.start_timestamp_ms
            self.con.execute(
                """
                INSERT INTO combat_fights (
                    capture_id, session_id, npc_name, npc_group, initiator, start_event_id,
                    end_event_id, start_timestamp_ms, end_timestamp_ms, duration_ms,
                    start_weapon, weapon_used, outcome, resolution_text,
                    you_hits, you_misses, they_hits, they_misses,
                    approx_damage_done, approx_damage_taken, notes
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """,
                (
                    session.capture_id,
                    session.db_id,
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
            fight_ids[fight.npc_name] = int(self.con.execute("SELECT last_insert_rowid()").fetchone()[0])
        for row in session.combat_event_rows:
            fight_id = fight_ids.get(row["fight_npc_name"]) if row["fight_npc_name"] else None
            self.con.execute(
                """
                INSERT INTO combat_events (
                    capture_id, session_id, fight_id, raw_event_id, timestamp_ms, seq_index,
                    tag_code, event_type, actor, participant_name, weapon_name,
                    approx_damage_done, approx_damage_taken, plain_text
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """,
                (
                    session.capture_id,
                    session.db_id,
                    fight_id,
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
        self.completed_sessions.append(session)
        self.open_session = None

    def _finalize_effect_windows(self, capture_id: str, stop_ts: int | None) -> None:
        for effect_name, start_event_id in list(self.open_effects.items()):
            start_ts = self._status_event_timestamp(start_event_id)
            self.con.execute(
                """
                INSERT INTO status_effect_windows (
                    id, capture_id, effect_name, start_event_id, end_event_id,
                    start_timestamp_ms, end_timestamp_ms, confidence, note
                ) VALUES (?, ?, ?, ?, NULL, ?, ?, 'low', ?)
                """,
                (
                    stable_id(capture_id, effect_name, start_event_id, "open"),
                    capture_id,
                    effect_name,
                    start_event_id,
                    start_ts,
                    stop_ts,
                    "effect still open at capture end",
                ),
            )
        self.open_effects.clear()

    def _associate_commands(self) -> None:
        for session in self.completed_sessions:
            if session.end_timestamp_ms is None:
                continue
            for event in self.tx_events:
                if event.is_client_probe:
                    continue
                if event.timestamp_ms is None:
                    continue
                if event.timestamp_ms < session.start_timestamp_ms - COMMAND_ASSOC_PRE_MS:
                    continue
                if event.timestamp_ms > session.end_timestamp_ms + COMMAND_ASSOC_POST_MS:
                    continue
                phase = "during"
                if event.timestamp_ms < session.start_timestamp_ms:
                    phase = "pre"
                elif event.timestamp_ms > session.end_timestamp_ms:
                    phase = "post"
                self.con.execute(
                    """
                    INSERT INTO combat_session_commands (
                        capture_id, session_id, raw_event_id, timestamp_ms, phase, command_text
                    ) VALUES (?, ?, ?, ?, ?, ?)
                    """,
                    (
                        session.capture_id,
                        session.db_id,
                        event.id,
                        event.timestamp_ms,
                        phase,
                        event.decoded_text,
                    ),
                )

    def _associate_snapshots(self, capture_id: str) -> None:
        stats_rows = self.con.execute(
            "SELECT id, timestamp_ms FROM stats_snapshots WHERE capture_id = ? ORDER BY timestamp_ms, seq_index",
            (capture_id,),
        ).fetchall()
        inv_rows = self.con.execute(
            "SELECT id, timestamp_ms FROM inventory_snapshots WHERE capture_id = ? ORDER BY timestamp_ms, seq_index",
            (capture_id,),
        ).fetchall()
        effect_rows = self.con.execute(
            """
            SELECT id, start_timestamp_ms, end_timestamp_ms
            FROM status_effect_windows
            WHERE capture_id = ?
            ORDER BY COALESCE(start_timestamp_ms, end_timestamp_ms)
            """,
            (capture_id,),
        ).fetchall()
        for session in self.completed_sessions:
            if session.end_timestamp_ms is None:
                continue
            for snapshot_id, ts in stats_rows:
                relation = None
                if ts == session.start_timestamp_ms:
                    relation = "start"
                elif ts == session.end_timestamp_ms:
                    relation = "end"
                elif session.start_timestamp_ms <= ts <= session.end_timestamp_ms:
                    relation = "during"
                if relation:
                    self.con.execute(
                        "INSERT OR IGNORE INTO combat_session_stats (session_id, snapshot_id, relation) VALUES (?, ?, ?)",
                        (session.db_id, snapshot_id, relation),
                    )
            for snapshot_id, ts in inv_rows:
                relation = None
                if ts == session.start_timestamp_ms:
                    relation = "start"
                elif ts == session.end_timestamp_ms:
                    relation = "end"
                elif session.start_timestamp_ms <= ts <= session.end_timestamp_ms:
                    relation = "during"
                if relation:
                    self.con.execute(
                        "INSERT OR IGNORE INTO combat_session_inventory (session_id, snapshot_id, relation) VALUES (?, ?, ?)",
                        (session.db_id, snapshot_id, relation),
                    )
            for window_id, start_ts, end_ts in effect_rows:
                eff_start = start_ts if start_ts is not None else session.start_timestamp_ms
                eff_end = end_ts if end_ts is not None else session.end_timestamp_ms
                overlap_start = max(session.start_timestamp_ms, eff_start)
                overlap_end = min(session.end_timestamp_ms, eff_end)
                if overlap_start <= overlap_end:
                    self.con.execute(
                        """
                        INSERT OR IGNORE INTO combat_session_status_effects (
                            session_id, status_window_id, overlap_start_ms, overlap_end_ms
                        ) VALUES (?, ?, ?, ?)
                        """,
                        (session.db_id, window_id, overlap_start, overlap_end),
                    )

    def _insert_raw_event(self, event: RawEvent) -> None:
        self.con.execute(
            """
            INSERT INTO raw_events (
                id, capture_id, seq_index, event_ordinal, timestamp_ms, direction,
                tag_code, category, event_type, actor, subject_name, weapon_name,
                decoded_text, snippet_text, record_json, is_client_probe
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                event.id,
                event.capture_id,
                event.seq_index,
                event.event_ordinal,
                event.timestamp_ms,
                event.direction,
                event.tag_code,
                event.category,
                event.event_type,
                event.actor,
                event.subject_name,
                event.weapon_name,
                event.decoded_text,
                event.snippet_text,
                event.record_json,
                event.is_client_probe,
            ),
        )

    def _status_event_timestamp(self, event_id: str | None) -> int | None:
        if event_id is None:
            return None
        row = self.con.execute(
            "SELECT timestamp_ms FROM status_effect_events WHERE id = ?",
            (event_id,),
        ).fetchone()
        return int(row[0]) if row and row[0] is not None else None


def ensure_schema(con: sqlite3.Connection) -> None:
    con.executescript(SCHEMA_FILE.read_text(encoding="utf-8"))


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("captures", nargs="+", type=Path, help="Capture JSONL files to reduce.")
    parser.add_argument("--db", type=Path, default=DEFAULT_DB, help="SQLite DB path.")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    args.db.parent.mkdir(parents=True, exist_ok=True)
    con = sqlite3.connect(args.db)
    try:
        ensure_schema(con)
        reducer = Reducer(con)
        for capture in args.captures:
            reducer.reload_capture(capture)
            print(f"Reduced {capture}")
        tables = [
            row[0]
            for row in con.execute(
                "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name"
            ).fetchall()
        ]
        print(f"DB ready: {args.db}")
        for table in tables:
            count = con.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0]
            print(f"  {table}: {count} rows")
        return 0
    finally:
        con.close()


if __name__ == "__main__":
    raise SystemExit(main())
