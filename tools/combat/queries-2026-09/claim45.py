import re, collections
from corpus import sources

VALUE = re.compile(r"The value of ([^\n]*?) is (-?[\d,]+) points?\.")
TITLE = re.compile(r"\b([A-Z][A-Za-z'\-]+) the ([a-z][a-z' \-]*?)(?=[\s]*(?:$|[.,!?\)]|\bis\b|\bhas\b|\bsays\b))")
WORD = re.compile(r"I don't know the word \"([^\"]*)\"\.")
SIR = re.compile(r"\b(Sir|Lady)\s+([A-Z][A-Za-z'\-]+)")

nonthe = collections.Counter()
titles = collections.Counter()
words = collections.Counter()
sirs = collections.Counter()
sir_examples = []

for scope, path, it in sources():
    tag = "CORPUS" if scope in ("TEMP", "CLOG", "CLOGEXTRA") else scope
    for ts, d, line in it:
        for m in VALUE.finditer(line):
            head = m.group(1)
            if not head.startswith("the "):
                nonthe[(tag, m.group(0))] += 1
        for m in TITLE.finditer(line):
            titles[(tag, m.group(2).strip())] += 1
        for m in WORD.finditer(line):
            words[(tag, m.group(1))] += 1
        for m in SIR.finditer(line):
            sirs[(tag, m.group(0))] += 1
            if len(sir_examples) < 40:
                sir_examples.append((tag, path, ts, d, line.strip()[:160]))

def sub(counter, tags):
    return collections.Counter({k[1]: v for k, v in counter.items() if k[0] in tags})

for label, tags in (("CORPUS (TEMP+clogs)", {"CORPUS"}), ("ALL sources", {"CORPUS", "RESEARCH", "MUCKAROOT"})):
    print(f"===== {label} =====")
    nt = sub(nonthe, tags)
    print(f'"The value of X" with NO "the " after "of": occurrences={sum(nt.values())} distinct={len(nt)}')
    for k, v in sorted(nt.items()):
        print(f"     {v:4d}  {k}")
    t = sub(titles, tags)
    print(f'"<Name> the <title>" : occurrences={sum(t.values())} distinct title forms={len(t)}')
    for k, v in sorted(t.items(), key=lambda kv: -kv[1]):
        print(f"     {v:5d}  {k!r}")
    w = sub(words, tags)
    print(f'I don\'t know the word "X". : occurrences={sum(w.values())} distinct words={len(w)}')
    for k, v in sorted(w.items(), key=lambda kv: -kv[1]):
        print(f"     {v:4d}  {k!r}")
    s = sub(sirs, tags)
    print(f'"Sir <Name>" / "Lady <Name>" : occurrences={sum(s.values())} distinct={len(s)}')
    for k, v in sorted(s.items(), key=lambda kv: -kv[1]):
        print(f"     {v:4d}  {k!r}")
    print()

print("=== Sir/Lady example lines ===")
for e in sir_examples:
    print("   ", e)
