"""Max SIMULTANEOUSLY-ENGAGED NPCs per encounter, from the live clog corpus.

Answers one question and stores the query beside the result, per CLAUDE.md: what is the largest
number of NPCs actually in a fight with the player at the same instant? COMBAT-RAIL-SPEC.md
section 3 says 4, sourced to the offline research capture; the owner reports a 5+ fight within the
last three days.

Method: walk each clog's event stream in order, keeping the set of NPCs whose fight is open.
FightStart opens one. Per-creature ends (Kill / NpcFled / NpcFleeFailed) close one. All-fights ends
(YouFled / YouFleeFailed / KilledByNpc / Withdrawn) close everything. FightEndOther is treated as
per-creature when it names an NPC and ignored otherwise - it is the "game closed it and gave no
reason" case, and guessing wider would under-report concurrency by clearing live opponents early.

A creature that appears only as the target of Hit/HitByNpc without a FightStart is still counted:
you cannot trade blows with something you are not fighting, and a missed FightStart line would
otherwise silently lower the peak.
"""
import collections
import datetime as dt
import glob
import io
import json
import os
import sys

CLOGS = os.path.expanduser("~/.mucka/clogs")

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


def scan(path):
    """-> (peak, when_peak_utc, roster_at_peak, total_distinct)"""
    live = set()
    peak, peak_at, peak_roster = 0, None, ()
    distinct = set()
    for d in events(path):
        kind, npc, ts = d["kind"], d.get("npc"), d.get("ts")

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
            distinct.add(npc)
            if len(live) > peak:
                peak, peak_at, peak_roster = len(live), ts, tuple(sorted(live))
    return peak, peak_at, peak_roster, len(distinct)


def main():
    days = int(sys.argv[1]) if len(sys.argv) > 1 else 0
    cutoff = None
    if days:
        cutoff = dt.datetime.now().timestamp() - days * 86400

    rows = []
    for path in sorted(glob.glob(os.path.join(CLOGS, "clog.*.jsonl"))):
        if cutoff and os.path.getmtime(path) < cutoff:
            continue
        peak, at, roster, distinct = scan(path)
        if peak:
            rows.append((peak, at, roster, distinct, os.path.basename(path)))

    if not rows:
        print("no clogs in range")
        return

    hist = collections.Counter(r[0] for r in rows)
    print(f"encounters scanned: {len(rows)}"
          + (f"   (mtime within {days} days)" if days else "   (whole clog corpus)"))
    print("\npeak simultaneous NPCs -> encounters")
    for k in sorted(hist):
        print(f"  {k:2d} -> {hist[k]:4d}")

    print("\nevery encounter peaking at 5 or more, worst first:")
    for peak, at, roster, distinct, name in sorted(rows, reverse=True):
        if peak < 5:
            break
        when = dt.datetime.fromtimestamp(at / 1000).strftime("%Y-%m-%d %H:%M:%S") if at else "?"
        print(f"  {peak:2d} at {when}  {distinct:2d} distinct over the fight  {name}")
        print(f"       {', '.join(roster)}")


if __name__ == "__main__":
    main()
