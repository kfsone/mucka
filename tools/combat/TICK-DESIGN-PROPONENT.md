# The two-stroke tick: a start-of-turn / commit-deadline design

**Position paper, proponent side.** Builds out the owner's proposal against the measurements in
`TICK-PHASE-REVIEW.md`. Numbers are taken from that document and not re-derived. Where I depart
from the owner's round numbers I say so and show the budget.

**Thesis in one line.** The rail should stop trying to predict the swing and start marking the
*player's* deadline, because the player's deadline is the only instant in the round that is both
actionable and invisible - and it is **360 ms earlier than the tick**, not 100 ms.

---

## 1. The player's decision loop, and where the deadline actually is

### 1.1 What a MUD2 combat turn is from the player's side

A round is not a reaction test. It is a small, complete work cycle:

1. **Read.** The previous round's text lands. Damage, misses, an NPC arriving, a stamina number.
2. **Decide.** Usually: nothing. Occasionally: flee, quaff, swap weapon, retarget, cast.
3. **Type.** At the owner's 120-130 wpm, most in-fight commands are under 500 ms of typing, and
   many are one F-key.
4. **Send.** Enter. From here the player has no further influence on this round.
5. **Wait.** The command crosses the Atlantic, the server resolves the round, the result crosses
   back.

The owner's framing is exactly right and worth restating in mechanical terms: **step 4 has a hard
deadline and steps 1-3 must be finished before it.** There is no EverQuest-style "press now for
a bonus swing". Nothing the player does *at* the tick is better than doing it 800 ms earlier. The
only thing that can go wrong is doing it too late, and the penalty for too late is not a smaller
number - it is **the whole action landing a full round later**, which in a permadeath game is the
difference between fleeing at 22 stamina and fleeing at 8.

That asymmetry is the design's foundation. **Early costs nothing. Late costs a round.** Every
number below is chosen from the safe side of an uncertainty band for that reason, and I flag each
time I do it.

### 1.2 Two lattices, not one

The client can only ever observe one thing: the instant swing text *arrives*. Call its phase
`phi_a`. The server's tick is at `T_s = phi_a - D`, where `D` = server-side output flush + one-way
return transit.

- One-way transit is **72 ms** (measured: echo RTT p50 144, p95-p5 spread 9 ms).
- Server think-plus-flush for a *command* is **188 ms** (measured: turnaround 332 - RTT 144).
- Whether tick-generated output pays the same 188 ms is **inferred, not measured**. So
  `D` is somewhere in **[72, 260] ms**.

But here is the fact that makes the design tractable: **`D` is unknown in level and near-constant
in variation.** The evidence is the tightness of both distributions - RTT jitter p95-p5 is 9 ms,
command turnaround's mode sits inside a 20 ms band, and the within-fight arrival residual has a
1 ms MAD. Whatever `D` is, it does not wander by more than a few tens of ms.

**Consequence, and it is the whole architectural argument:**

> The arrival lattice is a *rigid translate* of the server lattice. Anything the client anchors to
> arrivals is placed to within ~2 ms (estimator F). The unknown constant `D` matters in exactly
> one place - the commit deadline - and nowhere else.

This is why I reject `TICK-PHASE-REVIEW` section 2.1's 120 ms visual lead. That lead spends the
`D` inference on the *bar*, where it buys nothing the notch below does not buy honestly, and the
review's own failure mode 3 concedes the point: *"the visual's 120 ms lead is not 'cancelling
known delay', it is genuinely predicting."* Under this design `D` appears once, as a single named
constant, in the one computation that genuinely needs it.

### 1.3 Deriving the commit deadline

Let `A'` be the predicted arrival of the *next* round's text - i.e. `phi_a + 2000`.

A command sent at `S` reaches the server's input queue at `S + 72`. For it to affect the round
that resolves at `T_s' = A' - D`:

```
S + 72 + G  <=  A' - D
```

**Assumptions, marked as such:**

- **(A1)** MUD2 resolves combat on the tick but accepts and queues player input continuously. If
  instead input is itself tick-quantised, the deadline is unchanged - the command still has to be
  in the server's hands before the boundary. The one model that would break this is a server that
  applies a command's effect *before* its own tick handler regardless of arrival order, which
  would make the boundary sharp rather than fuzzy - still the same deadline.
- **(A2)** `G`, the server-side grace needed for a command to be dequeued ahead of the tick
  handler, is ~0. Unmeasurable from the client. If it is non-zero it is a further argument for the
  conservative deadline.
- **(A3)** Client keystroke-to-socket-write is <= 10 ms. Not measured. Invariant #1 makes it
  plausible; `INPUT_DIAG` could confirm it.
- **(A4)** Tick output flush = command think+flush = 188 ms. This is the conservative end of the
  `D` band and the single most load-bearing assumption in the document.

Budget, worst credible case:

| term | ms | source |
|---|---|---|
| return transit of the tick text (why `A'` trails `T_s'`) | 72 | measured |
| server tick-output flush | 188 | **assumed** (A4) |
| outbound transit of the command, p95 | 75 | measured (RTT p95 149 / 2) |
| phase estimate error, estimator F p90 | 16 | measured |
| client send latency | 10 | **assumed** (A3) |
| **total** | **361** | |

> ### **Commit deadline = `A' - 360 ms` = `phi_a + 1640 ms`.**
> 82% of the way through the round.

If (A4) is wrong and tick output flushes instantly, the true deadline is `A' - 173` = `phi_a +
1827`. So the honest statement is: **the deadline lies somewhere in `phi_a + 1640 .. +1827`, a
187 ms band, and we mark the near edge of it.** Cost of marking the near edge when the far edge
was true: 187 ms of unused thinking time per round, ~9%. Cost of marking the far edge when the
near edge was true: a lost round. Take the near edge.

Implement `D` as one config constant, `TickOutputFlushMs = 188`. If anyone ever contrives a
measurement, one number moves and every derived offset follows.

---

## 2. The two beats: what each one means

### Beat 2 - **LAND** (low stick), at `phi_a + 120 ms`

*"A round just resolved. Everything now on screen is its result. Your window is open."*

What the player knows after it that they did not know before:

- **On the 58% of ticks that carry no swing** (measured: swing gaps 68/46/35/12 at 1/2/3/4 ticks,
  so only 42% of ticks deliver a line) this beat is the *only* evidence that a round happened at
  all. Silence-with-a-heartbeat means "your opponent resolved and did nothing". Silence-without
  means "you have a network problem, or your `kill` never registered". Those are completely
  different situations and today the client cannot distinguish them.
- **On the 42% that do carry a swing** its information content is close to zero - the text is
  right there. Its job on those ticks is to be the felt zero that makes beat 1 legible.

Beat 2 survives the "cut anything unactionable" test, but it survives on the strength of the
58%-silent case plus its role as the phase reference. **If forced down to one beat, keep beat 1.**

### Beat 1 - **COMMIT** (high stick), at `phi_a + 1640 ms`

*"Send it now. Anything you press after this lands in the round after next."*

This is the only beat carrying information the player cannot obtain from the terminal. It is the
most actionable signal in the entire rail - more so than the stamina thresholds, which say *what*
is happening but not *when to act*.

### The cut: there is no third beat

A "start typing now" warning was considered and is cut. Three events per 2 s is noise, the client
cannot model the player's typing speed, and beat 2 already opens the window. The gap between the
two beats *is* the "do it now" period.

### Why two beats and not one

A single isochronous click at 0.5 Hz tells you the *period*. It cannot tell you where inside the
period the deadline sits - the player would have to interpolate 82% of the way, and humans are
poor at that. A two-element couplet is self-orienting: the asymmetric spacing tells you which
beat is which without counting.

**A one-beat metronome teaches the tempo. A two-beat metronome teaches the deadline.**

### The rhythm, as heard

Per 2000 ms cycle, the audible pattern is:

```
  ...................HI...........LO...................HI...........LO...
                     ^  480 ms    ^      1520 ms       ^
                   commit       land          (read / decide / type)
```

The ear groups by proximity, so the perceived couplet is **HI-then-LO, 480 ms apart, followed by
1520 ms of silence** - a falling pair with closure, a clock's escapement or a two-stroke engine,
which is the "mechanical-esque heartbeat" the owner asked for. The couplet straddles the round
boundary, and that is correct rather than a flaw: the boundary genuinely *is* a ~480 ms event
(your window closes, then the result comes back), not an instant. The long silence is the part of
the round that belongs to the player.

This also **deletes machinery**. Today's hi/lo alternation is derived from the fight's tick count
so that toggling off and on rejoins the pattern (`CombatMetronome.Click`, `tickIndex % 2`). Under
the two-stroke there is no pattern state: every tick is LO...HI. `_anchorUtc` and the tick-index
arithmetic both go.

---

## 3. Offsets, chosen from the data

### Anchor: the **arrival** lattice, always

Every offset below is relative to `phi_a`, the phase estimator's predicted text-arrival instant
(`TICK-PHASE-REVIEW` 2.3, estimator F - session-persistent p15 over a 64-sample ring). Defended in
1.2: it is the only lattice the client can measure, it is measured to ~2 ms, and it is a rigid
translate of the server's. The server lattice enters exactly once, inside the `A' - 360`
computation, via `TickOutputFlushMs`.

### Beat 2 = **+120 ms**, not the owner's ~100 and not 0

| candidate | argument |
|---|---|
| 0 ms (on the predicted arrival) | rejected - 12% of arrivals are later than +25 ms and up to +196 ms, so the click would precede its own text on ~1 tick in 8 |
| **+120 ms** | **chosen** |
| +200 ms | rejected - 100% arrival coverage, but it starts crowding the audiovisual binding window and buys one decile |

At +120 the beat follows the text on ~93% of swing ticks (arrival p90 = 47 ms, p95 = 135 ms), and
it clears estimator F's p90 phase error (16 ms) plus p90 arrival jitter (47 ms) with roughly 2x
headroom.

**The review's section 2.2 has the perceptual asymmetry backwards, and correcting it supports the
owner.** It argues for a *negative* audible offset on the grounds that "~100 ms of sound-before-
vision still reads as one event". The standard audiovisual finding is the opposite: because light
outruns sound in the world, the temporal binding window is wide on the *vision-first* side
(roughly +150 to +200 ms tolerated) and narrow on the sound-first side (~50 ms). A click *after*
the text binds to it; a click *before* it separates. So the click belongs at a small **positive**
offset - which is what the owner proposed. Tunable range 80-160 ms; 120 is the middle.

### Beat 1 = **`phi_a + 1640 ms`** (= 360 ms before the next text), not 100 ms before

This is the one place where the owner's round number has to move, and it matters. Placing the
end-of-turn beat at `-100 ms` puts it **260 ms past the deadline it is meant to mark**. A player
who trusts it and presses Enter on the beat has, under assumption (A4), already lost the round -
which is precisely the failure the beat exists to prevent. The beat has to sit at the *near* edge
of the uncertainty band, not at the tick.

The owner's own words are the justification for moving it: *"whatever choices you're going to make
need to be done and finished ~rtt+safety-margin before the end of the tick. Emphasis: done."* The
budget in 1.3 is that sentence with the measured numbers filled in. It comes out at 360, not 100.

### Audio output latency is a subtraction, and a prerequisite

`SoundService.Play` goes through pooled `Windows.Media.Playback.MediaPlayer` instances
(`Audio/SoundService.cs`, `MaxPooledPlayers = 8`, `WarmUp`). That is not a low-latency audio path.
Schedule at `offset - AudioOutputLatencyMs`; with a placeholder of 40 ms that is `phi_a + 80` and
`phi_a + 1600`.

**Prerequisite, stated honestly: measure `MediaPlayer`'s warm play-to-audible latency and its
jitter before shipping this.** The *level* does not matter - it is one constant. The **jitter**
does: if warm play latency varies by more than ~30 ms the couplet's shape wobbles and the two
beats stop reading as one gesture. If it does, the two-stroke needs a pre-decoded PCM path
(AudioGraph / XAudio2 with the two sticks resident) rather than `MediaPlayer`. That is a real
possible cost of this proposal and I am not hiding it.

### Gain

HI at the metronome's volume; LO at **60% of it** (~-4.5 dB). Asymmetric gain is what makes a
couplet read as one gesture rather than two separate events, and the quieter beat should be the
one whose information content is lowest.

---

## 4. What the bar does

### The bar as specified is lying, and it is lying against its own stated purpose

Section 6 says the bar *"answers 'how long have I got'"*. It does not. It answers "how long until
the next swing text", which is **360 ms more than the player has got**. The bar's stated semantics
are already the deadline semantics; only the implementation is on the arrival lattice.

### The fix: same animation, new furniture

Three changes, none of which touch `TickSweep`'s animation:

1. **Fill: unchanged.** Full at `phi_a`, empty at `phi_a + 2000`, strictly linear, Composition,
   one-shot chained with the 8 ms/tick slew from `TICK-PHASE-REVIEW` 2.3. **Visual lead: 0.**
2. **Commit notch.** A 1dp vertical mark on the static canvas at **18% of the track width from the
   left** (`360 / 2000`). The fill's right edge crosses it at the commit deadline.
3. **Commit scrim.** The leftmost 18% of the track carries a semi-transparent dark scrim (~35%
   alpha) on the canvas. Because the canvas already sits *in front of* the Composition fill layer
   (spec section 6: "the canvas draws the empty track only; the fill is a Composition-animated
   sibling behind it"), the fill automatically reads dimmer once its edge is inside that zone -
   **with zero per-frame work, zero UI-thread involvement, and no change to the rendering
   contract.** The scrim is static art on an already-static canvas.

### Why lead 0 on the empty end

The review's lead table exists to answer "does the bar warn before the text?" Under this design
**the notch is the warning**, and it warns 360 ms early on essentially 100% of ticks. The bar's
empty end is no longer a warning, it is a *confirmation* - and confirmations must not lead. Adding
a 120 ms lead on top would apply the same `D` inference twice, in two places, with no second
benefit. One inference, one constant, one place.

### What each region means, in words a player would use

| region | meaning |
|---|---|
| fill right of the notch | **yours.** Read, decide, type. |
| fill crossing the notch | **last call.** Enter now or it is next round. |
| fill inside the scrim | **not yours.** Your command is in the air or the server has already moved on. |
| empty | the round has resolved; the text is due. |

The scrim is honest about being an uncertainty band rather than a dead zone: its left portion
(`+1640..+1827`) is "probably still fine", its right portion is "definitely too late". Drawing
that distinction would need two densities and a stare, so it gets one scrim and stays conservative.

### It requires no stare

The notch and scrim are static dashboard furniture - a redline, learned once, never read. And the
actionable moment arrives by **sound**, so in steady state the bar never has to be looked at at
all. **The bar's real job is to teach the player what the high click means**, and to be the
fallback for players who keep the metronome off. That is a supporting role, which is exactly what
section 1 of the spec asks of every element on the rail.

Implementation notes: the notch and scrim must stay legible over the `stamina <= 30` red fill and
the `<= 20` glow; and the encounter-gauge swords are drawn over the same pixels, so check the 18%
mark for collision with the leftmost sword.

---

## 5. Hard cases

**58% of ticks carry no swing.** Both beats fire on the lattice regardless, and the bar runs
regardless. This is not a concession - it is the design **resolving** the review's open failure
mode 4 (*"a sharper warning that is silent 58% of the time may train the wrong reflex"*). The
round boundary and the commit deadline exist whether or not anyone swung. A clock that skips beats
when nothing happened is not a clock. The value of the beat is in fact *highest* on empty ticks,
because that is when there is no text to tell you where you are.

**The player is reading, typing, or scrolled back.** Audio needs no gaze - the whole point. The
beats never touch the UI thread: one thread-pool `Timer`, re-armed per beat, `SoundService.Play`
fire-and-forget as today. Terminal history mode hides the input but the fight continues, so the
beats continue.

**A fight starts mid-lattice.** It cannot, and this reframe matters: **the lattice is global and
never stops.** Only the client's attention to it starts. With session-scope phase
(`TICK-PHASE-REVIEW` 2.3), `phi_a` is already known from the previous fight, so beats and bar join
the beat already running - which is also the correct behaviour for `$clog on` mid-fight, a case
today's code cannot handle at all. Never `Restart()`; compute the offset into the current tick
from `phi_a` and start part-way through.

**The phase estimate is cold.** Degrade **asymmetrically** - drop the actionable beat first:

| state | LAND (lo) | COMMIT (hi) | bar |
|---|---|---|---|
| no samples, no persisted seed | silent | silent | at rest, dimmed |
| persisted seed only (worth ~20 ms) | on | silent | dimmed |
| seed + first swing agrees within 25 ms | on | on | normal |
| `sampleCount >= 2` | on | on | normal |

Rationale: being 190 ms wrong on a reference beat is harmless; being 190 ms wrong on a deadline
costs a round. **Never fire an actionable signal you do not trust.** The one measured 194 ms
outlier in the corpus was exactly this case - swing #2 of the first fight of the first capture.

**Being late.** If a beat's computed delay is under 15 ms when the timer arms, **skip that beat**
rather than firing it late. A late COMMIT beat says "you still have time" after you do not, which
is worse than silence.

**Slew.** Beats recompute their next fire time from `phi_a` on every re-arm, so the 8 ms/tick slew
is absorbed for free with no extra logic - the same self-correction the review already recommends
for the click, applied twice per tick instead of once.

**Grace period and fight end.** Unchanged from spec section 6: both bar and beats stop during the
5-second post-kill grace. Because phase is session-scoped, the next fight resumes in phase with no
restart artefact.

---

## 6. Does this serve the panel's purpose?

The rail exists to let the player keep their eyes on the terminal text. The honest test is not
"does this add information" - it is "does this reduce the number of times the player looks away".

**It does, and it is the only element on the rail that can.** Every other indicator - stamina
seals, opponent slots, combat beats, the encounter gauge - is a thing you *look at*. Audio is the
only channel on the panel that costs zero gaze. Section 6 already says this about the metronome:
*"a glance at the rail is a glance away from the terminal text."*

**Does two beats add a second thing to attend to? No - it removes one.** Periodic auditory
patterns entrain pre-attentively; that is why a musician can sight-read against a metronome. A
two-element ostinato at 0.5 Hz is well inside what entrains automatically. The attentional cost is
paid once, at learning time, and after that the pattern runs underneath the player's reading. By
contrast, a single click *does* demand ongoing attention, because to locate the deadline the
player must actively interpolate.

The beats are deliberately **not alarms**: fixed offsets, fixed pitches, fixed gains, no dynamics,
no variation with fight state. There is nothing in them to startle or interrupt. The rail already
has an alarm - `stamina <= 20` - and it is important that the tick row stays a clock so the alarm
keeps its meaning.

**What a new player has to learn - one sentence:**

> **Low click: the round landed, start thinking. High click: last call, press Enter now.**

That is it. The couplet's shape does the rest within one fight, and the notch on the bar is the
visual gloss on the high click for anyone who wants to see where it comes from.

**What it costs.** About 90 clicks in a 90-second fight, versus 45 today. Mitigated by the short
percussive sticks, the -4.5 dB on the LO, and the existing toggle. Fatigue is a real risk and the
toggle is the answer; a player who finds it wearing loses the deadline signal but keeps the notch.

---

## 7. The weakest point

Not the assumptions - those are bounded and each has a named remedy. The single argument most
likely to sink this proposal is:

> **In MUD2 you `kill` once and then auto-attack every round without typing anything. The modal
> round requires no player action at all. A commit deadline is a solution to a problem that occurs
> on maybe 1 round in 20. Beating a deadline 1,800 times an hour to serve 5% of rounds is
> over-signalling, and the 95% where the player does nothing will train them to tune it out - at
> which point it is not there on the round that matters either.**

That is a serious attack and I will not soften it. My answer, which I think holds but which a
skeptic can reasonably reject:

**The rounds that need action are precisely the rounds that decide whether the character survives,
and permadeath makes their value wildly out of proportion to their frequency. And a deadline you
meet once in twenty rounds is a deadline you cannot practise.** An entrained rhythm has to already
be running before the moment you need it; a signal that only appears when it becomes relevant
arrives too late to be learned. That is the entire case for beating continuously rather than
warning on demand - and it is a claim about how habituation and entrainment interact that I cannot
support from the corpus, only from the general shape of how metronomes work for musicians.

The runner-up weakness is assumption **(A4)**: the 360 ms deadline rests on tick output flushing
as slowly as command output does, which is inferred. If the true flush is near zero, the COMMIT
beat is ~190 ms early on every round forever, silently costing the player ~9% of every decision
window - and because they will have learned the beat, they will not notice. It is a bounded,
one-directional, safe-side error with a one-constant fix, which is why it is second and not first.
But it is the number to measure before anyone calls this locked.
