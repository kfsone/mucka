"""Build a plain-text line corpus from (a) session recordings and (b) clogs."""
import json, os, glob, re, io

TEMP = r"C:\Users\oliver.smith\AppData\Local\Temp\mucka"
RESEARCH = r"G:\Source\mucka\RESEARCH"
MUCKAROOT = r"G:\Source\mucka"
CLOGS = r"C:\Users\oliver.smith\.mucka\clogs"


def strip_c1(b: bytes) -> bytes:
    # C1 protocol bytes are 0x9B-0xFF; also drop NUL. Keep everything else.
    return bytes(x for x in b if (x < 0x9B and x != 0x00))


def recording_lines(path):
    """Yield (ts, dir, plaintext_line) for every newline-delimited line in a recording."""
    buf = {"rx": b"", "tx": b""}
    ts_of = {"rx": 0, "tx": 0}
    with open(path, "r", encoding="utf-8-sig") as f:
        for raw in f:
            raw = raw.strip()
            if not raw:
                continue
            try:
                rec = json.loads(raw)
            except Exception:
                continue
            if not isinstance(rec, list) or len(rec) < 3 or not isinstance(rec[2], str):
                continue
            ts, d, payload = rec[0], rec[1], rec[2]
            if d not in ("rx", "tx"):
                continue
            b = strip_c1(payload.encode("latin-1", "replace"))
            buf[d] += b
            ts_of[d] = ts
            while b"\n" in buf[d]:
                line, _, rest = buf[d].partition(b"\n")
                buf[d] = rest
                yield ts, d, line.decode("latin-1").rstrip("\r")
    for d in ("rx", "tx"):
        if buf[d]:
            yield ts_of[d], d, buf[d].decode("latin-1").rstrip("\r")


def clog_lines(path):
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        for raw in f:
            raw = raw.strip()
            if not raw:
                continue
            try:
                rec = json.loads(raw)
            except Exception:
                continue
            ts = rec.get("ts") if isinstance(rec, dict) else None
            def walk(o):
                if isinstance(o, str):
                    for ln in o.split("\n"):
                        yield ln.rstrip("\r")
                elif isinstance(o, list):
                    for v in o:
                        yield from walk(v)
                elif isinstance(o, dict):
                    for v in o.values():
                        yield from walk(v)
            for ln in walk(rec):
                yield ts, "clog", ln


def sources():
    for p in sorted(glob.glob(os.path.join(TEMP, "*.jsonl"))):
        yield "TEMP", p, recording_lines(p)
    for p in sorted(glob.glob(os.path.join(RESEARCH, "*.jsonl"))):
        yield "RESEARCH", p, recording_lines(p)
    for p in sorted(glob.glob(os.path.join(MUCKAROOT, "*.jsonl"))):
        yield "MUCKAROOT", p, recording_lines(p)
    for p in sorted(glob.glob(os.path.join(CLOGS, "*.jsonl"))):
        yield "CLOG", p, clog_lines(p)
    for extra in ("fights.jsonl.imported", "swings.jsonl.imported", "items.jsonl",
                  "fights.jsonl.v1.bak"):
        p = os.path.join(CLOGS, extra)
        if os.path.exists(p):
            yield "CLOGEXTRA", p, clog_lines(p)
