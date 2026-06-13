#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# ///
"""Decode a mucka capture (.jsonl) into readable, context-tagged text.

Capture lines are [timestamp_ms, "tx"|"rx"|"an", latin1-text] (see INTERNAL.md).
The rx payload is the raw wire stream, which has two layers to strip:

  1. telnet: IAC IAC (0xFF 0xFF) escapes a literal 0xFF data byte -- this is why
     C1 terminators appear doubled in raw captures; CR NUL (0x0D 0x00) is a bare
     carriage return; negotiation sequences are dropped.
  2. MUD2 C1 context codes (see MUD-ClientProto.md): bytes 155..254 are code
     components (value = byte - 155), the component list is terminated by 0xFF,
     the following text carries that context until a lone 0xFF pops it.
     Rendered here as {c02.01}text{/c02.01}. Codes nest.

Server lines arrive as CR NUL + CRLF; both collapse to one newline here. Long
descriptions stay wrapped at the server's column width.

Prompt containers (code 01) are collapsed to a [PROMPT] marker line: they delimit
the response segments of a $map probe (longlook, superlook, exits, look around,
qscan, fei, no -- in that order; the reply to "no" is the end marker "Don't then.").

Caveats (best-effort decoder, not a full client):
  - code 90 catch/throw (stack save/restore) is rendered as a tag like any other;
    a capture using it heavily may show unbalanced pops ({/?}).
  - pre-game telnet negotiation is dropped silently.

Usage:
  uv run tools/mapping/decode_probe.py <capture.jsonl> [more.jsonl ...]
  uv run tools/mapping/decode_probe.py --plain <capture.jsonl>   # strip tags too
"""

import json
import re
import sys
from pathlib import Path

IAC = 0xFF
PROBE_SEGMENTS = ["longlook", "superlook", "exits", "look around", "qscan", "fei", "fex", "no"]
FEX_MARKER = "{c12.08.02}"  # unique C1 code that identifies the fex segment
ASYNC_PREFIX_RE = re.compile(r"^\s*\{c(?:06|07|08|09|11|13|14|16|19)(?:\.[0-9]{2}){0,3}\}")


def telnet_strip(data: bytes) -> bytes:
    """Remove the telnet layer: unescape IAC IAC, drop negotiation."""
    out = bytearray()
    i, n = 0, len(data)
    while i < n:
        b = data[i]
        if b == 0x0D and i + 1 < n and data[i + 1] == 0x00:
            out.append(0x0D)     # telnet CR NUL = bare carriage return
            i += 2
            continue
        if b != IAC:
            out.append(b)
            i += 1
            continue
        if i + 1 >= n:
            break  # dangling IAC at packet end
        nxt = data[i + 1]
        if nxt == IAC:           # escaped literal 0xFF
            out.append(IAC)
            i += 2
        elif nxt in (251, 252, 253, 254):  # WILL/WONT/DO/DONT <opt>
            i += 3
        elif nxt == 250:         # SB ... IAC SE
            end = data.find(bytes([IAC, 240]), i + 2)
            i = end + 2 if end != -1 else n
        else:                    # other 2-byte command
            i += 2
    return bytes(out)


def c1_decode(data: bytes) -> str:
    """Convert C1 context codes to {cNN.NN} / {/cNN.NN} tags around their text."""
    out: list[str] = []
    stack: list[str] = []
    i, n = 0, len(data)
    while i < n:
        b = data[i]
        if 155 <= b <= 254:
            code = []
            while i < n and 155 <= data[i] <= 254 and len(code) < 4:
                code.append(data[i] - 155)
                i += 1
            if i < n and data[i] == IAC:  # terminator of the component list
                i += 1
            tag = ".".join(f"{c:02d}" for c in code)
            stack.append(tag)
            out.append("{c" + tag + "}")
        elif b == IAC:
            tag = stack.pop() if stack else "?"
            out.append("{/c" + tag + "}")
            i += 1
        else:
            out.append(chr(b))
            i += 1
    text = "".join(out)
    text = text.replace("\r\n", "\n")
    return text.replace("\r", "")  # bare CRs (from CR NUL) carry no content


# A whole prompt container: {c01}...{/c01}, possibly with the inner color code and
# the invisible-prompt parentheses. Non-greedy: prompts never nest in other prompts.
PROMPT_RE = re.compile(r"\{c01\}.*?\{/c01\}", re.DOTALL)
TAG_RE = re.compile(r"\{/?c[0-9.?]*\}")


def decode_rx(raw: str, plain: bool) -> str:
    text = c1_decode(telnet_strip(raw.encode("latin1")))
    text = PROMPT_RE.sub("\n[PROMPT]\n", text)
    if plain:
        text = TAG_RE.sub("", text)
    # collapse the blank-line noise the substitutions leave behind
    return re.sub(r"\n{3,}", "\n\n", text).strip("\n")


def label_probe_segments(decoded: str) -> str:
    """Title each [PROMPT]-separated segment of a probe response.

    Async server events can inject extra [PROMPT]-delimited sections mid-probe,
    shifting purely positional labels.  Anchor on the fex segment via its unique
    {c12.08.02} C1 marker and label everything else relative to that; surplus
    segments (the injected events) are labeled async-event-N.
    """
    segs = decoded.split("[PROMPT]")
    known_async = {idx for idx, seg in enumerate(segs) if ASYNC_PREFIX_RE.match(seg)}
    probe_segs = [(idx, seg) for idx, seg in enumerate(segs) if idx not in known_async]

    fex_probe_idx = next((i for i, (_idx, seg) in enumerate(probe_segs) if FEX_MARKER in seg), None)
    if fex_probe_idx is not None:
        before = ["longlook", "superlook", "exits", "look around", "qscan", "fei"]
        label_map: dict[int, str] = {}
        fex_seg_idx = probe_segs[fex_probe_idx][0]
        label_map[fex_seg_idx] = "fex"
        longlook_probe_idx = next(
            (
                i
                for i in range(fex_probe_idx - len(before), -1, -1)
                if probe_segs[i][1].lstrip().startswith("{c02.01}")
            ),
            None,
        )
        if longlook_probe_idx is not None:
            for offset, name in enumerate(before):
                label_map[probe_segs[longlook_probe_idx + offset][0]] = name
        else:
            for offset, name in enumerate(reversed(before), 1):
                probe_idx = fex_probe_idx - offset
                if probe_idx >= 0:
                    label_map[probe_segs[probe_idx][0]] = name
        if fex_probe_idx + 1 < len(probe_segs):
            label_map[probe_segs[fex_probe_idx + 1][0]] = "no"

        async_n = 0
        for seg_idx, _seg in probe_segs:
            if seg_idx not in label_map:
                async_n += 1
                label_map[seg_idx] = f"async-event-{async_n}"
        for seg_idx in sorted(known_async):
            async_n += 1
            label_map[seg_idx] = f"async-event-{async_n}"

        out = []
        for idx, seg in enumerate(segs):
            name = label_map[idx]
            body = seg.strip("\n")
            out.append(f"=== {name} ===\n{body if body else '(no output)'}")
        return "\n\n".join(out)

    # No fex marker: old 7-command probe.  Fall back to positional labeling.
    if len(probe_segs) < len(PROBE_SEGMENTS):
        return decoded
    names = [n for n in PROBE_SEGMENTS if n != "fex"]
    label_map: dict[int, str] = {}
    for idx, (seg_idx, _seg) in enumerate(probe_segs):
        label_map[seg_idx] = names[idx] if idx < len(names) else f"extra-{idx}"
    async_n = 0
    for seg_idx in sorted(known_async):
        async_n += 1
        label_map[seg_idx] = f"async-event-{async_n}"
    out = []
    for idx, seg in enumerate(segs):
        name = label_map[idx]
        body = seg.strip("\n")
        out.append(f"=== {name} ===\n{body if body else '(no output)'}")
    return "\n\n".join(out)


def decode_file(path: Path, plain: bool) -> str:
    out: list[str] = [f"##### {path.name}"]
    rx_buffer: list[str] = []
    probe_block = False   # the rx that follows a probe tx gets segment labels

    def flush() -> None:
        nonlocal probe_block
        if not rx_buffer:
            return
        decoded = decode_rx("".join(rx_buffer), plain)
        out.append(label_probe_segments(decoded) if probe_block else decoded)
        rx_buffer.clear()
        probe_block = False

    # utf-8-sig tolerates the BOM SessionCapture's StreamWriter writes.
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        if not line.strip():
            continue
        record = json.loads(line)
        if isinstance(record, dict):
            # Extra records ({"extra": "breadcrumbs", ...}) -- pass through verbatim.
            flush()
            out.append(f"--- extra: {json.dumps(record)}")
            continue
        _ts, mode, data = record
        if mode == "rx":
            rx_buffer.append(data)
            continue
        flush()
        if mode == "an":
            out.append(f"--- an: {data}")
        elif mode == "tx":
            if PROBE_SEGMENTS[0] in data:  # the probe command interrupt
                probe_block = True
            out.append(f"--- tx: {data!r}")
    flush()
    return "\n".join(out)


def main() -> int:
    args = sys.argv[1:]
    plain = "--plain" in args
    files = [Path(a) for a in args if a != "--plain"]
    if not files:
        print(__doc__, file=sys.stderr)
        return 2
    for f in files:
        print(decode_file(f, plain))
        print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
