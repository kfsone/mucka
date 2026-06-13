#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# ///
"""Reduce a mapping walk capture (.jsonl) to a compact analysis digest.

decode_probe.py output is faithful but probe-verbose: superlook / look around /
qscan largely restate the exits table, and every revisit repeats the whole probe.
Handing that to an analysis agent wastes context. This tool collapses a walk to
what map analysis (MUD-Cartography.md) actually consumes:

  R<n>   one room-observation entry per distinct (short, long, fex, exits-table)
         tuple; revisits become one-line pointers in route order. Keeps the
         known-as reference name, qscan direction->name pairs (free quickscan-
         dedup evidence) and fei item lists. Arrival ambient (20.xx) is attached
         to the entry for the visit that carried it.
  edge: / u-turn: console annotations verbatim (refusals included -- they are
         data), each edge tagged with the arrival ambient code when one was seen.

Usage:
  uv run tools/mapping/reduce_walk.py <capture.jsonl> [more.jsonl ...]
"""

import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from decode_probe import decode_rx, label_probe_segments  # noqa: E402

TAG_RE = re.compile(r"\{/?c[0-9.?]*\}")
SHORT_RE = re.compile(r"\{c02\.01\}(.*?)\{/c02\.01\}", re.DOTALL)
LONG_RE = re.compile(r"\{c02\.02\}(.*?)\{/c02\.02\}", re.DOTALL)
KNOWN_RE = re.compile(r'known as "\{c02\.01\}(.*?)\{/c02\.01\}"')
AMBIENT_RE = re.compile(r"\{c20\.(\d\d)\}")
EXIT_LINE_RE = re.compile(r"^(?:\{c12\.09\})?(\w+):\s+\{c02\.01\}(.*?)\{/c02\.01\}\.",
                          re.MULTILINE)
QSCAN_RE = re.compile(
    r'Looking\s+(\w+?)(?:ward)?s?,\s+you\s+see\s+a\s+place\s+known\s+as\s+'
    r'"\{c02\.01\}(.*?)\{/c02\.01\}"', re.DOTALL)
SEG_SPLIT_RE = re.compile(r"^=== (.+?) ===\n?", re.MULTILINE)

DIR_ABBREV = {
    "north": "n", "south": "s", "east": "e", "west": "w",
    "northeast": "ne", "northwest": "nw", "southeast": "se", "southwest": "sw",
    "up": "up", "down": "down", "in": "in", "out": "out",
    "upward": "up", "downward": "down", "swampward": "swamp", "over": "over",
}


def collapse(text: str) -> str:
    return " ".join(TAG_RE.sub("", text).split())


def segments(labeled: str) -> dict[str, str]:
    parts = SEG_SPLIT_RE.split(labeled)
    # parts = [preamble, name, body, name, body, ...]
    return {parts[i]: parts[i + 1] for i in range(1, len(parts) - 1, 2)}


def parse_probe(decoded: str) -> dict:
    seg = segments(label_probe_segments(decoded))
    longlook = seg.get("longlook", "")
    m = SHORT_RE.search(longlook)
    short = collapse(m.group(1)) if m else "(dark)"
    m = LONG_RE.search(longlook)
    long_desc = collapse(m.group(1)) if m else ""
    m = KNOWN_RE.search(seg.get("superlook", ""))
    known = m.group(1) if m else ""
    exits = [(DIR_ABBREV.get(d, d), name)
             for d, name in EXIT_LINE_RE.findall(seg.get("exits", ""))]
    qscan_seg = seg.get("qscan", "")
    qscan = [(DIR_ABBREV.get(d, d), collapse(name))
             for d, name in QSCAN_RE.findall(qscan_seg)]
    qscan_note = ""
    if "can't really see far" in qscan_seg:
        qscan_note = "obscured (fumes)"
    elif "too dark" in qscan_seg:
        qscan_note = "too dark"
    fex_m = re.search(r"\{c12\.08\.02\}(.*?)\{/c12\.08\.02\}", seg.get("fex", ""), re.DOTALL)
    fex = " ".join(sorted(
        DIR_ABBREV.get(w, w) for w in collapse(fex_m.group(1)).split())) if fex_m else ""
    fei_body = collapse(re.sub(r"=+", "", TAG_RE.sub("", seg.get("fei", ""))))
    items = fei_body.replace("(no output)", "").strip()
    return {"short": short, "long": long_desc, "known": known, "exits": tuple(exits),
            "qscan": qscan, "qscan_note": qscan_note, "fex": fex, "items": items}


def fmt_room(idx: int, p: dict, ambient: str) -> str:
    lines = [f"R{idx} {p['short']}" + (f"  [20.{ambient}]" if ambient else "")]
    if p["known"] and p["known"].lower() != p["short"].lower():
        lines.append(f"  known-as: {p['known']}")
    lines.append(f"  long: {p['long'] or '(none)'}")
    lines.append(f"  fex: {p['fex'] or '(none)'}")
    if p["exits"]:
        lines.append("  exits: " + "; ".join(f"{d}={n}" for d, n in p["exits"]))
    if p["qscan"]:
        lines.append("  qscan: " + " ".join(f"{d}={n}" for d, n in p["qscan"]))
    if p["qscan_note"]:
        lines.append(f"  qscan: {p['qscan_note']}")
    if p["items"]:
        lines.append(f"  fei: {p['items']}")
    return "\n".join(lines)


def reduce_file(path: Path) -> str:
    out: list[str] = [f"##### {path.name} (reduced)"]
    rooms: dict[tuple, int] = {}          # observation key -> R index
    first_ts = last_ts = None
    rx_buffer: list[str] = []
    probe_pending = False                 # next rx flush is a probe response
    ambient = ""                          # from the most recent move arrival

    def flush_rx() -> None:
        nonlocal probe_pending, ambient
        if not rx_buffer:
            return
        raw = "".join(rx_buffer)
        rx_buffer.clear()
        if probe_pending:
            probe_pending = False
            p = parse_probe(decode_rx(raw, plain=False))
            key = (p["short"], p["long"], p["fex"], p["exits"])
            if key in rooms:
                out.append(f"R{rooms[key]} revisit: {p['short']}"
                           + (f"  [20.{ambient}]" if ambient else ""))
            else:
                rooms[key] = len(rooms) + 1
                out.append(fmt_room(rooms[key], p, ambient))
            ambient = ""
        else:
            m = AMBIENT_RE.search(decode_rx(raw, plain=False))
            ambient = m.group(1) if m else ""

    for line in path.read_text(encoding="utf-8-sig").splitlines():
        if not line.strip():
            continue
        record = json.loads(line)
        if isinstance(record, dict):
            flush_rx()
            out.append(f"extra: {json.dumps(record)}")
            continue
        ts, mode, data = record
        first_ts = first_ts if first_ts is not None else ts
        last_ts = ts
        if mode == "rx":
            rx_buffer.append(data)
            continue
        flush_rx()
        if mode == "tx":
            if "longlook" in data:
                probe_pending = True
        elif mode == "an":
            if data.startswith("edge:") or data.startswith("u-turn:"):
                out.append(data)
            elif data.startswith("map walk:"):
                out.append(f"# {data}")
            # op:/room:/probe complete lines are redundant with R and edge entries
    flush_rx()

    if first_ts is not None:
        start = datetime.fromtimestamp(first_ts / 1000, tz=timezone.utc)
        mins = (last_ts - first_ts) / 60000
        out.insert(1, f"# span: {start:%Y-%m-%d %H:%M:%S}Z + {mins:.1f} min")
    out.append(f"# distinct room observations: {len(rooms)}")
    return "\n".join(out)


def main() -> int:
    files = [Path(a) for a in sys.argv[1:]]
    if not files:
        print(__doc__, file=sys.stderr)
        return 2
    for f in files:
        print(reduce_file(f))
        print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
