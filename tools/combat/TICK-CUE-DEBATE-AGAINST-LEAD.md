# The case against leading the tick cue

Adversarial brief. Position: **the tick indicator must not lead the swing, and the proposed
"hi at −100 ms / lo at +100 ms" bracket is the same design error in a costume.**

All numbers are taken from `TICK-PHASE-REVIEW.md` and `MECHANICS-VERIFICATION.md` and are not
re-derived. Where I reason about *perception* rather than measurement I say so and give a way to
test it inside Mucka for the cost of one evening's play.

Time is expressed relative to **A**, the estimated arrival at the client of a tick's swing text
(the lattice point `phi`). A is the only instant the player can actually observe.

---

## 1. The deadline arithmetic kills the −100 ms click outright

The owner's own criterion is the right one:

> whatever choices you're going to make need to be done and finished ~rtt+safety-margin before
> the end of the tick. Emphasis: done.

Then apply it. For a command to affect the tick after this one, it must **arrive at the server**
before that tick resolves. Working backwards from A:

- One-way transit up: **72 ms** (measured, RTT 144 ms; worst observed RTT 160 ms → 80 ms).
- The next tick resolves *server-side* at `A + 2000 − D`, where D is the server-resolve→client-text
  delay. The review puts D at **150–260 ms** (one-way 72 ms measured + ~188 ms think/flush, the
  latter measured for command replies and *inferred* for tick output).

| D (resolve→text) | last safe Enter | that is this far **before** the next bar-empty |
|---|---|---|
| 150 ms (optimistic) | A + 1778 | **222 ms** |
| 205 ms (mid) | A + 1723 | **277 ms** |
| 260 ms (pessimistic) | A + 1668 | **332 ms** |

Add the RTT tail (160 ms worst vs 144 median → +16 ms) and client send-path scheduling (~10 ms)
and the honest commit deadline is **≈300 ms before the perceived boundary**, range 240–350.

**A high click at −100 ms fires 140–250 ms after the deadline has already passed.** It is not an
early warning. It is a bell that rings once the window has shut. If a player ever *acts* on it,
the client has actively caused them to lose a round — and this is a permadeath game.

Note what this does to the proposal's own justification: the owner's rule of thumb, "rtt + safety
margin", is **≥144 ms** before anything else is counted. The proposed lead is 100 ms. **The
proposal fails the criterion its author stated in the same paragraph.** That is not a quibble; the
whole point of the bracket was to respect the deadline, and it does not.

## 2. No cue in the second half of the tick is actionable at all

Budget the human side. Auditory simple reaction time is conventionally ~160 ms (**textbook
assumption — not measured here**; see §8 for how to measure it in-app). Typing is measured for this
user: `CLAUDE.md` records 120–130 wpm ≈ 10–11 chars/s.

| response | keystrokes | time from hearing the cue to Enter |
|---|---|---|
| F-key macro | 1 | ~250 ms |
| `flee` | 5 | ~610 ms |
| `bs zombie` | 10 | ~1060 ms |

To land inside the A + 1723 deadline, a cue meant to prompt a *typed* response must fire no later
than **A + 660 ms** — the first third of the tick. For an F-key, no later than A + 1470.

So the space of cue positions splits cleanly:

- **A + 0 … A + 660**: actionable for any command. This is "start of turn".
- **A + 660 … A + 1470**: actionable for a macro only.
- **A + 1470 … A + 2000**: actionable for nothing. Dead zone.

The proposed hi click sits at A + 1900. It is in the dead zone by 430 ms. Any design that puts a
cue there is either (a) a rhythm-game beat marker, which the owner explicitly rejects, or (b) an
anxiety generator. There is no third reading.

## 3. Only a trailing cue can carry information; a leading cue is structurally incapable

This is the argument I would put first if I could only make one.

Swings occur on **42 % of ticks** (gaps of 1/2/3/4 ticks at 68/46/35/12, n = 161). At A − 100 ms
the client **does not know** whether this tick delivers a swing. It cannot know: the text has not
arrived. So a pre-boundary click can only ever mean "a tick boundary is near" — and dressed up in
the owner's own semantics ("pay attention, a status update is landing") **it is false 58 % of the
time**. A cue that asserts an event and is wrong on nearly three ticks in five is not an
instrument; it is a habituation machine. Within one 26-tick fight the player has heard it lie
fifteen times.

At A + 30 ms the client *does* know. That asymmetry is the entire design opportunity and leading
throws it away. A trailing click can say something true and useful — see §7.

The current spec's alternating hi/lo (`COMBAT-RAIL-SPEC.md` §6, derived from the fight's tick
count) encodes **zero bits about the fight**. It is decoration occupying the exact channel that
could carry "did anything just happen". Both the shipped design and the bracket proposal spend two
sound assets on nothing.

## 4. The "it reports the past 58 % of the time" table proves much less than it looks

This is the proponent case's strongest single line (`TICK-PHASE-REVIEW.md` §2.1) and it does not
survive contact with its own adjacent numbers.

At lead 0 the table reports 101 "failures" out of 175. Read the margin column next to it:

| lead | warned before text | median margin | p5 | p1 |
|---|---|---|---|---|
| 0 ms | 42.3 % | **−1 ms** | −6 ms | −50 ms |
| 25 ms | 98.3 % | +24 | +19 | −25 |
| 100 ms | 99.4 % | +99 | +94 | +50 |

The median "failure" at lead 0 is **one millisecond late**. The p5 is six. These are counted as
failures because the metric is a sign test, not because anything is perceptible. Meanwhile §1.3
gives the shape directly: **88.1 % of swings arrive within 25 ms of the lattice floor**, p50 and
p75 offsets of 6.5 and 7.5 ms.

So compare what the two settings actually do to `|cue − text|`, the quantity a human can perceive:

| lead | `|cue − text|` for the modal 75 % | for the ~9 % late tail (120–196 ms) |
|---|---|---|
| 0 ms | **≈ 0–8 ms** | 120–196 ms |
| 100 ms | **≈ 100 ms** | 220–296 ms |

**A 100 ms lead makes the instrument worse for 88 % of swings in order to fix a sign bit on the
other 12 %, and it does not fix the tail either** — it moves the tail from 196 ms early-text to
296 ms late-text. Mean absolute displacement goes from roughly 20 ms to roughly 110 ms. You cannot
optimise a perceptual instrument's p1 at 5× the cost of its p50.

And the table saturates long before 100: **25 ms buys 98.3 % and leaves 3 failures; 100 ms buys
99.4 % and leaves 1.** Seventy-five extra milliseconds of universal displacement to rescue **two
swings out of 175**. The table is an argument for a *small* lead, not a large one.

## 5. A leading bar contradicts the spec and reintroduces a bug the owner already caught in play

`COMBAT-RAIL-SPEC.md` line 263, stated as a design fact: **"on a countdown, empty means the swing
is due now."** A 120 ms lead redefines empty as "due in 120 ms". The spec's own semantics no longer
hold, and the one landmark on a linear bar — zero — stops meaning what it says.

Worse, the bar then **sits at zero** for 120 ms every single tick, and for 120–320 ms on the ~9 %
late tail. That is a hard stall at the right-hand end of the sweep, once every 2 s, for the whole
fight. The spec already records this exact percept being caught in play:

> *"combat tick bar is not smooth, it seems to slow down towards the right"*

— which was the cubic ease-in-out, fixed by mandating linear easing. A lead re-creates the same
visual signature (motion decelerating to a dead stop before the event) by a different mechanism.
**The owner has already rejected this look once.** (Assumption: a dwell at a landmark is more
salient than a small offset away from one. Cheap to test — §8.)

Note also that the pathology being fixed is not a lead problem at all. Today's anchor is wrong by a
median of **1037 ms late** (§1.2), which produces the opposite artifact: the bar still visibly
draining when the text has already landed. Fixing the anchor (`|err|` median 152 ms → 22 ms on
first-swing anchoring, → 2 ms with session p15) removes it entirely. **The lead is being asked to
paper over an anchor bug that is separately, and much better, fixed.**

## 6. Attacking the bracket specifically

Granting for a moment that bracketing is the goal, the proposal's numbers are wrong and its cost is
underrated.

1. **+100 ms is too early to close the bracket.** Measured arrival spread relative to the lattice
   floor: 88.1 % within 25 ms, 90.4 % within 50, 93.8 % within 100, **100 % within 200 ms
   (max 196)**. A closing click at +100 fires before the text on roughly **1 tick in 16 of those
   that carry a swing**, and on the late tail it fires 20–96 ms early. If the lo click means "the
   update has landed", it is lying precisely on the ticks where the player most needs it. The
   correct closing offset from this corpus is **+200 ms**, not +100. But at +200 the two clicks are
   300 ms apart and the whole figure is 15 % of the tick wide.

2. **Two clicks per tick doubles the audio load for zero added bits.** 177 swing instants across 16
   encounters at 42 % tick occupancy ≈ **26 ticks (~52 s) per fight**, so ~26 clicks/fight and
   ~420 across the 16-fight corpus. The bracket makes that ~840. The second click occurs at a fixed
   200 ms offset from the first, every time, forever: perfectly predictable, therefore carrying
   **zero information**. It is pure level.

3. **A 200 ms duplet repeating at 2 s is a groove, not a marker.** Two clicks 200 ms apart are far
   enough apart to be heard as two distinct events (assumption; fusion is roughly a sub-100 ms
   phenomenon) and close enough to form a rhythmic figure — a "da-DUM". Rhythmic figures invite
   entrainment and anticipation. **The bracket is therefore *more* rockband-ish than the single
   click it replaces**, which is the opposite of the proposal's stated intent.

4. **It creates four "nows" on one instrument.** Under the combined proposals the player is offered:
   bar empties at −120, hi click at −100, text at 0, lo click at +100. Four events across 220 ms,
   each purporting to mark the same instant, on a panel whose whole job is to say when the instant
   is. Any design that needs a per-modality calibration table of leads has already conceded that it
   does not know where the moment is.

## 7. What a non-leading cue can do that a leading one cannot

At A + 30 ms the client knows whether a swing line arrived. Spend the two existing assets on that
fact instead of on parity:

- **swing landed this tick → `Perc_Stick_hi.wav`** (42 % of ticks)
- **no swing this tick → `Perc_Stick_lo.wav`**, or silence as a user option (58 %)

Same one click per tick, same two files, no extra audio load — and now the click answers the
question the metronome exists to answer without looking at the rail ("a glance at the rail is a
glance away from the terminal text", spec §6). It is **100 % truthful by construction**, because it
is a report rather than a prediction. This design is only available if the cue does not lead.

Cost, conceded: the current tick-count parity alternation is lost, so a player using the beat to
count elapsed ticks loses that. I doubt anyone does — but make it a setting.

## 8. Concessions

Stated plainly, because a critique that concedes nothing is worthless.

1. **A small lead is genuinely free, and the sign argument has real force.** The late tail is
   one-sided: 0 % of arrivals are meaningfully early. So a lead cannot collide with anything, and
   the review's table shows 25 ms converts 42 % → 98.3 % warned-before-text. **I concede 25 ms.**
   25 ms is below any plausible threshold for noticing an offset between a bar edge and text
   appearing beside it (assumption). What I do not concede is 120, which is 5× the point of
   diminishing returns for two extra samples out of 175.
2. **The server really did resolve the event ~150–260 ms earlier.** The goal "display the server's
   clock, not the network's" is coherent, and if you adopt it the correct lead is ~205 ms, not 120.
   I argue it is the wrong goal: the player's only ground truth is the text on screen. An instrument
   calibrated to a reference the user can never see is one they can never check, and therefore one
   they must take on faith. Note too that the 188 ms think/flush component is, by the review's own
   §2.5.3, **inferred for tick output rather than measured** — so a lead justified as "cancelling a
   known constant" is partly cancelling an assumed one.
3. **The estimator work is right and I am not arguing with any of it.** Session-scope phase, p15
   circular quantile over a 64-sample packet-deduplicated ring, 8 ms/tick slew, never sampling
   downstream of `ClogRenderGate`, no `Restart()` on grace resumption. All of that stands
   regardless of which way this argument goes, and it is worth more than the lead debate: it takes
   `|err|` from 103 ms median / 974 ms max to 2 ms / 16 ms p90.
4. **The owner's instinct that "something different" is needed is right.** The current metronome is
   a beat with no content. §7 is my answer to that, and it is a real change, not a defence of the
   status quo.

## 9. What I would build

Concrete, with numbers.

**Audio — one click per tick, trailing.**
- Schedule at **`phi + 30 ms − L_out`**, where `L_out` is the measured `SoundService.Play`→speaker
  latency. Measure `L_out` first; if it exceeds 30 ms the scheduled offset floors at 0 and the
  effective placement is already correct.
- +30 ms is chosen so the click sits ~23 ms after the text on the modal 75 % of swings — inside any
  reasonable fusion window, so it reads as one event — while never preceding the text under ±10 ms
  of estimator error. It never claims an event that has not happened.
- **Content, not parity:** hi = a swing landed this tick, lo (or configurable silence) = no swing.
- Reject the −100/+100 bracket entirely.

**Visual — lead 25 ms, hard cap 50 ms.**
- Not 120. Justification is the review's own lead table saturating at 25, plus §5's empty-dwell
  artifact, which at 25 ms is 25–220 ms only on the 9 % tail rather than ≥120 ms on every tick.
- Keep linear easing. Keep "empty means due now" true to within 25 ms.

**If, and only if, the owner wants a second sound: put it at the deadline, not at the boundary.**
- A distinct quiet "last call" at **`phi + 1700 ms`** — i.e. **300 ms before the next bar-empty**,
  derived in §1 as the last instant an Enter press can still make the coming tick.
- This is the one pre-boundary cue that is both truthful (the 2.000 s lattice is metronomic, drift
  4 ppm) and actionable (it marks a threshold rather than predicting an event).
- Off by default. It doubles audio load for a signal that only matters when the player is
  mid-decision.

**Three cheap in-app measurements before any of this is called settled.**
1. **`L_out`.** Instrument `SoundService.Play` and capture via loopback. Needed for any placement,
   leading or trailing. Half an hour.
2. **Blind offset A/B.** Hidden setting cycling the click offset through {−100, 0, +30, +100} ms,
   randomised per encounter and unlabelled; owner rates each fight "did the click land on the
   text — early / on / late". 20 fights is one evening and settles the perceptual claims in §4 and
   §5 without appealing to any literature.
3. **Post-session log of `|click − text|`** per tick, from the raw receive path (never from a gated
   refresh). If the shipped setting's median `|click − text|` exceeds 50 ms, the setting is wrong,
   whichever side of the boundary it sits on.
