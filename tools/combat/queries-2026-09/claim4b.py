import re, collections
from corpus import sources

BASE = [
    "novice", "protector", "seeress", "seer", "yeowoman", "yeoman", "soothsayer",
    "warrior", "cabalist", "swordswoman", "swordsman", "magicienne", "magician",
    "heroine", "hero", "enchantress", "enchanter", "superheroine", "superhero",
    "spellbindress", "spellbinder", "championne", "champion", "sorceress", "sorcerer",
    "guardienne", "guardian", "necromancess", "necromancer", "legend", "warlock",
    "mage", "wizard", "witch",
    "discoverer", "pathfinder", "neophyte", "voyager", "pilgrim", "wayfarer", "acolyte",
    "scout", "friar", "rover", "cleric", "pioneer", "Brother", "explorer", "priestess",
    "priest", "ranger", "prelate", "minstrel", "patriarch", "matriarch", "Sister",
]
alt = "|".join(BASE)
SUFFIX = re.compile(r"\b([A-Z][A-Za-z'\-]+) the ((?:[a-z][a-z'\-]*[ ])*?(?:" + alt + r"))\b")
PREFIX = re.compile(r"\bthe ((?:[a-z][a-z'\-]*[ ])*?(?:" + alt + r")) ([A-Z][A-Za-z'\-]+)\b")
SIRLADY = re.compile(r"\b(Sir|Lady)\b")
WORD = re.compile(r"I don't know the word \"([^\"]*)\"\.")

suffix = collections.Counter()
prefix = collections.Counter()
sirlady = collections.Counter()
sirlady_lines = []
words = collections.Counter()

for scope, path, it in sources():
    tag = "CORPUS" if scope in ("TEMP", "CLOG", "CLOGEXTRA") else scope
    src = "TEMP" if scope == "TEMP" else ("CLOG" if scope in ("CLOG", "CLOGEXTRA") else scope)
    for ts, d, line in it:
        for m in SUFFIX.finditer(line):
            suffix[(src, m.group(2))] += 1
        for m in PREFIX.finditer(line):
            prefix[(src, m.group(0))] += 1
        if SIRLADY.search(line):
            sirlady[src] += 1
            if len(sirlady_lines) < 30:
                sirlady_lines.append((src, path, ts, d, line.strip()[:180]))
        for m in WORD.finditer(line):
            words[(src, m.group(1))] += 1

def sub(c, srcs):
    out = collections.Counter()
    for k, v in c.items():
        if k[0] in srcs:
            out[k[1]] += v
    return out

SCOPES = {
    "TEMP only": {"TEMP"},
    "CLOG only": {"CLOG"},
    "TEMP+CLOG (stated corpus)": {"TEMP", "CLOG"},
    "TEMP+CLOG+RESEARCH+ROOT": {"TEMP", "CLOG", "RESEARCH", "MUCKAROOT"},
}
for label, srcs in SCOPES.items():
    s = sub(suffix, srcs)
    p = sub(prefix, srcs)
    w = sub(words, srcs)
    print(f"--- {label} ---")
    print(f"  '<Name> the <title>' occurrences={sum(s.values())} distinct title forms={len(s)}")
    print(f"  'the <title> <Name>' (prefix order) occurrences={sum(p.values())} distinct={len(p)}")
    print(f"  unknown-word occurrences={sum(w.values())} distinct={len(w)}")
    print()

print("=== distinct title forms, TEMP+CLOG ===")
for k, v in sorted(sub(suffix, {"TEMP", "CLOG"}).items(), key=lambda kv: -kv[1]):
    print(f"   {v:5d}  {k}")
print()
print("=== prefix-order matches (any scope) ===")
for k, v in sorted(prefix.items(), key=lambda kv: -kv[1])[:40]:
    print(f"   {v:5d}  {k}")
print()
print("=== lines containing bare 'Sir' or 'Lady' ===")
print("counts by src:", dict(sirlady))
for e in sirlady_lines:
    print("   ", e)
