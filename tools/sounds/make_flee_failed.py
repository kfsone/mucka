#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# ///
"""Synthesize sounds/flee-failed.wav - the buzzer for a failed flee.

Owner's brief (2026-08-19): an "NRK"/"UNGH" - the ELECTRONIC game-show wrong-answer buzzer, not a
recorded human grunt. Occasion: `You have fled by trying to go <dir>.` The player tried to run and
did not move, which in MUD2 still costs points, an experience level and the weapon in their hand,
and leaves them standing in front of whatever they were running from. The owner's own report of the
frame this was requested from: "If I'd waited a heartbeat longer to qq, i'd have died."

So it has to read as REJECTION, immediately and without being looked at, from across a glance at the
terminal - the same job a quiz-show buzzer does.

Generated rather than sourced, deliberately: every other non-clio sound in this project either ships
with the Clio licence or is a plain tone, and a buzzer lifted from a TV show would be neither. This
script IS the provenance - re-run it and you get the identical file, and the parameters below are the
sound's actual definition rather than a description of something binary and opaque.

Design, and why each part is there:

  * Two square waves a minor second apart (~14 Hz beating). Square, not sine, for the harsh odd
    harmonics that read as "electronic"; the near-unison pair is what makes it a BUZZ rather than a
    note. A single square wave sounds like a chiptune bass, which reads as game, not as refusal.
  * A downward pitch bend across the whole sound, 175 Hz -> 128 Hz. This is the "UNGH": falling pitch
    is heard as negative, deflating, an answer being rejected. Rising would read as a question or a
    prompt.
  * A hard 4 ms attack. Anything softer reads as a swell and loses the "NRK" bite.
  * Two burst gate. The classic quiz-show buzzer is not one continuous tone - it is a short
    double-hit, and the gap is most of what makes it recognisable AS a buzzer.
  * Deliberately 0.42 s in total. Long enough to be unmistakable, short enough not to mask the
    swing text arriving on the next tick.

Usage:
  uv run tools/sounds/make_flee_failed.py                 # writes Resources/Raw/sounds/flee-failed.wav
  uv run tools/sounds/make_flee_failed.py --out other.wav
  uv run tools/sounds/make_flee_failed.py --preview       # also play it, if a player is available
"""

import argparse
import math
import struct
import subprocess
import sys
import wave
from pathlib import Path

# 48 kHz 16-bit stereo, matching Perc_Stick_hi/lo.wav so every client-generated sound in the app
# shares one format and the WinRT player never has to switch rates mid-fight.
RATE = 48_000
CHANNELS = 2
SAMPLE_WIDTH = 2

DURATION = 0.42          # whole sound, seconds
PITCH_START = 175.0      # Hz, the "UN"
PITCH_END = 128.0        # Hz, the "GH" - falling reads as rejection
DETUNE = 14.0            # Hz above the fundamental; the beat rate that makes it buzz rather than hum
ATTACK = 0.004           # 4 ms - hard enough to bite
RELEASE = 0.060          # gentle enough not to click on the way out
AMPLITUDE = 0.34         # headroom for the two summed voices plus the harmonic content

# The two-burst gate: (start, end) in seconds. The GAP is the recognisable part - a single 0.42 s
# tone is a klaxon, two bursts is a buzzer.
BURSTS = [(0.000, 0.150), (0.190, 0.420)]


def square(phase: float) -> float:
    """A square wave, band-limited crudely by summing odd harmonics.

    Six harmonics rather than a hard sign() flip: a raw square at 48 kHz aliases audibly on the
    downward bend, which adds a gritty shimmer that sounds like a bad encode rather than like a
    buzzer. Six is enough for the character and stays clear of Nyquist for the fundamentals here.
    """
    total = 0.0
    for h in (1, 3, 5, 7, 9, 11):
        total += math.sin(phase * h) / h
    return total * (4.0 / math.pi) / 1.18   # /1.18 normalises the partial sum back to about +-1


def envelope(t: float) -> float:
    """Burst gate x attack/release shaping, evaluated per sample."""
    gate = 0.0
    for start, end in BURSTS:
        if start <= t < end:
            since = t - start
            until = end - t
            attack = min(1.0, since / ATTACK) if ATTACK > 0 else 1.0
            release = min(1.0, until / RELEASE) if RELEASE > 0 else 1.0
            gate = max(gate, attack * release)
    return gate


def render() -> bytes:
    total_samples = int(RATE * DURATION)
    frames = bytearray()
    phase_a = 0.0
    phase_b = 0.0

    for n in range(total_samples):
        t = n / RATE
        # Linear bend over the WHOLE sound, so the second burst starts lower than the first ended -
        # the two bursts together read as one falling gesture rather than as two separate hits.
        freq = PITCH_START + (PITCH_END - PITCH_START) * (t / DURATION)

        phase_a += 2.0 * math.pi * freq / RATE
        phase_b += 2.0 * math.pi * (freq + DETUNE) / RATE

        sample = (square(phase_a) + square(phase_b)) * 0.5 * AMPLITUDE * envelope(t)
        clipped = max(-1.0, min(1.0, sample))
        value = int(clipped * 32767)
        # Mono content written to both channels: this is an alert, and a buzzer that favoured one ear
        # would read as positional information it does not have.
        frames += struct.pack('<hh', value, value)

    return bytes(frames)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    default_out = Path(__file__).resolve().parents[2] / 'Resources' / 'Raw' / 'sounds' / 'flee-failed.wav'
    ap.add_argument('--out', type=Path, default=default_out)
    ap.add_argument('--preview', action='store_true', help='play the result after writing it')
    args = ap.parse_args()

    args.out.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(args.out), 'wb') as w:
        w.setnchannels(CHANNELS)
        w.setsampwidth(SAMPLE_WIDTH)
        w.setframerate(RATE)
        w.writeframes(render())

    size = args.out.stat().st_size
    print(f"wrote {args.out}  ({size} bytes, {DURATION * 1000:.0f} ms, "
          f"{RATE} Hz {CHANNELS}ch {SAMPLE_WIDTH * 8}bit)")

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
