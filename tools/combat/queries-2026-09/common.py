"""Shared helpers. No scipy on this box: permutation tests are hand-rolled."""
import os, sqlite3, math
import numpy as np

DB = os.path.expanduser("~/.mucka/combat/mucka.db")

def con():
    # read-only: the operator's live session may hold this db.
    return sqlite3.connect(f"file:{DB}?mode=ro", uri=True)

def q(sql, args=()):
    c = con()
    try:
        cur = c.execute(sql, args)
        cols = [d[0] for d in cur.description]
        return cols, cur.fetchall()
    finally:
        c.close()

def table(cols, rows, widths=None):
    rows = [[("" if v is None else v) for v in r] for r in rows]
    allr = [cols] + [[fmt(v) for v in r] for r in rows]
    w = [max(len(str(r[i])) for r in allr) for i in range(len(cols))]
    out = []
    out.append("  ".join(str(cols[i]).ljust(w[i]) for i in range(len(cols))))
    out.append("  ".join("-" * w[i] for i in range(len(cols))))
    for r in allr[1:]:
        out.append("  ".join(str(r[i]).ljust(w[i]) for i in range(len(cols))))
    return "\n".join(out)

def fmt(v):
    if isinstance(v, float):
        return f"{v:.3f}"
    return str(v)

def perm_test_meandiff(a, b, iters=20000, seed=0):
    """Two-sample permutation test on |mean(a)-mean(b)|. Returns (obs_diff, p)."""
    rng = np.random.default_rng(seed)
    a = np.asarray(a, float); b = np.asarray(b, float)
    obs = a.mean() - b.mean()
    pool = np.concatenate([a, b]); na = len(a)
    cnt = 0
    for _ in range(iters):
        rng.shuffle(pool)
        if abs(pool[:na].mean() - pool[na:].mean()) >= abs(obs) - 1e-12:
            cnt += 1
    return obs, (cnt + 1) / (iters + 1)

def cohens_d(a, b):
    a = np.asarray(a, float); b = np.asarray(b, float)
    na, nb = len(a), len(b)
    sp2 = ((na - 1) * a.var(ddof=1) + (nb - 1) * b.var(ddof=1)) / (na + nb - 2)
    return (a.mean() - b.mean()) / math.sqrt(sp2) if sp2 > 0 else float("nan")

def cliffs_delta(a, b):
    """P(a>b) - P(a<b). Rank-based, robust to the heavy right tail."""
    a = np.sort(np.asarray(a, float)); b = np.sort(np.asarray(b, float))
    gt = np.searchsorted(b, a, side="left").sum()
    lt = (len(b) * len(a)) - np.searchsorted(b, a, side="right").sum()
    return (gt - lt) / (len(a) * len(b))

def boot_ci_mean(x, iters=5000, seed=1, lo=2.5, hi=97.5):
    rng = np.random.default_rng(seed)
    x = np.asarray(x, float)
    m = rng.choice(x, size=(iters, len(x)), replace=True).mean(axis=1)
    return float(np.percentile(m, lo)), float(np.percentile(m, hi))
