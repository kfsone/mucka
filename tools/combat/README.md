# Combat analysis tools

External (python/uv) processing for MUD2 combat-session research.

The reducer reuses `tools/mapping/decode_probe.py` for telnet stripping
and C1 tag decoding, then stores a replayable evidence trail in SQLite:

- `raw_events` keeps decoded combat/protocol events plus tx/an records.
- `stats_snapshots`, `inventory_snapshots`, `room_snapshots`, and
  `status_effect_windows` keep ancillary state.
- `combat_sessions` holds encounter-level rows.
- `combat_fights` holds one row per per-NPC fight inside an encounter.
- `combat_events` holds the replayable combat event stream, including plain-text
  weapon-switch / weapon-break / guard-drop events that were not wrapped in
  literal `08.05` / `08.06` tags in the research capture.

## Not tracked: carried weight

**Carried weight is deliberately not captured, stored or displayed anywhere in this project.** The
`weight_carried_grams` / `max_weight_grams` columns survive in `schema.sql` only because existing
`combat.db` files have them; nothing populates them, and nothing should start.

The reasons, so this does not get "fixed" by someone completing the score-sheet parser:

- **It is only ever as fresh as the last `score`.** The FES heartbeat does not carry it, so the sole
  source is the sheet - a figure that is minutes old by construction.
- **It changes on every pick-up and drop**, and the client cannot see those. So the stored value is
  not merely stale, it is stale in a way nothing can detect.
- **It is insufficient for the one thing it would feed.** The published effective-strength formula
  needs a PER-OBJECT weight breakdown (its third step sums half of each object's weight, rounded down
  individually), which this line does not give. A total cannot reconstruct it.

Stale, undetectably so, and insufficient - and worse than nothing, because a number invites
arithmetic. Anyone who wants it can type `sc` and read it.

And it cannot be fixed by asking more often. There is deliberately no periodic `score` injection: the
sheet is a dozen-plus lines, MUD2's link is not fat, and pushing housekeeping down it delays the
combat text and the flee acknowledgement coming back the other way. (The cost is bandwidth, not a
game turn - MUD2 turns are short server slices that exist to stop action spam, not combat rounds.)

**Objects carried IS kept**: it has a live source in the FEI inventory list, so it can be trusted
between sheets.

## Usage

Initialize the default database:

```bash
uv run tools/combat/init_db.py
```

Reduce one or more captures into the database:

```bash
uv run tools/combat/reduce_combat.py G:\Source\mucka\RESEARCH\mud2-multi-combat.jsonl
```

Write to a custom database path:

```bash
uv run tools/combat/reduce_combat.py --db path\to\combat.db capture1.jsonl capture2.jsonl
```

If `uv` is unavailable locally, plain `python` also works:

```bash
python tools/combat/reduce_combat.py --db path\to\combat.db capture.jsonl
```

Generate the markdown fight summary from the populated database:

```bash
uv run tools/combat/summarize.py
```

Ingest live per-encounter clog files from `~/.mucka/clogs` into the same database:

```bash
uv run tools/combat/ingest_clogs.py
```

Run the merged mechanics analysis pass and print a coverage/effectiveness report:

```bash
uv run tools/combat/analyze_mechanics.py
```

This does NOT touch `MECHANICS_NOTES.md` by default: that file accumulates hand-written
live-session research findings on top of a small fixed methodology template, so refreshing it is
a separate, deliberate action:

```bash
uv run tools/combat/analyze_mechanics.py --write-notes
```

`--write-notes` itself refuses to shrink an existing `MECHANICS_NOTES.md` (i.e. it will not
overwrite a file bigger than the template), since a bigger file almost certainly holds
hand-written notes the template does not reproduce. Pass `--force-notes-overwrite` in addition
only if you are certain you want to discard that content.

Test every claim in `MUD2-PUBLISHED-MECHANICS.md` against the capture corpus and print a
SUPPORTED/REFUTED/INCONCLUSIVE/INSUFFICIENT DATA verdict per claim, with sample sizes so it is
obvious when a verdict has earned an upgrade. Meant to be re-run as more sessions accumulate, not
a one-off:

```bash
uv run tools/combat/verify_mechanics.py
uv run tools/combat/verify_mechanics.py --db path/to/combat.db
uv run tools/combat/verify_mechanics.py --db verify.db --claim knees --claim damage
uv run tools/combat/verify_mechanics.py --list
```

## Current detection rules

- Combat starts on `08` family protocol events, never on room text or NPC presence.
- A bare `08` while already in combat is treated as a joiner / escalation inside
  the current session, not a new session.
- Bare `08` start text is classified as either player-initiated (`You attack...`)
  or NPC-initiated (`The X is ...`).
- Plain decoded prose is also scanned for combat-only weapon/guard transitions:
  `You are now using...`, switch/drop-guard text, weapon-break text, and the
  confusion guard-drop text.
- `08 10`, `08 11`, and `08 12` are explicit combat ends.
- `08 08` / `08 09` are only treated as implicit session ends if no further combat
  activity follows within a short window.
- `06 03` / `06 04` force-close any open combat as a reset boundary.

See `tools/combat/NOTES.md` for the observed behavior of the provided
`mud2-multi-combat.jsonl` research capture, and `tools/combat/SUMMARY.md`
for roll-up totals by weapon and NPC.

## Fight-end detection

`FIGHT-ENDS.md` is required reading before touching anything that decides when a fight or an
encounter is over. It lists the eight known ways MUD2 ends a fight, with a verbatim captured frame for
each, the per-creature vs all-fights split, and the frame guarantee that makes timers unnecessary. It
also records the one conclusion in `SESSION-NOTES-20260810.md` that turned out to be inverted, and
why -- that mistake lived in shipped code for months, so the correction is worth reading before
re-deriving the same wrong answer from the same capture.

Three of the eight ends were found by the owner noticing the readout was wrong, not by reading a
capture, and each time the then-current list said the frame he was looking at could not happen. Read
the list as open. `CombatTracker.NoteRoomChanged` is the backstop for the next one: a room change
proves a fight is over (you cannot walk out of one), and it force-ends with the reason string
`(forced end: room changed)` -- **grep clogs for that string before believing fight-end detection is
complete.**

## Concurrency and per-tick damage

Two standalone queries over `~/.mucka/clogs`, needing no database. They exist because the spec's
"maximum 4 simultaneous opponents" claim was stale for a month and nobody could cheaply re-check it -
prefer re-running these to citing any number written down elsewhere, including in `COMBAT-RAIL-SPEC.md`.

Peak simultaneously-engaged NPCs per encounter, as a histogram plus every fight at 5 or more:

```bash
uv run tools/combat/concurrency.py        # whole clog corpus
uv run tools/combat/concurrency.py 3      # only clogs modified in the last 3 days
```

What a tick actually cost in those fights - real worst single tick from exact post-hit stamina,
beside what the flee pill's worst-case-tick figure would have predicted:

```bash
uv run tools/combat/tickdamage.py
```

Both tolerate a half-written tail line, so they can be run while the game is open - the newest clog
belongs to a live session.

**Result as of 2026-08-28** (984 encounters): 85% are 1v1, but the maximum is **7** (2026-08-27), and
12 encounters have peaked at 5 or more since 2026-08-04. Mean damage per landed blow across the 38
encounters at 4+ concurrent is **3.2**, worst single observed tick **29**, biggest single blow **29**.
The pill's predicted worst-case tick runs roughly 1.5-2x the worst tick actually observed, which is the
intended pessimism - it assumes every live opponent lands, and rats miss about two swings in three.

## Tick phase: is the anchor any good?

Both the bar and the click take their phase from one instant - the first swing of an encounter
(`SidePanelViewModel.TickPhaseUtc`). These two queries test whether that is a sound choice.

```bash
uv run tools/combat/anchorphase.py        # per-encounter: first vs second vs third swing as anchor
uv run tools/combat/sessionlattice.py    # decisive: fit ONE lattice per session, judge the anchor against it
```

**Result as of 2026-08-28.** The session-wide lattice holds - one 2000 ms phase fits a whole play
session with a median mean-residual of **26.5 ms** across 65 sessions, confirming the spec's claim.
Measured against it, the FIRST-SWING anchor is: median **35 ms**, p75 118, p90 250, p99 846,
max **963** - so **18.9% of encounters are off by >150 ms and 6.5% by >500 ms.**

That is the fault behind "the ticker didn't seem to coincide with the server's combat tick - I was
receiving combat messages about 3/5th of the way thru the slider". The median is fine; the tail is not,
and the player only notices the tail.

**The spec's own justification argues against the implementation.** Section 6 says the phase is set
once per encounter because "one lattice fits a whole 40-minute session to ~4 ppm, so the phase does not
need chasing". The first half is true and measured. The conclusion drawn from it is backwards: if one
lattice fits the whole session, then discarding the previous encounter's evidence and re-deriving the
phase from a single noisy sample is strictly worse than keeping a session estimate.

`anchorphase.py` also shows a later swing is a better anchor than the first (median |residual| 4 ms
anchored on the third swing vs 13 ms on the first), and `opener_phase.py` isolates one confirmed
contributor:

```bash
uv run tools/combat/opener_phase.py
```

When the first swing arrives in the same frame as a player-initiated `kill` reply, its timestamp carries
the KEYSTROKE's phase. Those 48 encounters are **>100 ms off 52.1%** of the time, against **18.4%** for
openers that arrive more than a second after the fight starts (overall rate: 26.8%). That is the same
error the spec attributes to anchoring on the InCombat flip - anchoring on the first swing only partly
escapes it.

*Earlier text here said ">100 ms off 52% of the time against a ~20% baseline" and credited a script
called `whyoff`, which was never committed. The 52.1% is real and at 100 ms; the "~20%" was the >150 ms
overall rate (18.9%) quoted against a >100 ms percentage. `opener_phase.py` prints both thresholds so
the two cannot be conflated again.*

### The fix, and its measured effect

`Mucka.Core.TickPhase` replaced the single-sample anchor with a session-scoped estimate: an
exponentially-forgetting **circular** mean of folded swing residuals, re-based when it moves more than
15 ms, never reset between encounters. Circular because the quantity is an angle - a residual of +990 ms
and one of -1010 ms are the same phase, and an ordinary mean or median of folded residuals averages two
identical readings into a phase half a tick away.

```bash
uv run tools/combat/validate_tickphase.py    # replays the corpus through both, scores against the session lattice
```

**Result as of 2026-08-28**, anchor error against each session's own best-fit lattice:

| anchor | median | p90 | p99 | worst | >150 ms | >500 ms |
|---|---|---|---|---|---|---|
| old: encounter's first swing | 35.0 ms | 250 ms | 846 ms | 963 ms | 18.9% | 6.5% |
| new: estimate at fight start | 21.4 ms | 92 ms | 573 ms | 857 ms | **6.8%** | **2.4%** |
| new: estimate at fight end | 21.4 ms | 83 ms | 607 ms | 947 ms | 7.0% | 2.0% |

The bad tail shrinks by about 2.8x.

**There is probably little left to win on this data, but do not read 21.4 ms as a proven floor.** The
reference lattice these errors are measured against is itself a fit, whose own agreement with the swings
is a median-across-sessions of 26.5 ms - a different statistic of a different quantity from a median of
per-encounter anchor errors, so the two are not directly comparable and "at the noise floor" overstates
what the comparison establishes. The defensible claim is narrower: the estimator's error is now the same
order as the reference's own, so this corpus cannot resolve further improvement.

**The remaining 2.4% over half a second is not explained.** Candidate: a session that spans a MUD2
reset, where the server's lattice genuinely moves and one estimate cannot describe both halves. That
would show as a session whose best-fit lattice has an unusually poor residual, which is testable by
splitting such sessions at the reset boundary and re-fitting - worth doing before anyone tunes `Decay`
in response to the tail.

## Reset identity in the clogs (new, 2026-08-28)

Clog headers now carry a `reset` block, so an encounter can be attributed to the reset it happened in:

```json
"reset": { "targetUtcMs": 1787950812000, "uncertaintySec": 0.4, "phase": "Locked",
           "timeToReset": 1180, "derivedEpochMs": 1787950812345 }
```

**Group on `targetUtcMs`.** It is `ResetClock`'s single converged estimate of the reset instant, so it
stays put across every encounter in a reset. `null` before the clock has locked; `phase` and
`uncertaintySec` say how much to trust it.

**Do not group on `derivedEpochMs` without bucketing.** That is `ts + timeToReset * 1000`, the same
expression `swings.reset_epoch_ms` holds - and `CombatDb`'s comment calling it "constant across every
swing of one reset" is wrong as written. The FES reading is whole seconds, so the derived instant jitters
by up to a second between observations and grouping on it raw splits one reset into many. It is recorded
only because it is the sole reset context available in a clog written before the lock.

**What this was added for.** `validate_tickphase.py` leaves 2.4% of encounters with a tick phase more
than half a second off their session's best-fit lattice, and the leading explanation is a session that
SPANS a reset - where the server's lattice genuinely moves and one estimate cannot describe both halves.
That was untestable while nothing recorded which side of a reset an encounter sat on. The query to run
once a few multi-reset sessions have accumulated: split each session at its reset boundaries, re-fit a
lattice per segment, and see whether the >500 ms tail collapses. If it does, `TickPhase` should reset on
a reset boundary rather than carrying an estimate across one.

Older clogs have no `reset` block at all - treat a missing key as unknown, never as a single reset.

## The click samples had no leading silence (2026-08-28)

`Perc_Stick_hi.wav` / `Perc_Stick_lo.wav` were 169.6 ms files whose **peak sat at 0.48 ms and 1.58 ms** -
the entire audible transient in the first ~6 ms, everything after it tail more than 20 dB down, and no
leading silence at all. A percussive sound with all its content at the start of the buffer is at the mercy
of whatever plays it: any engine that begins a fraction late, ramps in, or drops its first buffer eats the
sound whole.

That is not inference. The owner reported the same behaviour in **Windows Media Player**, outside Mucka
entirely: *"I don't always hear it, I have to click a lot of times and then it's only audible some of the
times."* It is also why "still not hearing the tik" survived three rounds of fixes to the metronome's
SCHEDULING - the click was being scheduled correctly and then not reliably sounding.

```bash
uv run tools/combat/pad_click_samples.py           # dry run
uv run tools/combat/pad_click_samples.py --apply   # prepend 30 ms of silence
```

**No timing change was needed, and that is worth understanding before touching either.** The metronome
starts the pre-click early by the clip's *total* length so the file ENDS at `boundary - N`. A file that is
30 ms longer therefore starts 30 ms earlier, and its transient lands in exactly the same place as before -
verified at `boundary - 218 ms` both before and after. The padding is pure slack for the playback engine
to lose.

The script is idempotent (it refuses a file that already has leading silence), so it is safe to re-run
after replacing an asset - and it should BE re-run then, because a fresh export will have the same
problem.

### Audit: the clio sounds do NOT need the same fix

Audited all 76 assets after the Perc_Stick fix, on the hypothesis that the short combat sounds might share
the fault. **They do not, and the reasoning matters because "the clicks needed padding" invites padding
everything.**

The Perc_Stick failure shape was specific: ~6 ms of content at **-14.5 dBFS**, followed by 164 ms of
inaudible tail. Lose the front and you lose the entire sound. The combat sounds are a different shape -
continuous content across the whole file, at **0 to -1 dBFS**:

```
clio.0801   31.2 ms   per-ms %peak: 100 80 27 81 64 90 37 64 46 33 44 49 33 19 44 ...
clio.0803   11.8 ms                  65 93 66 79 61 76 85 88 36 100 88 50
clio.070000 10.4 ms                  50 90 93 79 65 61 76 85 88 36 100
```

No transient-then-tail. Losing a few milliseconds off the front clips them slightly; it does not silence
them. And they are 13 dB louder than the clicks, so a partial playback is still plainly audible.

**Confirmed by observation, which is what settles it:** the owner reported hearing every hit including
kills, across four fights, with the rail both on and off, in the same session that the tik was inaudible.
The clio sounds are working.

**Padding them would not be free.** The metronome's pad cost nothing because its scheduler starts the clip
early by its own length, absorbing it. A hit sound fires on a server event with no future boundary to aim
at, so 30 ms of silence is 30 ms of added latency with nothing to claw it back. Nothing in the codebase
reads a clio asset's duration (`WavDurationMs` is called only for `Perc_Stick_hi.wav`), so padding is
*safe* - it is just not *warranted*.

**What would change the answer:** hits starting to sound thin or intermittent. The open question the audit
could not settle from static analysis is whether the pooled playback path - which re-assigns `Source` on
every play, unlike the metronome's one-time open - truncates audio or merely delays its start. That needs
a loopback recording of one clio sound played repeatedly, the equivalent of the Media Player check that
confirmed the Perc_Stick fault.

Incidental findings, recorded rather than acted on:

- **Byte-identical duplicates** (MD5-confirmed): `clio.070000`/`0703`/`0704`/`0705` are one file shipped
  four times; likewise `070100`/`070101`, `070200`/`201`/`202`, and all 19 spell sounds
  `clio.1100`-`1121`. Copies, not links - a per-file fix must touch every path.
- **`clio.1324.wav` is not PCM**: format tag `0x0055`, MP3 in a WAV wrapper. Any header-based tool will
  refuse it, including `pad_click_samples.py`.
- **Level spread across the set is 14 dB** (0 dBFS on most 8-bit clio assets, -14.5 on the clicks). The
  clicks are the outlier, not the hits - so if the tik-tok reads weak against combat text now that its
  attack survives, the clicks are the end to move.
- **Clipping** on several loud-by-design files: `clio.1309` (BANG) ~12% of samples at full scale,
  `clio.1325` (HAWUMPH) ~10%, `clio.1307` ~7%, `clio.1301` (MOO) ~4%. Plausibly intentional.
- **No format consistency**: mostly 22050 Hz / 8-bit / stereo, but 48000/16, 44100/16, 32000/24 mono and
  22050/24 all appear. A level-and-format pass is separate work from anything above.
