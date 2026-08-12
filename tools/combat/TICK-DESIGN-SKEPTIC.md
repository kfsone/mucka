# Tick indicator: the case against the two-beat metronome

Adversarial review of the "end of turn / start of turn" double-beat proposal.
Written 2026-08-11. Timing facts are taken as given from `TICK-PHASE-REVIEW.md` and not
re-derived; everything new here is measured from the same corpus.

Reproduce the new measurements with `decision_rate.py` in the scratchpad against `verify.db`
(the same reduced DB `analyze_tick_phase.py` uses).

Every claim below is tagged **[MEASURED]** or **[JUDGEMENT]**. There are no unlabelled
assertions.

---

## 0. Verdict in one paragraph

The owner is right about the *frame* and wrong about the *instrument*. He is right that this
is not a reaction game and that whatever the player intends must be **finished** before the
boundary - that instinct is confirmed by measurement below, and it kills any design that asks
for a reaction. But the double beat answers a question the player is not asking: **88% of
in-fight ticks carry no player decision at all**, and the two moments the beat marks are not
the moments any measured decision was made. Meanwhile the corpus contains exactly one
genuinely sub-second, per-tick, life-or-points reflex - and it is not a timing problem, it is
a *state* problem, which a metronome can signal beautifully by **stopping**. That is the
design I would ship instead.

---

## 1. [MEASURED] The strongest objection: there is no per-tick decision

### 1.1 The numbers

Per encounter, counting player commands the DB records as issued while the encounter was open
(`combat_session_commands.phase = 'during'`), classified into *decisions* (attack, flee,
wield/use, spell, dreamword, item), *annotations* (`//NOTE:`), and *other* (movement, looting,
inventory).

| | |
|---|---|
| in-fight ticks in the corpus | **329** (308 excluding encounter 16, see 1.4) |
| player commands issued in-fight, total | 200 |
| of which **combat decisions** | **49** (43 excluding enc 16) |
| of which `//NOTE:` prose annotations | 16 |
| of which movement / looting / inventory | 135 |
| **decisions per tick** | **0.149** (0.140 excluding enc 16) |

Accounting is complete and cross-checked: the corpus holds 1048 non-probe player commands,
200 of which fall inside an open encounter window - exactly the 200 the `during` phase records.
Nothing was dropped by the classifier.

### 1.2 Tick occupancy - the number that decides this

Binning every decision command into the fight's own tick lattice, n = 329 in-fight ticks:

| ticks carrying | count | share |
|---|---|---|
| **0 decision commands** | **290** | **88.1%** |
| 1 | 32 | 9.7% |
| 2+ | 7 | 2.1% |

**Nearly nine in ten combat ticks contain no player action of any kind.** The most decision-dense
fight in the entire corpus is the ram (encounter 6, 41 ticks, 10 decisions) at **0.24 per tick**,
and that is the fight the owner describes as the session's worst and most frantic.

At two beats per tick, the instrument speaks **616 times per 10 minutes of combat** against 43
decisions. That is a **14:1 sound-to-decision ratio**. Even at one beat per tick it is 7:1.

### 1.3 The gap between decisions

n = 38 consecutive in-fight decision pairs: median **2110 ms**, p75 **6199 ms**, p90 **17009 ms**.
The median inter-decision interval is *slightly longer than one whole tick*, and the upper quartile
is three or more ticks. The player is not operating a per-tick loop. They are operating a
roughly-every-third-tick loop with long idle stretches, exactly as spec §1.4 already says
("Combat is a waiting/analysis game").

### 1.4 A caveat I am volunteering against myself

Encounter 16 (`primary_target = 'unknown'`, the raven chase) contributes 123 movement/looting
commands and inflates the raw rate. All headline figures above are given both ways; excluding it
changes 0.149 to 0.140 per tick. It changes nothing. It is also independent evidence that the
client's own notion of "in combat" is loose enough that a metronome tied to it will click through
treasure-running.

### 1.5 [MEASURED] The player's commands are not, and never have been, tick-aligned

Phase of every in-fight decision command relative to its own fight's swing lattice (0 = the
instant the swing text arrives), n = 49:

```
   [-1000, -800)   4 ####
   [ -800, -600)   6 ######
   [ -600, -400)   7 #######
   [ -400, -200)   4 ####
   [ -200,    0)   6 ######
   [    0,  200)   3 ###
   [  200,  400)   3 ###
   [  400,  600)   7 #######
   [  600,  800)   4 ####
   [  800, 1000)   5 #####
chi2 vs uniform (9 dof) = 4.3   (critical value 16.9 at p = 0.05)
```

**Statistically indistinguishable from uniform.** The player fires commands wherever in the tick
they happen to finish typing, and - see section 5 - it has never once cost them a tick. The beat
would be introducing a discipline for which there is no measured need and no measured deficit.

### 1.6 [MEASURED] Reaction latency rules out reaction-timing designs outright

Time from a swing packet to the next decision command in the same encounter, n = 48:
median **1538 ms** (0.77 ticks), p75 3199 ms, p90 4476 ms. Only **6 of 48** reactions land within
500 ms of the swing text.

This is the measurement that vindicates the owner's "this isn't rockband" instinct and
simultaneously destroys the beat. A player whose median reaction is 1.5 seconds cannot use a
100 ms cue for anything. But it also means the "end of turn" beat arrives ~1400 ms *after* the
player already decided.

---

## 2. [MEASURED] Typing time dwarfs everything the beat is measuring

In-fight decision commands, character length: median **9**, p75 15, p90 **16**, max 32.

At the owner's own stated 120-130 wpm (≈10 chars/s, the figure Invariant #1 is written around),
that is a median of **900 ms of typing** and a p90 of **1600 ms - 80% of an entire tick**.
`kill ws with axe` is 16 characters. `//NOTE: could 'clumsify' or 'weaken' the ram` is 44.

So the "end of turn" beat at T-100 ms is telling the player something they needed to know
**one full second earlier**. By the time it sounds, the only honest message it can carry is
"whatever you have half-typed is already too late" - which is not priming, it is a scolding.
The beat cannot be moved earlier to fix this either, because the amount of lead the player needs
depends on the length of the sentence they have chosen, which the client does know (it is in the
input box) but a fixed-offset sound structurally cannot express.

---

## 3. [MEASURED, in part] The "end of turn" beat asserts something nobody has measured

The claim is *"anything you try now lands randomly either side"*. Three problems:

**(a) The ambiguous window is ~72 ms, not 100 ms, and its position is derived not observed.**
One-way transit is ~72 ms [MEASURED, `TICK-PHASE-REVIEW` §1.4]. A command committed 100 ms before
the client-side boundary reaches the server ~28 ms before it. Whether that is "either side" depends
entirely on where the server's *internal* boundary is relative to the client's estimate of it -
and the client's estimate is derived from *output arrival*, which is displaced from the server
event by an **inferred** ~188 ms of think-plus-flush that has never been measured for tick-generated
output. So the beat's placement error is at least as large as the window it claims to mark.

**(b) There is no observation in the corpus of what happens near the boundary.** [MEASURED - as an
absence.] The measured command phases (section 1.5) are uniform; not one in-fight command landed
inside ±100 ms of a lattice boundary. The corpus contains **zero evidence** for the beat's central
claim. It is not that the claim is wrong; it is that it is unfalsifiable from anything we have.

**(c) What the corpus *does* show contradicts the spirit of it.** In the water-snake5 loop
(section 5.2) the player re-issued an attack at +395, +428, +443, +522, +527, +822 ms into the
tick - i.e. right after the boundary, never near it - and **all eight re-attacks resolved on the
next tick, with zero lost ticks over the whole 12-second run**. The lattice runs unbroken at
578.481 / 580.508 / 582.479 / 584.481 / 586.480 / 588.480 / 590.480 / 592.480 s.

A beat that asserts an unmeasured hazard, at a placement whose error exceeds the hazard's width,
in a region of the tick where no command has ever been sent, is not honest. It is a plausible
story rendered as an authoritative instrument, which is the exact error `COMBAT-RAIL-SPEC` §5
warns about with `UNARMED` ("a claim, not an observation").

---

## 4. [MEASURED + JUDGEMENT] Alignment risk, and one concrete instance of the beat lying

### 4.1 Silent ticks

[MEASURED] 177 of 329 in-fight ticks carry a swing packet - **46.2% silent**, not the 58% quoted
in the brief. (The brief's figure comes from the swing-to-swing gap histogram; mine counts occupied
tick indices across each fight's span. The difference is immaterial to the argument and I note it
only so the two numbers are not later treated as contradictory.) Either way, roughly half of all
beats confirm nothing and are confirmed by nothing.

### 4.2 The concrete lie [MEASURED]

`CombatTracker.NpcFleeFailed` matches `The <npc> has fled by trying to go <dir>.` and, by explicit
design, **does not end the encounter** - its own code comment concedes "at most a two-second window
where the panel says 'in combat' during the player's own re-attack". Measured: that happened
**8 times in 12 seconds** against water-snake5, and 7 more times elsewhere.

During each of those windows the server has terminated the fight (`You can fight it no longer.`)
and **nothing is scheduled to swing at the player**. The metronome, driven off `InCombat`, keeps
clicking a steady, confident beat counting down to a swing that will not happen unless the player
re-attacks. Under the two-beat proposal it would additionally fire an "end of turn - decide now"
cue during a period when there is no turn to end.

That is not a hypothetical failure mode. It is the single most action-dense passage in the corpus,
and the instrument is wrong through all of it.

### 4.3 Cold start [JUDGEMENT, on measured inputs]

`TICK-PHASE-REVIEW` §2.5 already mitigates the bar with "run dimmed until `sampleCount >= 2`".
**Audio has no dim.** A click is on the beat or it is a wrong note; there is no low-confidence
rendering of a percussive transient. Any audio design must therefore be *strictly* gated on phase
confidence, and the two-beat design doubles the exposure - two chances per tick to be audibly
wrong, and a *relationship* between them (the 350 ms lub-dub interval) that is wrong whenever
either is.

---

## 5. [MEASURED] What the player actually did, twice, and whether a beat helps

### 5.1 The 19-stamina ram flee (the 2,079-point loss)

```
1786401976168  The ram hits you (25/105).     You miss the ram.
1786401978169  The ram hits you (19/105).     You miss the ram.
1786401978375  tx  <FES,FEI probe>                (+206 ms)
1786401979523  tx  flee w                         (+1354 ms after the swing text)
1786401979852  rx  (Persona saved on -2,079 = 44,337). Axe0 dropped.
```

The tick boundaries here are ~1786401976168 / 978169 / 980169. The player committed `flee w`
**1354 ms after the swing text and 646 ms before the next boundary** - comfortably inside the tick,
with room to spare. There was no timing failure of any kind.

- The **"end of turn" beat** would have fired at ~1786401980069, **546 ms after the player had
  already sent the flee**. Zero effect.
- The **"start of turn" beat** would have fired at ~1786401978420, 45 ms after the FES probe the
  client sent itself. The player was already reading `(19/105)`. Zero effect.

What actually cost 2,079 points is in the owner's own note two minutes later:
*"But I couldn't tell how dangerous staying was."* He had been at 33 → 25 → 19 across three
consecutive ticks and did not know the ram's damage ceiling. That is an **information** deficit
(spec §9a item 1, "max observed hit per NPC"), and no beat, at any offset, addresses it.

### 5.2 The water-snake5 fake-flee loop - the corpus's one real sub-second reflex

Eight fight terminations in 12 seconds, each followed by a manual re-attack. Latency from the
terminating packet to the player's `kill ws with axe`:
**1689, 822, 428, 527, 395, 522, 443 ms** (median ~500 ms).

Two observations, both damning for the beat and both generative for the alternative:

1. **The player already achieves ~450-500 ms with no timing aid at all, and lost zero ticks.**
   The 500 ms is too fast to type 16 characters, so this is command-history recall - a single
   keystroke. The binding constraint was never the tick; it was *noticing the flee line in the
   scroll*, which is precisely the thing spec §1 says the rail exists to spare the player from.
2. **The trigger the player used was text arrival, not tick phase.** A "start of turn" beat is
   roughly co-located with that trigger and therefore adds nothing the eye did not already have -
   except that it *also* fires on the ~46% of ticks where nothing arrived, diluting exactly the
   discrimination that made the reflex work.

**Neither proposed beat would have changed either outcome.** That is the finding, stated as the
brief asked it to be stated.

---

## 6. [JUDGEMENT] Heartbeat or stutter, and the annoyance question

The proposal's two sounds sit roughly T-100 ms and T+250 ms (100 ms after text expected at ~T+150).
That is a **~350 ms separation**, repeated every 2000 ms.

- A real cardiac lub-dub at 60 bpm has a ~120 ms S1-S2 gap. At 350 ms the auditory system does not
  group two transients into one event - well past the ~150-200 ms range where perceptual grouping
  reliably holds. [JUDGEMENT, recalled psychoacoustics, not measured here.] The listener will hear
  *two clicks*, not a heartbeat. The metaphor that motivates the design is unlikely to survive
  contact with the speakers.
- The specific rhythm - short, long, short, long at 30 bpm - is the cadence of a fault, not a pulse.
  Dripping taps and failing bearings sound like this.
- **The half-life question, grounded.** The player typed **16 `//NOTE:` prose annotations while in
  combat** [MEASURED], including 44-character sentences. This is a player who uses combat's dead
  time for composition at 120-130 wpm. Invariant #1 exists because a 50 ms typing hitch is
  "downright offensive"; a doubled percussive interruption every two seconds through a
  sentence-composition task is the same offence in the auditory channel, and the codebase's own
  standard for that is zero tolerance.

**Prior art** [JUDGEMENT - recalled, not researched, treat as a prompt to check rather than as
evidence]: IRE/Mudlet balance-and-equilibrium trackers are visual and *event*-driven (they light
when balance returns), not isochronous. MMO global-cooldown indicators are visual sweeps; audio GCD
clickers exist only as third-party addons with a persistent reputation for fatigue. Isochronous
audio metronomes are standard in exactly one place - musical practice - where the task is
continuous entrainment and the output is continuous. MUD2 combat is discrete and 88% idle. The one
close precedent for a rhythm-timed MUD action is EQ bard twisting, which the owner explicitly and
correctly disowns.

---

## 7. [JUDGEMENT] Is sound the right channel at all?

The rail's whole justification (spec §1.1) is to let the player keep their eyes on the text. Audio's
advantage is real: it works when the eyes are elsewhere. Its cost is equally real and is usually
skipped: **audio cannot be ignored.** A bar in peripheral vision costs nothing until looked at; a
click is consumed whether or not it is wanted. Attention is spent on every one.

That asymmetry gives a clean rule, and I would make it the governing principle of the audio budget:

> **A sound must be rare, must mark something the eye will plausibly miss, and must have an action
> available within one tick. Time is none of these things.**

Time is continuous (so it cannot be rare), it is already rendered for free in peripheral vision,
and - measured - 88% of ticks have no action. **State transitions** are all three.

---

## 8. [MEASURED] One implementation objection that blocks the proposal as specified

`SoundService.PlayCoreAsync` (Windows) calls `MediaSource.CreateFromUri(new Uri(path))` on **every
play**, re-opening the WAV through the WinRT media pipeline each click, then `player.Play()`. The
`MediaPlayer` instances are pooled precisely because engine cold-start was found to be expensive -
but the per-play source creation is not pooled, and **neither the latency nor its jitter has ever
been measured.**

The proposal specifies two sounds placed to ±100 ms around a boundary, 350 ms apart. That is a
precision claim about a transport that has not been characterised, on a path that performs file I/O
per beat, twice per tick. `TICK-PHASE-REVIEW` §2.2 already flags this ("Measure `SoundService`'s
latency before committing a number; if it turns out to be 80 ms, the audible lead should be ~0").

**No timed-audio design in this project is verifiable until that number exists.** The two-beat
design is not merely unjustified, it is currently unbuildable to spec.

---

## 9. Where the proposal is right, and I would keep it

Stated plainly, because it matters:

1. **"Done, not started" is the correct frame** - and it is now measured, not preference: median
   reaction latency 1538 ms rules out any design that asks for a reaction to a cue. Every future
   proposal should be tested against that number first.
2. **Not leading the indicator into reaction-timing territory is correct.** The owner's rejection of
   the rhythm-game analogy is right for a better reason than he gave.
3. **RTT + safety margin is the right budget concept.** It is just not a sound. Section 10.4 turns
   it into an instrument that can actually carry it.
4. **The beat must be anchored to the game's tick, never to the player's keypress** (already spec §6,
   and already correctly implemented in `CombatMetronome.Start`). Unchanged.

---

## 10. The design I would ship instead

Same specificity demanded of the proposal.

### 10.1 The visual keeps time. Full stop.

Adopt `TICK-PHASE-REVIEW` §2.3 unchanged: session-scope `TickPhaseEstimator`, p15 circular quantile
over a 64-sample packet-deduplicated ring, ≤8 ms/tick slew, no `Restart()` on grace resume,
**120 ms visual lead**, linear easing. This is the entire timekeeping instrument. It is free, silent,
peripheral, and it degrades gracefully: a bar 100 ms out of phase looks fine, whereas a click 100 ms
out of phase is a wrong note.

### 10.2 The metronome: one click, and its job is to be MISSED

- **One sound per tick**, not two. `Perc_Stick_lo` only.
- **Drop the hi/lo alternation.** It encodes a 4-second super-beat, and nothing in MUD2 has a
  4-second period. It spends the design's only spare auditory dimension on nothing, so that a
  *change* in timbre is unavailable later for something real. [JUDGEMENT.]
- Audible lead **40-60 ms**, but **not until `SoundService` latency is measured** (section 8). If
  measured latency exceeds ~60 ms, the lead is 0 and the constant is already spent.
- **Gated on phase confidence**: silent until the estimator has `sampleCount >= 2`. There is no
  dimmed click.
- **It runs only while the client believes something is scheduled to swing next tick.** That is a
  narrower predicate than `InCombat`, and the spec already builds half of it (grace stops the bar).
  Extend it with the one measured case: on `CombatEventKind.NpcFleeFailed` - which the parser
  already emits - **the beat stops** until a fresh `fight-start` or swing re-establishes it.

**The information is in the silence.** A dropped beat in an established isochronous rhythm is
detected pre-attentively and fast; a novel sound is not [JUDGEMENT, recalled psychoacoustics]. So
the single moment in this entire corpus where a sub-second reflex genuinely paid - the water-snake5
loop, 8 terminations in 12 s - becomes the one moment the metronome speaks, by shutting up. The
player's measured ~500 ms recall reflex is already good enough to exploit it. Nothing else in the
design has to change to get this: it is one event subscription and one `StopLocked()`.

This inverts the noise economics completely. Under the proposal the instrument makes 616 sounds per
10 combat minutes to mark 43 decisions. Under this design it makes ~308 sounds and the *absence* of
a sound marks the ~15 moments that were actually urgent.

### 10.3 Default OFF, and persisted

Spec §6 argues on-by-default because "a feature that must be found and switched on every session is
a feature nobody uses". The premise is right and the conclusion is wrong: the fix for *every
session* is **persistence to `mucka.ini`** (§6 concedes it is only session-scoped today), not
defaulting a 14:1 noise-to-signal instrument to on. Persist the toggle; default it off; the switch
is already drawn permanently on the rail, so it advertises itself.

### 10.4 If the commit-deadline idea is kept, this is the honest form of it

Not a sound. **One 1dp static mark on the tick bar at 150 ms before empty** - 72 ms measured one-way
transit plus a 78 ms margin covering the 9 ms RTT p95-p5 spread and the estimator's 16 ms p90 error.

- Drawn dim, in the bar's own grey, never animated, never labelled (spec §6: "no colour coding and
  no label").
- It says exactly one thing the client can actually know: **a keystroke committed left of this mark
  is on the wire before the boundary.** It makes no claim about which tick the server resolves it
  into, because that is unmeasured (section 3a).
- It solves the typing problem the beat structurally cannot: the player sees how much bar remains
  *and* knows the length of the sentence they have chosen, so a 16-character command and a
  3-character one are both correctly judged against one static reference.
- Costs zero attention, degrades to invisible, cannot be a wrong note.

### 10.5 The audio budget: three sounds, and time is not one of them

By the rule in section 7, ranked by measured evidence:

| sound | trigger | measured support |
|---|---|---|
| **beat drop** (silence) | `NpcFleeFailed`; grace; encounter end | 15 occurrences; 8 in 12 s; the corpus's only sub-second reflex |
| **survival tone** | stamina crosses 20 downward, hysteresis to 25 | spec §6a; owner's tally: 3 of 5 occasions at exactly 20 cost the character |
| **join tone** | a new name enters the engaged roster mid-fight | rats 16/17/18/19/20/21 joined one at a time via room-description lines; §6a's "surprise blow of 5-15" |

Each fires once per transition, never repeats, and is distinguishable by timbre without counting.
Nothing else gets audio. In particular there is **no** "turn started" and **no** "turn ended" sound.

### 10.6 What the player learns

Within one fight, without being told:

- *Steady click* → something is swinging at me on this rhythm. Eyes stay on the text.
- *The click stops* → the fight is not running. This is the moment to act, and the action is a
  single history-recall keystroke. (Measured: the player already does this in ~500 ms cold.)
- *The bar* → how long I have, and (with the 150 ms mark) whether the sentence I am typing will make
  the boundary. Read only when I choose to look.
- *A distinct tone* → my stamina crossed 20, or something new joined. Both are the two things in the
  corpus that actually killed points.

### 10.7 If the owner keeps the two-beat design anyway

The minimum bar before it ships:

1. Measure `SoundService` end-to-end latency and jitter with a loopback capture. Without it the
   ±100 ms placements are unverifiable (section 8).
2. Hard-gate both beats on `sampleCount >= 2`; never fire either during a cold estimator.
3. Stop both beats on `NpcFleeFailed` and grace, or the instrument is confidently wrong through the
   corpus's most urgent passage (section 4.2).
4. Give the two beats timbres far enough apart that a listener can name which one they heard without
   counting - at 350 ms separation they will not group, so they must at least be distinguishable.
5. Ship it off by default and persisted, and re-measure decision-per-tick rate after a session with
   it on. If it does not move above 0.14, it is not doing anything.
