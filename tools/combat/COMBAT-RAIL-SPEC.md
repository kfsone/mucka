# Combat Rail - implementation spec

**Status: LOCKED. Implement as written.** Supersedes `DESIGN_FINAL.md` for everything about
the rail's appearance and content. `DESIGN_FINAL.md` remains valid only for window policy
(the rail is a separate right-edge panel toggled by `$clog on`/`off`, the window never
resizes itself on a combat event) and its performance contract.

Arrived at through a four-way design bid (atlas / ember / vellum / quarrel), three review
rounds, and nine revisions against the owner's direct feedback. Visual reference:
`bid-synthesis-v9.html` plus the two amendments in section 9.

ASCII only in code (`INTERNAL.md`). Every glyph below is drawn as an `SKPath`, never a font
character.

---

## 1. Governing principles

1. **The rail assists a glance, then gets out of the way.** Its job is to help the player
   re-focus on the terminal text, not to be read. Rich drill-down belongs in a separate tool
   window, out of scope here.
2. **Bottom-focused.** The player's gaze rests at bottom-center (input box + newest text).
   Live content clusters at the **bottom** of the rail; empty space goes at the top.
3. **Nothing moves.** Fixed reserved slots, car-dashboard style. Indicators change state in
   place - lit, unlit, colour, intensity. No flow layout, no growing or shrinking elements
   during a fight.
4. **Alarms are seen; information is read.** Combat is a waiting/analysis game with time to
   look, so the rail may be dense. But anything time-critical must work without reading.
5. **Never render "unknown" the same as "zero".**

## 2. Geometry

- Width **336dp**. Left panel stays 228dp, untouched.
- Bottom-up order (nearest gaze last):

```
   (empty - top of rail)
   overflow row          (only when opponents exceed slot capacity)
   opponent slots        (N slots, N computed from window height - see 3)
   [STA seal] weapon/alt-weapon [MAG seal]     96dp
   tick meter + encounter gauge                30dp
   (bottom edge)
```

- Opponent slots are **46dp** each, identical size, no primary/secondary distinction.
- Bottom row grid: `92 / 1fr / 92`.

## 3. Opponent slots - count is derived from window height

**Slot count is computed from available rail height**, filling from the bottom up:

```
slots = clamp(floor(availableHeight / 46dp), 1, someSaneMax)
```

Recompute only on **panel resize**, never on a combat event. Within a session the count is
fixed, so rule 3 (nothing moves) holds during a fight.

Slots fill **from the bottom**, so opponents accumulate upward and the most relevant sit
nearest the gaze. Unoccupied slots render as empty reserved frames.

**Overflow.** When engaged opponents exceed slot capacity, the **topmost** slot becomes an
overflow row - farthest from the gaze, being the least actionable content. It shows
**names only**, sorted by **damage dealt to the player (highest first)**, i.e. by how much
each has actually hurt you, not by arrival or alphabet. No health, no pips, no prose.

Measured context, **re-measured 2026-08-28; the previous figure was wrong.** This paragraph used to
read "the maximum simultaneously-engaged opponents across the whole capture corpus is **4** (68 of 71
sessions were 1v1... even the 16-rat brawl never exceeded 4 at once)". That described the offline
research capture only. Against the **live clog corpus** - 984 encounters,
`uv run tools/combat/concurrency.py`:

| peak simultaneous NPCs | encounters |
|---|---|
| 1 | 838 (85%) |
| 2 | 78 |
| 3 | 30 |
| 4 | 26 |
| 5 | 10 |
| 6 | 1 |
| **7** | **1** |

**The maximum is 7**, on 2026-08-27 (`large rat0` plus rat1/3/4/6/8/9; 9 distinct over the fight),
verified against the raw event stream and not only the state machine - five different rats are visible
acting on a single tick. Twelve encounters have peaked at 5 or more, spanning 2026-08-04 to
2026-08-27, so "4" was stale for essentially the whole life of the claim and nothing re-ran it. The
owner reported the 5+ fight from memory; the corpus had never been asked.

**Consequences.** Overflow is not the rare pathological case that paragraph implied - it must exist,
and it will be seen. The slot cap is 8, so at 7 live opponents there is **one creature of margin**
before live participants alone fill it and `RosterPlan.HiddenLiveCount` goes nonzero in ordinary play.
Nothing should be justified on the old one-in-seventy-one framing.

**Re-run it rather than citing this table.** The figure moved because a claim nobody could cheaply
re-check went unchecked for a month; `concurrency.py` exists so the next reader need not trust a
transcription. `tickdamage.py` beside it answers the companion question - what a tick in those fights
actually cost.

## 4. Opponent slot contents

Line 1: **name**.
Line 2: the **health gauge** - seven pips with the game's own health phrase overlaid.

**Pips - exact geometry, do not scale:**

```
width 16dp, height 6dp, corner radius 2dp, gap 3dp   (7 pips = 130dp)
empty      fill #171d20, 1dp border #262e32
filled     #3A96DD (Campbell index 6), flat - no severity gradient
stale      #3d5559
unknown    transparent fill, 1dp dashed border #3a4247
```

Thin dashes, never boxes. They are **fixed width and must never stretch to fill**.

**Fill direction: health REMAINING.** Seven pips lit at full health, depleting as the
creature is hurt, so `close to death` shows one lit pip. This matches the stamina seal
(both deplete) rather than inverting between two gauges on the same panel.

**The health phrase** (`critically injured`) is drawn **overlaid on the pip row**, centred, in
the terminal's monospace with a shadow for legibility - the pips are shorter than the text
and read behind and around it. Echoing the game's own wording anchors the panel to what the
player just read in the scroll. The game's grammatical filler is stripped (`to have minor
injuries` -> `minor injuries`) and nothing else is reworded.

**The scale is measured** - see `NpcHealthRungs`. Three vocabularies (living "injured",
undead "damaged", banshee "drained"), seven words each, and they line up rung for rung:

| rung | living | undead |
|---|---|---|
| 7 | fit | strong |
| 6 | superficially injured | superficially damaged |
| 5 | minor injuries | minor damage |
| 4 | covered in wounds | moderately damaged |
| 3 | seriously injured | seriously damaged |
| 2 | critically injured | critically damaged |
| 1 | close to death | close to expiry |

`covered in wounds` is rung **4**, not 6 - it is *better* than `seriously injured`. Counted
within reducer-segmented fights: **62 transitions to a worse rung, 4 to a better one, none
contradicting this order.** An earlier hand-written draft had it backwards by two rungs, in the
direction that reads a dying creature as healthier than it is. Do not "correct" it from
intuition.

**No published source covers the wound descriptions.** The MUD2 strategy guide documents damage
formulas, per-creature stamina pools and flee costs and says nothing about them at all - see
`MUD2-PUBLISHED-MECHANICS.md`. This ladder is the best available reading of observed behaviour,
not documented fact.

The ladder is **not a health percentage** and must never be drawn as one: seven words cannot
resolve a pool that runs from 1 (a firefly) to 800 (the dragon), and rung 2 on a 25-stamina rat
is not the same amount of trouble as rung 2 on a 100-stamina rat0. It is also **not a ratchet** -
creatures regenerate; a zombie in the corpus oscillates `strong` <-> `superficially damaged`
four times in one fight. Every observed improvement is exactly one rung, but they happen.
Always show the **latest** reading, never the worst seen; latching to the worst keeps promising
a kill that is no longer one swing away.

Staleness: the ladder only updates when you land a hit (player hit rate 0.57). A one-tick
gap is normal (68% of gaps); fade to **stale at 3 ticks** (6 s), to **unknown at 5** (10 s).
The unknown state is dashed outlines - never a full ladder and never an empty one, because
both of those are confident claims.

The same sentence appears in **room descriptions**, so a health reading is accepted only for
a creature already engaged. A phantom opponent on the panel is worse than a missing one.

**The creature's own weapon** is drawn right-aligned on its name line, in the hostile colour. It is a
fact about that participant, so it lives on that participant's row - it used to be printed as
`they: club` inside the player's own weapon column, which put an enemy fact in the block describing
you and, in a pack fight, could only ever name one of them.

Current target: marked by emphasis **within its own slot** (border, brightness) - never by
size.

## 5. The bottom row

**Stamina seal (left)** and **magic seal (right)**, weapon text between them.

- `STA` / `MAG` as **dim text inside the ring**. Never underneath.
- **No status dot** anywhere near the seals - the seal carries its own state.
- Stamina colour follows Clio's `colorcode()` ladder, identical to the top status strip:
  `>=100` bright green, `>=76` green, `>=36` bright yellow, `>=16` yellow, `>=6` red,
  else bright red. The rail and the strip must never disagree about the same number.
- **Magic** is purple shading blue (around `#8F84EE`), turning **red below 20**. When
  `maxMag == 0` the seal is **greyed and inert but still present** - removing it would be a
  displacement. Magic reaching 0 costs a quest that can delete the character.
- **Out of combat both seals go grey** (colour knocked back ~45%, hue kept). The numbers stay
  true - they ride the FES heartbeat and the top strip still shows them in full colour - but a
  lit alarm colour *on this panel* means "this is what is happening to you in this fight", so
  leaving the seals hot after a fight ends keeps alarming about a fight that is over. Dimmed,
  never removed: the row must not change shape.

**Weapon (centre). In-combat only.**
- Sword icon + weapon name, or open-hand icon + `UNARMED`. **Never the word "armed"** - the
  weapon name already says it.
- **Nothing is drawn here between fights.** MUD2 has no equipment slots and no default weapon:
  a weapon is chosen while fighting, or as part of starting a fight (`kill x with y`), and
  `wield` is per-engagement. So out of combat the client does not know what is in the player's
  hands - `UNARMED` there is a claim, not an observation (it read as one while the player sat
  in the tea room). Rule 5 applies; the space stays reserved and empty.

**Alternate weapon**, below the weapon: hotkey chip left, name right-aligned. **`Ctrl+W`**
(consistent with `Ctrl+F` flee, `Ctrl+E` chase, `Ctrl+G` follow). Registered as a keyboard
accelerator so the event is marked handled and never reaches a default close-window action.
- Sends `wield <noun>`, where the noun is the item name's final token - MUD2 does not want the
  descriptive words ("a rusty pick2" -> `wield pick2`).
- **Drawn only when there is a candidate**, since the chip is the key's only advertisement and
  advertising a dead key is worse than showing nothing. Its position is fixed, so lighting up
  mid-fight displaces nothing (rule 3).
- **Candidates are carried items already on file as having been fought with.** That record is
  the client's entire weapon vocabulary, and it is earned rather than guessed: MUD2 never says
  "this object is a weapon", and a wrong wield is not free - switching drops your guard and
  awards the opponent a swing. Ranked by highest median damage per landed blow *against this
  creature group* (the axis MUD2's hidden per-creature modifiers appear on); weapons with no
  record here rank after every weapon that has one, but are still offered. Deliberately not
  gated on beating what is in hand - the key gets pressed when a weapon has just broken or
  been refused, and at that moment "worse than what you had" is the only thing to fight with.
- Recomputed every refresh, never latched: the pack changes mid-fight, and a stale offer would
  wield something no longer carried.
- Hovering the alt-weapon shows a terse comparison. (Not yet built.)

**The flee pill.** `FLEE` and `Ctrl+F`, at the **top of the middle column** - the band above the
weapon line, level with the upper arc of both seal rings, so it sits literally between the two
readouts the decision is about. Reserved whether drawn or not; nothing in the column moves when it
lights up (rule 3). Added 2026-08-28 at the owner's request; see section 10's amendment for why this
is not the thing section 10 bans.

**Content:** `Flee {sta} (-{cost}) ^F` - e.g. `Flee 23 (-2.1k) ^F`. Stamina because that is the number
the decision is actually about and at the deciding moment the eye is on the chip; the price as a
parenthetical, **absent when there is none**; `^F` because its job is to remind a player whose hand is
already on the keyboard that they need not reach for the mouse - drawn even though the chip is also
clickable.

**Treatment: the floating dreamword chip's, in reds** (owner, 2026-08-28). A **filled** chip, not the
thin dim outline the rest of this panel favours - dark red fill `#3E181C`, a **2dp `#FF0000` border**,
**white bold** text, corner radius **10**, geometry matched to that chip. It is **152dp wide starting at
x=92**, wider than the weapon column below it and centred on the panel instead: the extra width is taken
over the seals' bounding BOXES without touching their drawn rings, which have narrowed to cx +/- 23.7 at
the pill's vertical centre. Do not widen further without redoing that arithmetic - the rings bulge fast
further down. `#FF0000` and the fill are
**the panel's only colours not derived from `TerminalTheme.Palette`** (section 11): Campbell has no pure
red, its bright red being `#E74856`, and the owner named the value directly. An explicit override, not
drift.

- **Four states**, resolved by `FleePillResolver` (pure, in mudsharp, unit-tested):

  | state | when | drawn as |
  |---|---|---|
  | `Hidden` | none of the below | nothing; the slot stays reserved |
  | `Visible` | stamina <= **26.5**, or one average bad tick would reach the survival threshold | the chip, knocked back to ~55% |
  | `Caution` | stamina <= **20**, or one tick's combined damage >= stamina, or **two hits left** | full strength, **border pulses** |
  | `EscapeNow` | stamina <= **6.5** | + a faint background breath behind the text |

- **The price is an ESTIMATE and is drawn coarsely on purpose** (`FleeCostEstimate`). MUD2 charges a
  fraction of total score to leave, so the figure needs the live score, which rides the FES heartbeat.
  The curve interpolates four anchors of which **exactly one is a measurement**: 10% flat at and above 20
  stamina (owner's stated maximum, never measured), **4.48% at 19** (score 46,416 -> 44,337, -2,079, the
  only flee in the corpus, n=1), ~1.2% at 7 (owner's recollection), free at 6 and below. Everything
  between 7 and 19 is a straight line between two points, one of which is a memory.

  **The cliff at 19/20 is kept unsmoothed** - fleeing at 20 costs more than twice what fleeing at 19
  costs - because it is the one feature of the shape anybody has actually observed, and it is the same
  perversity that makes 20 the threshold that matters: maximum price at exactly the moment holding on is
  most likely to kill you.

  **Format** (owner): bare points under 1,000; one decimal and `k` under 5,000; whole `k` above. Coarse
  because four anchors cannot support a figure printed to the point.

  **Unresolved and it could invalidate this for other characters:** the published guide states these
  bands as percentages of MAXIMUM stamina, the owner's are ABSOLUTE, and on a 105-max character the two
  nearly coincide so nothing here can tell them apart. `verify_mechanics.py` reports flee cost as
  INSUFFICIENT and is right to.

  **No parenthetical means free OR unpriceable, never zero.** `Points` returns null for both, and a
  paying flee never rounds down to nothing - a rendered `(-0)` would be a claim, and a zero conflated
  with "we do not know your score" is the reading that gets a character killed.

- **One shape across all three visible states; the escalation is brightness and motion.** Rule 3 lists
  intensity as a legitimate way to change state in place, and this is why the chip can be a strong
  visual without shouting from the moment it appears - a full-strength red chip at 26 stamina is an
  alarm that gets ignored at 20.

- **The whole-panel glow was halved to make room for this** (owner, 2026-08-28). Section 8's glow ran
  `1.0 -> 0.25 -> 1.0` and in play it dominated so completely that the pill did not draw the eye at all,
  in the exact fight it exists for - 23 stamina against a banshee. Both ends are now halved
  (`0.5 -> 0.125`) rather than the trough merely raised: raising the trough shrinks the swing while
  leaving the panel brighter on average, which is backwards. The glow is still the loudest thing the
  client owns; it is no longer the only thing visible while it runs.

- **The pulsing element is laid OVER the canvas**, unlike the panel glow and the tick fill, which sit
  behind it. The chip's fill is opaque, so a pulsing sibling behind would simply be painted over. In
  front is safe because it is stroke-dominant: the ring is opaque, the `EscapeNow` fill is low-alpha, so
  the white text stays legible through it. It sits above the canvas but **below `CombatMetronomeHit`**,
  so the panel's one real hit target keeps its clicks, and it is `InputTransparent` with no gestures.

- **The canvas draws the border only in the quiet state.** At the alarm states the ring is what moves,
  and a static full-strength ring underneath a pulsing one of the same colour would swallow the pulse:
  `#FF0000` over `#FF0000` does not dim, so the border would appear to go from full to full and read as
  motionless. Fill and text are drawn at every visible state, since neither is the thing moving.

- **`EscapeNow` exists because the danger there is MASKED, not because it is merely worse.** At that
  stamina MUD2 charges almost nothing to leave, and a fight that has stopped costing points reads as
  a fight that has stopped costing anything. It has not: one ordinary blow kills, and death in combat
  is deletion. The owner's framing, kept because it is the whole justification for a fourth state -
  *it is the tide retreating ahead of the tsunami.* Note what the panel does NOT do here: it does not
  say the word "free", and it does not draw the band as reached, achieved or safe.

- **Everything happens on the tick, so the damage figure is a lump, not a rate.** MUD2 resolves every
  combatant's swing on one 2.000 s boundary, so a pack's output does not arrive as a sequence the
  player can react between. Two quiet ticks and then `rat1 + rat2 + rat3` together is an ordinary
  shape. The figure tested against is therefore the **sum of what each live opponent hits for when it
  hits** - not a hit-rate-discounted damage-per-tick, which is the right number for "how long will
  this fight take" and the wrong one for "can the next boundary kill me".

- **The per-creature rule is subsumed, exactly.** "Any one of them averages more than my whole
  stamina" is a strict subset of the sum, so there is one rule rather than two that could only ever
  agree.

- **Two hits left is in, and it is the only entry that is a count rather than a band.** It is what
  speaks for a fight where stamina is untouched - two hits from death at full health is how a dragon
  kills someone - and it is already the sole override that promotes the whole-panel glow (section 8),
  so the pill agreeing with it stops two readouts disagreeing about one state.

- **An unmeasured creature counts as 20** (`FleePillResolver.AssumedUnknownHit`; owner, 2026-08-28,
  replacing a first version that counted it as nothing). Deliberately pessimistic, because the
  alternative was silence, and silence about an unmeasured creature reads as a claim that it is
  harmless.

  **What 20 rests on, stated honestly.** It is the top of the range *the owner* gives for ordinary NPC
  maximum damage - "many NPCs have a maximum hit in the 15-20 range", one of his own stated reasons the
  survival threshold sits at 20. An earlier draft of this entry called that figure **published**, which
  was wrong twice: the bullet is headed "per the owner", and the document holding it opens by saying its
  contents are hypotheses from a fan strategy guide and that nothing in it is settled. Lived experience
  outranks that guide for a mechanics question, but neither is a measurement. **There is a real ceiling
  available and nothing uses it yet:** `bestiary.tsv` gives every creature's STR, and `1..(CS/6)+1`
  turns that into a hard per-creature maximum. Until that reaches runtime, 20 is one person's
  reasonable guess and should be read as one.

  **A substitution, not a floor:** a creature with samples is described by its samples however mild
  they are. A rat measured at 4 a blow contributes 4.

  **A measured zero is a measurement.** MUD2 lands blows that take nothing off and `DamageProfile`
  counts them on purpose, so `Samples > 0, Sum == 0` describes a creature that has demonstrably failed
  to hurt anyone - it contributes **0**, not the assumption. The test is on the sample count, never on
  the resulting number. The first implementation branched on `worst > 0` and so gave a proven-harmless
  creature the full 20, identical treatment to one never seen before: the same unknown-is-not-zero error
  the assumption exists to fix, made in the opposite direction.

  **It shares a value with the survival threshold and is not the same quantity** - one is a stamina to
  act at, the other a damage a creature might deal. Do not merge them or derive one from the other. If
  either is tuned it moves alone. This project has already shipped a bug of exactly that shape.

  **It multiplies, and that is the one surprising consequence.** Four creatures nobody has ever been
  hit by sum to 80, raising the pill to Caution from 80 stamina. Transient - one landed blow each
  replaces the assumption with a measurement, and a species already met is covered by its group
  history - but a fresh species arriving in a pack alarms early. If that reads as crying wolf in play,
  this constant is the knob.

  Live opponents past the roster's row cap are extrapolated at the **mean of the ones with rows**
  rather than at the assumption, since reaching that case means eight rows of real participants are
  already in hand and eight readings say more about the ninth creature than a global default does.

- **It is a button.** Clicking it sends the same bare `flee` Ctrl+F sends, down the same path, and
  hands focus straight back to the command box - an invisible `Button` over the drawn chip, exactly as
  the metronome toggle is built, so the canvas keeps zero gesture recognizers and Invariant #0 holds by
  construction.

  **Amendment, 2026-08-28 (owner, in play).** It shipped non-interactive, on the reasoning that an
  accidental flee is among the most expensive misclicks in the game - MUD2 charges a share of total
  score, and charges for a FAILED attempt too (102 points and an experience level, in one captured
  frame, for an attempt that never moved the player). The owner's verdict on that was blunt and
  correct: *"it did not **do** anything"*. A control that looks like a button and is inert is worse
  than either a button or a label, and the reasoning had quietly optimised against the wrong failure.

  The misclick risk is answered by geometry instead of by refusal: **the hit target exists only while
  the pill is drawn.** It is `IsVisible="False"` at `Hidden`, so empty panel space is never a live flee
  button, and its width is stated explicitly rather than filled - a `Fill` button would stretch the
  whole panel and make a flee reachable from anywhere along the row.

- **Invariant #0 note, learned the hard way.** `InputTransparent` on the pulsing MAUI `Border` did NOT
  keep it out of the pointer path: clicking the pill took keyboard focus off the command box. Its
  platform view is now `IsHitTestVisible = false` directly. Separately, **`CombatPanelBorder` was never
  in `DisableFocusOnInteraction`'s list at all** - harmless while the panel held only an
  `InputTransparent` canvas, and a real hole the moment anything clickable arrived. It and its
  interactive children are listed now.

- **In-combat only, and grace counts as out.** Same gate as the tick meter: nothing is attacking
  during the post-kill grace window, and an instrument saying RUN then is asking the player to pay a
  real price to escape a finished fight.

- **The pulse is a Composition sibling** behind the canvas, sized from `CombatRailView.FleePillDp`,
  driven by `FleePulse` - the canvas draws only the still parts (Invariant #1, section 11). It shares
  `PulseLayer.PeriodMilliseconds` with the whole-panel glow, which at these stamina levels is already
  running: one period and one arming block is the nearest two visuals get to one heartbeat, and 4.2's
  "one pulsing element" rule is about competing phases, not about a count. **Worth a look in play** -
  if the two read as noise rather than as one alarm, the fix is to drop the pill to static colour.

**Unarmed timing.** `wield` is **per-engagement, not sticky** - every encounter starts with
nothing in hand until stated. So an unarmed opening is *normal*: stay calm for the first
ticks, go amber only **after damage has landed**, and never straight to red.

## 6. Tick meter and encounter gauge

- Ember's tick, at the very bottom, **pale and dim** - grey/white, low opacity. It is a
  timer, not a judgement, so **no colour coding and no label**. A small drawn metronome mark
  is permitted; text is not.
- **It moves, and it drains.** The bar starts **full** at the top of a tick and shrinks
  **leftwards** - its left end pinned, its right end travelling left - reaching empty as the next
  swing lands. A countdown, not a progress bar: it answers "how long have I got", and it matches
  the health pips (lit = remaining) rather than inverting between two gauges on one panel. This is
  the behaviour of the prototype the owner approved; a version that grew from empty was rejected.
- **Strictly linear.** WinUI Composition applies a cubic ease-in-out to any keyframe that carries
  no easing function, which makes the bar crawl at both ends and race through the middle. It
  shipped that way once and was caught in play - *"combat tick bar is not smooth, it seems to slow
  down towards the right"*. A clock that does not tick evenly is worse than no clock, because it is
  read as information. Every keyframe takes an explicit linear easing.
- MUD2's tick is exactly 2.000 s and phase-locked, so a sweep started at the fight's first swing
  stays in phase for the rest of the fight without resyncing. The canvas draws the **empty track
  only**; the fill is a Composition-animated sibling behind it (`TickSweep`), because a 2-second
  progress bar repainted on the UI thread is the single most typing-hostile thing the panel could
  contain.
- **In-combat only.** The whole row - track, fill and opponent count - is absent between
  fights. A tick still running in the tea room reads as a fight that never ended.
- Two exceptions only: **red at stamina <= 30**, **glow at stamina <= 20**. Both are now backed by
  mechanics rather than taste - see "The three stamina thresholds" below.
- The **encounter gauge is drawn over the tick**, sharing its pixels - not above, below or
  beside it.
- Count format: **up to 5 crossed-swords marks, plus `+N` for the remainder.** Nine opponents
  renders 5 swords and `+4`; fourteen renders 5 swords and `+9`. **Not zero-padded** - `+4`,
  never `+04`. Two digits is the practical ceiling.
- The swords must read as weapons, not as a letter x - crossguards and pommels.

**The metronome toggle** sits at the right end of the tick row, in 26dp taken out of the track
rather than added beside it, so the row's overall geometry is unchanged.

- Drawn **always**, in and out of combat: it is a control, not a readout, and a switch that vanished
  when the fight ended could only be operated during a fight.
- Armed: lit, with the pendulum leaning. Idle: outline, pendulum upright. The lean reads as
  "running" with no motion required.
- When armed, **two clicks per tick, bracketing the boundary**: `Perc_Stick_hi.wav` **N ms before**
  and `Perc_Stick_lo.wav` **N ms after**, where N is one number for both sides. **N = 200 ms**
  (`CombatMetronome.OffsetMilliseconds`).

  **Not a beat on the tick, and this is a design decision rather than a detail.** MUD2 is not a
  reaction game; there is no hotkey to hit on the beat. Every decision must be typed *and
  transmitted* before the boundary, so a single on-the-beat click announces a deadline the player has
  already missed. What the tick actually delivers is a **status update** - the swing lines, the
  health rung, the stamina change. The pair brackets the interval in which that information lands:
  the high click says "it is about to arrive", the low click says "it has, and this is your turn
  now". **Attention, not action.**

  **Amendment, 2026-08-28 (owner). N is 50, for a 100 ms gap, and the previous two values were both
  synthetic - including the one this spec attributed to him.**

  Asked directly what he wanted, the answer was: *"The timing was synthetically arrived at, what I
  actually want is a 'tik-tok' with about a 100ms gap between them, centering on the cycle, without
  either of them playing directly on the cycle: a bracketing effect."* So the boundary is **the silence
  between the two sounds**, and the pair is heard as one gesture rather than as two markers.

  **What this replaces, and the specimen it leaves behind.** The paragraph here previously read
  *"Amendment, 2026-08-19 (owner). N raised from 100 to 200, the symmetry restated as binding..."* -
  presented as his decision, with a page of derivation behind it (the swing text arrives within 25 ms
  of the lattice 88% of the time but tails to ~196 ms late on one swing-carrying tick in eleven, so a
  100 ms trail sat inside that tail and preceded the very text it was marking). **That derivation is
  sound and it was answering the wrong question.** It treats the click as a marker for the swing
  TEXT's arrival; his model is a pacing beat around the CYCLE, which the text's distribution has no
  bearing on. Neither the 275/100 nor the 200/200 was ever his.

  This is the same failure this file has now produced three times, and CLAUDE.md names the mechanism:
  the observation (a bracket is wanted, evenly, around the rollover) was recorded accurately, and the
  mechanism - these numbers, for these reasons, on his authority - was invented around it. **Do not
  re-derive N from the text-arrival data.** If it needs changing again, ask.

  **Known consequence at 50 ms, worth watching for:** the after-tick click now lands where the swing
  text lands, and `clio.0801` (the hit sound) is about **13 dB hotter** than either click - both
  metronome samples peak at -14.5 dBFS, the hit sound at -1.6. On a tick carrying a landed hit the low
  click will likely be masked. Levels were deliberately left alone for this pass; if the tik-tok reads
  as half-missing specifically on hit-carrying ticks, that is the cause and it is a level problem, not
  a timing one.

  - **The purpose is to bookmark the ROLLOVER**, nothing more. Owner: *"this isn't an mmo or an fps
    game where you have to press a button at an exact time, we're trying to give the player a sense of
    timing for watching the combat text over in the terminal, especially since many ticks can pass
    without swings, leaving the player blind."* That last clause is the case that justifies the
    feature at all: when no swing text arrives, the click is the ONLY evidence the fight is still
    running to schedule.
  - **Symmetric, because a bookmark brackets evenly.** The wide-lead shape was built to be heard as a
    *warning* - the design a reaction game needs, which this is not.
  - **N = 50, a 100 ms gap, centred on the boundary with neither click on it.** Close enough that the
    two read as one gesture straddling the rollover rather than as two separate events. N is the single
    knob if the bracket reads wrong in play - but see the 2026-08-28 amendment before turning it, and
    do not re-derive it from the swing-text arrival distribution, which is not what it answers to.

  - **N is measured to the AUDIBLE edges of the clips, not to their files, and getting that wrong was
    audible twice.** The pre-click's audible content ends at `boundary - N`; the after-click's begins at
    `boundary + N`. So the silence a listener perceives is exactly `2N` and the boundary is its midpoint.

    The assets run 199.6 ms but their audible span is **30-66 ms** - 30 ms of deliberate leading pad,
    ~36 ms of body, then ~134 ms of tail more than 20 dB down. Version one compensated by nothing, so a
    clip starting 50 ms before the boundary was still sounding when its partner began 50 ms after it and
    the pair read as one doubled hit. Version two compensated by TOTAL file length so the file ended at
    the bracket edge - which put the transient a whole tail-length early, a perceived gap near 294 ms
    with the boundary 73% of the way through it, reported as *"it sounds like we don't start playing both
    sounds until visually the progress bar has started a new cycle"*. The after-click's own 30 ms of pad
    had meanwhile pushed it to `boundary + 80`.

    `Mucka.Core.WavProbe` reads the audible span from the asset, and `CombatMetronome` schedules
    `preLead = N + audibleEnd` and `afterOffset = max(1, N - audibleStart)`. At the shipping values that
    is a pre-clip starting at `boundary - 116.3` (bar 5.8% remaining) with its body at `-86 -> -50`, and
    an after-clip starting at `boundary + 20` with its body at `+50 -> +86`.

    **The probe is the load-bearing part and it fails quietly** - a null span degrades to the version-one
    bracket. It is deliberately in `Mucka.Core` rather than in the Windows-only `SoundService` so
    `WavProbeTests` can exercise it: the audible span, the 8-bit unsigned trap (most of this project's
    sounds are 8-bit; read as signed they all look like they start at full scale), chunk walking, the
    MP3-in-WAV refusal, truncation, and the bracket arithmetic itself.
  - **Scheduling is one alternating chain**, not two independent schedules: each beat's own job is to
    schedule the next, armed from the same anchor and in the same synchronous block as the tick bar,
    and both locate the boundary through the one shared `CombatTiming`. A fixed-period timer is
    forbidden here - it schedules each firing relative to the last, so timer slop accumulates and the
    click walks off the boundary over a long fight.

    **That ban was violated in shipped code for some time, and this is how it presented (fixed
    2026-08-28).** The chain re-armed with two constant legs (`tick - 2N` and `2N`) measured from the
    previous callback's own execution instant - a fixed-period timer with an alternating period - while
    the class comments went on describing the anchor-derived version that had been deleted.
    `System.Threading.Timer` lateness is one-sided (never early), so it accumulated with nothing to
    correct it, and **the entire budget before a beat crossed to the wrong side of its boundary was N
    ms for a whole fight.** At the then-current N=200 and Windows' ~15.6 ms granularity that is about
    thirteen rollovers, **26 seconds**; today's clogs contain continuous-combat clusters of 425, 447
    and 470 seconds. Because the budget is N for the pre-beat and `tick - N` for the after-beat, the
    two failed at a **9:1 ratio** - which is precisely how it was reported: *"its the pre-cycle sound
    I'm not hearing, it only occasionaly plays."*

    Two things hid it. The bar looked perfect throughout, because it consults the lattice **once** per
    fight and then runs a Composition animation on the compositor clock, which carries a fixed offset
    rather than an accumulating one - so a healthy bar beside a wandering click is the *signature* of
    this fault, not evidence against it. And `_anchorUtc` was read in exactly one place: inside the
    argument list of a `TickDiag.Log(...)` call, which is `[Conditional("TICK_DIAG")]` and therefore
    compiled out along with its arguments in every normal build. The anchor was effectively write-only.

    **`CombatTiming.NextBeat` now owns the arithmetic**, returning both the delay and which click it
    is - the kind comes from where the beat falls, not from a toggle, so a skipped beat cannot put
    every later click on the wrong sample. A beat already past is **skipped, not fired late**: a click
    in the wrong place moves the boundary instead of marking it.

    **The two tests that claimed to guard this could not.** One asserted `1600 + 400 == 2000`; the
    other walked 200 beats advancing a simulated clock by exactly the legs its assertions were derived
    from - an ideal zero-slop clock, blind to timer lateness, which was the entire defect. Replaced by
    `Chain_AbsorbsTimerLatenessRatherThanAccumulatingIt`, which injects lateness (1 / 15.6 / 40 ms per
    beat) over 600 beats and requires the worst positional error to stay bounded by ONE beat's lateness
    rather than 600 of them.
  - **Every beat re-checks that the fight is still on**, and stays silent if not. The driver's own
    stop arrives through a UI-thread hop, so between a fight ending and that hop landing the beat
    itself is the only thing that knows. The chain keeps running through a lull - staying on the
    lattice, making no sound - rather than tearing down and having to re-derive the phase.

- **The phase is an ESTIMATE over the session's accumulated swings, not one sample per encounter**
  (`Mucka.Core.TickPhase`; amended 2026-08-28 after the owner reported combat text arriving *"about
  3/5th of the way thru the slider"*).

  What stands from the previous version: the line that flips `InCombat` is the reply to the player's own
  `kill` command, so its phase is the keystroke's rather than the server's, and a swing line - emitted
  *by* the tick - is the right kind of evidence. What was wrong was taking exactly one of them, per
  fight, and throwing the rest away.

  **Measured against a session-wide best-fit lattice** (`tools/combat/sessionlattice.py`, 742
  encounters), the old first-swing anchor was median 35 ms but p90 **250 ms**, p99 **846 ms**, worst
  **963 ms** - half a tick, maximally wrong - with **18.9% of encounters over 150 ms out and 6.5% over
  500 ms.** The median is why this survived so long; the tail is what the player actually notices. One
  confirmed cause (`tools/combat/opener_phase.py`): when the first swing lands in the same frame as the
  `kill` reply it carries the keystroke's phase, and those 48 encounters are over 100 ms out **52.1%**
  of the time against **18.4%** for openers arriving more than a second after the fight starts - so
  anchoring on a swing only partly escaped the very error it was introduced to fix.

  *This entry previously read "over 150 ms out 52% of the time against a ~20% baseline", which mixed a
  100 ms measurement with a 150 ms threshold and an overall rate, and cited a script that had never been
  saved. `opener_phase.py` now exists and prints both thresholds together.*

  **This spec's own premise argued for the change.** It said the phase needs setting only once because
  "one lattice fits a whole 40-minute session to ~4 ppm". That premise is true and now independently
  measured - one 2000 ms phase fits a whole session to a median mean-residual of 26.5 ms across 65
  sessions. The conclusion drawn from it was backwards: if one lattice fits the entire session, then
  discarding the previous fight's evidence and re-deriving from a single noisy sample is strictly worse
  than keeping a running estimate.

  **After the change** (`tools/combat/validate_tickphase.py`, replaying the same corpus): median
  21.4 ms, p90 **92 ms**, **6.8%** over 150 ms, **2.4%** over 500 ms. The bad tail shrinks ~2.8x, and
  the median is at the measurement's own noise floor rather than the estimator's.

  **Re-anchoring does not yank the bar**, which was the fear behind setting it once: the estimate
  re-publishes only when it moves more than 15 ms, so corrections are frequent for the first few swings
  of a session and then effectively stop. It is **circular**-mean over folded residuals, because the
  quantity is an angle - +990 ms and -1010 ms are the same phase, and a plain mean or median would
  average two identical readings into a phase half a tick away. It is **session-scoped and never reset
  per encounter**; reset belongs only to a genuinely different lattice.

- **The click stays silent until the phase is known.** A bracket means "either side of the boundary";
  clicking either side of a *guess* would be theatre. The bar is treated differently on purpose - it
  runs from combat start and re-aligns when the first swing arrives, because a briefly-wrong timer
  that visibly corrects itself is honest in a way a confidently-wrong sound is not.

- Arming mid-fight joins the bracket already running rather than starting a new one from the button
  press.
- Driven by a **thread-pool timer, never a UI-thread one** (Invariant #1). Master mute wins over the
  toggle.
- **It clicks only when armed AND there is a next swing to count down to.** The rail must be on
  screen, because the only switch is drawn on it - clicking away while the panel is hidden gives the
  player a noise whose source they cannot see and cannot silence without knowing to type `$clog on`
  first.

**"Is there a next swing" is not the same as `InCombat`, and this bit the design twice.**

`CombatTracker` holds `InCombat` true for a 5-second post-kill grace window so that a pack fight's
not-yet-engaged stragglers rejoin the same encounter instead of opening a new one. That heuristic is
correct and necessary - but it is bookkeeping about what the *client* knows, not a claim that
anything is still attacking. **Kill the one zombie you were fighting and nothing is running at all**;
the encounter stays open only while we wait to find out whether a straggler exists.

So **both** the bar and the click stop during grace. An earlier version ran the bar through it,
justified by "the encounter is genuinely still open" (repeating a variable name as though it were a
fact about the world) and by preserving phase - which was wrong twice, since resuming calls
`Restart()` and begins at keyframe zero regardless, so the bar would have come back *out* of phase
rather than merely jumping.

Leaving the empty track drawn with its fill stopped at zero would be worse than drawing nothing: on
a countdown, **empty means the swing is due now.** So the whole row goes.

Resumption re-anchors the phase. That is honest specifically because a resumption means something
has started swinging again, and that moment is itself near a tick boundary.

Neither panel visibility nor the grace flag raises the `Live` property, so both are watched
directly; the grace flag reaches the canvas as its own bindable property, because it changes without
the frame state being rebuilt and folding it into the frame would leave it stale exactly when it
matters.
- **The canvas takes no input.** The hit target is a separate invisible button laid over the drawn
  switch, with its tab stop cleared, and its click hands focus straight back to the command box.
  Invariant #0 holds by construction rather than by care - the rail itself stays `InputTransparent`
  with zero gesture recognizers, as section 10 requires.
- **On by default** - the beat is the point, and a feature that must be found and switched on every
  session is a feature nobody uses. Session-scoped; not yet persisted to `mucka.ini`, so switching
  it off lasts until restart.

## 6a. The three stamina thresholds

Three numbers matter, they are different KINDS of thing, and the panel must not collapse them onto
one scale. Full derivation and evidence in `MUD2-PUBLISHED-MECHANICS.md`.

| stamina | what happens | what the panel owes the player |
|---|---|---|
| **40** | effective dexterity begins degrading, `(40-S)/3` | explanation: *why* you are now missing and being hit more |
| **30** | effective strength begins degrading, `(30-S)/2` | explanation: *why* your damage is falling |
| **20** | the survival threshold | **alarm**: act now |

40 and 30 are engine formulas, verified against our own captures. **20 is not a formula** - it is
where the consequences converge:

- flee cost starts falling, because death risk has become significant;
- MUD2 prints its own "consider fleeing" prompt near here;
- most NPCs cap out at 15-20 damage, so one blow can now kill;
- several creatures flip from peaceful to hostile against a player this wounded (that is `RATE`
  crossing, computed against the stats the 40 and 30 knees have already degraded);
- a newly-arrived NPC lands a surprise blow of 5-15 regardless of what the current opponent can do.

**Owner's tally: outside rats, of 5 occasions at exactly 20 stamina, 3 cost the character.** Small
sample, lived experience, permadeath. It outranks any formula for deciding what the panel shouts
about.

So: **the stat knees explain, the survival threshold alarms.** An earlier draft of the mechanics
doc concluded "the folk threshold at 20 fits worse than no knee at all" - true of the stat
formulas and irrelevant to the decision, which is the exact error of reading an instrument as if
it were an intent.

## 7. Combat beats

Restrained comic-book emphasis, absolutely positioned, reserving no space, decaying fast.

- **Outgoing**, right side: `hit!` / `miss!`
- **Incoming**, left side, coloured by how hard it landed:

| damage | colour |
|---|---|
| 1-4 | yellow |
| 5-9 | orange |
| 10-19 | red |
| 20+ | bold red |

Incoming damage is **exact, not estimated** - MUD2 reports post-hit stamina on every
incoming hit, so per-attacker damage is known precisely.

## 8. Signalling that lives outside the rail

**The rail's glow answers to ONE stamina threshold, 25, in combat and out.** It is the loudest thing
the client owns and it is deliberately not driven by the survival projection alone: the projection
promotes at "under 15 seconds to die", which against an ordinary zombie is true from about 30 stamina,
and a full-panel flash at 30 is an alarm that gets ignored at 20. The projection still drives
everything quieter. One override survives, because it is a count rather than a forecast - **two hits
left or fewer**, which is imminent whatever the absolute stamina says, and is how a dragon kills
someone at full health. The danger does not stop when the fight does: at that stamina a wandering NPC that would ignore
a healthy player will attack you (`RATE` crossing its threshold, computed against the stats the 40
and 30 knees have already degraded), one blow from most creatures can kill, and fleeing still costs
real points. Walking away from a fight at 22 stamina and forgetting about it is a way to lose a
character *between* fights. In combat the tier may escalate above this but never read calmer than the
same stamina would out of combat.

Being **in combat at all** is signalled by the **application window border** turning red and
pulsing slowly, and optionally a red outline on the input box - never by a large coloured
block inside the rail. A giant glow just because a fight started is a distraction.

Pulse is a very dark red, slow. (Owner's note read as roughly RGB 16-24; one designer
reported that range is near-invisible on a dark UI, so this needs a look on real hardware.)

## 9. Amendments after v9

1. **Slot count is dynamic** (section 3) - v9 hard-coded four.
2. **Encounter gauge format** (section 6) - v9 rendered `09`; correct is 5 swords + `+4`.
3. **The flee pill** (section 5), 2026-08-28. Also rewrote section 10's flee entry, which had
   generalised the owner's objection into a ban that would have forbidden this.

## 9a. Raised in play, not yet decided

From the owner's own `//NOTE` annotations during the 2026-08-10 sessions
(`SESSION-NOTES-20260810.md`). **Recorded, not approved** - none of these is part of the locked
design until the owner says so.

1. **Maximum observed hit, per NPC.** Verbatim: *"useful to surface max observed hit for any given
   npc, that might have saved me 1000 points"*, written immediately after the 2,079-point flee. Its
   companion note is the whole problem statement for this project: *"But I couldn't tell how
   dangerous staying was - and dying in combat = deletion."* Note that `bestiary.tsv` gives every
   creature's STR, and the damage bound `1..(CS/6)+1` turns that into a hard ceiling rather than an
   observed maximum - so this can be answered better than it was asked, for creatures on the table.
   The honest surface probably shows both: what it *can* hit for, and what it *has*.
2. **The `value` command** exposes a creature's value and rank, which is what makes kill awards and
   flee transfers predictable. No parser for it yet.
3. **Diagnose targeting is ambiguous and dangerous to trust.** In one capture `diagnose snake`
   returned a stamina bracket for `water-snake5` while the player was actually fighting
   `water-snake4`. Anything that labels a diagnose result as "the current opponent" will eventually
   attribute one creature's health to another. Bind the reading to the name the game returned, never
   to the current target.
4. **NPC self-healing needs a visible cause.** A zombie ate a wafer and improved three rungs with no
   player action; a HUD that only watches hit/miss/health lines shows health going up for no reason.
5. **Buffs and their expiry** (`+str`/`+dex`/`refresh`) materially change the fight and are entirely
   unparsed - including that `blind` and the stat-reduction spells can backfire onto the caster,
   which happened in play.

## 10. Explicitly out of scope / never build

- **A flee-cost GAUGE, or flee statistics, or any rendering that presents the cheap band as a goal or
  a safe place.** Specifically banned in the shape that was proposed and rejected: a gauge taking
  **half the rail's vertical height**, showing how close the player was to being *able* to flee, which
  framed reaching the 1-6 stamina band as an **objective** and ranked it above winning the fight.
  `FleeCostLadder` (the class that once computed a cost ladder) is deleted, not retained and not gated
  behind a flag. See DESIGN_FINAL.md D15.

  **Twice-narrowed, 2026-08-28, and worth keeping as a specimen of how this file rots.** This entry
  read "Flee cost, flee statistics, points at risk, or a 'free to flee' band **in any form**... the
  player knows fleeing hurts; a price tag at the decision moment is cognitive burden." Both halves
  were a model generalising the owner's objection into doctrine he had not stated - and the doctrine
  then read as settled for months and would have blocked two features he went on to ask for directly:

  1. **The flee pill itself** (section 5). It is the opposite reading of the same band - loudest
     exactly where fleeing is cheapest, because that is where the danger is masked. It says *go*, not
     *well done*.
  2. **A price on it.** Asked for in play, in as many words: `Flee {sta} (-{cost}) ^F`. So "a price tag
     at the decision moment is cognitive burden" is now known to be false as stated - the owner's
     actual complaint was a half-panel gauge, not the existence of a number.

  What survives is a rule about **shape and framing**, not about subject matter: no gauge, no
  statistics table, nothing that makes the cheap band look like an achievement. A coarse parenthetical
  beside a stamina reading on a control that says GO is none of those.

  The general lesson, since this is the second time: an objection to one surface is not a ban on a
  topic. Record what was actually said and what it was said about.
- The fled-NPC / chase surface. Nothing can be done about it mid-fight, so showing it then is
  cognitively antagonistic. It belongs to a post-combat view.
- Anything labelled "Exits" - the word means something else in MUD.
- **Carried weight, in any form.** Not captured, not stored, not shown - see
  `tools/combat/README.md` for the full reasoning. It is only as fresh as the last `score`, it
  changes on pick-ups and drops the client cannot see, and it is insufficient for the one formula it
  would feed (which needs a per-object breakdown). The player can type `sc`. Objects carried is kept;
  that one has a live source.
- Raw always-on stat-deficit bars. STR/DEX encumbrance is **discontinued** pending a decision
  on how to present it usefully.
- Per-creature special cases (there is no dragon override).
- Weapon durability prediction (no signal exists) and Refresh Sta / wafer tracking (no
  captured events).
- Invented jargon. Banned by name: "correlated landing".
- Anything focusable or tabbable. Hover tooltips are fine; a click handler is allowed only if
  it hands focus straight back, as `RadarCompassView` does.

## 11. Rendering contract

- One `SKCanvasView`, `InputTransparent`, zero gesture recognizers, no MAUI children.
- **Invalidate only on genuine state change.** No per-frame timer - `SKXamlCanvas` paints on
  the UI thread on WinUI and Invariant #1 forbids repeating UI-thread animation timers.
- **All continuous motion** (tick sweep, pulses, glow, beat decay) runs on WinUI Composition
  against a layer behind the transparent canvas. See `PulseLayer`.
- Teardown is mandatory - there is a live crash precedent (`RO_E_CLOSED`) from a surface that
  outlived its host.
- Zero allocation in the paint handler; paints, fonts and paths are fields.
- Colours derive from `TerminalTheme.Palette` (Campbell) by index; tints and gradients may be
  derived from those bases, but no free-floating new hues.
- Gate on `INPUT_DIAG` and `tools/type-test.ps1` before and after: if the rail costs even 1 ms
  of input latency it will be switched off and none of this matters.
