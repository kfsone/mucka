import re, collections
from corpus import sources

BASE = [
    "novice", "protector", "seeress", "seer", "yeowoman", "yeoman", "soothsayer",
    "warrior", "cabalist", "swordswoman", "swordsman", "magicienne", "magician",
    "heroine", "hero", "enchantress", "enchanter", "superheroine", "superhero",
    "spellbindress", "spellbinder", "championne", "champion", "sorceress", "sorcerer",
    "guardienne", "guardian", "necromancess", "necromancer", "legend", "warlock",
    "mage", "wizard", "witch",
]
alt = "|".join(BASE)
SUFFIX = re.compile(r"\b([A-Z][A-Za-z'\-]+) the ((?:[a-z][a-z'\-]*[ ])*?(?:" + alt + r"))\b")

full = collections.Counter()
for scope, path, it in sources():
    src = "TEMP" if scope == "TEMP" else ("CLOG" if scope in ("CLOG", "CLOGEXTRA") else scope)
    for ts, d, line in it:
        for m in SUFFIX.finditer(line):
            full[(src, m.group(0))] += 1

for label, srcs in (("TEMP+CLOG", {"TEMP", "CLOG"}), ("ALL", {"TEMP", "CLOG", "RESEARCH", "MUCKAROOT"})):
    c = collections.Counter()
    for k, v in full.items():
        if k[0] in srcs:
            c[k[1]] += v
    print(f"--- {label}: full '<Name> the <title>' strings: occurrences={sum(c.values())} distinct={len(c)}")
    for k, v in sorted(c.items(), key=lambda kv: -kv[1]):
        print(f"   {v:5d}  {k}")
    print()
