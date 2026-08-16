#!/usr/bin/env python3
"""Measure how far Mucka's combat tick indicator lags MUD2's real swings.

Reads the reduced capture DB (reduce_combat.py's output) for swing arrival
timestamps, and optionally the raw session-rec jsonl files for a telnet echo
RTT distribution. Everything is measured against the SAME clock the client
uses: SessionCapture.RecordRx stamps a packet the instant ReadAsync returns,
before MudSession.Feed ever sees it, so a swing's timestamp_ms here is the
earliest instant the client could possibly have known about that swing.

Definitions used throughout:
  tick period P     = 2000 ms (established ground truth, MECHANICS-VERIFICATION.md)
  arrival phase     = timestamp_ms mod P
  lattice           = phi + k*P, phi estimated from a fight's own swing arrivals
  residual          = wrap(arrival - phi) in [-P/2, +P/2)
  signed prediction = predicted_tick - actual_arrival  (negative == predictor early)

ASCII output only; stdlib only; Python 3.10.
"""

from __future__ import annotations

import argparse
import json
import os
import sqlite3
import statistics
import sys
from collections import Counter, defaultdict

PERIOD = 2000.0

SWING_TYPES = ("you-hit", "you-miss", "they-hit", "they-miss")


# ---------------------------------------------------------------- small helpers


def wrap(x: float, period: float = PERIOD) -> float:
    """Fold x into [-period/2, +period/2)."""
    y = x % period
    if y >= period / 2:
        y -= period
    return y


def circ_median(phases: list[float], period: float = PERIOD) -> float:
    """Median of phases on a circle: pick the rotation that minimises spread.

    Cheap and exact enough for n <= a few hundred: try each sample as the
    reference cut, take the ordinary median of the wrapped offsets, keep the
    candidate with the smallest sum of absolute deviations.
    """
    if not phases:
        raise ValueError("no phases")
    best = None
    for ref in phases:
        offs = [wrap(p - ref, period) for p in phases]
        med = statistics.median(offs)
        cost = sum(abs(o - med) for o in offs)
        cand = (ref + med) % period
        if best is None or cost < best[0]:
            best = (cost, cand)
    return best[1]


def circ_quantile(phases: list[float], q: float, period: float = PERIOD) -> float:
    """Quantile of phases relative to their circular median."""
    c = circ_median(phases, period)
    offs = sorted(wrap(p - c, period) for p in phases)
    if len(offs) == 1:
        return c
    idx = q * (len(offs) - 1)
    lo = int(idx)
    hi = min(lo + 1, len(offs) - 1)
    frac = idx - lo
    return (c + offs[lo] * (1 - frac) + offs[hi] * frac) % period


def pct(values: list[float], q: float) -> float:
    if not values:
        return float("nan")
    s = sorted(values)
    if len(s) == 1:
        return s[0]
    idx = q * (len(s) - 1)
    lo = int(idx)
    hi = min(lo + 1, len(s) - 1)
    frac = idx - lo
    return s[lo] * (1 - frac) + s[hi] * frac


def fmt(v: float, nd: int = 1) -> str:
    if v != v:  # NaN
        return "n/a"
    return f"{v:.{nd}f}"


def histogram(values: list[float], binw: float, lo: float, hi: float) -> str:
    if not values:
        return "  (no samples)"
    bins: Counter[float] = Counter()
    for v in values:
        v = min(max(v, lo), hi - 1e-9)
        bins[(v - lo) // binw * binw + lo] += 1
    peak = max(bins.values())
    lines = []
    b = lo
    while b < hi:
        n = bins.get(b, 0)
        if n:
            bar = "#" * max(1, int(round(40.0 * n / peak)))
            lines.append(f"  [{b:>7.0f},{b + binw:>7.0f})  {n:>4}  {bar}")
        b += binw
    return "\n".join(lines)


# ---------------------------------------------------------------- data loading


class Encounter:
    def __init__(self, sid: int, capture: str, start_ms: int) -> None:
        self.sid = sid
        self.capture = capture
        self.start_ms = start_ms          # reduce_combat's fight-start line arrival
        self.swings: list[int] = []       # distinct swing PACKET arrival times, sorted
        self.first_event_ms: int | None = None   # first Begin()-triggering line
        self.first_event_type = ""
        self.targets: list[str] = []

    @property
    def span_ms(self) -> int:
        return (self.swings[-1] - self.swings[0]) if len(self.swings) > 1 else 0


def load(db_path: str) -> tuple[list[Encounter], dict[str, list[int]], sqlite3.Connection]:
    con = sqlite3.connect(db_path)
    con.row_factory = sqlite3.Row

    encs: dict[int, Encounter] = {}
    for r in con.execute(
        "select id, capture_id, start_timestamp_ms from combat_sessions "
        "order by start_timestamp_ms"
    ):
        encs[r["id"]] = Encounter(r["id"], r["capture_id"], r["start_timestamp_ms"])

    # Swings, de-duplicated to distinct arrival instants: several swing lines
    # (yours and theirs, or several pack members) share one rx packet, and the
    # client learns of all of them at once. One packet == one observed tick.
    swing_ts: dict[int, set[int]] = defaultdict(set)
    for r in con.execute(
        "select session_id, timestamp_ms, participant_name from combat_events "
        f"where event_type in ({','.join('?' * len(SWING_TYPES))}) "
        "order by timestamp_ms",
        SWING_TYPES,
    ):
        sid = r["session_id"]
        swing_ts[sid].add(r["timestamp_ms"])
        e = encs.get(sid)
        if e is not None and r["participant_name"] and r["participant_name"] not in e.targets:
            e.targets.append(r["participant_name"])

    for sid, ts in swing_ts.items():
        if sid in encs:
            encs[sid].swings = sorted(ts)

    # The line that would have flipped CombatTracker.InCombat -> true, i.e. the
    # first line in the encounter that calls Begin(): a fight-start line, or any
    # hit/miss line, whichever the parser reaches first.
    begin_types = SWING_TYPES + ("fight-start",)
    for sid, e in encs.items():
        row = con.execute(
            "select timestamp_ms, event_type from combat_events where session_id=? "
            f"and event_type in ({','.join('?' * len(begin_types))}) "
            "order by timestamp_ms, seq_index limit 1",
            (sid, *begin_types),
        ).fetchone()
        if row:
            e.first_event_ms = row["timestamp_ms"]
            e.first_event_type = row["event_type"]

    per_capture: dict[str, list[int]] = defaultdict(list)
    for e in encs.values():
        per_capture[e.capture].extend(e.swings)
    for k in per_capture:
        per_capture[k] = sorted(set(per_capture[k]))

    return [encs[k] for k in sorted(encs, key=lambda i: encs[i].start_ms)], per_capture, con


# ---------------------------------------------------------------- measurements


def q1_anchor_residual(encs: list[Encounter], pipeline_ms: float, min_swings: int) -> None:
    print("=" * 78)
    print("Q1  Client anchor vs the tick lattice implied by the fight's own swings")
    print("=" * 78)
    print(f"pipeline delay added to the anchor: +{fmt(pipeline_ms)} ms "
          "(decode + tracker + MainThread dispatch; not observable in the capture)")
    print()
    print("  enc  n   span_s  phi_hat  |res|med  res_p95  first-line     "
          "anchor_err  swing1_err")
    print("  ---  --  ------  -------  --------  -------  -------------  "
          "----------  ----------")
    anchor_errs: list[float] = []
    swing1_errs: list[float] = []
    by_kind: dict[str, list[float]] = defaultdict(list)
    used = 0
    for e in encs:
        if len(e.swings) < min_swings or e.first_event_ms is None:
            continue
        used += 1
        phases = [t % PERIOD for t in e.swings]
        phi = circ_median(phases)
        res = [abs(wrap(t - phi)) for t in e.swings]
        anchor = e.first_event_ms + pipeline_ms
        a_err = wrap(anchor - phi)
        s_err = wrap(e.swings[0] - phi)
        anchor_errs.append(a_err)
        swing1_errs.append(s_err)
        by_kind[e.first_event_type].append(a_err)
        print(f"  {e.sid:>3}  {len(e.swings):>2}  {e.span_ms / 1000:>6.1f}  "
              f"{phi:>7.0f}  {pct(res, 0.5):>8.0f}  {pct(res, 0.95):>7.0f}  "
              f"{e.first_event_type:<13}  {a_err:>+10.0f}  {s_err:>+10.0f}")

    print()
    print(f"  encounters used: {used} of {len(encs)} (>= {min_swings} distinct swing arrivals)")
    print()
    print("  ANCHOR ERROR (current behaviour: UtcNow at the first Live refresh of the fight)")
    print(f"    n={len(anchor_errs)}  median {fmt(statistics.median(anchor_errs))} ms  "
          f"mean {fmt(statistics.fmean(anchor_errs))} ms")
    print(f"    |err|: median {fmt(pct([abs(x) for x in anchor_errs], 0.5))}  "
          f"p90 {fmt(pct([abs(x) for x in anchor_errs], 0.9))}  "
          f"max {fmt(max(abs(x) for x in anchor_errs))}")
    print(f"    range {fmt(min(anchor_errs))} .. {fmt(max(anchor_errs))} ms")
    print("    (a positive error means the bar empties LATE relative to the swing;")
    print("     negative means it empties early. Both are visible; neither is intended.)")
    print()
    print("  by kind of first line:")
    for kind, vals in sorted(by_kind.items(), key=lambda kv: -len(kv[1])):
        print(f"    {kind:<14} n={len(vals):<3} median {fmt(statistics.median(vals)):>8}  "
              f"|err| med {fmt(pct([abs(x) for x in vals], 0.5)):>7}  "
              f"max |err| {fmt(max(abs(x) for x in vals)):>7}")
    print()
    print("  ANCHOR ERROR IF ANCHORED ON THE FIRST SWING LINE INSTEAD")
    print(f"    n={len(swing1_errs)}  median {fmt(statistics.median(swing1_errs))} ms  "
          f"|err| med {fmt(pct([abs(x) for x in swing1_errs], 0.5))}  "
          f"max {fmt(max(abs(x) for x in swing1_errs))}")
    print()
    print("  distribution of anchor error, 100 ms bins:")
    print(histogram(anchor_errs, 100.0, -1000.0, 1000.0))
    print()
    print("  AS THE PLAYER EXPERIENCES IT: the bar empties every 2000 ms, so an anchor")
    print("  error of -600 ms is not 'early' - the bar's next empty is 1400 ms AFTER the")
    print("  swing. Lag-to-next-empty = anchor_err mod 2000, always in [0, 2000):")
    lag = [x % PERIOD for x in anchor_errs]
    print(f"    n={len(lag)}  median {fmt(statistics.median(lag))} ms  "
          f"mean {fmt(statistics.fmean(lag))} ms  "
          f"p25 {fmt(pct(lag, 0.25))}  p75 {fmt(pct(lag, 0.75))}")
    for thr in (100, 200, 300, 500, 1000):
        n = sum(1 for x in lag if x > thr)
        print(f"    fights whose bar empties more than {thr:>4} ms after the swing: "
              f"{n:>2}/{len(lag)} ({100.0 * n / len(lag):.0f}%)")
    print()


def q2_stability(encs: list[Encounter], per_capture: dict[str, list[int]],
                 min_swings: int) -> None:
    print("=" * 78)
    print("Q2  Arrival-phase stability: jitter vs bias, within a fight and across a session")
    print("=" * 78)
    print("  enc  n   span_s  phi_hat  p5   p50  p95  spread  1st-half  2nd-half  drift")
    print("  ---  --  ------  -------  ---  ---  ---  ------  --------  --------  -----")
    all_res: list[float] = []
    drifts: list[float] = []
    for e in encs:
        if len(e.swings) < min_swings:
            continue
        phases = [t % PERIOD for t in e.swings]
        phi = circ_median(phases)
        res = [wrap(t - phi) for t in e.swings]
        all_res.extend(res)
        half = len(res) // 2
        h1 = statistics.median(res[:half]) if half else float("nan")
        h2 = statistics.median(res[half:]) if half else float("nan")
        drift = (h2 - h1) if half else float("nan")
        if half:
            drifts.append(drift)
        print(f"  {e.sid:>3}  {len(e.swings):>2}  {e.span_ms / 1000:>6.1f}  {phi:>7.0f}  "
              f"{pct(res, 0.05):>+4.0f} {pct(res, 0.5):>+4.0f} {pct(res, 0.95):>+4.0f}  "
              f"{pct(res, 0.95) - pct(res, 0.05):>6.0f}  {h1:>+8.0f}  {h2:>+8.0f}  "
              f"{drift:>+5.0f}")

    print()
    print(f"  pooled within-fight residuals: n={len(all_res)}")
    print(f"    p5 {fmt(pct(all_res, 0.05))}  p25 {fmt(pct(all_res, 0.25))}  "
          f"p50 {fmt(pct(all_res, 0.5))}  p75 {fmt(pct(all_res, 0.75))}  "
          f"p95 {fmt(pct(all_res, 0.95))}  p99 {fmt(pct(all_res, 0.99))}  "
          f"max {fmt(max(all_res))}")
    print(f"    stdev {fmt(statistics.pstdev(all_res))} ms, "
          f"median abs dev {fmt(statistics.median([abs(x - statistics.median(all_res)) for x in all_res]))} ms")
    print(f"    within-fight drift (2nd-half median minus 1st-half median), n={len(drifts)}: "
          f"median {fmt(statistics.median(drifts))}  "
          f"p90 |drift| {fmt(pct([abs(d) for d in drifts], 0.9))}  "
          f"max |drift| {fmt(max(abs(d) for d in drifts))}")
    print()
    print("  pooled residual histogram, 20 ms bins:")
    print(histogram(all_res, 20.0, -200.0, 400.0))
    print()
    print("  SESSION-WIDE phase lock (all swing arrivals in a capture, one lattice):")
    for cap, ts in per_capture.items():
        phases = [t % PERIOD for t in ts]
        phi = circ_median(phases)
        res = [wrap(t - phi) for t in ts]
        span = (ts[-1] - ts[0]) / 1000.0
        inbin = Counter(int(p // 20) for p in phases).most_common(1)[0][1]
        print(f"    {cap[:12]}  n={len(ts):<4} span {span:>7.1f}s  phi={phi:>4.0f}  "
              f"res p5..p95 {pct(res, 0.05):>+5.0f}..{pct(res, 0.95):>+5.0f}  "
              f"|res| med {pct([abs(x) for x in res], 0.5):>5.1f}  "
              f"max {max(abs(x) for x in res):>5.0f}  "
              f"single 20ms bin holds {100.0 * inbin / len(ts):.0f}%")
        # is the lattice stable end to end? compare first and last thirds
        third = max(1, len(ts) // 3)
        print(f"      first third median res {statistics.median(res[:third]):>+6.1f}  "
              f"last third {statistics.median(res[-third:]):>+6.1f}  "
              f"=> drift over {span:.0f}s = {statistics.median(res[-third:]) - statistics.median(res[:third]):+.1f} ms")
    print()


def _predict(phi: float, ref: float, target: float) -> float:
    """Nearest lattice point (phase phi, period P, anchored anywhere) to target."""
    k = round((target - phi) / PERIOD)
    return phi + k * PERIOD


def q3_candidates(encs: list[Encounter], con: sqlite3.Connection,
                  pipeline_ms: float, min_swings: int) -> None:
    print("=" * 78)
    print("Q3  Candidate anchors, scored as one-step-ahead predictors of the next swing")
    print("=" * 78)
    print("For every swing after the first in each fight, each estimator predicts that")
    print("swing's arrival using only information available BEFORE it. Signed error =")
    print("predicted - actual; negative means the estimator fires EARLY.")
    print()

    # Warm-started global phase, learned across the whole capture in time order.
    global_phases: dict[str, list[float]] = defaultdict(list)

    results: dict[str, list[float]] = defaultdict(list)
    per_index: dict[str, dict[int, list[float]]] = defaultdict(lambda: defaultdict(list))

    for e in encs:
        if len(e.swings) < min_swings or e.first_event_ms is None:
            continue
        anchor_phase = (e.first_event_ms + pipeline_ms) % PERIOD
        first_swing_phase = e.swings[0] % PERIOD
        seen: list[float] = []
        for i, t in enumerate(e.swings):
            phases_before = list(seen)
            gp = list(global_phases[e.capture])
            if i > 0:
                cands = {
                    "A current (fight-start anchor)": anchor_phase,
                    "B first swing of fight": first_swing_phase,
                }
                if phases_before:
                    cands["C running median (this fight)"] = circ_median(phases_before)
                    cands["D running p15 (this fight)"] = circ_quantile(phases_before, 0.15)
                if gp:
                    cands["E session median (all fights so far)"] = circ_median(gp)
                    cands["F session p15 (all fights so far)"] = circ_quantile(gp, 0.15)
                if phases_before and gp:
                    blend_n = len(phases_before)
                    w = min(1.0, blend_n / 8.0)
                    m_local = circ_median(phases_before)
                    m_glob = circ_median(gp)
                    cands["G session->fight blend (n/8 weight)"] = (
                        m_glob + w * wrap(m_local - m_glob)) % PERIOD
                for name, phi in cands.items():
                    err = _predict(phi, phi, t) - t
                    results[name].append(err)
                    per_index[name][min(i, 10)].append(err)
            seen.append(t % PERIOD)
            global_phases[e.capture].append(t % PERIOD)

    print("  estimator                              n     med    |err|med  |err|p90  "
          "|err|max  early%")
    print("  -------------------------------------  ----  -----  --------  --------  "
          "--------  ------")
    for name in sorted(results, key=lambda n: pct([abs(x) for x in results[n]], 0.5)):
        v = results[name]
        av = [abs(x) for x in v]
        early = 100.0 * sum(1 for x in v if x < 0) / len(v)
        print(f"  {name:<37}  {len(v):>4}  {statistics.median(v):>+5.0f}  "
              f"{pct(av, 0.5):>8.1f}  {pct(av, 0.9):>8.1f}  {max(av):>8.0f}  {early:>5.0f}%")

    print()
    print("  does it improve as a fight goes on? |err| median by swing index within fight")
    print("  (index 1 = second swing of the fight; 10+ pooled)")
    idxs = sorted({i for n in per_index for i in per_index[n]})
    hdr = "  estimator                              " + "".join(f"{i:>6}" for i in idxs)
    print(hdr)
    for name in sorted(per_index, key=lambda n: pct([abs(x) for x in results[n]], 0.5)):
        cells = ""
        for i in idxs:
            v = per_index[name].get(i)
            cells += f"{pct([abs(x) for x in v], 0.5):>6.0f}" if v else "     -"
        print(f"  {name:<37}" + cells)
    print()

    # --- FES heartbeat and regen ticks as alternative phase sources -----------
    print("  OTHER PHASE SOURCES")
    fes = [r[0] for r in con.execute(
        "select timestamp_ms from raw_events where event_type='fes' order by timestamp_ms")]
    probes = con.execute(
        "select count(*) from raw_events where is_client_probe=1").fetchone()[0]
    if fes:
        ph = [t % PERIOD for t in fes]
        phi = circ_median(ph)
        res = [wrap(t - phi) for t in fes]
        gaps = [fes[i + 1] - fes[i] for i in range(len(fes) - 1)]
        print(f"    FES heartbeat: n={len(fes)}  client probes in capture={probes}")
        print(f"      inter-arrival: median {fmt(statistics.median(gaps))} ms  "
              f"p5 {fmt(pct(gaps, 0.05))}  p95 {fmt(pct(gaps, 0.95))}")
        print(f"      phase mod 2000: |res| median {fmt(pct([abs(x) for x in res], 0.5))} ms  "
              f"p5..p95 {pct(res, 0.05):+.0f}..{pct(res, 0.95):+.0f}  "
              f"=> {'phase-locked' if pct([abs(x) for x in res], 0.5) < 60 else 'NOT phase-locked (uniform)'}")
    st = list(con.execute(
        "select capture_id, timestamp_ms, stamina from stats_snapshots "
        "where stamina is not null order by capture_id, timestamp_ms"))
    regen: list[int] = []
    prev_cap, prev_sta = None, None
    for cap, ts, sta in st:
        if cap == prev_cap and prev_sta is not None and sta > prev_sta:
            regen.append(ts)
        prev_cap, prev_sta = cap, sta
    if regen:
        ph = [t % PERIOD for t in regen]
        phi = circ_median(ph)
        res = [wrap(t - phi) for t in regen]
        print(f"    regeneration ticks (first FES poll showing stamina up): n={len(regen)}")
        print(f"      phase mod 2000: |res| median {fmt(pct([abs(x) for x in res], 0.5))} ms  "
              f"p5..p95 {pct(res, 0.05):+.0f}..{pct(res, 0.95):+.0f}  "
              f"=> {'phase-locked' if pct([abs(x) for x in res], 0.5) < 60 else 'NOT phase-locked (poll-quantised)'}")
    print()


def q4_transit(paths: list[str], all_res: list[float]) -> None:
    print("=" * 78)
    print("Q4  Transit jitter: is 'early' free?")
    print("=" * 78)
    rtts: list[float] = []
    turnarounds: list[float] = []
    for p in paths:
        if not os.path.exists(p):
            print(f"  (missing capture, skipped: {p})")
            continue
        recs = []
        with open(p, encoding="utf-8-sig", errors="replace") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    recs.append(json.loads(line))
                except ValueError:
                    continue
        # A telnet line echo: client sends "<text>\r\n", server echoes exactly
        # that back. First rx whose payload STARTS with the sent text is the echo,
        # so tx->that rx is a pure round trip with no server think time in it.
        #
        # Repeated identical commands are skipped: walking sends "n\r\n" several
        # times in a row, and then the Nth send can be matched against the echo
        # of an earlier one, which fabricates an impossibly short RTT.
        n_local = 0
        last_sent: dict[str, float] = {}
        for i, (ts, d, payload) in enumerate(recs):
            if d != "tx" or not payload.endswith("\r\n") or payload.startswith("\x1b"):
                continue
            prev = last_sent.get(payload)
            last_sent[payload] = ts
            if prev is not None and ts - prev < 3000:
                continue
            for ts2, d2, p2 in recs[i + 1:i + 6]:
                if d2 != "rx":
                    continue
                if p2.startswith(payload):
                    rtts.append(ts2 - ts)
                    n_local += 1
                break
        # tx -> first rx that is NOT the pure echo: echo RTT plus whatever the
        # server spent deciding and flushing. The difference between the two
        # bounds how far before the TEXT the server-side event actually happened.
        n_resp = 0
        last_sent2: dict[str, float] = {}
        for i, (ts, d, payload) in enumerate(recs):
            if d != "tx" or not payload.endswith("\r\n") or payload.startswith("\x1b"):
                continue
            prev = last_sent2.get(payload)
            last_sent2[payload] = ts
            if prev is not None and ts - prev < 3000:
                continue
            saw_echo = False
            for ts2, d2, p2 in recs[i + 1:i + 8]:
                if d2 == "tx":
                    break
                if d2 != "rx":
                    continue
                if not saw_echo and p2.startswith(payload):
                    saw_echo = True
                    if len(p2) > len(payload) + 4:   # echo and reply in one packet
                        turnarounds.append(ts2 - ts)
                        n_resp += 1
                        break
                    continue
                if saw_echo:
                    turnarounds.append(ts2 - ts)
                    n_resp += 1
                    break
        print(f"  {os.path.basename(p)}: {len(recs)} records, {n_local} echo round trips, "
              f"{n_resp} command turnarounds")
    if rtts:
        print()
        print(f"  telnet echo RTT: n={len(rtts)}  min {fmt(min(rtts))}  "
              f"p5 {fmt(pct(rtts, 0.05))}  p50 {fmt(pct(rtts, 0.5))}  "
              f"p95 {fmt(pct(rtts, 0.95))}  max {fmt(max(rtts))} ms")
        print(f"  implied one-way transit ~ RTT/2: median {fmt(pct(rtts, 0.5) / 2)} ms, "
              f"p95 {fmt(pct(rtts, 0.95) / 2)} ms")
        print(f"  RTT jitter (p95 - p5): {fmt(pct(rtts, 0.95) - pct(rtts, 0.05))} ms  "
              f"(p99-p1 {fmt(pct(rtts, 0.99) - pct(rtts, 0.01))} ms)")
        print()
        print("  echo RTT histogram, 20 ms bins:")
        print(histogram(rtts, 20.0, 0.0, 600.0))
    if turnarounds:
        print()
        print(f"  command turnaround (tx -> first rx carrying the game's REPLY, not the echo):")
        print(f"    n={len(turnarounds)}  p5 {fmt(pct(turnarounds, 0.05))}  "
              f"p50 {fmt(pct(turnarounds, 0.5))}  p95 {fmt(pct(turnarounds, 0.95))} ms")
        print(f"    minus echo RTT ({fmt(pct(rtts, 0.5))} ms) => server think+flush "
              f"~ {fmt(pct(turnarounds, 0.5) - pct(rtts, 0.5))} ms (median)")
        print("  turnaround histogram, 20 ms bins:")
        print(histogram(turnarounds, 20.0, 0.0, 800.0))
    print()
    if all_res:
        base = min(all_res)
        excess = [x - base for x in all_res]
        print("  RECEIVE JITTER SEEN BY THE TICK (swing arrival minus the earliest arrival")
        print("  observed on the lattice; this is the one-sided delay tail that matters):")
        print(f"    n={len(excess)}  p50 {fmt(pct(excess, 0.5))}  p75 {fmt(pct(excess, 0.75))}  "
              f"p90 {fmt(pct(excess, 0.9))}  p95 {fmt(pct(excess, 0.95))}  "
              f"p99 {fmt(pct(excess, 0.99))}  max {fmt(max(excess))} ms")
        for thr in (0, 25, 50, 75, 100, 150, 200):
            frac = 100.0 * sum(1 for x in excess if x <= thr) / len(excess)
            print(f"    within {thr:>3} ms of the earliest arrival: {frac:>5.1f}%")
    print()


def _margins(encs: list[Encounter], min_swings: int, mode: str,
             quantile: float) -> list[float]:
    """Warning margins (actual arrival - predicted tick) for a given estimator."""
    out: list[float] = []
    pool: dict[str, list[float]] = defaultdict(list)
    for e in encs:
        if len(e.swings) < min_swings:
            continue
        local: list[float] = []
        for t in e.swings:
            src = pool[e.capture] if mode.startswith("session") else local
            if src:
                phi = (circ_quantile(src, quantile) if mode.endswith("p")
                       else circ_median(src))
                out.append(t - _predict(phi, phi, t))
            local.append(t % PERIOD)
            pool[e.capture].append(t % PERIOD)
    return out


def q5_lead(encs: list[Encounter], min_swings: int, quantile: float) -> None:
    print("=" * 78)
    print("Q5  Choosing a lead time against the numbers")
    print("=" * 78)
    print("The indicator reaches empty at predicted_tick - LEAD. 'warning margin' =")
    print("actual arrival minus that moment: positive means the player was warned")
    print("before the swing text landed; negative means the bar reported the past.")
    print()
    for mode, label in (("fight-median", "C: running median, reset each fight"),
                        (f"session-p", f"F: session-persistent p{quantile * 100:.0f} "
                                       "(recommended)")):
        margins = _margins(encs, min_swings, mode, quantile)
        print(f"  estimator {label}   (n={len(margins)})")
        print("  lead   warned-before-text  median margin  p5 margin  p1 margin  "
              "max early  late count")
        print("  -----  -----------------  -------------  ---------  ---------  "
              "---------  ----------")
        for lead in (0, 25, 50, 75, 100, 125, 150, 200, 250):
            m = [x + lead for x in margins]
            good = 100.0 * sum(1 for x in m if x >= 0) / len(m)
            late = sum(1 for x in m if x < 0)
            print(f"  {lead:>5}  {good:>16.1f}%  {statistics.median(m):>+13.0f}  "
                  f"{pct(m, 0.05):>+9.0f}  {pct(m, 0.01):>+9.0f}  "
                  f"{max(m):>+9.0f}  {late:>10}")
        print()
    print("  'max early' is how far ahead of the swing text the bar would empty in the")
    print("  worst observed case - the cost of the lead, paid as a visibly premature bar.")
    print()


def q6_slew(encs: list[Encounter], min_swings: int, quantile: float, warm: bool) -> None:
    print("=" * 78)
    print("Q6  Sizing the re-anchor: how far does the phase estimate move per swing?")
    print("=" * 78)
    print(f"Estimator: session-persistent p{quantile * 100:.0f} of every swing arrival phase, "
          f"{'warm-started across fights' if warm else 'reset each fight'}.")
    print("A correction is applied by slewing the animation's phase, so what matters is")
    print("the size of the per-swing correction, not the absolute error.")
    print()
    corrections: list[float] = []
    first_fight_of_capture: dict[str, int] = {}
    for e in encs:
        first_fight_of_capture.setdefault(e.capture, e.sid)
    pool: dict[str, list[float]] = defaultdict(list)
    big: list[tuple[int, int, float]] = []
    for e in encs:
        if len(e.swings) < min_swings:
            continue
        local: list[float] = []
        prev_phi: float | None = None
        for i, t in enumerate(e.swings):
            src = pool[e.capture] if warm else local
            if src:
                phi = circ_quantile(src, quantile)
                if prev_phi is not None:
                    d = wrap(phi - prev_phi)
                    corrections.append(d)
                    if abs(d) > 20.0:
                        big.append((e.sid, i, d))
                prev_phi = phi
            local.append(t % PERIOD)
            pool[e.capture].append(t % PERIOD)
    ac = [abs(x) for x in corrections]
    print(f"  per-swing phase corrections: n={len(corrections)}  "
          f"median |d| {fmt(pct(ac, 0.5))}  p90 {fmt(pct(ac, 0.9))}  "
          f"p99 {fmt(pct(ac, 0.99))}  max {fmt(max(ac))} ms")
    for thr in (1, 2, 5, 10, 20, 50):
        frac = 100.0 * sum(1 for x in ac if x <= thr) / len(ac)
        print(f"    |correction| <= {thr:>2} ms: {frac:>5.1f}% of swings")
    print(f"  corrections larger than 20 ms: {len(big)} of {len(corrections)}")
    for sid, i, d in big[:12]:
        first = " (first fight of this capture)" if sid in first_fight_of_capture.values() else ""
        print(f"    enc {sid:>2} swing #{i}  {d:+.0f} ms{first}")
    print()
    print("  A slew of 8 ms per 2000 ms tick (0.4% rate change) absorbs any correction")
    print("  at or under 8 ms within one tick and is far below the ~1 Hz / few-percent")
    print("  threshold at which a constant-rate bar's speed change is noticeable.")
    print()


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--db", required=True, help="reduce_combat.py output DB")
    ap.add_argument("--capture", action="append", default=[],
                    help="raw session-rec jsonl (repeatable) for echo RTT")
    ap.add_argument("--pipeline-ms", type=float, default=0.0,
                    help="ms to add to the client anchor for decode + tracker + "
                         "MainThread dispatch (default 0: report the floor)")
    ap.add_argument("--min-swings", type=int, default=3,
                    help="minimum distinct swing arrivals for a fight to be scored")
    ap.add_argument("--quantile", type=float, default=0.15,
                    help="low quantile used by the recommended estimator (default 0.15)")
    args = ap.parse_args()

    encs, per_capture, con = load(args.db)
    print(f"DB: {args.db}")
    print(f"encounters: {len(encs)}   "
          f"distinct swing arrivals: {sum(len(e.swings) for e in encs)}   "
          f"captures: {len(per_capture)}")
    print()

    q1_anchor_residual(encs, args.pipeline_ms, args.min_swings)
    q2_stability(encs, per_capture, args.min_swings)
    q3_candidates(encs, con, args.pipeline_ms, args.min_swings)

    pooled: list[float] = []
    for e in encs:
        if len(e.swings) < args.min_swings:
            continue
        phi = circ_median([t % PERIOD for t in e.swings])
        pooled.extend(wrap(t - phi) for t in e.swings)
    q4_transit(args.capture, pooled)
    q5_lead(encs, args.min_swings, args.quantile)
    q6_slew(encs, args.min_swings, args.quantile, warm=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
