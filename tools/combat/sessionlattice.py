"""Test the anchor against a whole SESSION's lattice, not just its own encounter's.

COMBAT-RAIL-SPEC.md section 6 claims MUD2's tick is phase-locked well enough that "one lattice fits
a whole 40-minute session to ~4 ppm". If true, the honest measure of an anchor's error is against
that session-wide lattice - a 6-sample per-encounter median is too noisy to trust for the small
errors and can be dragged by a couple of late frames.

Method: group clogs into sessions by their filename timestamp (clog.YYYYMMDD-HHMMSSmmm-N), pool
every swing in the session, then brute-force the single 2000 ms phase offset that minimises total
absolute folded residual. Report how well that one lattice fits, then measure each encounter's
FIRST SWING against it - which is the actual error the bar and the click inherit.
"""
import collections
import glob
import io
import json
import os
import re
import statistics

CLOGS = os.path.expanduser("~/.mucka/clogs")
TICK_MS = 2000.0
SWINGS = {"Hit", "Miss", "HitByNpc", "MissByNpc"}
NAME = re.compile(r"clog\.(\d{8})-(\d{6})(\d{3})?-?(\d+)?\.jsonl")


def swings(path):
    out = []
    for line in io.open(path, encoding="utf-8", errors="replace"):
        line = line.strip()
        if not line:
            continue
        try:
            d = json.loads(line)
        except json.JSONDecodeError:
            continue
        if d.get("type") == "event" and d.get("kind") in SWINGS and d.get("ts") is not None:
            out.append(d["ts"])
    return out


def fold(x):
    r = x % TICK_MS
    return r - TICK_MS if r > TICK_MS / 2 else r


def best_phase(times):
    """The phase offset (0..2000) minimising total |residual|. 1 ms grid, then 0.05 ms refine."""
    best, best_cost = 0.0, float("inf")
    for step, span, centre in ((1.0, TICK_MS, 0.0), (0.05, 4.0, None)):
        base = best if centre is None else 0.0
        p = base - span / 2
        while p < base + span / 2:
            cost = sum(abs(fold(t - p)) for t in times)
            if cost < best_cost:
                best_cost, best = cost, p
            p += step
    return best % TICK_MS, best_cost / len(times)


# Group clogs into play sessions: same date, and started within 20 minutes of the previous one.
sessions = collections.defaultdict(list)
files = sorted(glob.glob(os.path.join(CLOGS, "clog.*.jsonl")), key=os.path.getmtime)
key, last = None, None
for f in files:
    m = NAME.match(os.path.basename(f))
    if not m:
        continue
    mt = os.path.getmtime(f)
    if last is None or mt - last > 1200:
        key = os.path.basename(f)
    last = mt
    sessions[key].append(f)

print(f"sessions: {len(sessions)}   clogs: {sum(len(v) for v in sessions.values())}\n")

fits, anchor_errors, bad = [], [], []
for key, paths in sessions.items():
    per_file = {p: swings(p) for p in paths}
    pooled = [t for v in per_file.values() for t in v]
    if len(pooled) < 40:
        continue
    phase, mean_abs = best_phase(pooled)
    fits.append(mean_abs)

    for p, times in per_file.items():
        if len(times) < 4:
            continue
        err = abs(fold(times[0] - phase))
        anchor_errors.append(err)
        if err > 150:
            bad.append((err, os.path.basename(p)))

print(f"one 2000 ms lattice per session, mean |residual| over all swings:")
print(f"  median across sessions: {statistics.median(fits):6.1f} ms      "
      f"worst: {max(fits):6.1f} ms   (n={len(fits)} sessions)")
print(f"  -> the session-wide lattice {'HOLDS' if statistics.median(fits) < 60 else 'DOES NOT HOLD'}, "
      f"so it is a fair reference for judging an anchor.\n")

absv = sorted(anchor_errors)
within = lambda ms: 100.0 * sum(1 for a in absv if a <= ms) / len(absv)
print(f"FIRST-SWING anchor error against the session lattice (n={len(absv)} encounters):")
print(f"  median={statistics.median(absv):6.1f} ms   p75={absv[int(.75*len(absv))]:6.1f}   "
      f"p90={absv[int(.90*len(absv))]:6.1f}   p99={absv[int(.99*len(absv))]:6.1f}   max={absv[-1]:6.1f}")
print(f"  <=25ms: {within(25):5.1f}%    <=100ms: {within(100):5.1f}%    "
      f">150ms: {100-within(150):5.1f}%   >500ms: {100-within(500):5.1f}%")

print(f"\nworst offenders (>150 ms), {len(bad)} of {len(absv)}:")
for err, name in sorted(bad, reverse=True)[:12]:
    print(f"  {err:7.1f} ms   {name}")
