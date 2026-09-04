import re, collections
from corpus import sources

LOOSE = re.compile(r"know the word")
FULL = re.compile(r"I don't know the word \"([^\"]*)\"\.")

loose = collections.Counter()
full = collections.Counter()
unmatched = []
for scope, path, it in sources():
    src = "TEMP" if scope == "TEMP" else ("CLOG" if scope in ("CLOG", "CLOGEXTRA") else scope)
    for ts, d, line in it:
        n = len(LOOSE.findall(line))
        if n:
            loose[src] += n
            got = FULL.findall(line)
            for w in got:
                full[(src, w)] += 1
            if len(got) != n:
                unmatched.append((src, path, ts, d, repr(line[:200])))

print("loose 'know the word' occurrences by src:", dict(loose))
print("total loose:", sum(loose.values()))
for srcs, label in (({"TEMP", "CLOG"}, "TEMP+CLOG"), ({"TEMP"}, "TEMP"), ({"CLOG"}, "CLOG"),
                    ({"TEMP", "CLOG", "RESEARCH", "MUCKAROOT"}, "ALL")):
    c = collections.Counter()
    for k, v in full.items():
        if k[0] in srcs:
            c[k[1]] += v
    ci = collections.Counter()
    for k, v in c.items():
        ci[k.lower()] += v
    print(f"{label}: occ={sum(c.values())} distinct={len(c)} distinct_case_insensitive={len(ci)}")
print()
print("lines where the strict regex missed a loose hit:")
for u in unmatched:
    print("   ", u)
