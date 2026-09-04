import json, sys

path = sys.argv[1]
target = int(sys.argv[2])
recs = []
with open(path, "r", encoding="utf-8-sig") as f:
    for line in f:
        line = line.strip()
        if not line:
            continue
        recs.append(json.loads(line))

idx = [i for i, r in enumerate(recs) if r[0] == target]
for i in idx:
    lo = max(0, i - 3)
    hi = min(len(recs), i + 5)
    for j in range(lo, hi):
        ts, d, p = recs[j][0], recs[j][1], recs[j][2]
        b = p.encode("latin-1")
        vis = "".join(chr(x) if 32 <= x < 127 else ("\n" if x == 10 else "<%02X>" % x) for x in b)
        mark = ">>" if j == i else "  "
        print(f"{mark} {ts} {d}: {vis}")
    print("-" * 60)
