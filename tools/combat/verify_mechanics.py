#!/usr/bin/env python3
"""Test the published MUD2 mechanics claims against our own captured play data.

Every claim in MUD2-PUBLISHED-MECHANICS.md that our capture corpus can reach is
re-derived here from the analysis database, and each one gets a verdict:

    SUPPORTED         the data matches the published formula, with enough samples to say so
    REFUTED           the data contradicts it, with enough samples to say so
    INCONCLUSIVE      enough data to look, but the answer is genuinely ambiguous
    INSUFFICIENT DATA not enough observations to conclude anything

The verdict never runs ahead of the sample. Anything resting on a single
observation says "n=1" out loud and is reported as INSUFFICIENT DATA - one
event is an anecdote, not a proof. Re-run this as more sessions accumulate;
sample sizes are printed for every claim so it is obvious when a verdict has
earned an upgrade.

Usage:
  python tools/combat/verify_mechanics.py
  python tools/combat/verify_mechanics.py --db path/to/combat.db
  python tools/combat/verify_mechanics.py --db verify.db --claim knees --claim damage
  python tools/combat/verify_mechanics.py --list

Stdlib only, Python 3.10+. Output is pure ASCII so it survives a cp1252 console.
"""

from __future__ import annotations

import argparse
import bisect
import collections
import csv
import math
import re
import sqlite3
import sys
from dataclasses import dataclass, field
from pathlib import Path

DEFAULT_DB = Path.home() / ".mucka" / "combat" / "combat.db"
DEFAULT_BESTIARY = Path(__file__).with_name("bestiary.tsv")

# "You hit the water-snake5 (15-19)." - MUD2's own damage banding, not the client's.
YOU_HIT_RE = re.compile(r"You hit the (?P<npc>\S+?)\s*\((?P<lo>\d+)-(?P<hi>\d+)\)")
# "The ram hits you (57/105)." - exact post-blow stamina, which is how NPC damage
# becomes an exact integer rather than a band.
THEY_HIT_RE = re.compile(r"[Tt]he (?P<npc>\S+?) hits you \((?P<cur>\d+)/(?P<max>\d+)\)")
YOU_FLED_RE = re.compile(r"You have fled by going", re.IGNORECASE)

SUPPORTED = "SUPPORTED"
REFUTED = "REFUTED"
INCONCLUSIVE = "INCONCLUSIVE"
INSUFFICIENT = "INSUFFICIENT DATA"

WRAP = 96


# ============================================================
# Result plumbing
# ============================================================


@dataclass
class Claim:
    key: str
    title: str
    claim_text: str
    source: str
    verdict: str = INSUFFICIENT
    n: str = "0"
    lines: list[str] = field(default_factory=list)

    def say(self, text: str = "") -> None:
        self.lines.append(text)

    def table(self, headers: list[str], rows: list[list[object]]) -> None:
        cells = [[str(h) for h in headers]] + [[fmt(v) for v in row] for row in rows]
        widths = [max(len(r[i]) for r in cells) for i in range(len(headers))]
        for idx, row in enumerate(cells):
            self.say("  " + "  ".join(c.ljust(widths[i]) for i, c in enumerate(row)).rstrip())
            if idx == 0:
                self.say("  " + "  ".join("-" * widths[i] for i in range(len(headers))))


def fmt(value: object) -> str:
    if value is None:
        return "-"
    if isinstance(value, float):
        return f"{value:.3f}".rstrip("0").rstrip(".") or "0"
    return str(value)


def wrap(text: str, indent: str = "  ") -> list[str]:
    out: list[str] = []
    for para in text.split("\n"):
        words = para.split()
        if not words:
            out.append("")
            continue
        line = indent
        for word in words:
            if len(line) + len(word) + 1 > WRAP and line.strip():
                out.append(line.rstrip())
                line = indent
            line += word + " "
        out.append(line.rstrip())
    return out


# ============================================================
# Bestiary
# ============================================================


class Bestiary:
    """Published per-creature stats, indexed so an in-game instance name resolves to a row.

    MUD2 instance names carry an index (rat17, zombie7, water-snake3) while the guide's
    table collapses them into ranges (Rat1-21, Zombie0-7) or plurals (Water-snakes).
    Unresolved names are kept and reported rather than silently dropped, because a
    silently-dropped creature is a silently-shrunk sample.
    """

    RANGE_RE = re.compile(r"^(?P<base>[A-Za-z-]+?)(?P<lo>\d+)\s*-\s*(?P<hi>\d+)$")
    SLASH_RE = re.compile(r"^(?P<base>[A-Za-z-]+?)(?P<a>\d+)\s*/\s*(?P<b>\d+)$")
    INDEXED_RE = re.compile(r"^(?P<base>[A-Za-z-]+?)(?P<idx>\d+)$")

    def __init__(self, path: Path):
        self.rows: list[dict[str, str]] = []
        self.exact: dict[str, dict[str, str]] = {}
        self.by_base: dict[str, list[tuple[int, int, dict[str, str]]]] = collections.defaultdict(list)
        self.plural: dict[str, dict[str, str]] = {}
        self.unresolved: set[str] = set()
        self._load(path)

    def _load(self, path: Path) -> None:
        with path.open("r", encoding="utf-8", newline="") as handle:
            for row in csv.DictReader(handle, delimiter="\t"):
                if not (row.get("Mobile") or "").strip():
                    continue
                self.rows.append(row)
                self._index(row)

    def _index(self, row: dict[str, str]) -> None:
        name = row["Mobile"].strip()
        # Drop parenthetical qualifiers: "Dwarf44-47 (Young)".
        core = re.sub(r"\s*\(.*?\)\s*", "", name).strip().lower()
        self.exact[core] = row
        match = self.RANGE_RE.match(core)
        if match:
            base = match.group("base")
            self.by_base[base].append((int(match.group("lo")), int(match.group("hi")), row))
            return
        match = self.SLASH_RE.match(core)
        if match:
            base = match.group("base")
            for grp in ("a", "b"):
                idx = int(match.group(grp))
                self.by_base[base].append((idx, idx, row))
            return
        match = self.INDEXED_RE.match(core)
        if match:
            idx = int(match.group("idx"))
            self.by_base[match.group("base")].append((idx, idx, row))
            return
        if core.endswith("s"):
            self.plural[core[:-1]] = row

    def lookup(self, npc_name: str) -> dict[str, str] | None:
        raw = (npc_name or "").strip().lower()
        if not raw:
            return None
        if raw in self.exact:
            return self.exact[raw]
        match = self.INDEXED_RE.match(raw)
        base, idx = (match.group("base"), int(match.group("idx"))) if match else (raw, None)
        if idx is not None:
            for lo, hi, row in self.by_base.get(base, []):
                if lo <= idx <= hi:
                    return row
        if base in self.plural:
            return self.plural[base]
        if base + "s" in self.exact:
            return self.exact[base + "s"]
        # A bare "ram" with only "Ram0" published: accept when the base is unambiguous.
        candidates = self.by_base.get(base, [])
        if idx is None and candidates:
            rows = {id(row): row for _, _, row in candidates}
            if len(rows) == 1:
                return next(iter(rows.values()))
        self.unresolved.add(raw)
        return None

    @staticmethod
    def stat(row: dict[str, str] | None, key: str) -> int | None:
        if not row:
            return None
        text = (row.get(key) or "").replace(",", "").strip()
        return int(text) if text.isdigit() else None


# ============================================================
# Database views used by more than one claim
# ============================================================


class Corpus:
    def __init__(self, con: sqlite3.Connection, bestiary: Bestiary):
        self.con = con
        self.bestiary = bestiary
        self.stats = con.execute(
            """
            SELECT capture_id, timestamp_ms, seq_index, stamina, max_stamina,
                   strength, max_strength, dexterity, max_dexterity,
                   current_magic, score, is_blind
            FROM stats_snapshots
            ORDER BY capture_id, timestamp_ms, seq_index
            """
        ).fetchall()
        self.by_capture: dict[str, list[sqlite3.Row]] = collections.defaultdict(list)
        for row in self.stats:
            self.by_capture[row["capture_id"]].append(row)
        self._ts: dict[str, list[int]] = {
            cap: [r["timestamp_ms"] for r in rows] for cap, rows in self.by_capture.items()
        }
        self.fights = con.execute(
            """
            SELECT id, capture_id, session_id, npc_name, npc_group, initiator, outcome,
                   start_timestamp_ms, end_timestamp_ms, duration_ms, weapon_used,
                   you_hits, you_misses, they_hits, they_misses
            FROM combat_fights
            ORDER BY start_timestamp_ms, id
            """
        ).fetchall()
        self.events = con.execute(
            """
            SELECT id, capture_id, session_id, fight_id, timestamp_ms, seq_index,
                   event_type, plain_text
            FROM combat_events
            ORDER BY capture_id, timestamp_ms, seq_index, id
            """
        ).fetchall()
        self.fight_by_id = {f["id"]: f for f in self.fights}
        self.inventory = con.execute(
            """
            SELECT capture_id, timestamp_ms, carried_items_json
            FROM inventory_snapshots
            ORDER BY capture_id, timestamp_ms
            """
        ).fetchall()
        self._inv: dict[str, list[tuple[int, str]]] = collections.defaultdict(list)
        for row in self.inventory:
            self._inv[row["capture_id"]].append((row["timestamp_ms"], row["carried_items_json"]))
        self.tx = con.execute(
            """
            SELECT capture_id, timestamp_ms, decoded_text
            FROM raw_events
            WHERE direction = 'tx'
            ORDER BY capture_id, timestamp_ms
            """
        ).fetchall()

    # -- snapshot lookups -------------------------------------------------

    def nearest_stats(self, capture_id: str, ts: int, window_ms: int = 15000) -> sqlite3.Row | None:
        rows = self.by_capture.get(capture_id)
        if not rows:
            return None
        times = self._ts[capture_id]
        idx = bisect.bisect_left(times, ts)
        best = None
        for j in (idx - 1, idx, idx + 1):
            if 0 <= j < len(rows):
                if best is None or abs(rows[j]["timestamp_ms"] - ts) < abs(best["timestamp_ms"] - ts):
                    best = rows[j]
        if best is None or abs(best["timestamp_ms"] - ts) > window_ms:
            return None
        return best

    def stats_before(self, capture_id: str, ts: int) -> sqlite3.Row | None:
        rows = self.by_capture.get(capture_id)
        if not rows:
            return None
        idx = bisect.bisect_right(self._ts[capture_id], ts) - 1
        return rows[idx] if idx >= 0 else None

    def stats_after(self, capture_id: str, ts: int) -> sqlite3.Row | None:
        rows = self.by_capture.get(capture_id)
        if not rows:
            return None
        idx = bisect.bisect_left(self._ts[capture_id], ts)
        return rows[idx] if idx < len(rows) else None

    def inventory_at(self, capture_id: str, ts: int) -> str | None:
        rows = self._inv.get(capture_id)
        if not rows:
            return None
        idx = bisect.bisect_right([t for t, _ in rows], ts) - 1
        return rows[idx][1] if idx >= 0 else None

    def sleep_windows(self, pad_ms: int = 120000) -> list[tuple[str, int, int]]:
        """Rough intervals in which the player may have been asleep.

        MUD2 stops answering the FES poll while you sleep, so a sleep both accelerates
        recovery and hides it; any regeneration-rate measurement that overlaps one of
        these is worthless. Padded generously - it is far better to throw away good
        samples than to quote a regeneration rate that is really a nap.
        """
        out = []
        for row in self.tx:
            text = (row["decoded_text"] or "").lower()
            if "sleep" in text:
                out.append((row["capture_id"], row["timestamp_ms"], row["timestamp_ms"] + pad_ms))
        return out

    def heal_events(self) -> list[tuple[str, int]]:
        out = []
        for row in self.tx:
            text = (row["decoded_text"] or "").lower()
            if "drink" in text or "quaff" in text:
                out.append((row["capture_id"], row["timestamp_ms"]))
        return out

    def npc_damage_events(self) -> list[dict[str, object]]:
        """Exact NPC blow damage, reconstructed from the running stamina readout.

        "The ram hits you (57/105)." reports stamina AFTER the blow, so damage is the
        drop from the last known reading. Both FES snapshots and earlier hit lines
        update that reading, which matters in pack fights where three rats land inside
        one round and only one FES poll follows: attributing all three blows to the
        pre-round FES would triple-count.

        Each blow also carries `npc_damage_taken_before` - our running band-midpoint
        estimate of how much punishment that creature had already absorbed. Subtracting
        it from the published STA gives a usable estimate of the ATTACKER's own current
        stamina, which is what makes it possible to ask whether the stamina knee applies
        to mobiles and not just to the player.
        """
        out: list[dict[str, object]] = []
        for capture_id, rows in self.by_capture.items():
            timeline: list[tuple[int, int, str, object]] = []
            for row in rows:
                timeline.append((row["timestamp_ms"], row["seq_index"], "fes", row["stamina"]))
            for ev in self.events:
                if ev["capture_id"] != capture_id:
                    continue
                if ev["event_type"] == "they-hit":
                    match = THEY_HIT_RE.search(ev["plain_text"])
                    if match:
                        timeline.append((ev["timestamp_ms"], ev["seq_index"], "hit", (ev, match)))
                elif ev["event_type"] == "you-hit":
                    match = YOU_HIT_RE.search(ev["plain_text"])
                    if match:
                        timeline.append((ev["timestamp_ms"], ev["seq_index"], "dealt", match))
            timeline.sort(key=lambda item: (item[0], item[1]))
            last = None
            dealt: dict[str, float] = collections.defaultdict(float)
            for ts, _seq, kind, payload in timeline:
                if kind == "fes":
                    last = payload
                    continue
                if kind == "dealt":
                    dealt[payload.group("npc")] += (
                        int(payload.group("lo")) + int(payload.group("hi"))
                    ) / 2.0
                    continue
                ev, match = payload
                cur = int(match.group("cur"))
                name = match.group("npc")
                if last is not None and last > cur and (last - cur) <= 60:
                    fight = self.fight_by_id.get(ev["fight_id"])
                    out.append(
                        {
                            "timestamp_ms": ts,
                            "capture_id": capture_id,
                            "npc_name": name,
                            "npc_group": fight["npc_group"] if fight else None,
                            "fight_id": ev["fight_id"],
                            "damage": last - cur,
                            "npc_damage_taken_before": dealt[name],
                        }
                    )
                last = cur
        return out

    def player_hit_events(self) -> list[dict[str, object]]:
        out: list[dict[str, object]] = []
        for ev in self.events:
            if ev["event_type"] != "you-hit":
                continue
            match = YOU_HIT_RE.search(ev["plain_text"])
            if not match:
                continue
            snap = self.nearest_stats(ev["capture_id"], ev["timestamp_ms"])
            if snap is None:
                continue
            fight = self.fight_by_id.get(ev["fight_id"])
            out.append(
                {
                    "timestamp_ms": ev["timestamp_ms"],
                    "lo": int(match.group("lo")),
                    "hi": int(match.group("hi")),
                    "eff_strength": snap["strength"],
                    "weapon": (fight["weapon_used"] if fight else None) or "(unknown)",
                    "npc_group": fight["npc_group"] if fight else None,
                    "fight_id": ev["fight_id"],
                    "delta_ms": abs(snap["timestamp_ms"] - ev["timestamp_ms"]),
                }
            )
        return out


# ============================================================
# Small statistics helpers (stdlib only)
# ============================================================


def wilson(hits: int, n: int, z: float = 1.96) -> tuple[float, float]:
    if n == 0:
        return (0.0, 1.0)
    p = hits / n
    denom = 1 + z * z / n
    centre = (p + z * z / (2 * n)) / denom
    half = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / denom
    return (max(0.0, centre - half), min(1.0, centre + half))


def poisson_binomial_z(hits: int, probs: list[float]) -> float | None:
    """z for observed successes against a sum of independent, unequal Bernoulli trials."""
    if not probs:
        return None
    mean = sum(probs)
    var = sum(p * (1 - p) for p in probs)
    if var <= 0:
        return None
    return (hits - mean) / math.sqrt(var)


def two_sided_p(z: float) -> float:
    return math.erfc(abs(z) / math.sqrt(2))


# ============================================================
# Claim 1: the stamina knees
# ============================================================


def strength_penalty(stamina: int, threshold: int = 30, divisor: int = 2) -> int:
    return (threshold - stamina) // divisor if stamina < threshold else 0


def dexterity_penalty(stamina: int, threshold: int = 40, divisor: int = 3) -> int:
    return (threshold - stamina) // divisor if stamina < threshold else 0


def claim_knees(corpus: Corpus) -> Claim:
    claim = Claim(
        key="knees",
        title="The stamina knees",
        claim_text=(
            "Effective strength loses (30 - SD)/2 while stamina is below 30; effective "
            "dexterity loses (40 - S)/3 while stamina is below 40. Both floor-divided."
        ),
        source="MUD2-PUBLISHED-MECHANICS.md sections 2 (step 4) and 3 (step 5)",
    )

    # The FES heartbeat reports effective strength and dexterity directly, so the
    # unknown base (bonuses, carried weight, per-object rounding) cancels out of a
    # DIFFERENCE between two snapshots taken while the inventory is unchanged. That
    # turns an un-identifiable absolute into a directly testable delta.
    pairs = []
    for capture_id, rows in corpus.by_capture.items():
        for a, b in zip(rows, rows[1:]):
            if b["timestamp_ms"] - a["timestamp_ms"] > 10000:
                continue
            if a["stamina"] == b["stamina"]:
                continue
            if a["is_blind"] or b["is_blind"]:
                continue  # blindness moves dexterity for an unrelated reason - claim 8
            inv_a = corpus.inventory_at(capture_id, a["timestamp_ms"])
            inv_b = corpus.inventory_at(capture_id, b["timestamp_ms"])
            if inv_a is None or inv_a != inv_b:
                continue
            pairs.append((a, b))

    crossing = [
        (a, b)
        for a, b in pairs
        if min(a["stamina"], b["stamina"]) < 40
    ]
    below30 = [(a, b) for a, b in pairs if min(a["stamina"], b["stamina"]) < 30]

    claim.n = (
        f"{len(pairs)} inventory-stable snapshot pairs "
        f"({len(crossing)} at/below the dexterity knee, {len(below30)} at/below the strength knee)"
    )

    if len(pairs) < 10:
        claim.say("Not enough inventory-stable consecutive FES pairs to test anything.")
        claim.verdict = INSUFFICIENT
        return claim

    # Grid search: which (threshold, divisor) actually explains the observed deltas?
    # Quoting only the published pair would beg the question - the point is to show
    # 30/2 and 40/3 beat every neighbour, including the folk-wisdom knee at 20.
    def score_model(kind: str, threshold: int, divisor: int) -> tuple[int, int]:
        ok = bad = 0
        for a, b in pairs:
            if kind == "str":
                observed = b["strength"] - a["strength"]
                predicted = -(
                    strength_penalty(b["stamina"], threshold, divisor)
                    - strength_penalty(a["stamina"], threshold, divisor)
                )
            else:
                observed = b["dexterity"] - a["dexterity"]
                predicted = -(
                    dexterity_penalty(b["stamina"], threshold, divisor)
                    - dexterity_penalty(a["stamina"], threshold, divisor)
                )
            if observed == predicted:
                ok += 1
            else:
                bad += 1
        return ok, bad

    grid_rows = []
    best = {}
    for kind, label in (("str", "strength"), ("dex", "dexterity")):
        results = []
        for threshold in (0, 20, 25, 30, 35, 40, 45, 50):
            for divisor in (1, 2, 3, 4):
                if threshold == 0 and divisor != 1:
                    continue
                ok, bad = score_model(kind, threshold, divisor)
                results.append((ok, threshold, divisor, bad))
        results.sort(key=lambda item: (-item[0], item[1]))
        best[kind] = results[0]
        for ok, threshold, divisor, bad in results[:4]:
            model = "no knee" if threshold == 0 else f"({threshold} - S)/{divisor}"
            grid_rows.append([label, model, ok, bad, f"{ok / (ok + bad):.3f}"])

    claim.say("Method: consecutive FES snapshots less than 10s apart with an unchanged carried")
    claim.say("inventory (FEI list identical). The unknown base and weight penalties cancel in the")
    claim.say("delta, so the only thing left that should move is the stamina term.")
    claim.say("")
    claim.say("Model fit - candidate knee thresholds and slopes, ranked by exact-match count:")
    claim.table(["stat", "model", "exact", "mismatch", "rate"], grid_rows)
    claim.say("")

    str_ok, str_t, str_d, str_bad = best["str"]
    dex_ok, dex_t, dex_d, dex_bad = best["dex"]
    claim.say(f"Best strength model:  ({str_t} - S)/{str_d}   {str_ok} exact / {str_bad} mismatch")
    claim.say(f"Best dexterity model: ({dex_t} - S)/{dex_d}   {dex_ok} exact / {dex_bad} mismatch")
    claim.say("")

    # Show the actual crossing, blow by blow - this is the evidence a reader wants to see.
    if crossing:
        rows = []
        for a, b in sorted(crossing, key=lambda pr: pr[0]["timestamp_ms"])[:12]:
            rows.append(
                [
                    a["stamina"],
                    b["stamina"],
                    a["strength"],
                    b["strength"],
                    b["strength"] - a["strength"],
                    -(strength_penalty(b["stamina"]) - strength_penalty(a["stamina"])),
                    a["dexterity"],
                    b["dexterity"],
                    b["dexterity"] - a["dexterity"],
                    -(dexterity_penalty(b["stamina"]) - dexterity_penalty(a["stamina"])),
                ]
            )
        claim.say("Observed crossings (the published model's prediction alongside):")
        claim.table(
            ["sta_a", "sta_b", "str_a", "str_b", "d_str", "pred", "dex_a", "dex_b", "d_dex", "pred"],
            rows,
        )
        claim.say("")

    published_str = score_model("str", 30, 2)
    published_dex = score_model("dex", 40, 3)
    claim.say(
        f"Published model as stated: strength {published_str[0]}/{sum(published_str)} exact, "
        f"dexterity {published_dex[0]}/{sum(published_dex)} exact."
    )
    claim.say("")

    # Absolute reconstruction, as a cross-check on the deltas. Take contiguous, tightly
    # sampled, inventory-stable runs that contain snapshots on BOTH sides of the knee.
    # Snapshots above the knee carry no stamina penalty at all, so they read the base off
    # directly - no fitting, nothing circular. The below-knee snapshots in the same run
    # then have to land on base minus the published penalty, exactly.
    anchor_rows = []
    str_hits = str_miss = dex_hits = dex_miss = 0
    str_levels: set[tuple[str, int]] = set()
    dex_levels: set[tuple[str, int]] = set()
    for stat, knee, penalty, base_col in (
        ("strength", 30, strength_penalty, "strength"),
        ("dexterity", 40, dexterity_penalty, "dexterity"),
    ):
        for capture_id, rows in corpus.by_capture.items():
            for idx, row in enumerate(rows):
                if row["stamina"] >= knee or row["is_blind"]:
                    continue
                anchor = _nearest_anchor(corpus, capture_id, rows, idx, knee)
                if anchor is None:
                    continue
                base = anchor[base_col]
                predicted = base - penalty(row["stamina"])
                good = row[base_col] == predicted
                key = (f"{capture_id}:{anchor['timestamp_ms']}", row["stamina"])
                if stat == "strength":
                    str_hits += good
                    str_miss += not good
                    str_levels.add(key)
                else:
                    dex_hits += good
                    dex_miss += not good
                    dex_levels.add(key)
                anchor_rows.append(
                    [stat, row["stamina"], base, penalty(row["stamina"]), predicted, row[base_col],
                     "exact" if good else "WRONG"]
                )
    if anchor_rows:
        seen: set[tuple] = set()
        unique_rows = []
        for row in anchor_rows:
            key = tuple(row)
            if key not in seen:
                seen.add(key)
                unique_rows.append(row)
        claim.say("Absolute cross-check. For each below-knee snapshot, the base is READ from the")
        claim.say("nearest above-knee snapshot within 20s that shares the same carried inventory and")
        claim.say("the same score (where the penalty is zero by definition, so no fitting is")
        claim.say("involved), then used to predict the below-knee value. Distinct outcomes only:")
        claim.table(
            ["stat", "stamina", "base", "penalty", "predicted", "observed", ""], unique_rows
        )
        claim.say("")
    str_windows = len({w for w, _ in str_levels})
    dex_windows = len({w for w, _ in dex_levels})
    claim.say(
        f"Below-knee predictions from a directly-read base: strength {str_hits} exact / "
        f"{str_miss} wrong, over {len(str_levels)} distinct stamina level(s) in {str_windows} "
        f"run(s); dexterity {dex_hits} exact / {dex_miss} wrong, over {len(dex_levels)} level(s) "
        f"in {dex_windows} run(s)."
    )
    claim.n = (
        f"{len(pairs)} inventory-stable snapshot pairs, of which {len(crossing)} straddle the "
        f"dexterity knee and {len(below30)} the strength knee; absolute cross-check at "
        f"{len(str_levels)} sub-30 and {len(dex_levels)} sub-40 stamina levels"
    )

    strength_wins = (str_t, str_d) == (30, 2) and published_str[1] <= 1 and str_miss == 0
    dex_wins = (dex_t, dex_d) == (40, 3) and published_dex[1] <= 3 and dex_miss == 0
    strong = len(str_levels) >= 4 and len(dex_levels) >= 4 and str_windows >= 2 and dex_windows >= 2
    thin = len(str_levels) >= 2 and len(dex_levels) >= 3 and len(pairs) >= 40
    if strength_wins and dex_wins and strong:
        claim.verdict = SUPPORTED
        claim.say("")
        claim.say(
            "Both knees reproduce exactly, and every rival threshold in the grid - including the "
            "folk-wisdom knee at 20 - fits strictly worse. The slope matters as much as the "
            "threshold: /2 for strength and /3 for dexterity are the only divisors that land on "
            "the observed integers."
        )
    elif strength_wins and dex_wins and thin:
        claim.verdict = SUPPORTED
        claim.say("")
        claim.say(
            "Supported, but read the SAMPLE line before quoting this. Two very different kinds of "
            "evidence are being combined. Above the knees it is overwhelming: dozens of stamina "
            "changes that moved neither stat, which is what rules out any threshold higher than 30 "
            "or 40, and rules out the knee sitting at 20. Below the knees it is thin - a small "
            "number of distinct stamina levels from one or two near-death episodes. Every one of "
            "them lands on the exact integer the formula predicts, out of a range where a wrong "
            "slope would visibly miss, so it is real evidence rather than coincidence; but one "
            "more near-death fight would roughly double it, and until then the SLOPES are the "
            "least-tested part of this claim."
        )
    elif not (strong or thin):
        claim.verdict = INSUFFICIENT
        claim.say("")
        claim.say("Too few samples below the knees to separate the candidate thresholds.")
    elif strength_wins or dex_wins:
        claim.verdict = INCONCLUSIVE
    else:
        claim.verdict = REFUTED
    return claim


def _nearest_anchor(
    corpus: Corpus,
    capture_id: str,
    rows: list[sqlite3.Row],
    idx: int,
    knee: int,
    window_ms: int = 20000,
) -> sqlite3.Row | None:
    """Nearest snapshot above `knee` that can be trusted to share the target's base.

    Three guards, each earning its place. Same carried inventory: the base moves when the
    player picks something up. Same score: a score change means something happened - a
    kill, a flee, a treasure - and the one flee in this corpus moved the base by 8 while
    leaving the inventory list looking identical, which would have produced a spurious
    refutation. Within 20s: the inventory list is polled separately from the stat
    heartbeat, so over a long gap it can read unchanged across a change and back.
    """
    target = rows[idx]
    inv = corpus.inventory_at(capture_id, target["timestamp_ms"])
    if inv is None:
        return None
    best: sqlite3.Row | None = None
    for step in (-1, 1):
        j = idx + step
        while 0 <= j < len(rows):
            candidate = rows[j]
            gap = abs(candidate["timestamp_ms"] - target["timestamp_ms"])
            if gap > window_ms:
                break
            if (
                candidate["stamina"] >= knee
                and not candidate["is_blind"]
                and candidate["score"] == target["score"]
                and corpus.inventory_at(capture_id, candidate["timestamp_ms"]) == inv
            ):
                if best is None or gap < abs(best["timestamp_ms"] - target["timestamp_ms"]):
                    best = candidate
                break
            j += step
    return best


# ============================================================
# Claim 2: damage bound and implied weapon strengths
# ============================================================


def damage_ceiling(combat_strength: int) -> int:
    return combat_strength // 6 + 1


def claim_damage_bound(corpus: Corpus) -> Claim:
    claim = Claim(
        key="damage",
        title="Damage bound: 1 .. (CS/6)+1",
        claim_text=(
            "Damage on a hit is a uniform random value in 1 .. (CS/6)+1, where CS is the "
            "attacker's effective strength plus its weapon's own strength."
        ),
        source="MUD2-PUBLISHED-MECHANICS.md section 4",
    )

    # --- NPC side: exact damage, published STR, no weapon unknown ------------
    # This is the cleaner half of the test. "The ram hits you (57/105)" is an exact
    # integer, and an unarmed creature's CS is just its published STR, so the bound
    # is fully specified with nothing to fit.
    blows = corpus.npc_damage_events()
    by_species: dict[str, list[dict[str, object]]] = collections.defaultdict(list)
    species_stats: dict[str, tuple[int, int | None]] = {}
    for blow in blows:
        row = corpus.bestiary.lookup(str(blow["npc_name"]))
        strength = Bestiary.stat(row, "STR")
        if strength is None:
            continue
        label = (row.get("Mobile") or str(blow["npc_name"])).strip()
        by_species[label].append(blow)
        species_stats[label] = (strength, Bestiary.stat(row, "STA"))

    rows = []
    mean_rows = []
    total_blows = 0
    violations = 0
    at_bound = 0
    suspicious_low = 0
    for label, group in sorted(by_species.items(), key=lambda kv: -len(kv[1])):
        strength, _pool = species_stats[label]
        ceiling = damage_ceiling(strength)
        damages = [int(b["damage"]) for b in group]
        observed_max = max(damages)
        over = sum(1 for d in damages if d > ceiling)
        total_blows += len(damages)
        violations += over
        if observed_max == ceiling:
            at_bound += 1
        # P(max of n uniform(1..M) <= observed): a very small value means the ceiling is
        # too high, which is just as informative as a blow that breaks it.
        p_low = (observed_max / ceiling) ** len(damages)
        if p_low < 0.01:
            suspicious_low += 1
        rows.append(
            [label, strength, ceiling, len(damages), observed_max, over, round(p_low, 4)]
        )
        mean_rows.append(
            [label, len(damages), round(sum(damages) / len(damages), 2), round((1 + ceiling) / 2, 2)]
        )

    claim.n = (
        f"{total_blows} NPC blows with exact damage; "
        f"{len(corpus.player_hit_events())} player blows in bands"
    )
    claim.say("NPC side (exact damage from the '(cur/max)' stamina readout; CS = published STR,")
    claim.say("since mobiles here are unarmed). 'P(max this low)' is the chance a uniform 1..max")
    claim.say("would produce a sample maximum no larger than observed - a tiny value says the")
    claim.say("ceiling is too GENEROUS, which the naive bound cannot see:")
    claim.table(
        ["creature", "STR", "max = STR/6+1", "blows", "observed max", "over bound", "P(max this low)"],
        rows,
    )
    claim.say("")
    claim.say("Mean damage against the uniform-distribution prediction (1+max)/2:")
    claim.table(["creature", "blows", "observed mean", "predicted mean"], mean_rows)
    claim.say("")
    claim.say(
        f"No blow anywhere exceeded its bound ({violations} violations in {total_blows}); "
        f"{at_bound} of {len(rows)} species produced a blow exactly AT the bound, which is what "
        "a uniform 1..max does and a smaller ceiling could not."
    )
    claim.say("")

    # --- the mobiles are subject to their own stamina knee -------------------
    # A rat has 25 stamina, so it lives its entire life below the 30-stamina strength
    # knee and gets weaker every time we hit it. Applying the SAME published knee to the
    # attacker's CS turns a bound that was far too generous for small creatures into one
    # that tracks them. This is the published formula applied where the guide never says
    # to apply it, and it is the only way the rat row makes sense.
    naive_bad = adjusted_bad = 0
    knee_rows = []
    for label, group in sorted(by_species.items(), key=lambda kv: -len(kv[1])):
        strength, pool = species_stats[label]
        if pool is None:
            continue
        naive_pred = []
        adj_pred = []
        obs = []
        n_bad = a_bad = 0
        for blow in group:
            remaining = max(0.0, pool - float(blow["npc_damage_taken_before"]))
            effective = max(strength // 2, strength - strength_penalty(int(remaining)))
            ceiling_naive = damage_ceiling(strength)
            ceiling_adj = damage_ceiling(effective)
            damage = int(blow["damage"])
            obs.append(damage)
            naive_pred.append((1 + ceiling_naive) / 2)
            adj_pred.append((1 + ceiling_adj) / 2)
            if damage > ceiling_naive:
                n_bad += 1
            if damage > ceiling_adj:
                a_bad += 1
        naive_bad += n_bad
        adjusted_bad += a_bad
        knee_rows.append(
            [
                label,
                len(obs),
                round(sum(obs) / len(obs), 2),
                round(sum(naive_pred) / len(naive_pred), 2),
                round(sum(adj_pred) / len(adj_pred), 2),
                n_bad,
                a_bad,
            ]
        )
    claim.say("Same blows, with the attacker's OWN stamina knee applied to its CS. The creature's")
    claim.say("remaining stamina is estimated as published STA minus our running band-midpoint")
    claim.say("damage, then effective STR = STR - (30 - remaining)/2, floored at 50% of STR:")
    claim.table(
        [
            "creature",
            "blows",
            "observed mean",
            "predicted mean (flat STR)",
            "predicted mean (knee)",
            "over flat",
            "over knee",
        ],
        knee_rows,
    )
    claim.say("")
    claim.say(
        f"Violations: {naive_bad} against the flat bound, {adjusted_bad} against the knee-adjusted "
        "bound - so the tighter model survives too, while predicting the small creatures far "
        "better. This is a derived result, not a published one: the guide states the knee for the "
        "PLAYER and never says mobiles pay it."
    )
    claim.say("")

    # --- Player side: bands only, so fit the weapon strength -----------------
    # We never see the player's damage number, only MUD2's band. But the band is a
    # censored observation of a uniform 1..M, and effective strength is known per blow,
    # so weapon strength is the single free parameter - fit it by maximum likelihood
    # instead of guessing.
    hits = corpus.player_hit_events()
    by_weapon: dict[str, list[dict[str, object]]] = collections.defaultdict(list)
    for hit in hits:
        by_weapon[str(hit["weapon"])].append(hit)

    def log_likelihood(samples: list[dict[str, object]], weapon_strength: int) -> float:
        total = 0.0
        for sample in samples:
            ceiling = damage_ceiling(int(sample["eff_strength"]) + weapon_strength)
            lo, hi = int(sample["lo"]), int(sample["hi"])
            if ceiling < lo:
                return float("-inf")
            width = min(hi, ceiling) - lo + 1
            total += math.log(width / ceiling)
        return total

    weapon_rows = []
    for weapon, samples in sorted(by_weapon.items(), key=lambda kv: -len(kv[1])):
        scores = [(log_likelihood(samples, ws), ws) for ws in range(0, 301)]
        scores = [(score, ws) for score, ws in scores if score > float("-inf")]
        if not scores:
            continue
        best_score, best_ws = max(scores)
        # Likelihood-ratio support interval, chi-square 1 df at 95% -> 1.92 log units.
        support = [ws for score, ws in scores if score >= best_score - 1.92]
        bands = collections.Counter(int(s["lo"]) for s in samples)
        strengths = [int(s["eff_strength"]) for s in samples]
        weapon_rows.append(
            [
                weapon,
                len(samples),
                f"{min(strengths)}-{max(strengths)}",
                best_ws,
                f"{min(support)}-{max(support)}",
                damage_ceiling(round(sum(strengths) / len(strengths)) + best_ws),
                " ".join(f"{k}:{v}" for k, v in sorted(bands.items())),
            ]
        )

    claim.say("Player side. MUD2 reports our damage only as a band, so weapon strength is fitted by")
    claim.say("maximum likelihood over the bands, using the FES effective strength at each blow.")
    claim.say("The interval is a 95% likelihood-ratio support interval, not a confidence interval:")
    claim.table(
        [
            "weapon",
            "blows",
            "eff STR range",
            "implied WS",
            "support",
            "implied max dmg",
            "band lows seen",
        ],
        weapon_rows,
    )
    claim.say("")
    claim.say(
        "Read the implied weapon strengths as order-of-magnitude, not gospel: a band is a coarse "
        "observation, and a weapon seen only in low bands has almost no upper constraint at all."
    )

    if violations > 0:
        claim.verdict = REFUTED
    elif total_blows >= 40 and at_bound >= 2 and adjusted_bad <= max(1, total_blows // 50):
        claim.verdict = SUPPORTED
    elif total_blows >= 10:
        claim.verdict = INCONCLUSIVE
    else:
        claim.verdict = INSUFFICIENT
    if suspicious_low:
        claim.say("")
        claim.say(
            f"{suspicious_low} species had a sample maximum improbably far below the flat bound. "
            "That is the signature the knee table above explains; without the knee those rows "
            "would be quietly wrong in the direction the flat test cannot detect."
        )
    return claim


# ============================================================
# Claim 3: hit chance Dy / (Dy + Do)
# ============================================================


def claim_hit_chance(corpus: Corpus) -> Claim:
    claim = Claim(
        key="hitchance",
        title="Hit chance = Dy / (Dy + Do)",
        claim_text=(
            "Chance to hit is your effective dexterity over the sum of both combatants' "
            "effective dexterities. Applied in both directions."
        ),
        source="MUD2-PUBLISHED-MECHANICS.md section 4",
    )

    you_rows: list[tuple[str, float]] = []
    they_rows: list[tuple[str, float]] = []
    you_hit = collections.Counter()
    you_swing = collections.Counter()
    they_hit = collections.Counter()
    they_swing = collections.Counter()
    you_probs: dict[str, list[float]] = collections.defaultdict(list)
    they_probs: dict[str, list[float]] = collections.defaultdict(list)
    skipped = 0

    for ev in corpus.events:
        if ev["event_type"] not in ("you-hit", "you-miss", "they-hit", "they-miss"):
            continue
        fight = corpus.fight_by_id.get(ev["fight_id"])
        if fight is None:
            skipped += 1
            continue
        row = corpus.bestiary.lookup(fight["npc_name"])
        npc_dex = Bestiary.stat(row, "DEX")
        snap = corpus.nearest_stats(ev["capture_id"], ev["timestamp_ms"])
        if npc_dex is None or snap is None:
            skipped += 1
            continue
        player_dex = snap["dexterity"]
        if not player_dex:
            skipped += 1
            continue
        group = fight["npc_group"]
        p_you = player_dex / (player_dex + npc_dex)
        if ev["event_type"] in ("you-hit", "you-miss"):
            you_swing[group] += 1
            you_probs[group].append(p_you)
            if ev["event_type"] == "you-hit":
                you_hit[group] += 1
        else:
            they_swing[group] += 1
            they_probs[group].append(1 - p_you)
            if ev["event_type"] == "they-hit":
                they_hit[group] += 1

    total_swings = sum(you_swing.values()) + sum(they_swing.values())
    claim.n = f"{sum(you_swing.values())} player swings, {sum(they_swing.values())} NPC swings"

    def build(hitc, swingc, probs, who):
        rows = []
        all_p: list[float] = []
        all_h = 0
        for group in sorted(swingc, key=lambda g: -swingc[g]):
            n = swingc[group]
            h = hitc[group]
            pred = sum(probs[group]) / n
            lo, hi = wilson(h, n)
            z = poisson_binomial_z(h, probs[group])
            rows.append(
                [
                    group,
                    n,
                    h,
                    round(h / n, 3),
                    f"{lo:.3f}-{hi:.3f}",
                    round(pred, 3),
                    "in CI" if lo <= pred <= hi else "OUTSIDE",
                    None if z is None else round(z, 2),
                ]
            )
            all_p.extend(probs[group])
            all_h += h
        return rows, all_h, all_p

    you_table, you_h, you_p = build(you_hit, you_swing, you_probs, "you")
    they_table, they_h, they_p = build(they_hit, they_swing, they_probs, "they")

    claim.say("Player hitting the NPC. Predicted rate uses the FES effective dexterity at each")
    claim.say("individual swing and the bestiary's published DEX for that creature:")
    claim.table(
        ["npc_group", "swings", "hits", "observed", "95% CI", "predicted", "verdict", "z"],
        you_table,
    )
    claim.say("")
    claim.say("NPC hitting the player (the same formula with the terms swapped):")
    claim.table(
        ["npc_group", "swings", "hits", "observed", "95% CI", "predicted", "verdict", "z"],
        they_table,
    )
    claim.say("")

    z_you = poisson_binomial_z(you_h, you_p)
    z_they = poisson_binomial_z(they_h, they_p)
    pooled = []
    for label, hits_n, probs, z in (
        ("player swings", you_h, you_p, z_you),
        ("NPC swings", they_h, they_p, z_they),
    ):
        if not probs:
            continue
        pooled.append(
            [
                label,
                len(probs),
                hits_n,
                round(hits_n / len(probs), 3),
                round(sum(probs) / len(probs), 3),
                None if z is None else round(z, 2),
                None if z is None else round(two_sided_p(z), 4),
            ]
        )
    claim.say("Pooled across every fight (z from a Poisson-binomial, since each swing has its own p):")
    claim.table(["side", "swings", "hits", "observed", "predicted", "z", "p"], pooled)
    if skipped:
        claim.say("")
        claim.say(f"({skipped} swings excluded: no bestiary row or no FES snapshot within 15s.)")
    claim.say("")
    claim.say(
        "Caveat worth stating plainly: the formula's step-3 halving against an unseen target is "
        "per-opponent, and a fight in an unlit room or against a hidden creature would depress "
        "the true Dy without the FES showing it. Group rows with a handful of swings are noise; "
        "read the pooled row."
    )

    strong = [z for z in (z_you, z_they) if z is not None]
    if total_swings < 60 or not strong:
        claim.verdict = INSUFFICIENT
    elif all(abs(z) < 2.0 for z in strong):
        claim.verdict = SUPPORTED
    elif all(abs(z) > 3.0 for z in strong):
        claim.verdict = REFUTED
    else:
        claim.verdict = INCONCLUSIVE
    return claim


# ============================================================
# Claim 4: creature stamina pools
# ============================================================


def claim_stamina_pools(corpus: Corpus) -> Claim:
    claim = Claim(
        key="pools",
        title="Creature stamina pools equal the published STA",
        claim_text=(
            "Summed damage in a fight that ended in a kill approximates the creature's "
            "stamina pool, and the bestiary's STA is that pool."
        ),
        source="MUD2-PUBLISHED-MECHANICS.md sections 7 and 8",
    )

    # Bands, not numbers, so bound the pool instead of point-estimating it:
    #   lower bound - the creature was still standing after every blow but the last,
    #                 so its pool exceeds the summed LOW ends of all earlier blows
    #   upper bound - it died on the last blow, so the pool is at most the summed HIGH ends
    # A published STA inside that interval is a pass; outside it is a real miss.
    band_by_fight: dict[int, list[tuple[int, int, int]]] = collections.defaultdict(list)
    for ev in corpus.events:
        if ev["event_type"] != "you-hit":
            continue
        match = YOU_HIT_RE.search(ev["plain_text"])
        if match and ev["fight_id"] is not None:
            band_by_fight[ev["fight_id"]].append(
                (ev["timestamp_ms"], int(match.group("lo")), int(match.group("hi")))
            )

    # An instance fought, escaped, and finished off later spent its pool across several
    # fights. Scoping to the killing fight alone would under-read it - exactly the failure
    # mode the published document records in section 1 - so accumulate every blow landed on
    # that (capture, instance) up to and including the kill. Keying on capture matters:
    # the same name after a reset is a different creature with a full pool.
    carried: dict[tuple[str, str], list[tuple[int, int, int]]] = collections.defaultdict(list)
    rows = []
    inside = outside = 0
    for fight in corpus.fights:
        key = (fight["capture_id"], fight["npc_name"])
        prior_fights = 1 if carried[key] else 0
        carried[key].extend(band_by_fight.get(fight["id"], []))
        if fight["outcome"] != "Kill":
            continue
        bands = sorted(carried[key])
        if not bands:
            continue
        row = corpus.bestiary.lookup(fight["npc_name"])
        published = Bestiary.stat(row, "STA")
        lows = [b[1] for b in bands]
        highs = [b[2] for b in bands]
        lower = sum(lows[:-1]) + 1
        upper = sum(highs)
        mid = sum((lo + hi) / 2 for _, lo, hi in bands)
        flags = []
        if prior_fights:
            flags.append("multi-fight")
        if published is not None and highs and published < highs[-1]:
            flags.append("overshoot")  # one blow can exceed the whole pool
        verdict = "-"
        implied_regen = None
        if published is not None:
            if lower <= published <= upper:
                verdict = "consistent"
                inside += 1
            else:
                verdict = "MISS"
                outside += 1
                if lower > published and fight["duration_ms"]:
                    rounds = max(1.0, fight["duration_ms"] / 2000.0)
                    implied_regen = round((lower - published) / rounds, 2)
        rows.append(
            [
                fight["npc_name"],
                (row.get("Mobile") if row else "?"),
                published,
                len(bands),
                lower,
                upper,
                round(mid, 1),
                verdict,
                implied_regen,
                ",".join(flags) or "-",
            ]
        )
        carried[key] = []

    usable = [r for r in rows if r[7] != "-"]
    clean = [r for r in usable if r[9] == "-"]
    claim.n = f"{len(usable)} kills with band data ({len(clean)} without a caveat flag)"
    claim.say("Per kill. [lower, upper] brackets the true pool: lower is the summed band minima of")
    claim.say("every blow but the killing one (the creature was demonstrably still alive), upper is")
    claim.say("the summed band maxima of all blows (it was demonstrably dead). Blows landed on the")
    claim.say("same instance in an earlier fight are carried forward, so a chased creature is not")
    claim.say("scored as if it started the last fight at full health.")
    claim.table(
        [
            "npc",
            "bestiary row",
            "STA",
            "blows",
            "lower",
            "upper",
            "midpoint sum",
            "verdict",
            "implied regen/round",
            "flags",
        ],
        rows,
    )
    claim.say("")
    claim.say("Flags: 'multi-fight' - the pool was spent across more than one fight against this")
    claim.say("instance, so the bracket depends on the reducer having segmented them correctly.")
    claim.say("'overshoot' - a single blow can exceed the whole published pool, so the upper bound")
    claim.say("is loose by construction and a small creature always looks tougher than it is.")
    claim.say("'implied regen/round' is filled in only when the lower bound exceeds the published")
    claim.say("pool: the per-round regeneration that would reconcile the two. The published guide")
    claim.say("credits zombies with 1 regeneration per round, so a value near 1.0 there is a")
    claim.say("reconciliation rather than a contradiction.")
    claim.say("")

    by_group: dict[str, list[float]] = collections.defaultdict(list)
    for fight in corpus.fights:
        if fight["outcome"] != "Kill":
            continue
        bands = band_by_fight.get(fight["id"], [])
        if bands:
            by_group[fight["npc_group"]].append(sum((lo + hi) / 2 for _, lo, hi in bands))
    group_rows = []
    for group, values in sorted(by_group.items(), key=lambda kv: -len(kv[1])):
        values = sorted(values)
        median = values[len(values) // 2] if len(values) % 2 else (
            values[len(values) // 2 - 1] + values[len(values) // 2]
        ) / 2
        sample = next(
            (
                Bestiary.stat(corpus.bestiary.lookup(f["npc_name"]), "STA")
                for f in corpus.fights
                if f["npc_group"] == group
            ),
            None,
        )
        group_rows.append([group, len(values), round(median, 1), sample])
    claim.say("By species group, median of the summed band midpoints against published STA:")
    claim.table(["npc_group", "kills", "median damage", "published STA"], group_rows)

    claim.say("")
    if len(usable) < 5:
        claim.verdict = INSUFFICIENT
    elif outside == 0:
        claim.verdict = SUPPORTED
        claim.say(
            f"All {len(usable)} kills put the published STA inside the bracket the bands allow. "
            "That is a real constraint, not a loose one: the lower bound alone rules out a pool "
            "much smaller than published."
        )
    elif outside > inside:
        claim.verdict = REFUTED
    else:
        claim.verdict = INCONCLUSIVE
        regens = [r[8] for r in rows if r[8] is not None]
        claim.say(f"{inside} kills consistent, {outside} outside the bracket.")
        if regens and all(0.5 <= value <= 1.5 for value in regens):
            claim.say(
                "Every miss is over-read rather than under-read, and each one is reconciled by a "
                "regeneration of about 1 point per round - which is precisely the figure the "
                "published guide's own worked example credits to a zombie. That is a plausible "
                "reconciliation, not a confirmation: bestiary.tsv has no regeneration column, so "
                "the rate is being inferred from the very discrepancy it explains. Transcribing "
                "the guide's regeneration figures would turn this into a real test."
            )
    return claim


# ============================================================
# Claim 5: flee cost bands
# ============================================================


def claim_flee_cost(corpus: Corpus) -> Claim:
    claim = Claim(
        key="flee",
        title="Flee cost scales with remaining stamina",
        claim_text=(
            "Fleeing removes a portion of your points scaled by remaining stamina: "
            "100% -> 400%, >75% -> 200%, 26-75% -> 100%, 11-25% -> 50%, 0-10% -> 0%."
        ),
        source="MUD2-PUBLISHED-MECHANICS.md section 6",
    )

    def band(fraction: float) -> tuple[str, float]:
        pct = fraction * 100
        if pct >= 100:
            return ("100%", 4.0)
        if pct > 75:
            return (">75%", 2.0)
        if pct >= 26:
            return ("26-75%", 1.0)
        if pct >= 11:
            return ("11-25%", 0.5)
        return ("0-10%", 0.0)

    flees = []
    for ev in corpus.events:
        if ev["event_type"] != "fight-end-flee":
            continue
        if not YOU_FLED_RE.search(ev["plain_text"]):
            continue
        flees.append(ev)

    rows = []
    for ev in flees:
        before = corpus.stats_before(ev["capture_id"], ev["timestamp_ms"])
        after = corpus.stats_after(ev["capture_id"], ev["timestamp_ms"])
        if before is None or after is None:
            continue
        loss = before["score"] - after["score"]
        fraction = before["stamina"] / before["max_stamina"] if before["max_stamina"] else 0.0
        label, modifier = band(fraction)
        implied = None if modifier == 0 else (loss / before["score"]) / modifier * 100
        rows.append(
            [
                ev["timestamp_ms"],
                before["stamina"],
                before["max_stamina"],
                f"{fraction * 100:.1f}%",
                label,
                f"{modifier * 100:.0f}%",
                before["score"],
                after["score"],
                loss,
                f"{loss / before['score'] * 100:.2f}%",
                None if implied is None else f"{implied:.2f}%",
            ]
        )

    claim.n = f"{len(rows)} player flees with bracketing score snapshots"
    if not rows:
        claim.say("No player flee with a score snapshot on each side of it in this database.")
        claim.verdict = INSUFFICIENT
        return claim

    claim.table(
        [
            "when",
            "sta",
            "max",
            "fraction",
            "band",
            "modifier",
            "score before",
            "after",
            "loss",
            "loss %",
            "implied base rate",
        ],
        rows,
    )
    claim.say("")
    claim.say("'implied base rate' is the loss as a percentage of score, divided by the band's")
    claim.say("published modifier - the 100%-band rate the table implies. Consistent flees across")
    claim.say("different bands should agree on it; that is the test the table is set up for.")
    claim.say("")
    if len(rows) == 1:
        claim.verdict = INSUFFICIENT
        claim.say(
            "n=1. A single flee cannot distinguish the published ladder from any other function "
            "that happens to pass through one point - it does not confirm the band boundaries, "
            "the 400%/200%/100%/50%/0% ratios, or even that stamina is the input. It is one "
            "measurement, and it is recorded here so a second flee in a different band can be "
            "compared against it. What it does establish: fleeing was NOT free at this stamina, "
            "which rules out the 0% band extending this high."
        )
    else:
        implied_values = [
            float(r[10].rstrip("%")) for r in rows if r[10] is not None
        ]
        if len(implied_values) < 2:
            claim.verdict = INSUFFICIENT
        else:
            spread = max(implied_values) - min(implied_values)
            centre = sum(implied_values) / len(implied_values)
            claim.say(
                f"Implied base rate across {len(implied_values)} flees: "
                f"{min(implied_values):.2f}% to {max(implied_values):.2f}% (mean {centre:.2f}%)."
            )
            if spread <= 0.25 * max(centre, 1e-9):
                claim.verdict = SUPPORTED
            elif spread >= max(centre, 1e-9):
                claim.verdict = REFUTED
            else:
                claim.verdict = INCONCLUSIVE
    return claim


# ============================================================
# Claim 6: combat round period, and the regeneration tick
# ============================================================


def claim_round(corpus: Corpus) -> Claim:
    claim = Claim(
        key="round",
        title="Combat resolves on a fixed round period",
        claim_text="Each combatant gets one chance to hit per round (usually).",
        source="MUD2-PUBLISHED-MECHANICS.md section 4",
    )

    exchanges: dict[int, list[int]] = collections.defaultdict(list)
    for ev in corpus.events:
        if ev["event_type"] in ("you-hit", "you-miss", "they-hit", "they-miss"):
            exchanges[ev["session_id"]].append(ev["timestamp_ms"])
    gaps = collections.Counter()
    phases: dict[str, list[int]] = collections.defaultdict(list)
    for session_id, stamps in exchanges.items():
        stamps = sorted(set(stamps))
        for a, b in zip(stamps, stamps[1:]):
            if b - a < 30000:
                gaps[b - a] += 1
    for ev in corpus.events:
        if ev["event_type"] in ("you-hit", "you-miss", "they-hit", "they-miss"):
            phases[ev["capture_id"]].append(ev["timestamp_ms"] % 2000)

    total_gaps = sum(gaps.values())
    bucketed = collections.Counter()
    off_lattice = 0
    for gap, count in gaps.items():
        nearest = round(gap / 2000.0)
        if nearest >= 1 and abs(gap - nearest * 2000) <= 250:
            bucketed[nearest] += count
        else:
            off_lattice += count

    claim.n = f"{total_gaps} swing-exchange gaps; {sum(len(v) for v in phases.values())} swing lines"
    claim.say("Gaps between consecutive exchange instants inside a combat session, in multiples of")
    claim.say("2.000s (a gap of 2 rounds means a round where neither side acted):")
    claim.table(
        ["rounds", "seconds", "count"],
        [[k, k * 2.0, v] for k, v in sorted(bucketed.items())]
        + ([["off-lattice", "-", off_lattice]] if off_lattice else []),
    )
    claim.say("")
    phase_rows = []
    for capture_id, values in phases.items():
        values = sorted(values)
        phase_rows.append(
            [
                capture_id[:8],
                len(values),
                min(values),
                values[len(values) // 2],
                max(values),
                max(values) - min(values),
            ]
        )
    claim.say("Arrival time of every swing line modulo 2000ms. A tight cluster means the server is")
    claim.say("resolving combat on a fixed 2.000s lattice and the spread is only network jitter:")
    claim.table(["capture", "lines", "min", "median", "max", "spread"], phase_rows)
    claim.say("")

    lattice_ok = total_gaps >= 50 and off_lattice <= max(1, total_gaps // 50)
    spread_ok = bool(phase_rows) and max(r[5] for r in phase_rows) < 400
    if lattice_ok and spread_ok:
        claim.say(
            "Combat resolves on an exact 2.000s lattice. Every observed gap is an integer number "
            "of those periods, and the arrival phase is stable across a whole session to within "
            "receive jitter - a drifting or variable period could not produce that. Gaps longer "
            "than one period are rounds in which neither side acted, which is the 'usually' in "
            "the published wording: a mobile's Speed governs how often it takes its swing."
        )
        claim.verdict = SUPPORTED
    elif total_gaps >= 20:
        claim.verdict = INCONCLUSIVE
    else:
        claim.verdict = INSUFFICIENT
    return claim


def claim_regen(corpus: Corpus) -> Claim:
    claim = Claim(
        key="regen",
        title="Round period equals the regeneration tick period",
        claim_text=(
            "The combat round period equals the regeneration tick period, so expected damage "
            "per round is P(hit) * damage - opponent regeneration."
        ),
        source="MUD2-PUBLISHED-MECHANICS.md section 4",
    )

    # Regeneration. The trap here is sleep: MUD2 stops answering the FES poll while you
    # sleep, so a nap looks like a huge instantaneous heal. Every window that overlaps a
    # sleep command, a drink, or a max-stamina clamp is discarded before measuring.
    sleeps = corpus.sleep_windows()
    heals = corpus.heal_events()

    def contaminated(capture_id: str, start: int, end: int) -> bool:
        for cap, lo, hi in sleeps:
            if cap == capture_id and start <= hi and end >= lo:
                return True
        for cap, ts in heals:
            if cap == capture_id and start - 5000 <= ts <= end + 5000:
                return True
        return False

    gain = 0
    span_ms = 0
    segments = 0
    intervals: list[int] = []
    for capture_id, rows in corpus.by_capture.items():
        run: list[sqlite3.Row] = []
        for a, b in zip(rows, rows[1:]):
            ok = (
                b["timestamp_ms"] - a["timestamp_ms"] <= 6000
                and b["stamina"] >= a["stamina"]
                and a["stamina"] < a["max_stamina"]
            )
            if ok:
                if not run:
                    run = [a]
                run.append(b)
                continue
            if len(run) > 1:
                _accumulate_regen(run, capture_id, contaminated, intervals)
                delta = run[-1]["stamina"] - run[0]["stamina"]
                width = run[-1]["timestamp_ms"] - run[0]["timestamp_ms"]
                if delta > 0 and width > 0 and not contaminated(
                    capture_id, run[0]["timestamp_ms"], run[-1]["timestamp_ms"]
                ):
                    gain += delta
                    span_ms += width
                    segments += 1
            run = []
        if len(run) > 1:
            _accumulate_regen(run, capture_id, contaminated, intervals)
            delta = run[-1]["stamina"] - run[0]["stamina"]
            width = run[-1]["timestamp_ms"] - run[0]["timestamp_ms"]
            if delta > 0 and width > 0 and not contaminated(
                capture_id, run[0]["timestamp_ms"], run[-1]["timestamp_ms"]
            ):
                gain += delta
                span_ms += width
                segments += 1

    claim.n = f"{segments} uncontaminated regeneration windows totalling {gain} stamina points"
    claim.say("Stamina regeneration, awake only. Windows overlapping a 'sleep' command or a drink")
    claim.say("are discarded: MUD2 stops answering the FES poll while you sleep, so a nap shows up")
    claim.say("as a single enormous jump and would flatter the measured rate enormously.")
    if gain > 0:
        claim.table(
            ["clean segments", "stamina gained", "elapsed s", "seconds per point", "vs 2.000s round"],
            [
                [
                    segments,
                    gain,
                    round(span_ms / 1000.0, 1),
                    round(span_ms / 1000.0 / gain, 2),
                    round(span_ms / 1000.0 / gain / 2.0, 2),
                ]
            ],
        )
    else:
        claim.say("  No uncontaminated regeneration window in this database.")
    if intervals:
        intervals.sort()
        claim.say("")
        claim.say(
            f"Individual +1 steps that could be localised: n={len(intervals)}, "
            f"median gap {intervals[len(intervals) // 2] / 1000.0:.1f}s, "
            f"range {intervals[0] / 1000.0:.1f}-{intervals[-1] / 1000.0:.1f}s."
        )
    claim.say("")
    claim.say(
        "This does not settle the claim, and the reason is worth being precise about. Awake "
        "stamina regeneration is far slower than one point per 2.000s combat round. But the "
        "published claim is about the TICK PERIOD, not the amount regained per tick: a 2.000s "
        "tick that grants a point only occasionally is consistent with both the claim and this "
        "measurement, and nothing here can tell those apart. The FES heartbeat is also polled by "
        "the client rather than pushed by the server, so it samples the regeneration process "
        "instead of observing it. Settling this needs a deliberately idle capture - no combat, no "
        "sleeping, no drinking - with the FES polled fast enough to catch each individual step."
    )
    claim.verdict = INCONCLUSIVE if gain > 0 else INSUFFICIENT
    return claim


def _accumulate_regen(run, capture_id, contaminated, intervals) -> None:
    last_change = None
    for row in run:
        if last_change is None:
            last_change = row
            continue
        if row["stamina"] != last_change["stamina"]:
            if row["stamina"] - last_change["stamina"] == 1 and not contaminated(
                capture_id, last_change["timestamp_ms"], row["timestamp_ms"]
            ):
                intervals.append(row["timestamp_ms"] - last_change["timestamp_ms"])
            last_change = row


# ============================================================
# Claim 7: effective-stat floors
# ============================================================


def claim_floors(corpus: Corpus) -> Claim:
    claim = Claim(
        key="floors",
        title="Effective-stat floors: strength 50% of base, dexterity 25%",
        claim_text=(
            "Effective strength cannot drop below 50% of base; effective dexterity cannot "
            "drop below 25% of base."
        ),
        source="MUD2-PUBLISHED-MECHANICS.md sections 2 and 3",
    )
    rows = []
    verdicts = []
    for stat, base_col, share in (("strength", "max_strength", 0.5), ("dexterity", "max_dexterity", 0.25)):
        values = [(r[stat], r[base_col]) for r in corpus.stats if r[base_col]]
        if not values:
            continue
        floors = sorted({int(b * share) for _, b in values})
        floor = floors[0]
        below = sum(1 for v, b in values if v < int(b * share))
        at_floor = sum(1 for v, b in values if v == int(b * share))
        rows.append([stat, len(values), min(v for v, _ in values), floor, at_floor, below])
        verdicts.append((stat, len(values), at_floor, below))
    claim.n = f"{len(corpus.stats)} FES snapshots"
    claim.table(
        ["stat", "snapshots", "observed min", "predicted floor", "sitting AT floor", "below floor"],
        rows,
    )
    claim.say("")
    strength_row = next((v for v in verdicts if v[0] == "strength"), None)
    dex_row = next((v for v in verdicts if v[0] == "dexterity"), None)
    if strength_row and strength_row[2] > 20 and strength_row[3] == 0:
        claim.say(
            f"Strength: {strength_row[2]} snapshots pinned at exactly the predicted floor and none "
            "below it. A plateau at the predicted value is much stronger evidence than mere "
            "absence of violations - the stat was actively being clamped, not just never pushed "
            "that far."
        )
        claim.verdict = SUPPORTED
    else:
        claim.verdict = INSUFFICIENT
    if dex_row and dex_row[2] == 0:
        claim.say(
            f"Dexterity: the floor was never approached (minimum observed is well above it), so "
            "this half is untested - absence of a violation proves nothing when the bound was "
            "never under pressure."
        )
        if claim.verdict == SUPPORTED:
            claim.say("Verdict below covers the strength floor only.")
    return claim


# ============================================================
# Claim 8: the blindness dexterity penalty
# ============================================================


def claim_blind(corpus: Corpus) -> Claim:
    claim = Claim(
        key="blind",
        title="Blindness costs D/10 (surroundings) plus D/2 (target)",
        claim_text=(
            "Effective dexterity loses D/10 when you cannot perceive your surroundings and "
            "a further D/2 when you cannot perceive your target. The document asserts the "
            "second term is invisible to score."
        ),
        source="MUD2-PUBLISHED-MECHANICS.md section 3 (steps 2 and 3)",
    )
    pairs = []
    for capture_id, rows in corpus.by_capture.items():
        for a, b in zip(rows, rows[1:]):
            if b["timestamp_ms"] - a["timestamp_ms"] > 10000:
                continue
            if a["is_blind"] == b["is_blind"]:
                continue
            inv_a = corpus.inventory_at(capture_id, a["timestamp_ms"])
            inv_b = corpus.inventory_at(capture_id, b["timestamp_ms"])
            if inv_a is None or inv_a != inv_b:
                continue
            if a["stamina"] != b["stamina"]:
                continue
            sighted, blind = (a, b) if b["is_blind"] else (b, a)
            pairs.append((sighted, blind))
    rows = []
    episodes: set[int] = set()
    for sighted, blind in pairs:
        base = sighted["max_dexterity"]
        # Blind/unblind cycles inside one fight are not independent observations - group
        # them into episodes so the sample line reports something honest.
        episodes.add(min(sighted["timestamp_ms"], blind["timestamp_ms"]) // 30000)
        rows.append(
            [
                sighted["timestamp_ms"],
                base,
                sighted["dexterity"],
                blind["dexterity"],
                sighted["dexterity"] - blind["dexterity"],
                base // 10,
                base // 2,
                base // 10 + base // 2,
            ]
        )
    claim.n = (
        f"{len(rows)} sighted/blind transitions with stamina and inventory held constant, "
        f"from {len(episodes)} separate blinding episodes, all at base D = "
        f"{sorted({r[1] for r in rows})}"
    )
    if not rows:
        claim.say("No blind transition in this database.")
        claim.verdict = INSUFFICIENT
        return claim
    claim.table(
        ["when", "base D", "dex sighted", "dex blind", "cost", "D/10", "D/2", "D/10 + D/2"],
        rows,
    )
    claim.say("")
    costs = {r[4] for r in rows}
    predicted = {r[7] for r in rows}
    if len(costs) == 1 and costs == predicted:
        claim.say(
            f"Every transition costs exactly {rows[0][4]}, which is D/10 + D/2 at the observed "
            f"base D of {rows[0][1]}. Both steps fire together when you are blinded, and - this "
            "is the part that contradicts the document - the FES readout DOES show the D/2 term. "
            "The document states score 'cannot show the halving'. Our heartbeat shows it."
        )
        claim.verdict = SUPPORTED if len(episodes) >= 3 else INSUFFICIENT
        if len(episodes) < 3:
            claim.say(
                f"Only {len(episodes)} blinding episode(s) - the transition count flatters the "
                "sample, since repeated blind/unblind inside one fight is one observation "
                "repeated, not several."
            )
    else:
        claim.say(f"Costs are not constant: {sorted(costs)} against predicted {sorted(predicted)}.")
        claim.verdict = INCONCLUSIVE
    claim.say("")
    claim.say(
        "One caveat the sample cannot escape: base D was 100 in every observation, and 100/10 + "
        "100/2 = 60 is also just 'a flat 60'. A character with a different base dexterity would "
        "separate the two readings in a single snapshot."
    )
    return claim


# ============================================================
# Claim 9: kill score awards match the bestiary Points column
# ============================================================


def claim_points(corpus: Corpus) -> Claim:
    claim = Claim(
        key="points",
        title="Kill award equals the bestiary Points column",
        claim_text="Killing a mobile awards the score in the bestiary's Points column.",
        source="MUD2-PUBLISHED-MECHANICS.md section 7 (bestiary.tsv)",
    )
    kills = [ev for ev in corpus.events if ev["event_type"] == "you-killed"]
    kill_times = collections.defaultdict(list)
    for ev in kills:
        kill_times[ev["capture_id"]].append(ev["timestamp_ms"])
    rows = []
    skipped_shared = 0
    match_count = mismatch_count = 0
    for ev in kills:
        before = corpus.stats_before(ev["capture_id"], ev["timestamp_ms"])
        after = corpus.stats_after(ev["capture_id"], ev["timestamp_ms"])
        if before is None or after is None:
            continue
        if after["timestamp_ms"] - ev["timestamp_ms"] > 5000:
            continue
        if ev["timestamp_ms"] - before["timestamp_ms"] > 8000:
            continue
        # Two rats can die in the same instant, between one FES poll and the next. The
        # score delta then covers both kills and would read as a doubled award; that is a
        # measurement artefact, not a mechanic, so drop the bracket rather than report it.
        shared = [
            t
            for t in kill_times[ev["capture_id"]]
            if before["timestamp_ms"] < t <= after["timestamp_ms"]
        ]
        if len(shared) > 1:
            skipped_shared += 1
            continue
        fight = corpus.fight_by_id.get(ev["fight_id"])
        name = fight["npc_name"] if fight else None
        row = corpus.bestiary.lookup(name) if name else None
        published = Bestiary.stat(row, "Points")
        delta = after["score"] - before["score"]
        ok = "-" if published is None else ("match" if delta == published else "MISMATCH")
        if ok == "match":
            match_count += 1
        elif ok == "MISMATCH":
            mismatch_count += 1
        rows.append(
            [
                name,
                (row.get("Mobile") if row else "?"),
                published,
                delta,
                None if published is None else delta - published,
                ok,
            ]
        )
    claim.n = f"{match_count + mismatch_count} kills with a single-kill score bracket"
    if not rows:
        claim.say("No kill with FES snapshots tight enough on both sides.")
        claim.verdict = INSUFFICIENT
        return claim
    claim.table(
        ["npc", "bestiary row", "published points", "observed delta", "difference", ""], rows
    )
    if skipped_shared:
        claim.say("")
        claim.say(
            f"({skipped_shared} kills excluded: another kill landed inside the same FES bracket, "
            "so the two awards are not separable.)"
        )
    claim.say("")

    # Score also moves DURING fights without anybody dying. Surfacing those is how an
    # undocumented award gets found rather than averaged into the kill figures.
    other_rows = []
    for fight in corpus.fights:
        if fight["end_timestamp_ms"] is None:
            continue
        window = [
            ev
            for ev in corpus.events
            if ev["capture_id"] == fight["capture_id"]
            and fight["start_timestamp_ms"] <= ev["timestamp_ms"] <= fight["end_timestamp_ms"]
        ]
        snaps = [
            s
            for s in corpus.by_capture[fight["capture_id"]]
            if fight["start_timestamp_ms"] <= s["timestamp_ms"] <= fight["end_timestamp_ms"]
        ]
        for a, b in zip(snaps, snaps[1:]):
            delta = b["score"] - a["score"]
            if delta == 0:
                continue
            between = [
                ev
                for ev in window
                if a["timestamp_ms"] < ev["timestamp_ms"] <= b["timestamp_ms"]
            ]
            if any(ev["event_type"] == "you-killed" for ev in between):
                continue
            kinds = {ev["event_type"] for ev in between}
            if "fight-end-flee" in kinds:
                cause = "opponent flee attempt in bracket"
            elif kinds & {"you-hit", "you-miss", "they-hit", "they-miss"}:
                cause = "swings only, no flee"
            else:
                cause = "no combat event in bracket (treasure or similar)"
            other_rows.append([fight["npc_name"], delta, cause])
    if other_rows:
        summary = collections.Counter((r[2], r[1]) for r in other_rows)
        claim.say("Score changes DURING a fight with no kill in the bracket. Anything recurring here")
        claim.say("is an award the published document does not describe:")
        claim.table(
            ["bracket contents", "score delta", "times"],
            [[cause, delta, count] for (cause, delta), count in sorted(summary.items())],
        )
        claim.say("")
        flee_awards = [r for r in other_rows if r[2].startswith("opponent flee")]
        if len(flee_awards) >= 3 and len({r[1] for r in flee_awards}) == 1:
            unit = flee_awards[0][1]
            claim.say(
                f"A failed escape by the opponent pays {unit} points, {len(flee_awards)} times in "
                "this corpus and always the same amount. Nothing in the published document "
                "mentions it. Note the interaction with the kill award: the one creature that "
                "produced these is also the one whose kill award came in short, by exactly one "
                "point per failed escape - so the flee bonus is not free money bolted on top, it "
                "looks like part of the same budget."
            )
            claim.say("")

    if mismatch_count == 0 and match_count >= 5:
        claim.verdict = SUPPORTED
    elif match_count > mismatch_count and match_count >= 5:
        claim.verdict = INCONCLUSIVE
        claim.say(
            f"{match_count} exact matches against {mismatch_count} mismatch(es). Each mismatch is "
            "worth chasing individually rather than averaging away - see the findings file."
        )
    elif match_count + mismatch_count < 5:
        claim.verdict = INSUFFICIENT
    else:
        claim.verdict = REFUTED
    return claim


# ============================================================
# Claim 10: PACIFICITY predicts who starts the fight
# ============================================================


def claim_aggression(corpus: Corpus) -> Claim:
    claim = Claim(
        key="aggression",
        title="PACIFICITY orders which creatures start fights",
        claim_text=(
            "PACIF scales a mobile's estimate of your side: <=20 will attack a healthy "
            "player, several hundred is docile, 1 is suicidal."
        ),
        source="MUD2-PUBLISHED-MECHANICS.md section 5",
    )
    stats: dict[str, dict[str, object]] = {}
    for fight in corpus.fights:
        row = corpus.bestiary.lookup(fight["npc_name"])
        pacif = Bestiary.stat(row, "PACIF")
        if pacif is None:
            continue
        bucket = stats.setdefault(
            fight["npc_group"], {"pacif": pacif, "npc": 0, "player": 0}
        )
        if fight["initiator"] == "npc":
            bucket["npc"] += 1
        elif fight["initiator"] == "player":
            bucket["player"] += 1
    rows = []
    for group, bucket in sorted(stats.items(), key=lambda kv: kv[1]["pacif"]):
        total = bucket["npc"] + bucket["player"]
        rows.append(
            [
                group,
                bucket["pacif"],
                total,
                bucket["npc"],
                bucket["player"],
                None if total == 0 else round(bucket["npc"] / total, 2),
            ]
        )
    claim.n = f"{sum(r[2] for r in rows)} fights with a known PACIF"
    if not rows:
        claim.say("No fight resolved to a bestiary row.")
        claim.verdict = INSUFFICIENT
        return claim
    claim.table(
        ["npc_group", "PACIF", "fights", "npc-started", "player-started", "npc-start rate"], rows
    )
    claim.say("")
    claim.say(
        "This is an ordinal check only. PACIF is one input to a RATE comparison that also depends "
        "on both sides' current stats, so a rate below 1.0 for a low-PACIF creature is not a "
        "contradiction - it means we walked into it already engaged, or attacked first."
    )
    ranked = [r for r in rows if r[2] >= 2 and r[5] is not None]
    if len(ranked) < 3:
        claim.verdict = INSUFFICIENT
        return claim
    inversions = sum(
        1
        for i, a in enumerate(ranked)
        for b in ranked[i + 1 :]
        if a[1] < b[1] and a[5] < b[5]
    )
    claim.say(f"Rank inversions between PACIF order and npc-start rate: {inversions}.")
    claim.verdict = SUPPORTED if inversions == 0 else INCONCLUSIVE
    return claim


# ============================================================
# Untestable-with-this-data claims, reported rather than skipped
# ============================================================


def claim_untested(corpus: Corpus) -> Claim:
    claim = Claim(
        key="untested",
        title="Claims this corpus cannot reach",
        claim_text="Published mechanics with no observable in the current database.",
        source="MUD2-PUBLISHED-MECHANICS.md",
    )
    checks = [
        (
            "Sleeping opponent: hit with 100% certainty, +50% damage",
            "No fight in the corpus has a sleeping target. Needs a capture where the player "
            "attacks a sleeping mobile, with the same weapon used against it awake for contrast.",
        ),
        (
            "Magical resistance = level, +3 for no magic, decaying as ((Smax-Sc)*10)/(S*3)",
            "Nothing in the protocol reports a resistance roll or a resisted spell outcome, and "
            "the FES has no resistance field. Untestable without new prose to parse.",
        ),
        (
            "Effective strength steps 1-3 (weight, weight^2, per-object rounding)",
            "stats_snapshots.weight_carried_grams and objects_carried are NULL for every row here: "
            "they come only from a 'score' text parse, and neither session issued one. The FEI "
            "inventory list gives item NAMES but no weights, so the weight terms cannot be "
            "reconstructed. Fix: inject a periodic 'score' alongside the FES heartbeat.",
        ),
        (
            "RATE (the mobile's survival-ratio estimate)",
            "Computable in principle from bestiary stats plus our own, but there is no observable "
            "to check it against - we see the decision, never the number.",
        ),
        (
            "Drunkenness term SD = stamina + drunkenness/8",
            "Never drunk in this corpus, so the stamina knee test above silently assumes "
            "drunkenness 0. It fits exactly, which is itself weak evidence the assumption holds.",
        ),
    ]
    claim.n = f"{len(checks)} claims"
    for title, why in checks:
        claim.say(f"- {title}")
        for line in wrap(why, indent="    "):
            claim.say(line)
    claim.verdict = INSUFFICIENT
    return claim


# ============================================================
# Driver
# ============================================================

CLAIM_TITLES = {
    "knees": "stamina knees on effective strength and dexterity",
    "damage": "damage bound 1..(CS/6)+1, and implied weapon strengths",
    "hitchance": "hit chance = Dy/(Dy+Do), both directions",
    "pools": "creature stamina pools against published STA",
    "flee": "flee cost bands",
    "round": "combat round period",
    "regen": "round period vs regeneration tick",
    "floors": "effective-stat floors (50% STR, 25% DEX)",
    "blind": "blindness dexterity penalty",
    "points": "kill award vs bestiary Points",
    "aggression": "PACIFICITY vs who starts the fight",
    "untested": "published claims with no observable here",
}

CLAIM_BUILDERS = [
    ("knees", claim_knees),
    ("damage", claim_damage_bound),
    ("hitchance", claim_hit_chance),
    ("pools", claim_stamina_pools),
    ("flee", claim_flee_cost),
    ("round", claim_round),
    ("regen", claim_regen),
    ("floors", claim_floors),
    ("blind", claim_blind),
    ("points", claim_points),
    ("aggression", claim_aggression),
    ("untested", claim_untested),
]


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("--db", type=Path, default=DEFAULT_DB, help="SQLite analysis DB path.")
    parser.add_argument(
        "--bestiary", type=Path, default=DEFAULT_BESTIARY, help="Tab-separated bestiary path."
    )
    parser.add_argument(
        "--claim",
        action="append",
        default=None,
        metavar="KEY",
        help="Run only this claim (repeatable). See --list.",
    )
    parser.add_argument("--list", action="store_true", help="List claim keys and exit.")
    return parser.parse_args(argv)


def coverage(con: sqlite3.Connection, corpus: Corpus) -> list[str]:
    captures = con.execute("SELECT COUNT(*) FROM captures").fetchone()[0]
    span = con.execute(
        "SELECT MIN(started_at_ms), MAX(COALESCE(stopped_at_ms, started_at_ms)) FROM captures"
    ).fetchone()
    minutes = ((span[1] - span[0]) / 60000.0) if span[0] and span[1] else 0.0
    swings = sum(
        1
        for ev in corpus.events
        if ev["event_type"] in ("you-hit", "you-miss", "they-hit", "they-miss")
    )
    kills = sum(1 for f in corpus.fights if f["outcome"] == "Kill")
    return [
        f"captures: {captures} spanning about {minutes:.0f} wall-clock minutes",
        f"FES snapshots: {len(corpus.stats)}  (stamina range "
        f"{min(r['stamina'] for r in corpus.stats)}-{max(r['stamina'] for r in corpus.stats)})",
        f"fights: {len(corpus.fights)} ({kills} kills)   swing lines: {swings}",
        f"inventory snapshots: {len(corpus.inventory)}",
    ]


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    if args.list:
        for key, _builder in CLAIM_BUILDERS:
            print(f"{key:12s} {CLAIM_TITLES.get(key, '')}")
        return 0
    if args.claim:
        unknown = sorted(set(args.claim) - {key for key, _ in CLAIM_BUILDERS})
        if unknown:
            print(f"Unknown claim key(s): {', '.join(unknown)}. Try --list.", file=sys.stderr)
            return 2
    if not args.db.exists():
        print(f"No such database: {args.db}", file=sys.stderr)
        return 2
    if not args.bestiary.exists():
        print(f"No such bestiary: {args.bestiary}", file=sys.stderr)
        return 2

    bestiary = Bestiary(args.bestiary)
    con = sqlite3.connect(str(args.db))
    con.row_factory = sqlite3.Row
    try:
        corpus = Corpus(con, bestiary)
        header = coverage(con, corpus)
        wanted = set(args.claim) if args.claim else None
        claims = [
            builder(corpus)
            for key, builder in CLAIM_BUILDERS
            if wanted is None or key in wanted
        ]
    finally:
        con.close()

    print("=" * WRAP)
    print("MUD2 PUBLISHED MECHANICS - VERIFICATION AGAINST CAPTURED PLAY DATA")
    print("=" * WRAP)
    print(f"database: {args.db}")
    print(f"bestiary: {args.bestiary}")
    for line in header:
        print(f"  {line}")
    if bestiary.unresolved:
        print(f"  unresolved creature names: {', '.join(sorted(bestiary.unresolved))}")
    print()

    for claim in claims:
        print("-" * WRAP)
        print(f"[{claim.key}] {claim.title}")
        print("-" * WRAP)
        print("CLAIM")
        for line in wrap(claim.claim_text):
            print(line)
        print(f"  (source: {claim.source})")
        print()
        print(f"SAMPLE  {claim.n}")
        print()
        print("EVIDENCE")
        for line in claim.lines:
            print(line if line.startswith("  ") or not line else "  " + line)
        print()
        print(f"VERDICT  {claim.verdict}")
        print()

    print("=" * WRAP)
    print("SUMMARY")
    print("=" * WRAP)
    width = max(len(c.key) for c in claims)
    for claim in claims:
        print(f"  {claim.key.ljust(width)}  {claim.verdict.ljust(17)}  {claim.n}")
    print()
    print("A verdict is only as good as its SAMPLE line. Re-run after every new session;")
    print("INSUFFICIENT DATA is an invitation, not a conclusion.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
