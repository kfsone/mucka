import re
from corpus import sources

PAT = re.compile(r"The value of (?!the )([^\n]*?) is (-?[\d,]+) points?\.")

for scope, path, it in sources():
    src = "TEMP" if scope == "TEMP" else ("CLOG" if scope in ("CLOG", "CLOGEXTRA") else scope)
    recent = []
    for ts, d, line in it:
        recent.append((ts, d, line))
        if len(recent) > 12:
            recent.pop(0)
        if PAT.search(line):
            print(f"### {src} {path}")
            for r in recent:
                print(f"    {r[0]} {r[1]}: {r[2].strip()[:140]}")
            print()
