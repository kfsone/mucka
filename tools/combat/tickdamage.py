"""What one MUD2 tick actually cost, in the biggest fights on record.

The flee pill tests stamina against a WORST-CASE TICK: the sum of what each live opponent hits for
when it hits. That number is only useful if it resembles what a bad tick really does, so this
measures the real thing from the clog corpus and puts the two side by side.

Incoming blows carry exact post-hit stamina ("The rat9 hits you (97/100)"), so per-blow damage is
the drop from the previous reading - no estimation. Blows are grouped into 2.000 s tick buckets,
since MUD2 resolves every combatant's swing on one boundary and that lump is the thing the pill is
about.

Prints, per encounter: the peak concurrency, the worst single tick actually observed, the mean
damage per landed blow, and the pill's own predicted worst-case tick if every live opponent that
tick had landed at its own observed mean.
"""
import collections
import datetime as dt
import glob
import io
import json
import os

CLOGS = os.path.expanduser("~/.mucka/clogs")
TICK_MS = 2000

PER_CREATURE_END = {"Kill", "NpcFled", "NpcFleeFailed"}
ALL_END = {"YouFled", "YouFleeFailed", "KilledByNpc", "Withdrawn"}
ENGAGES = {"FightStart", "Hit", "Miss", "HitByNpc", "MissByNpc", "NpcHealth", "NpcWeaponEquip"}


def events(path):
    """Parsed event dicts from a clog, skipping unparseable lines.

    The newest clog is being written by a LIVE session, so its tail line can be half-flushed - and
    a crash can leave the same shape behind. Dropping one truncated line at the end of a file is
    correct; the alternative is that no query can ever be run while the game is open.
    """
    for line in io.open(path, encoding="utf-8", errors="replace"):
        line = line.strip()
        if not line:
            continue
        try:
            d = json.loads(line)
        except json.JSONDecodeError:
            continue
        if d.get("type") == "event":
            yield d


def analyse(path):
    live, peak = set(), 0
    sta = None
    blows = []                       # (ts, npc, damage)
    per_npc = collections.defaultdict(list)
    live_at = []                     # (ts, frozenset) sampled on every engage

    for d in events(path):
        kind, npc, ts = d["kind"], d.get("npc"), d.get("ts")

        if kind == "HitByNpc" and d.get("rangeLow") is not None:
            now = d["rangeLow"]
            if sta is not None and now < sta:
                dmg = sta - now
                blows.append((ts, npc, dmg))
                per_npc[npc].append(dmg)
            sta = now

        if kind in ALL_END:
            live.clear()
            continue
        if npc is None:
            continue
        if kind in PER_CREATURE_END or kind == "FightEndOther":
            live.discard(npc)
            continue
        if kind in ENGAGES:
            live.add(npc)
            peak = max(peak, len(live))
            live_at.append((ts, frozenset(live)))

    if not blows:
        return None

    buckets = collections.Counter()
    for ts, _npc, dmg in blows:
        buckets[ts // TICK_MS] += dmg
    worst_tick = max(buckets.values())

    means = {n: sum(v) / len(v) for n, v in per_npc.items()}
    # The pill's figure, evaluated at the moment of peak concurrency: every live opponent lands at
    # its own observed mean. Opponents with no blow on record in this fight are skipped rather than
    # given the 20-point assumption, so this is the MEASURED-ONLY comparison.
    predicted = 0.0
    for ts, roster in live_at:
        if len(roster) != peak:
            continue
        predicted = max(predicted, sum(means.get(n, 0.0) for n in roster))

    return dict(
        peak=peak, worst_tick=worst_tick, predicted=predicted,
        blows=len(blows), mean=sum(d for _, _, d in blows) / len(blows),
        biggest_blow=max(d for _, _, d in blows),
        when=dt.datetime.fromtimestamp(blows[0][0] / 1000).strftime("%Y-%m-%d %H:%M"),
    )


rows = []
for path in sorted(glob.glob(os.path.join(CLOGS, "clog.*.jsonl"))):
    r = analyse(path)
    if r and r["peak"] >= 4:
        r["name"] = os.path.basename(path)
        rows.append(r)

print(f"{'peak':>4} {'when':16} {'blows':>5} {'mean':>5} {'max1':>4} "
      f"{'worst REAL tick':>15} {'pill predicts':>13}")
for r in sorted(rows, key=lambda r: -r["peak"]):
    print(f"{r['peak']:>4} {r['when']:16} {r['blows']:>5} {r['mean']:>5.1f} {r['biggest_blow']:>4} "
          f"{r['worst_tick']:>15} {r['predicted']:>13.1f}")

if rows:
    allb = [r["mean"] for r in rows]
    print(f"\nencounters at 4+ concurrent: {len(rows)}")
    print(f"mean damage per landed blow across them: {sum(allb) / len(allb):.2f}")
    print(f"worst single tick anywhere in them: {max(r['worst_tick'] for r in rows)}")
    print(f"biggest single blow anywhere in them: {max(r['biggest_blow'] for r in rows)}")
