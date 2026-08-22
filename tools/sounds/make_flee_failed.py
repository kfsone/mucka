#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# ///
"""Synthesize a buzzer for a failed flee.

SUPERSEDED, 2026-08-22: the sound that actually ships (Resources/Raw/sounds/mucka.flee_failed.wav)
is the owner's own, not this script's output. This is kept as history and as a fallback - it
documents what was tried and why - and it REFUSES TO OVERWRITE an existing file, so running it
cannot destroy the shipped sound. Pass --force only if you genuinely mean to replace it.

Owner's brief, for the record: an "NRRRK" / "UNNNK" / "BZZT". A SINGLE NOTE, buzzer-like. Not an
electrical zap. Then: harsher, more saw, half an octave deeper.

Occasion: `You have fled by trying to go <dir>.` The player tried to run and did not move, which in
MUD2 still costs points, an experience level and the weapon in their hand, and leaves them standing
in front of whatever they were running from. It has to read as rejection instantly, without being
looked at.

One oscillator, one pitch, one burst. An earlier version of this file had a pitch bend, a detuned
second voice and a two-burst gate, and the owner's verdict was that it was too complex - a buzzer is
not a composition. Everything below is deliberately the smallest set of parameters that still
produces a buzz rather than a musical note.

SAWTOOTH, not square (owner: "harsher - more saw"). A square carries only ODD harmonics, which is
what gives it that hollow, slightly woodwind quality; a saw carries every harmonic, and the even ones
filling the gaps are the whole difference between "a low note" and "a rasp". Same reason a brass
instrument sounds harsher than a clarinet.

What keeps it a buzz and not a zap is the ceiling, not the waveform: harmonics stop around 2.5 kHz.
A saw summed all the way to Nyquist puts energy right up the spectrum, and that broadband hiss is
exactly the electrical-spark character to avoid. Low fundamental + every harmonic + a hard ceiling is
a rasp; the same thing unbounded is a spark.

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

# Half an octave below the previous 150 Hz (owner), i.e. 150 / sqrt(2). Deep enough to land in the
# chest as "UNNNK" while staying well clear of the range small laptop speakers simply cannot produce.
FREQUENCY = 106.0    # Hz
DURATION = 0.28      # s. Long enough to be unmistakable, short enough not to mask the next tick.
HARMONICS = 24       # ALL harmonics 1..24, so the ceiling sits near 2.5 kHz - see the module docstring.
ATTACK = 0.003       # s. Hard, so it reads as "NRRRK" and not as a swell.
RELEASE = 0.030      # s. Just enough to avoid a click on the way out.
# Set against RMS, not peak. A saw's ramp spends less time near its extremes than a square's flat
# top does, so the two are nowhere near equally loud at equal peak - swapping the waveform at the
# previous 0.40 dropped perceived level by about 4.5 dB and made the alert quieter than the thing it
# replaced. 0.62 restores the square version's ~30% RMS and still leaves ~38% of headroom.
AMPLITUDE = 0.62


def saw(phase: float) -> float:
    """Band-limited sawtooth: EVERY harmonic to the ceiling, unnormalised."""
    total = 0.0
    for h in range(1, HARMONICS + 1):
        total += math.sin(phase * h) / h
    return total


def peak_of_one_cycle() -> float:
    """Measures the partial sum's actual peak instead of assuming it.

    A truncated Fourier series overshoots (Gibbs), by an amount that depends on how many harmonics
    were kept - so a hardcoded normalisation constant silently becomes wrong the moment HARMONICS is
    touched, and the file clips or goes quiet with no obvious cause. Measuring costs one cycle at
    startup and makes the knob safe to turn.
    """
    step = 2.0 * math.pi / 4096
    return max(abs(saw(i * step)) for i in range(4096)) or 1.0


def envelope(t: float) -> float:
    """Flat, with a hard attack and a short release. No gate, no shaping in between."""
    attack = min(1.0, t / ATTACK) if ATTACK > 0 else 1.0
    remaining = DURATION - t
    release = min(1.0, remaining / RELEASE) if RELEASE > 0 else 1.0
    return max(0.0, attack * release)


def render() -> bytes:
    frames = bytearray()
    phase_step = 2.0 * math.pi * FREQUENCY / RATE
    scale = AMPLITUDE / peak_of_one_cycle()
    for n in range(int(RATE * DURATION)):
        t = n / RATE
        sample = saw(n * phase_step) * scale * envelope(t)
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
    ap.add_argument('--force', action='store_true',
                    help='overwrite an existing file (refused by default - see the module docstring)')
    args = ap.parse_args()

    # The shipped sound is the owner's, not this script's. Silently replacing it with a synthesized
    # approximation because someone ran the generator to see what it did would be a real loss and an
    # entirely invisible one - the file is binary, so a stray regeneration reads as noise in a diff.
    if args.out.exists() and not args.force:
        print(f"refusing to overwrite {args.out}", file=sys.stderr)
        print("  The shipped sound is the owner's own, not this script's output.", file=sys.stderr)
        print("  Re-run with --force to replace it, or --out <path> to write elsewhere.",
              file=sys.stderr)
        return 1

    args.out.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(args.out), 'wb') as w:
        w.setnchannels(CHANNELS)
        w.setsampwidth(SAMPLE_WIDTH)
        w.setframerate(RATE)
        w.writeframes(render())

    print(f"wrote {args.out}  ({args.out.stat().st_size} bytes, {DURATION * 1000:.0f} ms, "
          f"saw at {FREQUENCY:.0f} Hz, {HARMONICS} harmonics to {FREQUENCY * HARMONICS:.0f} Hz)")

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
