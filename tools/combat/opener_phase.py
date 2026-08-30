"""Why is the first swing sometimes off-lattice? Test the keystroke-phase hypothesis.

Stored rather than run ad hoc: an earlier pass quoted this analysis in three documents while the
script lived only in a scratchpad, so the figure could not be re-derived and drifted between the
copies. See COMBAT-RAIL-SPEC.md section 6 and tools/combat/README.md.

If the first swing arrives in the same frame as the player's OWN `kill` command reply, its timestamp
is the keystroke's rather than the tick's - which is the ~1.0 s error COMBAT-RAIL-SPEC.md section 6
attributes to anchoring on the InCombat flip. Anchoring on the first swing was supposed to escape
that; this checks whether it actually does.

Splits encounters by who opened the fight (a player-initiated FightStart names the player as actor)
and by how close the first swing is to that FightStart, then reports the first-swing lattice error
for each group.
"""
import glob
import io
import json
import os
import statistics

CLOGS = os.path.expanduser("~/.mucka/clogs")
TICK_MS = 2000.0
SWINGS = {"Hit", "Miss", "HitByNpc", "MissByNpc"}


def events(path):
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
    first_start = None          # (ts, actor) of the first FightStart
    swing_ts = []
    for d in events(path):
        k = d.get("kind")
        if k == "FightStart" and first_start is None:
            first_start = (d.get("ts"), d.get("actor"))
        elif k in SWINGS and d.get("ts") is not None:
            swing_ts.append(d["ts"])

    if first_start is None or len(swing_ts) < 6:
        return None

    anchor = swing_ts[0]
    res = []
    for t in swing_ts[1:]:
        r = (t - anchor) % TICK_MS
        if r > TICK_MS / 2:
            r -= TICK_MS
        res.append(r)

    return dict(
        error=abs(statistics.median(res)),
        actor=first_start[1],
        gap=anchor - first_start[0],        # first swing minus fight start, ms
        n=len(res),
        name=os.path.basename(path),
    )


rows = [r for r in (analyse(p) for p in sorted(glob.glob(os.path.join(CLOGS, "clog.*.jsonl")))) if r]

def group(label, sel):
    picked = [r for r in rows if sel(r)]
    if not picked:
        print(f"  {label:38} none")
        return
    errs = [r["error"] for r in picked]
    over100 = sum(1 for e in errs if e > 100)
    over150 = sum(1 for e in errs if e > 150)
    # BOTH thresholds, because an earlier write-up quoted the 100 ms percentage against the 150 ms
    # baseline and the two are materially different.
    print(f"  {label:38} n={len(picked):4d}  median={statistics.median(errs):6.1f} ms  "
          f">100ms: {100.0*over100/len(picked):5.1f}%   >150ms: {100.0*over150/len(picked):5.1f}%")

print(f"encounters analysed: {len(rows)}\n")
print("first-swing lattice error, split by who opened the fight:")
group("player opened (kill command)", lambda r: r["actor"] == "Player")
group("NPC opened", lambda r: r["actor"] == "Npc")

print("\nand by how soon the first swing followed the FightStart:")
group("first swing within 100 ms of start", lambda r: abs(r["gap"]) <= 100)
group("first swing 100-1000 ms after", lambda r: 100 < r["gap"] <= 1000)
print()
group("first swing >1000 ms after", lambda r: r["gap"] > 1000)

print("\ncross-tab: player-opened AND first swing in the same frame (<=100 ms):")
group("player-opened, same frame", lambda r: r["actor"] == "Player" and abs(r["gap"]) <= 100)
group("player-opened, later swing", lambda r: r["actor"] == "Player" and r["gap"] > 100)
group("NPC-opened, same frame", lambda r: r["actor"] == "Npc" and abs(r["gap"]) <= 100)
group("NPC-opened, later swing", lambda r: r["actor"] == "Npc" and r["gap"] > 100)
