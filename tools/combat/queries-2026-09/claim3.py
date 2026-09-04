import re, collections, sys
from corpus import sources

PAT = re.compile(r"The value of .*?is -?[\d,]+ points?\.")

lines_by_scope = collections.defaultdict(list)
per_file = collections.Counter()

for scope, path, it in sources():
    for ts, d, line in it:
        for m in PAT.finditer(line):
            s = m.group(0)
            lines_by_scope[scope].append((path, ts, d, s))
            per_file[(scope, path)] += 1


def report(name, scopes, verbose=True):
    rows = []
    for sc in scopes:
        rows.extend(lines_by_scope[sc])
    texts = [r[3] for r in rows]
    distinct = sorted(set(texts))
    player, creature, obj = [], [], []
    for t in texts:
        rest = t[len("The value of "):]
        if rest.startswith("the "):
            creature.append(t)
        else:
            head = rest.rsplit(" is ", 1)[0]
            if re.search(r"\bthe\b", head):
                player.append(t)
            else:
                obj.append(t)
    neg = [t for t in texts if re.search(r"is -[\d,]+ point", t)]
    negobj = [t for t in neg if t in obj]
    singular = [t for t in texts if t.endswith(" is 1 point.")]
    print(f"--- {name} ---")
    print(f"  total occurrences : {len(texts)}")
    print(f"  distinct lines    : {len(distinct)}")
    print(f"  creature form ('of the ')  : occ={len(creature)} distinct={len(set(creature))}")
    print(f"  player form (Name the ttl) : occ={len(player)} distinct={len(set(player))}")
    if verbose:
        for t in sorted(set(player)):
            print(f"      PLAYER: {t}")
    print(f"  object form (no 'the ')    : occ={len(obj)} distinct={len(set(obj))}")
    if verbose:
        for t in sorted(set(obj)):
            print(f"      OBJ: {t}")
    print(f"  negative-valued occurrences: {len(neg)} (object-form among them: {len(negobj)})")
    if verbose:
        for t in sorted(set(neg)):
            print(f"      NEG: {t}")
    print(f"  singular 'is 1 point.'     : {len(singular)} distinct={len(set(singular))}")
    if verbose:
        for t in sorted(set(singular)):
            print(f"      SING: {t}")
    print()


report("TEMP recordings + CLOGS (comment's stated corpus)", ["TEMP", "CLOG", "CLOGEXTRA"])
report("TEMP only", ["TEMP"], verbose=False)
report("CLOGS only", ["CLOG", "CLOGEXTRA"], verbose=False)
report("EVERYTHING (TEMP+RESEARCH+MUCKAROOT+CLOGS)", ["TEMP", "RESEARCH", "MUCKAROOT", "CLOG", "CLOGEXTRA"], verbose=False)

print("=== top files ===")
for (sc, p), n in per_file.most_common(20):
    print(f"  {n:5d}  {sc}  {p}")
