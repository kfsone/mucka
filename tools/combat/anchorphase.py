"""Is the encounter's FIRST SWING a good tick anchor?

Both instruments (the bar and the click) derive their phase from one instant: the timestamp of the
first swing event in an encounter (SidePanelViewModel.TickPhaseUtc). COMBAT-RAIL-SPEC.md section 6
justifies that choice with a measured median error of ~22 ms, against ~1.0 s for anchoring on the
line that flips InCombat. This re-measures it from the live clog corpus, because the owner reports
combat text arriving about 3/5 of the way along the bar rather than at its zero crossing.

Method: for each encounter, take the first swing's timestamp as the anchor, then express every
LATER swing as a residual against the 2000 ms lattice that anchor defines, folded into
[-1000, +1000]. If the first swing sits on the same lattice as the rest, residuals cluster on zero.
A cluster away from zero means the anchor itself is off-lattice and every later beat inherits that
error - which is what an out-of-phase bar looks like.

Also reports the same figures anchored on the SECOND and THIRD swings, so the first swing's
suitability is judged against alternatives rather than in isolation.
"""
import glob
import io
import json
import os
import statistics
import sys

CLOGS = os.path.expanduser("~/.mucka/clogs")
TICK_MS = 2000.0
SWINGS = {"Hit", "Miss", "HitByNpc", "MissByNpc"}


def swings(path):
    """Swing timestamps in order, skipping unparseable lines (the newest clog is being written)."""
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


def residuals(times, anchor_index):
    """Folded lattice residuals of every swing after the anchor, in ms."""
    if len(times) <= anchor_index + 1:
        return []
    anchor = times[anchor_index]
    out = []
    for t in times[anchor_index + 1:]:
        r = (t - anchor) % TICK_MS
        if r > TICK_MS / 2:
            r -= TICK_MS
        out.append(r)
    return out


def summarise(label, all_res):
    if not all_res:
        print(f"  {label:24} no data")
        return
    absr = [abs(r) for r in all_res]
    within = lambda ms: 100.0 * sum(1 for a in absr if a <= ms) / len(absr)
    print(f"  {label:24} n={len(all_res):5d}  median={statistics.median(all_res):7.1f}  "
          f"median|.|={statistics.median(absr):6.1f}  <=25ms:{within(25):5.1f}%  <=100ms:{within(100):5.1f}%")


def main():
    days = int(sys.argv[1]) if len(sys.argv) > 1 else 0
    cutoff = None
    if days:
        import time
        cutoff = time.time() - days * 86400

    per_anchor = {0: [], 1: [], 2: []}
    encounters = 0
    # Encounters whose first swing is badly off the lattice its own later swings define.
    offenders = []

    for path in sorted(glob.glob(os.path.join(CLOGS, "clog.*.jsonl"))):
        if cutoff and os.path.getmtime(path) < cutoff:
            continue
        times = swings(path)
        if len(times) < 6:          # need enough later swings for a median to mean anything
            continue
        encounters += 1
        for idx in per_anchor:
            per_anchor[idx].extend(residuals(times, idx))

        first = residuals(times, 0)
        if first:
            med = statistics.median(first)
            if abs(med) > 100:
                offenders.append((abs(med), med, len(first), os.path.basename(path)))

    print(f"encounters with >=6 swings: {encounters}"
          + (f"   (last {days} days)" if days else "   (whole clog corpus)"))
    print("\nlattice residuals of later swings, by which swing was used as the anchor:")
    for idx, label in ((0, "first swing (shipping)"), (1, "second swing"), (2, "third swing")):
        summarise(label, per_anchor[idx])

    print(f"\nencounters whose FIRST swing is >100 ms off its own later lattice: "
          f"{len(offenders)} of {encounters}")
    for absmed, med, n, name in sorted(offenders, reverse=True)[:15]:
        print(f"  median {med:+8.1f} ms over {n:3d} later swings   {name}")


if __name__ == "__main__":
    main()
