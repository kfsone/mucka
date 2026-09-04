import json, sys, os, glob, re

DIRS = [
    r"C:\Users\oliver.smith\AppData\Local\Temp\mucka",
    r"G:\Source\mucka\RESEARCH",
    r"G:\Source\mucka",
]

files = []
for d in DIRS:
    for p in glob.glob(os.path.join(d, "*.jsonl")):
        files.append(p)
files = sorted(set(files))

MAGIC = b"Something magical is happening"
C06C06 = b"\xa1\xa1\xff\xff"
C06C04 = b"\xa1\x9f\xff\xff"

tot_magic = tot_66 = tot_64 = 0
print("=== per-file scan of session recordings ===")
for p in files:
    nmagic = n66 = n64 = 0
    events = []  # (ts, kind)
    nlines = 0
    bad = 0
    with open(p, "r", encoding="utf-8-sig") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            nlines += 1
            try:
                rec = json.loads(line)
            except Exception:
                bad += 1
                continue
            if not isinstance(rec, list) or len(rec) < 3:
                bad += 1
                continue
            ts, dirn, payload = rec[0], rec[1], rec[2]
            if not isinstance(payload, str):
                continue
            try:
                b = payload.encode("latin-1")
            except Exception:
                b = payload.encode("utf-8", "replace")
            c = b.count(MAGIC)
            if c:
                nmagic += c
                events.append((ts, "MAGIC-TEXT", dirn))
            c = b.count(C06C06)
            if c:
                n66 += c
                events.append((ts, "C06C06", dirn))
            c = b.count(C06C04)
            if c:
                n64 += c
                events.append((ts, "C06C04", dirn))
    tot_magic += nmagic; tot_66 += n66; tot_64 += n64
    if nmagic or n66 or n64:
        print(f"{p}\n   lines={nlines} bad={bad} magicText={nmagic} C06C06={n66} C06C04={n64}")
        for ts, k, d in events:
            print(f"      {ts} {d} {k}")

print()
print(f"TOTAL over {len(files)} recording files: magicText={tot_magic} C06C06={tot_66} C06C04={tot_64}")
