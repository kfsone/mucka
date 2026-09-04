import re, collections, os, glob, json
from corpus import recording_lines, clog_lines, TEMP, CLOGS

BASE = ["novice","protector","seeress","seer","yeowoman","yeoman","soothsayer","warrior","cabalist",
        "swordswoman","swordsman","magicienne","magician","heroine","hero","enchantress","enchanter",
        "superheroine","superhero","spellbindress","spellbinder","championne","champion","sorceress",
        "sorcerer","guardienne","guardian","necromancess","necromancer","legend","warlock","mage",
        "wizard","witch"]
alt = "|".join(BASE)
SUFFIX = re.compile(r"\b([A-Z][A-Za-z'\-]+) the ((?:[a-z][a-z'\-]*[ ])*?(?:" + alt + r"))\b")

per_file = {}
for p in sorted(glob.glob(os.path.join(TEMP, "*.jsonl"))):
    c = collections.Counter()
    for ts, d, line in recording_lines(p):
        for m in SUFFIX.finditer(line):
            c[m.group(0)] += 1
    per_file[p] = c
for p in sorted(glob.glob(os.path.join(CLOGS, "*.jsonl"))):
    c = collections.Counter()
    for ts, d, line in clog_lines(p):
        for m in SUFFIX.finditer(line):
            c[m.group(0)] += 1
    if c:
        per_file[p] = c

# cumulative, ordered by file mtime
rows = sorted(per_file.items(), key=lambda kv: os.path.getmtime(kv[0]))
cum = collections.Counter()
import datetime
print("cumulative title occurrences / distinct full strings / distinct titles, by file mtime")
for p, c in rows:
    if not c:
        continue
    cum.update(c)
    titles = {k.split(" the ", 1)[1] for k in cum}
    mt = datetime.datetime.fromtimestamp(os.path.getmtime(p)).isoformat(timespec="seconds")
    print(f"  {mt}  occ={sum(cum.values()):6d} fullDistinct={len(cum):3d} titleDistinct={len(titles):3d}  {os.path.basename(p)}")
