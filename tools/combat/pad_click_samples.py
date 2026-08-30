"""Prepend silence to the metronome click samples so their transient survives playback.

## The fault

Measured from the shipped assets: `Perc_Stick_hi.wav` and `Perc_Stick_lo.wav` are 169.6 ms files whose
peak sits at **0.48 ms and 1.58 ms** respectively. The whole audible transient is in the first ~6 ms;
everything after is tail more than 20 dB down. There is NO leading silence - the attack is at sample
zero.

A percussive sound with its entire content at the very start of the buffer is at the mercy of whatever
plays it. Any engine that begins a fraction late, ramps in, or drops its first buffer eats the sound
entirely. That is not a theory: the owner reports the same behaviour in **Windows Media Player**, outside
Mucka altogether - "I don't always hear it, I have to click a lot of times and then it's only audible
some of the times."

It also explains a symptom that survived three rounds of fixes to the metronome's SCHEDULING: the click
was being scheduled correctly and then not reliably sounding.

## The fix, and why it needs no timing change

Prepending silence moves the transient later *within the file* by exactly the amount the file grows. The
metronome starts the pre-click early by the clip's total length (so the file ENDS at `boundary - N`), so
a file that is `pad` ms longer starts `pad` ms earlier - and the transient lands in exactly the same
place as before. The padding is pure slack for the playback engine to lose.

Idempotent: refuses a file that already has leading silence.
"""
import io
import os
import struct
import sys

PAD_MS = 30
SILENCE_FLOOR = 1.0 / 512.0     # ~ -54 dBFS; anything under this is silence for our purposes
TARGETS = ["Perc_Stick_hi.wav", "Perc_Stick_lo.wav"]
SOUNDS = os.path.join("Resources", "Raw", "sounds")


def chunks(data):
    pos = 12
    while pos + 8 <= len(data):
        cid = data[pos:pos + 4]
        size = struct.unpack("<I", data[pos + 4:pos + 8])[0]
        yield cid, pos, size
        pos += 8 + size + (size % 2)


def lead_in_ms(fmt, payload):
    """Milliseconds of near-silence at the start of the audio."""
    _tag, ch, sr, _br, align, bits = fmt
    n = len(payload) // align
    for i in range(n):
        off = i * align
        if bits == 16:
            level = max(abs(v) for v in struct.unpack_from("<" + "h" * ch, payload, off)) / 32768.0
        else:
            level = max(abs(v - 128) for v in payload[off:off + ch]) / 128.0
        if level > SILENCE_FLOOR:
            return i / sr * 1000.0
    return len(payload) / align / sr * 1000.0


def pad(path, dry_run):
    data = io.open(path, "rb").read()
    if data[:4] != b"RIFF" or data[8:12] != b"WAVE":
        print(f"  {os.path.basename(path):20} not a WAV - skipped")
        return False

    fmt = payload = None
    data_at = data_size = None
    for cid, pos, size in chunks(data):
        if cid == b"fmt ":
            fmt = struct.unpack("<HHIIHH", data[pos + 8:pos + 8 + 16])
        elif cid == b"data":
            data_at, data_size = pos + 8, size
            payload = data[pos + 8:pos + 8 + size]
    if fmt is None or payload is None:
        print(f"  {os.path.basename(path):20} no fmt/data chunk - skipped")
        return False

    _tag, ch, sr, _br, align, bits = fmt
    existing = lead_in_ms(fmt, payload)
    total = len(payload) / align / sr * 1000.0
    print(f"  {os.path.basename(path):20} {total:6.1f} ms, lead-in {existing:5.2f} ms", end="")

    if existing >= PAD_MS * 0.5:
        print("  -> already padded, left alone")
        return False

    silence_frames = int(round(PAD_MS / 1000.0 * sr))
    quiet = (b"\x00" if bits == 16 else b"\x80") * (align * silence_frames)
    if bits == 16:
        quiet = b"\x00" * (align * silence_frames)

    out = bytearray(data)
    out[data_at:data_at + data_size] = quiet + payload
    # Both sizes that describe the payload have to grow with it.
    struct.pack_into("<I", out, data_at - 4, data_size + len(quiet))
    struct.pack_into("<I", out, 4, struct.unpack("<I", data[4:8])[0] + len(quiet))

    print(f"  -> +{PAD_MS} ms silence, now {total + PAD_MS:.1f} ms", end="")
    if dry_run:
        print("  (dry run)")
        return False
    io.open(path, "wb").write(bytes(out))
    print("  written")
    return True


if __name__ == "__main__":
    dry = "--apply" not in sys.argv
    if dry:
        print("DRY RUN - pass --apply to write\n")
    changed = 0
    for name in TARGETS:
        p = os.path.join(SOUNDS, name)
        if not os.path.exists(p):
            print(f"  {name:20} not found at {SOUNDS}")
            continue
        changed += 1 if pad(p, dry) else 0
    print(f"\n{changed} file(s) changed."
          + ("" if dry else "  Rebuild to copy them into bin/."))
