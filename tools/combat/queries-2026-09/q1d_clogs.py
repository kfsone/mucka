"""Q1 replication on the clog corpus -- an independent channel.

The clogs carry no per-blow stamina (a `stats` snapshot appears only 1418 times
in 36291 lines, at encounter boundaries), so EXACT incoming damage is not
recoverable from them. What they do carry, over a WIDER span than the swings
ledger (earliest clog ts 1785663468768 vs swings min 1786689980708), is:
  - 168 NpcWeaponEquip events, vs 109 announcing fights in the db
  - HitByNpc / MissByNpc, i.e. P(hit|swing) as a second outcome variable
So: does arming change the creature's HIT RATE, on a corpus the db test did not
see all of?
"""
import sys, os, json, glob, collections, re
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import table, perm_test_meandiff
import numpy as np

OUT = []
def p(*a):
    s = " ".join(str(x) for x in a); print(s); OUT.append(s)

files = sorted(glob.glob(os.path.expanduser("~/.mucka/clogs/*.jsonl")))
p(f"clog files: {len(files)}")

def norm(npc):
    n = re.sub(r"\d+$", "", (npc or "")).strip().lower()
    return n

equips = collections.Counter()
raw_equip_lines = collections.Counter()
# per (file, npc): ordered stream of (kind, ts)
enc = []
for fp in files:
    stream = []
    for line in open(fp, encoding="utf-8"):
        line = line.strip()
        if not line:
            continue
        try:
            o = json.loads(line)
        except Exception:
            continue
        k = o.get("kind")
        if k == "NpcWeaponEquip":
            equips[(norm(o.get("npc")), o.get("weapon"))] += 1
            raw_equip_lines[o.get("raw", "")[:120]] += 1
        if k in ("HitByNpc", "MissByNpc", "NpcWeaponEquip", "FightStart", "Kill",
                 "NpcFled", "KilledByNpc"):
            stream.append((k, o.get("ts"), o.get("npc"), o.get("weapon")))
    enc.append((fp, stream))

p("")
p("=" * 78)
p("Q1o. EVERY NPC WEAPON EQUIP IN THE CLOG CORPUS")
p("=" * 78)
byn = collections.Counter()
for (n, w), c in equips.items():
    byn[n] += c
p("")
p(table(["npc kind", "equip events", "distinct weapons"],
        [[n, c, len({w for (nn, w) in equips if nn == n})] for n, c in byn.most_common()]))
p(f"\nTotal equip events: {sum(equips.values())}   distinct (kind, weapon) pairs: {len(equips)}")
p("Same closed list of kinds as the db: humanoids only. No animal ever equips.")

p("")
p("=" * 78)
p("Q1p. IS THERE ANY 'NPC LOSES ITS WEAPON' LINE AT ALL?")
p("=" * 78)
p("Scanning every raw line in the corpus for a disarm-shaped sentence about an NPC.")
pat = re.compile(r"(drops? the|drops? its|disarm|loses? the|loses? its|breaks to bits|"
                 r"stopped using|no longer using)", re.I)
hits = collections.Counter()
for fp in files:
    for line in open(fp, encoding="utf-8"):
        try:
            o = json.loads(line)
        except Exception:
            continue
        for t in [o.get("raw"), o.get("text")]:
            if t and pat.search(t):
                hits[t.strip()[:110]] += 1
        for t in (o.get("preroll") or []):
            if pat.search(t):
                hits[t.strip()[:110]] += 1
p("")
if hits:
    p(table(["line", "count"], hits.most_common(25)))
else:
    p("  none")
p("""Any 'breaks to bits' hits are the PLAYER's weapon (CombatTracker.WeaponBroke
matches '^The <weapon> breaks to bits.$' with no actor). Nothing in the corpus
announces an NPC losing a weapon, which is why npc_weapon can only ever ratchet
on. So: a disarm is not observable, and its absence in the data is not evidence
that disarms do not happen.""")

# ------------------------------------------- hit rate before/after the equip
p("")
p("=" * 78)
p("Q1q. NPC HIT RATE BEFORE vs AFTER IT ARMS ITSELF (within the same encounter)")
p("=" * 78)
p("""Per (clog file, npc instance): count HitByNpc / MissByNpc strictly before the
first NpcWeaponEquip for that npc, and strictly after. Paired, one pair per
instance that has >=3 swings on each side.""")
pairs = []
for fp, stream in enc:
    byn2 = collections.defaultdict(list)
    for k, ts, npc, w in stream:
        if npc:
            byn2[npc].append((k, ts, w))
    for npc, ev in byn2.items():
        eq = [i for i, e in enumerate(ev) if e[0] == "NpcWeaponEquip"]
        if not eq:
            continue
        i0 = eq[0]
        pre = [e for e in ev[:i0] if e[0] in ("HitByNpc", "MissByNpc")]
        post = [e for e in ev[i0:] if e[0] in ("HitByNpc", "MissByNpc")]
        if len(pre) >= 3 and len(post) >= 3:
            pairs.append([os.path.basename(fp), npc, ev[i0][2],
                          len(pre), sum(1 for e in pre if e[0] == "HitByNpc") / len(pre),
                          len(post), sum(1 for e in post if e[0] == "HitByNpc") / len(post)])
if pairs:
    for r in pairs:
        r.append(r[6] - r[4])
    p("")
    p(table(["clog", "npc", "weapon", "n_pre", "hitrate_pre", "n_post", "hitrate_post", "delta"], pairs))
    d = np.array([r[7] for r in pairs])
    rng = np.random.default_rng(33)
    obs = d.mean()
    cnt = sum(1 for _ in range(20000)
              if abs((d * rng.choice([-1.0, 1.0], len(d))).mean()) >= abs(obs) - 1e-12)
    p(f"\nPaired instances: {len(d)}   mean delta hit rate = {obs:+.3f}   "
      f"median {np.median(d):+.3f}   sign-flip p = {(cnt+1)/20001:.3f}")
    p(f"Instances where post > pre: {int((d>0).sum())} / {len(d)}")
else:
    p("\nNo instance has >=3 swings on both sides of its equip. Not testable here.")

# ----------------------------------------- pooled hit rate, armed vs unarmed
p("")
p("=" * 78)
p("Q1r. POOLED HIT RATE, ARMED vs NOT-YET-ARMED (weapon-capable kinds only)")
p("=" * 78)
tab = collections.defaultdict(lambda: [0, 0, 0, 0])  # armed hit, armed sw, un hit, un sw
for fp, stream in enc:
    byn2 = collections.defaultdict(list)
    for k, ts, npc, w in stream:
        if npc:
            byn2[npc].append((k, ts, w))
    for npc, ev in byn2.items():
        armed = False
        for k, ts, w in ev:
            if k == "NpcWeaponEquip":
                armed = True
            elif k in ("HitByNpc", "MissByNpc"):
                t = tab[norm(npc)]
                if armed:
                    t[1] += 1; t[0] += (k == "HitByNpc")
                else:
                    t[3] += 1; t[2] += (k == "HitByNpc")
rows = [[n, v[1], v[0] / v[1] if v[1] else None, v[3], v[2] / v[3] if v[3] else None,
         (v[0] / v[1] - v[2] / v[3]) if v[1] and v[3] else None]
        for n, v in sorted(tab.items(), key=lambda kv: -kv[1][1]) if v[1] >= 10]
p("")
p(table(["npc kind", "sw_armed", "hitrate_armed", "sw_unarmed", "hitrate_unarmed", "delta"], rows))
p("""'unarmed' here means 'has not yet been seen to equip in this encounter' -- the
same not-observed-is-not-absent caveat as the db. Read the delta column only as
a direction check on Q1's damage result, never as a rate in its own right.""")

open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "q1d_out.txt"), "w",
     encoding="utf-8").write("\n".join(OUT))
