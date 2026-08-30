"""Does the session-scoped phase estimator actually beat the old single-swing anchor?

Replays the real clog corpus through both and scores each against the session-wide best-fit lattice
that `sessionlattice.py` establishes as the reference.

The estimator here is a faithful PORT of Mucka.Core.TickPhase - exponentially-forgetting circular
mean of folded residuals, with re-basing - and not the shipping code itself. Kept deliberately
short so it can be eyeballed against that class; if the two ever disagree, the C# is authoritative
and this file is wrong.

Scored at the moment each encounter's phase would first be published, and again at the end of the
encounter, because those answer different questions: the first is "was the bar right when the fight
started", the second is "did it converge before the fight ended".
"""
import collections
import glob
import io
import json
import math
import os
import re
import statistics

CLOGS = os.path.expanduser("~/.mucka/clogs")
TICK = 2000.0
SWINGS = {"Hit", "Miss", "HitByNpc", "MissByNpc"}
NAME = re.compile(r"clog\.(\d{8})-(\d{6})")

# Mirrors Mucka.Core.TickPhase.
DECAY = 0.995
REBASE_MS = 15.0
MIN_SAMPLES = 3


class TickPhase:
    def __init__(self):
        self.ref = None
        self.cos = self.sin = self.weight = 0.0
        self.n = 0

    @property
    def anchor(self):
        return self.ref if self.n >= MIN_SAMPLES else None

    def observe(self, t):
        if self.n == 0:
            self.ref, self.cos, self.sin, self.weight, self.n = t, 1.0, 0.0, 1.0, 1
            return
        r = fold(t - self.ref)
        th = r / TICK * 2 * math.pi
        self.cos = self.cos * DECAY + math.cos(th)
        self.sin = self.sin * DECAY + math.sin(th)
        self.weight = self.weight * DECAY + 1.0
        self.n += 1
        off = math.atan2(self.sin, self.cos) / (2 * math.pi) * TICK
        if abs(off) < REBASE_MS:
            return
        self.ref += off
        c, s = math.cos(-off / TICK * 2 * math.pi), math.sin(-off / TICK * 2 * math.pi)
        self.cos, self.sin = self.cos * c - self.sin * s, self.cos * s + self.sin * c


def fold(ms):
    r = ms % TICK
    return r - TICK if r > TICK / 2 else r


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


def best_phase(times):
    best, cost = 0.0, float("inf")
    for step, span, base in ((1.0, TICK, 0.0), (0.05, 4.0, None)):
        b = best if base is None else 0.0
        p = b - span / 2
        while p < b + span / 2:
            c = sum(abs(fold(t - p)) for t in times)
            if c < cost:
                cost, best = c, p
            p += step
    return best % TICK


# Group clogs into play sessions the way sessionlattice.py does.
sessions = collections.defaultdict(list)
files = sorted(glob.glob(os.path.join(CLOGS, "clog.*.jsonl")), key=os.path.getmtime)
key, last = None, None
for f in files:
    if not NAME.match(os.path.basename(f)):
        continue
    mt = os.path.getmtime(f)
    if last is None or mt - last > 1200:
        key = os.path.basename(f)
    last = mt
    sessions[key].append(f)

old_err, new_first, new_end = [], [], []

for paths in sessions.values():
    per_file = [(p, swings(p)) for p in paths]
    pooled = [t for _, v in per_file for t in v]
    if len(pooled) < 40:
        continue
    truth = best_phase(pooled)

    # The estimator is session-scoped: one instance across every encounter in the session, never reset.
    phase = TickPhase()
    for _p, times in per_file:
        if len(times) < 4:
            for t in times:
                phase.observe(t)
            continue

        # OLD: the encounter's own first swing, discarded and re-derived every fight.
        old_err.append(abs(fold(times[0] - truth)))

        # NEW: whatever the session estimate says at the moment this encounter's first swing lands,
        # then again once the encounter has finished feeding it.
        phase.observe(times[0])
        a = phase.anchor
        if a is not None:
            new_first.append(abs(fold(a - truth)))
        for t in times[1:]:
            phase.observe(t)
        a = phase.anchor
        if a is not None:
            new_end.append(abs(fold(a - truth)))


def show(label, v):
    if not v:
        print(f"  {label:34} no data")
        return
    v = sorted(v)
    pct = lambda q: v[min(int(q * len(v)), len(v) - 1)]
    within = lambda ms: 100.0 * sum(1 for a in v if a <= ms) / len(v)
    print(f"  {label:34} n={len(v):4d}  median={statistics.median(v):6.1f}  p90={pct(.90):6.1f}  "
          f"p99={pct(.99):6.1f}  max={pct(1.0):6.1f}   >150ms:{100-within(150):5.1f}%  >500ms:{100-within(500):5.1f}%")


print("anchor error against each session's own best-fit lattice, in ms:\n")
show("OLD: encounter's first swing", old_err)
show("NEW: estimate at fight start", new_first)
show("NEW: estimate at fight end", new_end)
