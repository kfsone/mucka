#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# ///
"""Synthesize sounds/mucka.flee_failed.wav - the buzzer for a failed flee.

Owner's brief: an "NRRRK" / "UNNNK" / "BZZT". A SINGLE NOTE, buzzer-like. Not an electrical zap.

Occasion: `You have fled by trying to go <dir>.` The player tried to run and did not move, which in
MUD2 still costs points, an experience level and the weapon in their hand, and leaves them standing
in front of whatever they were running from. It has to read as rejection instantly, without being
looked at.

One oscillator, one pitch, one burst. An earlier version of this file had a pitch bend, a detuned
second voice and a two-burst gate, and the owner's verdict was that it was too complex - a buzzer is
not a composition. Everything below is deliberately the smallest set of parameters that still
produces a buzz rather than a musical note.

What makes it a buzz and not a zap: the harmonics stop at the 13th (about 1.9 kHz here). A raw
square wave, or one summed to Nyquist, puts energy right up the spectrum and that broadband hiss is
exactly the "electrical zap" character to avoid. Low fundamental plus a hard edge plus a bounded
harmonic series is a buzzer.

Usage:
  uv run tools/sounds/make_flee_failed.py
  uv run tools/sounds/make_flee_failed.py --preview     # play it after writing
"""

import argparse
import math
import struct
import subprocess
import sys
import wave
from pathlib import Path

# 48 kHz 16-bit stereo, matching Perc_Stick_hi/lo.wav so every client-generated sound shares a format.
RATE = 48_000
CHANNELS = 2
SAMPLE_WIDTH = 2

FREQUENCY = 150.0    # Hz. Low enough to land as "UNNNK" rather than a beep, high enough to carry.
DURATION = 0.28      # s. Long enough to be unmistakable, short enough not to mask the next tick.
HARMONICS = 7        # odd partials (1,3,..,13). More would sizzle; fewer would sound like a flute.
ATTACK = 0.003       # s. Hard, so it reads as "NRK" and not as a swell.
RELEASE = 0.030      # s. Just enough to avoid a click on the way out.
AMPLITUDE = 0.40


def square(phase: float) -> float:
    """Band-limited square: odd harmonics only, stopped well below Nyquist."""
    total = 0.0
    for k in range(HARMONICS):
        h = 2 * k + 1
        total += math.sin(phase * h) / h
    return total * (4.0 / math.pi) / 1.18   # normalise the partial sum back to about +-1


def envelope(t: float) -> float:
    """Flat, with a hard attack and a short release. No gate, no shaping in between."""
    attack = min(1.0, t / ATTACK) if ATTACK > 0 else 1.0
    remaining = DURATION - t
    release = min(1.0, remaining / RELEASE) if RELEASE > 0 else 1.0
    return max(0.0, attack * release)


def render() -> bytes:
    frames = bytearray()
    phase_step = 2.0 * math.pi * FREQUENCY / RATE
    for n in range(int(RATE * DURATION)):
        t = n / RATE
        sample = square(n * phase_step) * AMPLITUDE * envelope(t)
        value = int(max(-1.0, min(1.0, sample)) * 32767)
        # Mono content on both channels: an alert must not imply a direction it does not have.
        frames += struct.pack('<hh', value, value)
    return bytes(frames)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    default_out = (Path(__file__).resolve().parents[2]
                   / 'Resources' / 'Raw' / 'sounds' / 'mucka.flee_failed.wav')
    ap.add_argument('--out', type=Path, default=default_out)
    ap.add_argument('--preview', action='store_true', help='play the result after writing it')
    args = ap.parse_args()

    args.out.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(args.out), 'wb') as w:
        w.setnchannels(CHANNELS)
        w.setsampwidth(SAMPLE_WIDTH)
        w.setframerate(RATE)
        w.writeframes(render())

    print(f"wrote {args.out}  ({args.out.stat().st_size} bytes, {DURATION * 1000:.0f} ms, "
          f"{FREQUENCY:.0f} Hz, {HARMONICS} odd harmonics)")

    if args.preview:
        try:
            subprocess.run(
                ['powershell', '-NoProfile', '-Command',
                 f"(New-Object Media.SoundPlayer '{args.out}').PlaySync()"],
                check=False, timeout=15)
        except Exception as exc:   # preview is a convenience, never a failure
            print(f"(preview unavailable: {exc})", file=sys.stderr)

    return 0


if __name__ == '__main__':
    raise SystemExit(main())
