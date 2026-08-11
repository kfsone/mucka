# Combat tick indicator: phase review

Measured 2026-08-11 from two live captures against `mud2.co.uk`.
Reproduce with:

```
python tools/combat/analyze_tick_phase.py \
  --db  <verify.db> \
  --capture session-rec.mud2.co.uk.20260810-152631.jsonl \
  --capture session-rec.mud2.co.uk.20260810-161441.jsonl
```

**Corpus.** 2 captures (2484 s and 952 s of play), 16 encounters, **177 distinct swing
arrival instants** (369 swing lines, de-duplicated to the rx packet that delivered them -
several swing lines share one packet and the client learns of all of them at once), 497
telnet echo round trips, 503 command turnarounds, 1429 FES heartbeats, 33 regeneration
steps. Everything below is small-n at the *fight* level (16) and moderate-n at the *swing*
level (177). Treat per-fight percentages as indicative; the swing-level and RTT
distributions are solid.

**Clock used.** `SessionCapture.RecordRx` stamps a packet the instant `ReadAsync` returns,
*before* `MudSession.Feed`. So a swing's `timestamp_ms` is the earliest instant the client
could possibly have known about that swing - the right baseline for asking how late the
indicator is, because everything the client does afterwards only adds.

---

## 1. The measured numbers

### 1.1 The tick lattice is real, and tighter than the spec claims

Fitting `phi + k*2000 ms` to each fight's own swing arrivals:

| pooled within-fight residual | value |
|---|---|
| p5 / p25 / p50 / p75 | -2.5 / -1.0 / 0.0 / +1.0 ms |
| p95 / p99 / max | +128 / +173 / +189 ms |
| median absolute deviation | **1.0 ms** |

n = 177. Half of all swing arrivals land within **1 ms** of their fight's lattice. This is a
much stronger lock than the "76-94% in a single 20 ms bin" already on record: the 20 ms
binning was hiding a sub-millisecond core.

Session-wide, one lattice per capture:

| capture | n | span | phi | \|res\| median | max | one 20 ms bin holds | drift over span |
|---|---|---|---|---|---|---|---|
| `0a605df5144a` | 80 | 2484 s | 172 ms | 4.5 ms | 202 ms | 72% | **-9.5 ms** |
| `6dbee77203eb` | 97 | 952 s | 151 ms | 3.0 ms | 156 ms | 88% | **-7.0 ms** |

The phase holds across an entire 41-minute session, not just within a fight. Residual drift
is -7 to -10 ms end to end (~4 ppm - ordinary crystal/NTP-slew territory, and the two
captures' `phi` differ by 21 ms across a 22-minute gap, consistent with that). **There is no
per-fight phase. There is one session phase.** That single fact reshapes the whole problem.

### 1.2 The current anchor is the entire error budget

`_tickPhaseAnchorUtc = DateTime.UtcNow` is taken at the first `Live` refresh of the fight,
which `CombatTracker.Begin()` triggers from whichever line comes first. In all 16 encounters
that line was the **fight-start line** (`08.00` - "You attack the zombie...", or an NPC aggro
line), never a swing. A fight-start line is *not on the tick lattice*: it is the server's
reply to the player's `kill` command, so its phase is whatever the player's keystroke
happened to be.

Signed anchor error against each fight's own lattice (n = 16):

- median 0 ms, mean -58 ms - **the mean is meaningless here**, the errors cancel
- **|error| median 152 ms, p90 790 ms, max 972 ms**, range -898 .. +972 ms
- essentially uniform on [-1000, +1000]: see the 100 ms histogram in the script output

Because the bar empties every 2000 ms, a -600 ms anchor is not "early" in play - the bar's
next empty is 1400 ms *after* the swing. Folded that way (`err mod 2000`, i.e. "how long
after the swing does the bar reach empty"):

| lag-to-next-empty | value |
|---|---|
| median | **1037 ms** |
| p25 / p75 | 97 / 1646 ms |
| fights > 200 ms late | **10/16 (62%)** |
| fights > 500 ms late | 10/16 (62%) |
| fights > 1000 ms late | 8/16 (50%) |

**The owner's "300+ms" report is measured and is if anything an understatement.** Median lag
in this corpus is ~1 s. It is not a systematic bias - it is a per-fight lottery, which is
exactly why it feels intermittent: 6 of 16 fights were accidentally within 200 ms and felt
fine, 10 were not.

Anchoring on the **first swing line** instead, same 16 fights: |error| median **22 ms**,
max **189 ms**. One change, a 7x improvement in the median and a 5x improvement in the worst
case. (Pipeline delay - decode, `CombatTracker`, `MainThread.BeginInvokeOnMainThread`,
`RefreshCombatDisplay` - is *not* included in these numbers; it is unobservable in the
capture and adds a small positive bias on top of everything, probably single-digit to low
tens of ms. The script takes `--pipeline-ms` if you ever measure it.)

### 1.3 Within a fight: no drift, tiny jitter, a discrete late tail

Per fight, first-half vs second-half median residual: median drift **-1 ms**, p90 |drift|
25 ms, max 92 ms (n = 16). The 92 ms case is encounter 1, which has only 5 swings and whose
first arrival is a +189 ms outlier - that is a small-sample artefact, not drift.

**Phase does not drift within a fight, and barely drifts within a session.** The spread is
pure arrival jitter, and it is one-sided:

| swing arrival minus earliest-on-lattice | value |
|---|---|
| p50 / p75 | 6.5 / 7.5 ms |
| p90 / p95 / p99 | 47 / 135 / 179 ms |
| max | 196 ms |
| within 25 ms of earliest | **88.1%** |
| within 50 / 100 / 200 ms | 90.4% / 93.8% / 100% |

So ~88% of swings arrive in a sub-25 ms window and ~9% arrive 120-196 ms late. The late tail
is **not** diffuse: it clusters at 140-180 ms, which is one echo RTT (see below). Inference
(not measured): that tail is a TCP retransmit or a delayed-ACK-coupled send, i.e. a lost
segment resent one RTT later. If so it is unpredictable in principle - no estimator will
ever see it coming - but it is always *late*, which as it happens is the harmless direction.

### 1.4 Transit

Telnet echo round trips (client sends `<cmd>\r\n`, server echoes it verbatim; repeated
identical commands excluded so a later send is never matched to an earlier echo):

| echo RTT | value |
|---|---|
| n | 497 |
| min / p5 / p50 / p95 / max | 135 / 140 / **144** / 149 / 160 ms |
| jitter p95-p5 | **9 ms** |
| jitter p99-p1 | 14 ms |

One-way transit ~72 ms, and **the transatlantic link is far more stable than expected: 9 ms
of jitter at p95-p5, 481 of 497 samples in one 20 ms bin.** The transport is not the problem.

Command turnaround (tx to the first rx carrying the game's *reply*, not the echo): n = 503,
p50 **332 ms**, mode tightly in 320-340 ms. Minus the 144 ms echo RTT, that leaves ~**188 ms**
of server-side think-plus-flush.

### 1.5 Candidate anchors, scored as one-step-ahead predictors

For each swing after the first in each fight, each estimator predicts that swing's arrival
using only prior information. n = 161 predictions.

| estimator | \|err\| median | \|err\| p90 | \|err\| max | fires early |
|---|---|---|---|---|
| **A. current** (fight-start anchor) | **103 ms** | **681 ms** | **974 ms** | 52% |
| B. first swing of the fight | 30 ms | 153 ms | 194 ms | 25% |
| C. running median, reset each fight | 2.0 ms | 70 ms | 194 ms | 34% |
| D. running p15, reset each fight | 1.0 ms | 45 ms | 194 ms | 57% |
| E. session median (warm across fights) | 4.0 ms | 34 ms | 194 ms | 13% |
| **F. session p15 (warm across fights)** | **2.0 ms** | **16 ms** | 194 ms | 30% |
| G. session->fight blend (n/8 weight) | 2.2 ms | 36 ms | 194 ms | 25% |

|err| median by swing index within the fight (index 1 = second swing; 10+ pooled):

| estimator | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 |
|---|---|---|---|---|---|---|---|---|---|---|
| A current | 152 | 146 | 144 | 149 | 102 | 114 | 127 | 127 | 104 | 16 |
| B first swing | 22 | 23 | 46 | 4 | 42 | 44 | 60 | 30 | 52 | 2 |
| C fight median | 22 | 12 | 3 | 2 | 1 | 1 | 2 | 2 | 2 | 1 |
| D fight p15 | 22 | 5 | 3 | 1 | 1 | 0 | 1 | 1 | 2 | 1 |
| **F session p15** | **2** | **2** | **2** | **2** | **1** | **2** | **2** | **2** | **2** | **1** |

Read the A row carefully: **the current anchor never improves.** It cannot - it is fixed for
the fight's duration, so whatever it got wrong at second 0 it is still getting wrong at
second 80. Fight-local estimators (C, D) converge but need ~3 swings to do it, and swings
only arrive on **42% of ticks** (swing-to-swing gaps: 68 x 1 tick, 46 x 2, 35 x 3, 12 x 4;
n = 161), so "3 swings" is 6-12 s into a fight. The session-warm estimator (F) is already at
2 ms on the *first* prediction of a fight, because the phase it needs was learned in the
*previous* fight. Median fight-start-to-first-swing delay is 712 ms (range 0-2000 ms), and
median time to the 4th swing is ~13 s - so a fight-local estimator spends the dangerous
opening seconds still wrong.

The one 194 ms max shared by C-G is a single event: swing #2 of encounter 1, the first fight
of the first capture, where no prior phase existed at all.

### 1.6 The rejected phase sources

- **FES heartbeat: unusable.** n = 1429. Inter-arrival median 1251 ms (p5 527, p95 5008) and
  phase mod 2000 is **uniform** (|residual| median 472 ms, p5..p95 -871..+857). This is
  expected once you look: FES is a *reply to a client-issued probe* (1707 probes in the
  corpus), so it carries the client's own polling phase, not the server's tick.
- **Regeneration ticks: unusable.** n = 33 stamina increases, phase also uniform (|residual|
  median 447 ms). Regen state is only visible via FES polls, so the observable instant is
  quantised by the poll, not by the server tick. Even if regen is genuinely tick-locked, this
  measurement path destroys the information.

---

## 2. Recommendation

### 2.1 Yes, lead it - and the owner is right, but for a better reason than "it feels safer"

The strong reason is not preference, it is that **the swing text is already a report of the
past when it arrives.** One-way transit is ~72 ms (measured), and server think-plus-flush is
~188 ms (measured for commands; *inferred*, not measured, for tick-generated output). A bar
that empties exactly when the text lands is already ~150-260 ms behind the server-side event
it is timing. Leading by ~150 ms does not make the instrument dishonest; it moves it *closer*
to the moment being timed.

The second reason is the jitter shape. The tail is one-sided late (9% of arrivals 120-196 ms
late, 0% meaningfully early). A lead is therefore nearly free: making the bar empty earlier
cannot collide with an arrival that was going to be earlier still, because there aren't any.

Measured, with estimator F (session-persistent p15) as the predictor, n = 175:

| lead | warned before the text | median margin | p5 margin | p1 margin | worst-case early | failures |
|---|---|---|---|---|---|---|
| 0 ms | 42.3% | -1 ms | -6 | -50 | +184 | 101 |
| 25 ms | 98.3% | +24 | +19 | -25 | +209 | 3 |
| 50 ms | 98.9% | +49 | +44 | -0 | +234 | 2 |
| **100 ms** | **99.4%** | **+99** | **+94** | +50 | +284 | **1** |
| 150 ms | 99.4% | +149 | +144 | +100 | +334 | 1 |
| 200 ms | 100% | +199 | +194 | +150 | +384 | 0 |

With lead 0 the instrument is a coin flip: **it reports the past 58% of the time.** That is
the strongest single argument in this document for leading at all. Note also how bad the
fight-local estimator is at low lead by comparison (estimator C at lead 25: 90.1%, 16
failures; F at lead 25: 98.3%, 3 failures) - the lead and the estimator have to be chosen
together.

**Recommended lead: 120 ms visual.** It buys >99% warned-before-text with a p5 margin of
~+114 ms, costs at most ~300 ms of premature emptying in the worst observed case, and sits
inside the ~150-260 ms window by which the text already trails the server event - so the bar
is not claiming to know the future, it is correcting for a known constant delay. Do not go
past 200 ms: beyond that the bar is emptying while the *previous* tick's text is still being
read, and the gain over 150 ms is one sample out of 175.

### 2.2 Visual and audible need different leads

They do not want the same number.

- **Visual: 120 ms.** A bar reaching empty is a continuous, spatially-precise signal; the eye
  resolves its arrival against the text appearing beside it. Early is visible.
- **Audible: 40-60 ms.** Auditory-visual simultaneity tolerance is asymmetric and wide -
  ~100 ms of sound-before-vision still reads as one event. A click 120 ms before the text
  would be heard as *before*, which is worse than useless for a rhythm instrument: the whole
  value of the metronome is that the click coincides with the swing so the player can stop
  looking. Give the click a small lead only, to cancel audio output latency
  (`SoundService.Play` -> speakers is not free; if it is ~40 ms then lead 40 ms and the click
  lands on the text).

This is an argument from perceptual asymmetry, and I am **inferring** it - I did not measure
Mucka's audio output latency or the owner's simultaneity threshold. Measure `SoundService`'s
latency before committing a number; if it turns out to be 80 ms, the audible lead should be
~0 and the constant is already spent.

### 2.3 Re-anchor continuously - and specifically, keep the phase at session scope

The measurement is unambiguous: **there is one session phase, not a phase per fight.** So the
estimator should be a session-lifetime object, not a per-fight one.

Proposed estimator - `TickPhaseEstimator`, off the UI thread entirely, fed from the same
feed-thread path that already reaches `CombatTracker.Observe`:

1. On every swing line, record `arrivalUtc % 2000` into a bounded ring (last **64** phases).
   Feed it *raw arrival time from the receive path*, not the time the view model got round to
   refreshing - the render gate must never be in the phase estimate's path.
2. Estimate `phi` as the **p15 circular quantile** of the ring, not the median. Justified by
   the data: jitter is one-sided late, so a low quantile sits nearer the true tick. Measured
   |err| p90 is 16 ms for p15 vs 34 ms for the median.
3. De-duplicate by packet: several swing lines in one rx packet are one phase sample, not
   four. (The measurement above does this; an implementation that didn't would let pack
   fights outvote solo fights in the ring.)
4. Persist `phi` across fights for the whole session. Reset only on reconnect. Optionally
   persist to `mucka.ini` between sessions as a *seed* - but the two captures' `phi` differ by
   21 ms, so treat a stored seed as a starting guess worth ~20 ms, not as truth.
5. Publish `phi` and a confidence (`sampleCount`) as an immutable snapshot the UI can read
   without locking.

**How to apply it without the bar jumping.** Do not restart the animation to re-phase. The
corrections are tiny - measured per-swing correction magnitude for this estimator (n = 159):
median |delta| **0.0 ms**, p90 **0.8 ms**, p99 54 ms, max 165 ms, with **95.6% of swings
needing <= 1 ms** and only 3 of 159 needing more than 20 ms. All three of those were in the
first fight of a capture, before the ring had any content.

So:

- **Correct by slew, not by jump.** Cap the applied correction at **8 ms per 2000 ms tick**
  (a 0.4% rate change, invisible on a constant-rate bar). At that rate 95.6% of corrections
  are fully absorbed within a single tick, and even the 165 ms cold-start case converges in
  ~21 ticks (42 s) without a single visible discontinuity.
- Implement the slew as *the next iteration's duration*, not as a mid-flight edit: at each
  tick boundary, start the next keyframe animation with `Duration = 2000 +/- slew` (one-shot,
  not `IterationBehavior.Forever`), then hand back to a 2000 ms forever-animation once the
  correction is spent. Two Composition animations, no per-frame work, nothing on the UI
  thread. `TickSweep.Restart()`'s hard reset is kept for exactly one case: the first fight of
  a session when the ring is empty.
- **Never call `Restart()` on grace-period resumption again.** Today's `shouldSweep`
  transition re-anchors on resume; with a session-lifetime phase there is nothing to
  re-anchor to and re-anchoring can only inject error. Resume by computing the offset into
  the current tick from `phi` and starting the animation part-way through - which is also the
  correct fix for the panel being opened mid-fight (`$clog on`), a case today's code cannot
  handle at all.
- **The metronome needs the same treatment and gets it more cheaply**: it already computes
  `DelayToNextBeat` from an anchor, so replace the anchor with `phi` and let it recompute per
  beat. A thread-pool `Timer` re-armed each click (`Change(delay, Infinite)`) with delay
  derived from `phi` self-corrects with no slew logic at all, and also fixes the existing
  latent drift from `Timer`'s own period accuracy over a 90 s fight.

### 2.4 What must not happen

- **No UI-thread timer** (Invariant #1). The phase estimator runs on the feed thread; the
  visual stays a Composition animation on the compositor; the click stays a thread-pool
  `Timer`. Nothing here needs a tick on the UI thread and nothing here may acquire one.
- **No SkiaSharp repaint driving the sweep.** `SKCanvasView` paints on the UI thread on
  WinUI; the existing reasoning in `TickSweep`'s remarks stands unchanged.
- **The phase estimate must not be sampled downstream of `ClogRenderGate`.** The gate is a
  220 ms throttle; taking phase from a gated refresh would inject 0-220 ms of quantisation
  into the one number that has to be accurate to single-digit milliseconds. Today's anchor
  escapes the gate only because `OnInCombatChanged` renders unthrottled - that is luck, not
  design, and a session-scope estimator should not depend on it.
- **Keep the linear easing.** Unchanged and non-negotiable.

### 2.5 Failure modes

1. **Cold start.** First fight after connecting, empty ring: the estimator is as bad as
   today's for the first ~2 swings (the measured 194 ms outlier) and worse than today's
   during the fight-start-to-first-swing gap (median 712 ms, up to 2000 ms). Mitigation: run
   the bar *dimmed* or hold it at rest until `sampleCount >= 2`, and seed from
   `mucka.ini` where available. Do not silently show a confident-looking bar built on one
   sample.
2. **A route change or a bad network minute shifts the arrival floor.** The corpus has 9 ms of
   RTT jitter and a stable floor, but 2 captures on 1 evening from 1 location is not evidence
   about a bad day. A p15 over 64 samples plus the 8 ms/tick slew cap converges in ~40 s for a
   200 ms shift - acceptable, but during those 40 s the bar is wrong and does not say so.
   Consider widening the ring's influence when consecutive corrections all point the same way.
3. **The lead is wrong when the server is the thing that is late.** The lead is a fixed
   constant justified by a measured RTT and an *inferred* server flush delay. If MUD2's output
   flush is not the ~188 ms measured for commands, the visual's 120 ms lead is not
   "cancelling known delay", it is genuinely predicting - defensible, but not what section 2.1
   claims. Worth a direct measurement if one can be contrived.
4. **58% of ticks carry no swing.** Measured: swing-to-swing gaps are 68/46/35/12 at 1/2/3/4
   ticks, so only 42% of ticks deliver a swing line. Leading the bar makes it a sharper
   warning about *the tick*, and the tick is when a swing *can* land - but in a permadeath
   game a sharper warning that is silent 58% of the time may train the wrong reflex. This is
   a spec question, not a phase question, and this change makes it more pressing rather than
   less.
5. **Compositor clock vs `DateTime.UtcNow`.** The 2000 ms animation duration is measured by
   the compositor; `phi` is in UTC. Any rate difference between the two accumulates over a
   long fight and is invisible to this analysis (no capture can see it). Continuous
   re-anchoring happens to absorb it, which is a genuine additional argument for section 2.3 -
   but if the compositor's rate is off by more than ~4 ms/tick the slew cap will fight it
   forever. Worth a one-off check against a wall clock over 60 s.
6. **Small n at the fight level.** 16 encounters, one player, one evening, one weapon class,
   mostly zombies and water-snakes. Every per-fight percentage in section 1.2 is +/- one
   fight. The swing-level (177) and RTT-level (497) numbers are the ones to lean on.
