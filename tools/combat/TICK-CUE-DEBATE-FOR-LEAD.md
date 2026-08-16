# The case FOR leading the tick cue

Adversarial brief. A second document argues the opposite; this one argues one side and says so.
All numbers are taken from `TICK-PHASE-REVIEW.md` and `MECHANICS-VERIFICATION.md` and are not
re-derived here. Where I reason about *perception* rather than measurement I say so and give a
cheap test.

---

## 0. Position

The owner's objection is to *prediction*. I agree with the objection and it does not apply. Almost
none of the proposed lead is prediction — it is **cancellation of a delay the client has already
measured**. The remainder of the disagreement is not about how much to lead; it is about **what the
number zero on the bar denotes**. Today it denotes "text arrived". It should denote "your window to
act on this round has closed". Those two instants are 144–332 ms apart, and the whole argument is
that gap.

The owner's own sentence is the thesis:

> whatever choices you're going to make need to be done and finished ~rtt+safety-margin before the
> end of the tick

Exactly. So compute `rtt + safety-margin` and put the bar's zero there. That is all I am asking for.
Currently the bar's zero sits `rtt + safety-margin` **on the wrong side** of the boundary.

---

## 1. The crux: two reference frames, and only one of them makes "lead" mean prediction

There are two clocks in this system and the debate keeps sliding between them.

- **Server frame.** `S_N` = the instant the server resolves tick N. Unobservable directly.
- **Client frame.** `t_text(N)` = the instant the swing text lands in the receive path.

Measured: `t_text(N) = S_N + F + 72`, where 72 ms is one-way transit (half the measured 144 ms echo
RTT, n=497, p95−p5 spread 9 ms) and `F` is server think-plus-flush for tick output.

A "lead" quoted against `t_text` is not a claim about the future. It is a partial refund of `F + 72`,
a quantity that has already elapsed. A lead of 200 ms against `t_text` still lands **after** `S_N` on
every plausible value of `F`. The client is not predicting the tick; it is failing to be as late as
it currently is.

**This distinction disposes of the "we don't want to predict" objection entirely, up to `L = F + 72`
(≈ 260 ms).** Only a lead larger than that is genuine prediction, and nothing in this document asks
for one.

It also matters for reading the owner's proposal. "High click 100 ms before the boundary" has two
readings:

- *before the boundary as the client observes it* (i.e. before `t_text`) → a 100 ms lead, less than
  the correction the delay alone justifies, and therefore still a retrospective cue;
- *before the true server boundary `S_N`* → that is `L = F + 72 + 100` ≈ 360 ms against `t_text`,
  which is **larger than anything I am about to recommend**.

If the owner means the second, we already agree and the argument is about notation. I suspect they
mean the first, so the rest of this document argues the arithmetic.

---

## 2. The deadline, computed

A command is only useful for round N+1 if it reaches the server before `S_{N+1}`. Client-side send
deadline:

```
deadline(N+1) = S_{N+1} - 72 ms          (one-way transit up)
```

Where should the bar hit zero, expressed as a lead `L` before the *next* text arrival?

```
t_text(N)     = S_N + F + 72
deadline(N+1) = S_N + 2000 - 72         = t_text(N) + 1856 - F
t_text(N+1)   = t_text(N) + 2000
L             = t_text(N+1) - deadline(N+1)
              = 2000 - (1856 - F)
              = 144 + F
```

**`L = 144 + F`.** Two consequences, and the first is the hardest number in this document:

**(a) `F ≥ 0`, so `L ≥ 144 ms` unconditionally.** This rests on nothing but the measured echo RTT.
There is no assumption about the server in it. Any lead below 144 ms is provably too small — the bar
reaches zero after the player's deadline has already passed, on every tick, forever. That range
includes 0 ms (today), the review's recommended 120 ms visual, and the owner's 100 ms pre-click.

**(b) Best estimate: `F ≈ 188 ms` → `L ≈ 332 ms`.** 188 ms is measured (command turnaround p50 332 ms
minus echo RTT 144 ms, n=503, mode tightly 320–340). Transferring it from command replies to
tick-generated output is an **inference**, and it is the one soft joint in this argument — see §7.

Scored against the deadline rather than against the text:

| lead `L` | margin to deadline, `F=188` | margin, `F=0` | warns in time? |
|---|---|---|---|
| 0 ms (today) | **−332 ms** | −144 ms | never |
| 100 ms (owner's pre-click) | **−232 ms** | −24 ms | never |
| 120 ms (review's visual) | −212 ms | −4 ms | never |
| 150 ms | −182 ms | **+6 ms** | only if `F ≈ 0` |
| 250 ms | −82 ms | +106 ms | if `F < 106` |
| 332 ms | **0 ms** | +188 ms | yes |

Note what the middle row means. **The owner's own proposal — a high click 100 ms before the client's
boundary — still fires after the deadline it is meant to protect, under every value of `F` including
the most charitable.** It is a better instrument than today's, and it is the right *shape* (see §6),
but it is placed 232 ms too late on the best estimate.

The review's table measured "warned before the *text*", which is a much weaker criterion and is why
it landed on 120 ms. Against that criterion 120 ms is fine. Against the criterion the owner actually
stated — decisions *finished* before the tick ends — 120 ms scores zero.

### 2a. The deadline is hard, not soft, and that is why it deserves an instrument

Command turnaround has a *tight* mode at 320–340 ms with no 2000 ms quantisation (n=503). If commands
were queued and resolved on the tick, turnaround would smear roughly uniformly across 144–2144 ms. It
does not. **Inference:** MUD2 processes commands on arrival, not on the tick.

That is not a problem for the deadline argument — it is the reason the deadline exists. Whether your
`flee` beats this round's blow is decided by whether your packet arrives before `S_N`. Send at
`S_N − 200 ms` → the server sees it at `S_N − 128`, before the round. Send at `S_N − 50 ms` → the
server sees it at `S_N + 22`, after. **A 150 ms difference in send time flips the outcome**, and in a
permadeath game where fleeing at 20 stamina costs more than twice fleeing at 19 (measured: 4.48% of a
46 416 score at 19 stamina, against a ~10% ceiling), one such flip is expensive.

*How to test the inference cheaply:* histogram command turnaround **restricted to in-combat sends**,
mod 2000. If it is flat-moded at ~332, commands are immediate. The DB already has both timestamps.

---

## 3. A cyclic bar has no cost function for lead. It only has a choice of what zero means

This is the point I think both the review and the owner have mis-costed.

`TickSweep` runs `IterationBehavior.Forever`. The bar never rests at zero mid-fight; it rolls
straight from empty into the next fill. **Therefore a lead is a pure phase shift of a periodic signal,
and a phase-shifted periodic signal is pixel-for-pixel identical to the original.** There is no
premature-empty artifact, no dead time, no visible discontinuity — nothing to see at all.

The review's table has a column headed "worst-case early" (+284 ms at `L=100`, +384 at `L=200`) and
treats it as the cost of leading. On a cycling bar that column is measuring a non-event. The only
thing a lead changes is the relationship between **the bar's reading** and **the text on screen** —
i.e. what the bar *claims*. So the question is not "how much early is safe" but "is the claim true".

Check the claim at both settings. Text for tick N lands at `S_N + 260` (using `F = 188`). The next
deadline is `S_N + 1928`. True remaining time at the moment the text lands: **1668 ms.**

- **Today (`L = 0`).** Bar is at full, reading 2000 ms remaining. **Overstates by 332 ms**, which is
  20% of the player's actual usable window. It overstates by the same amount at *every* instant of
  the cycle, not just at the boundary — this is a constant multiplicative-free offset on a readout
  that exists to answer "how long have I got".
- **At `L = 332`.** Bar is 83% full, reading 1668 ms remaining. **Correct at every instant.**

The bar is a remaining-time readout. Its zero should be the instant remaining time reaches zero. That
is the send deadline. Today's zero is set to a different quantity — the arrival of a report — and the
bar is consequently wrong by a fixed 332 ms all the way round the dial.

The review's advice "do not go past 200 ms, beyond that the bar is emptying while the previous tick's
text is still being read" does not survive the arithmetic either. At `L = 332` the bar empties
1668 ms after the previous text landed. To collide with the previous text you would need `L > 1740`.
The 200 ms cap is over-conservative by an order of magnitude and is not backed by any measured
quantity in the review.

---

## 4. Any *fixed* lead is wrong. Derive it from the RTT the client already measures

`L = 144 + F` — and 144 is **this player's median RTT to `mud2.co.uk` on one evening from one
location**. A player in Sydney with a 300 ms RTT needs `L = 300 + F`. A hardcoded 120 ms would be
wrong for them by more than 300 ms, i.e. wrong by a sixth of a tick, and wrong in the direction that
makes the instrument lie about a deadline in a permadeath game.

Neither camp's constant survives this. The lead must be **computed**:

```
L = RttP50 + FlushAllowanceMs
```

The client can measure `RttP50` live and continuously — the corpus got 497 echo round trips out of
69 minutes of ordinary play, so the sample rate is ample, the estimator is trivial (match a sent
command to its verbatim telnet echo, excluding repeats), and the distribution is extremely tight
(p95−p5 = 9 ms). This is the single most defensible part of the proposal and it is also the cheapest.

A related point in the lead's favour: **the deadline lives on the server lattice and is therefore
immune to receive jitter.** Text arrival is not — 9% of arrivals are 120–196 ms late, one-sided. A
cue triggered *by* the text inherits that tail and is extra-retrospective exactly on the ticks where
the network hiccupped. A cue computed from `phi` fires on time regardless. That is an argument for
computing the cue from the phase estimator rather than from arrivals, independent of the lead.

---

## 5. Entrainment, and why "only 42% of ticks carry a swing" is an argument *for* the lead

The measured lattice is 2.000 s with a **1 ms median absolute deviation** and **one phase per
41-minute session**. That is not an event stream. That is a clock, and a startlingly good one.

The review's failure mode 4 worries that a sharper cue "silent 58% of the time may train the wrong
reflex". That concern is correct for an **event** cue and inverted for a **lattice** cue:

- The player cannot know in advance which ticks will carry a swing (gaps 68/46/35/12 at 1/2/3/4
  ticks — no usable pattern).
- Therefore the *deadline* applies to **100% of ticks**. You are insuring against the possibility of
  a blow, not reacting to a certainty.
- A cue that fired only on the 42% would train the player to relax on precisely the ticks where the
  mobile's `Speed` roll is about to come up.

So the cue must fire on every tick — which is what the current design already does, and which is only
coherent if the cue marks the *lattice* rather than an event. And once it marks the lattice, the
"don't predict the future" framing loses its grip: nothing is being predicted about *whether* a swing
lands. The lattice point itself is known to ~2 ms.

**Perceptual claim, labelled as such:** the value of a steady 2 s beat is that after a handful of
cycles the player stops reading the bar and just knows where they are in the round, which is the only
mechanism by which a structurally-late system can produce anticipation. I have not measured this and
am not citing literature for it.

*Cheap test the app can run on itself:* log every command-send timestamp during combat as an offset
from `phi`, tagged with the current lead setting and metronome state. Two questions fall straight out
of the histogram — (1) does the send distribution shift earlier and tighten when the beat is armed,
(2) does the count of sends landing after `S_N` (i.e. missing the round) go down. That is the outcome
the entire debate is about, it is free to collect, and it converts a taste argument into a measurement
within a few sessions.

---

## 6. Visual and audible are different instruments, and the owner's doublet is right

They are not the same signal and should not carry the same number.

**Visual — a continuous readout.** The bar has no "event"; it never claims a swing just happened. It
claims *remaining time*, and §3 shows the correct zero for remaining time is the deadline. There is no
perceptual cost to shifting it because a cycling bar looks identical at any phase. **Full deadline
lead. `L = RttP50 + F`.**

**Audible — a point event, and here the objection has real force.** A click is instantaneous, so it
*does* assert "now". At `L = 332` it would sound clearly before the text appears. Whether that reads
as a warning or as a broken sync is a perception question I cannot settle from this corpus.

But the review's recommendation — a 40–60 ms audible lead so the click lands *on* the text — makes the
click **redundant**. If the click coincides with the text, the text is already there, is already
louder, and carries strictly more information. The click earns its place precisely by arriving when
the text has not yet arrived.

Which is why **the owner's doublet is the correct structure and I want to adopt it.** It resolves the
tension by refusing to choose: two clicks, two meanings, both true.

- **High click at the deadline** (`L = RttP50 + F` before text): *your window is closing.*
- **Low click at text arrival** (`L ≈ 0`): *and here is what happened.*

That is the owner's "pay attention" bracket, kept exactly as proposed in shape, but with its two ends
nailed to the two instants that actually mean something instead of being placed symmetrically around
a boundary the client cannot observe. The gap between them becomes self-calibrating: it *is* the
round-trip, so a player on a bad link hears a wider bracket and is correctly told their window is
tighter.

Both clicks must be scheduled minus `SoundService`'s output latency, which is **unmeasured**. Measure
it before committing either number (loopback-record a click, compare to the `Play()` call timestamp).
If it is 80 ms, the low click's lead is already spent and should be 0.

---

## 7. Concessions

These are the places the evidence genuinely does not go my way.

1. **`F` is inferred, not measured, for tick output.** 188 ms is measured for *command replies*. If
   MUD2 pushes tick output through a different path that flushes immediately, `F ≈ 0` and the correct
   lead is 144 ms, not 332 — and the review's failure mode 3 is right that at that point the extra
   190 ms *is* prediction rather than correction. This is the load-bearing assumption in §2 and it is
   why my shipping recommendation is staged (§8). It is also contrivable to measure: time a command
   to arrive ~300 ms before an estimated boundary and check whether its reply and the tick output
   arrive in the **same rx packet**. Co-arrival means one output pump means `F` transfers.

2. **Panic-typing in a permadeath game is a real risk and I cannot measure it.** A cue that says
   "hurry" every 2 s could push a player to commit a half-formed command. The cost is quantified: an
   unnecessary `flee` at 20 stamina costs more than twice one at 19, and the one measured flee cost
   4.48% of score. That is not a hypothetical.

   My rebuttal is partial, not total. The risk is a function of the cue's *character*, not its
   *timing*: a pale, unlabelled, uncoloured, linearly-draining bar is not an alarm at any phase, and
   the spec already forbids everything that would make it one ("a timer, not a judgement"). The
   danger shape is a sharp startle-flavoured click, which is why §8 keeps the doublet optional and
   the visual unchanged in every respect except phase. I also note the counterfactual is not calm —
   a player who does not know the deadline either rushes on every tick or dawdles on every tick. But
   I concede I am reasoning about perception here and the owner has played this game and I have not.

   *Cheap countermeasure with a measurable signature:* count flees at stamina **above** 20 (i.e.
   above the cliff, where fleeing is strictly a mistake) before and after. The DB already has it. If
   that count rises, the cue is inducing panic and the doublet should go back to a single click.

3. **The 42%-silent objection is not fully answered.** §5 answers it for a *lattice* cue. It does not
   answer the separate spec question of whether the rail should distinguish "a swing landed" from "a
   tick passed" at all. Making the cue sharper does make that question more pressing, as the review
   says. I am arguing about phase and am not entitled to claim I solved it.

4. **A 300 ms-shifted bar will initially look wrong to someone who learned the old one.** At the
   instant the text appears the bar will read ~85% rather than 100%. The disagreement is *correct*,
   but "correct" and "reads as broken" are different properties and I cannot measure the second.
   Mitigating fact, and it is a real one: **85% full still reads as start-of-turn.** The owner asked
   for a start-of-turn indicator and at this lead they still get one — what changes is only that the
   bar's *zero* now falls on the actionable instant instead of an arbitrary one.

5. **Small n at the fight level.** 16 encounters, one player, one evening, one route. The RTT (n=497)
   and swing-arrival (n=177) distributions are solid; nothing here is evidence about a bad network
   day, which is a further argument for deriving `L` from a live RTT rather than freezing tonight's
   number into a constant.

6. **Rhythm-game leads and this lead are justified differently.** The owner is right that this is not
   Rock Band. A rhythm game leads to pre-empt ~150–200 ms of human motor latency. Mucka leads to
   pre-empt measured network and server latency. Same direction, different reason, and here the
   required lead is *larger* because the loop includes a transatlantic round trip.

7. **The reductio deserves an answer.** "If a bigger lead is better, why not lead by a second?"
   Because the lead should mark the deadline **exactly** — no more. If the player needs longer than
   `2000 − L` to compose a command (`flee` is 5 keystrokes; at the author's own 120 wpm that is
   ~500 ms *after* the decision), the fix is pre-typed commands and F-keys, not a lying instrument.
   The bar's job is to state the deadline truthfully; having the command ready is the player's job.

---

## 8. What I would build

Staged, so the uncontroversial half ships without waiting on `F`.

**Stage 0 — phase, unchanged from the review.** I have no dispute with any of it and it is orthogonal
to the lead. `TickPhaseEstimator` at session scope, p15 circular quantile over a 64-deep
packet-deduplicated ring, fed from the receive path, never downstream of `ClogRenderGate`, published
as an immutable snapshot. Slew-corrected at **8 ms per 2000 ms tick**, applied as the next
iteration's `Duration`, never as a `Restart()`. Kill the `Restart()` on grace resumption; resume
part-way through the cycle from `phi`, which also fixes mid-fight `$clog on`. Cold start: hold the
bar at rest until `sampleCount >= 2`.

**Stage 1 — lead 150 ms, ship now.** `L = 150 ms`, applied as a phase offset to the sweep and to the
metronome's `DelayToNextBeat`.
- Justification: `L ≥ 144` is unconditional and rests only on the measured RTT. 150 is a strict
  improvement over 0 and over 120 under **every** value of `F`, and overshoots by ≤ 15 ms even in the
  worst case (`F = 0`, RTT at its measured minimum of 135).
- Cost: none. A cycling bar at a different phase is pixel-identical (§3).
- This is the part the sceptic cannot refute without disputing the 144 ms RTT.

**Stage 2 — derive the lead, after two measurements.**
1. Measure `SoundService.Play` → speaker latency. Loopback record; one afternoon.
2. Measure `F` via the packet co-arrival experiment (§7.1). If commands and tick output share a
   packet, `F = 188` transfers.

Then:
```ini
[combat]
tick_lead_flush_ms = 150     ; default; 0 if the co-arrival test says tick output flushes instantly
                             ; 188 if it shares the command output pump
audio_output_latency_ms = 0  ; measured, subtracted from both click schedules
metronome_mode = doublet     ; doublet | warn | land
```
`L = RttP50 + tick_lead_flush_ms`. With tonight's measured RTT that is **294 ms**, and it self-corrects
for any player on any link. Bar reads true remaining time at every instant; overstatement drops from
332 ms to ~0.

**Stage 3 — the doublet.** Adopt the owner's structure with the anchors fixed:
- **High click** at `phi − L` (the deadline). "Window closing."
- **Low click** at `phi` (text arrival). "Here is what happened."
- Both minus `audio_output_latency_ms`. Both from the thread-pool `Timer`, re-armed per click via
  `Change(delay, Infinite)` with the delay recomputed from `phi` — which also fixes the existing
  latent `Timer`-period drift over a 90 s fight and removes the need for the tick-count parity trick,
  since high/low now derive from `phi` directly.
- Default on, but `metronome_mode = warn` (high only) available, because 1 Hz of clicking may simply
  be too busy and I cannot measure annoyance.

**Stage 4 — instrument the argument.** Behind the existing diagnostic flag, log per combat command:
send timestamp minus `phi`, the active `L`, metronome state, and whether the reply landed before or
after the round's damage line. Three outcome measures the app can collect for free:
- did sends shift earlier and tighten;
- did the count of sends that missed their round fall;
- did flees at stamina **> 20** rise (the panic signature, §7.2).

**What must not change:** linear easing; no UI-thread timer; no SkiaSharp repaint driving the sweep;
no colour, label, or flash on the tick meter at any lead. The lead relocates zero. It must not add a
gram of urgency to how the bar looks.

---

## 9. If you read nothing else

- `L = 144 + F`. `F ≥ 0`. **So the correct lead is at least 144 ms under any assumption whatsoever**,
  and every proposal on the table — 0 ms, the owner's 100 ms, the review's 120 ms — is below that
  floor and reaches zero after the player's deadline has already gone.
- On a cycling bar a lead has **no visual cost at all**. It is a phase shift of a periodic signal.
  The only thing it changes is whether the bar's claim is true, and today's claim overstates the
  player's remaining time by ~332 ms at every instant of every tick.
- The owner's doublet is the right shape for audio. Its two ends are just in the wrong place; nail
  them to the send deadline and to text arrival and it becomes self-calibrating.
- The soft joint is `F`, and it is measurable in an afternoon. Ship 150 ms today, which is safe under
  every value of `F`, and derive the rest once `F` is known.
