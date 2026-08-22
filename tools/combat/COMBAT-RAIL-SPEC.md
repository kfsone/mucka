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

Measured context: the maximum simultaneously-engaged opponents across the whole capture
corpus is **4** (68 of 71 sessions were 1v1; kills clear slots as fast as new creatures
join, so even the 16-rat brawl never exceeded 4 at once). Overflow is therefore rare, but
must exist - a player can aggravate a whole room.

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

  **Amendment, 2026-08-19 (owner).** N raised from 100 to 200, the symmetry restated as binding, and
  the scheduling mechanism specified - the implementation had drifted to an asymmetric 275 ms lead
  with a 100 ms trail, which this spec never described.

  - **The purpose is to bookmark the ROLLOVER**, nothing more. Owner: *"this isn't an mmo or an fps
    game where you have to press a button at an exact time, we're trying to give the player a sense of
    timing for watching the combat text over in the terminal, especially since many ticks can pass
    without swings, leaving the player blind."* That last clause is the case that justifies the
    feature at all: when no swing text arrives, the click is the ONLY evidence the fight is still
    running to schedule.
  - **Symmetric, because a bookmark brackets evenly.** The wide-lead shape was built to be heard as a
    *warning* - the design a reaction game needs, which this is not.
  - **N = 200 puts the trailing click after the swing text.** Text lands within 25 ms of the lattice
    88% of the time but tails to ~196 ms late on about one swing-carrying tick in eleven
    (`archive/TICK-PHASE-REVIEW.md`); a 100 ms trail sat inside that tail and so preceded the very
    text it was marking. 400 ms between the two also still reads as a pair rather than as one
    doubled hit. N is the single knob if the bracket reads wrong in play.
  - **Scheduling is one alternating chain**, not two independent schedules: each beat's own job is to
    schedule the next (after-tick -> pre-tick -> after-tick), armed from the same anchor and in the
    same synchronous block as the tick bar, and both locate the boundary through the one shared
    `CombatTiming.MillisecondsToNextBoundary`. A fixed-period timer is forbidden here - it schedules
    each firing relative to the last, so timer slop accumulates and the click walks off the boundary
    over a long fight.
  - **Every beat re-checks that the fight is still on**, and stays silent if not. The driver's own
    stop arrives through a UI-thread hop, so between a fight ending and that hop landing the beat
    itself is the only thing that knows. The chain keeps running through a lull - staying on the
    lattice, making no sound - rather than tearing down and having to re-derive the phase.

- **The phase comes from the encounter's first SWING, not from the moment combat started.** The line
  that flips `InCombat` is the reply to the player's own `kill` command, so its phase is the
  keystroke's rather than the server's - measured across 16 encounters that put the indicator a
  median of **~1.0 s** from the real boundary, effectively at random, which is exactly why the lag
  felt intermittent. A swing line is emitted *by* the tick; anchoring there measures a median error
  of **~22 ms**. Set once per encounter and then left alone: one lattice fits a whole 40-minute
  session to ~4 ppm, so the phase does not need chasing, and re-anchoring every swing would yank the
  bar and the click around several times a fight.

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

- Flee cost, flee statistics, points at risk, or a "free to flee" band **in any form**.
  The player knows fleeing hurts; a price tag at the decision moment is cognitive burden.
  `FleeCostLadder` (the class that once computed this) has been deleted outright - not retained,
  not gated behind a flag. See DESIGN_FINAL.md D15. The two stamina thresholds it used (20, 6.5)
  survive only as `CombatTierResolver.WarningStaminaThreshold`/`CriticalStaminaThreshold`, which
  drive risk tiers, not a cost figure.
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
