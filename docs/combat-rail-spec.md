# Combat Rail - implementation spec

ASCII only in code. Every glyph is drawn as an `SKPath`, never a font character.

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

The numbers live in the code and are named here rather than copied: `CombatRailResize.ContentWidthDp`
(the rail's width, which narrows when the stat rows are switched off) and `SidePanelWidthDp` (the left
panel, untouched by anything here); `CombatRailView.SlotHeight`, `PlayerTileHeight` and
`TickRowHeight` for the bands below. A value restated here is a value free to drift from the one that
draws.

The ORDER is stated in `CombatRailView`'s bottom-up offset chain (`PlayerTileBottomOffset`,
`TickRowBottomOffset`, `EncounterBottomOffset`), which is the authority. Bottom-up, nearest gaze
first:

```
   (bottom edge)
   the player's own tile
   tick meter          (the flee pill rides its top; the metronome toggle its right end)
   encounter table     (a heading row reserved always, painted on hover, and a value row)
   opponent slots      (N slots, N computed from window height - see 3)
   overflow row        (only when opponents exceed slot capacity)
   (empty - top of rail)
```

- Opponent slots are identical in size, with no primary/secondary distinction.
- Empty space goes at the TOP, per rule 2. Nothing in the bottom bands moves when the count above
  them changes.
- The encounter table's headings are RESERVED whether painted or not: headings that appeared on
  hover would push the whole rail down every time the pointer crossed it.

## 3. Opponent slots - count is derived from window height

**Slot count is computed from available rail height**, filling from the bottom up.
`RailSlotGeometry.Capacity` owns that arithmetic and `Mucka.Util.Tests/RailSlotGeometryTests` pins it;
what matters here is the rule, not the divisor.

Recompute only on **panel resize**, never on a combat event. Within a session the count is
fixed, so rule 3 (nothing moves) holds during a fight.

Slots fill **from the bottom**, so opponents accumulate upward and the most relevant sit
nearest the gaze. Unoccupied slots render as empty reserved frames.

**Overflow.** When engaged opponents exceed slot capacity, the **topmost** slot becomes an
overflow row - farthest from the gaze, being the least actionable content. It shows
**names only**, sorted by **damage dealt to the player (highest first)**, i.e. by how much
each has actually hurt you, not by arrival or alphabet. No health, no pips, no prose.

**Concurrency observation.** Against a corpus of 984 encounters, peak simultaneous engaged
NPCs:

| peak simultaneous NPCs | encounters |
|---|---|
| 1 | 838 (85%) |
| 2 | 78 |
| 3 | 30 |
| 4 | 26 |
| 5 | 10 |
| 6 | 1 |
| **7** | **1** |

The maximum observed is **7**, verified against the raw event stream and not only the state
machine. Twelve encounters have peaked at 5 or more.

The slot cap is 8, so at 7 live opponents there is one creature of margin before live
participants alone fill it and `RosterPlan.HiddenLiveCount` goes nonzero in ordinary play.
Overflow is not a rare pathological case - it must exist, and it will be seen.

## 4. Opponent slot contents

A slot is four bands at fixed baselines (`CombatRailView.TileNameBaseline`, `TileHealthBaseline`,
`TileUpperBaseline`, `TileLowerBaseline`), with a tempo frame around the whole thing:

1. **name**, with the creature's own weapon right-aligned beside it;
2. the **health gauge**, with the game's health phrase overlaid and two prediction lanes under it;
3. two **stat rows**, and the **exchange spark** beside them.

**The health gauge** is a full-width horizontal bar notched into sevenths, with the game's own
health phrase overlaid (`CombatRailView.DrawVitalityBar`).

**Fill direction: health REMAINING.** The bar fills from the left with what is left: full at
full health, depleting as the creature is hurt, so `close to death` shows one seventh. This
matches the player's stamina gauge (both deplete) rather than inverting between two gauges on
the same panel. The notches are the rungs; the estimator's absolute figures may place the
boundary within a rung but never label it.

**The health phrase** (`critically injured`) is drawn **overlaid on the bar**, centred, in the
terminal's monospace with a shadow for legibility. The game's grammatical filler is stripped
(`to have minor injuries` -> `minor injuries`) and nothing else is reworded.

**The scale** - see `NpcHealthRungs`. Three vocabularies (living "injured", undead "damaged",
banshee "drained"), seven words each, and they line up rung for rung:

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
contradicting this order.**

No published source covers the wound descriptions; this ladder is the best available reading
of observed behaviour, not documented fact.

The ladder is **not a health percentage** and must never be drawn as one: seven words cannot
resolve a pool that runs from 1 (a firefly) to 800 (the dragon), and rung 2 on a 25-stamina rat
is not the same amount of trouble as rung 2 on a 100-stamina rat0. It is also **not a ratchet** -
creatures regenerate; a zombie in the corpus oscillates `strong` <-> `superficially damaged`
four times in one fight. Every observed improvement is exactly one rung, but they happen.
Always show the **latest** reading, never the worst seen; latching to the worst keeps promising
a kill that is no longer one swing away.

Staleness: the ladder only updates when you land a hit (player hit rate 0.57). A one-tick
gap is normal (68% of gaps); the reading fades to **stale at 3 ticks**
(`RosterRow.StaleAfterSeconds`, 6 s; the stamina reading fades on its own clock,
`RosterRow.StaminaReadStaleAfterSeconds`). Staleness changes tone only: the last reading
stays drawn, never replaced by a full or an empty ladder, because both of those are confident
claims.

The same sentence appears in **room descriptions**, so a health reading is accepted only for
a creature already engaged. A phantom opponent on the panel is worse than a missing one.

**The two prediction lanes** run in a thin band under the fill (`CombatRailView.DrawPredictionLane`):
where the bar would stand after the next blow, and after the one following it. **Nothing is drawn
when a band is absent** - an unsupported prediction has to look like no prediction, never like a
small one (rule 5). A `diagnose` reading is different in kind, being a number MUD2 printed to the
player in so many words, so it earns a bright tick on the fill itself rather than a lane: the one
hard measurement on this side of the panel.

**The creature's own weapon** is drawn right-aligned on its name line, in the hostile colour -
a fact about that participant, so it lives on that participant's row.

**The two stat rows** (`CombatRailView.DrawStatRow`), one per direction of the exchange. They are
named by POSITION, not by content: **the upper row is always what the SUBJECT of the tile is
taking**, so on an opponent's tile the upper row is what you have dealt it and on the player's own
tile the upper row is what the creatures have dealt you. A name like "dealt" would be true on one
tile and a lie on the other.

Four fixed columns, so the two rows of a tile lock to each other and to every other tile: a
direction mark, the running total, the blow shape (low / high / avg), and damage per tick. The
columns are positions, not a measured flow, so a wide figure cannot shift the group. **A row with
no samples prints its own column names** rather than dashes - the tile teaches its layout while it
has nothing to report and goes quiet the moment a figure lands. Either way rule 5 holds: a word can
no more be read as a measurement than a dash can. `ExchangeLine` owns what those figures mean;
"low/high/avg" rather than "min/max/avg" because on the outgoing side the outer two are the upper
bounds of the smallest and largest blows, so three identical `(5-9)` blows read 9 / 9 / 7 and a
label promising a minimum below the average would be contradicted by the commonest case there is.
Damage per tick of 0 means "under one tick elapsed", not "no damage", and draws as the unknown it is.

**The exchange spark** sits to the right of the stat rows: one mark per recent swing, its height
linear in that blow's damage between `RailReadout.SparkMinBar` and `RailReadout.SparkMaxBar`,
saturating at `RailReadout.SparkDamageCap`. A swing that happened but whose size is unknown draws at
the floor, so "happened" and "hurt" stay separate readings (rule 5).

**The tempo frame** (`CombatRailView.DrawTempoFrame`) is the slot's own border, and its DASH
DENSITY is how often blows are landing. The rectangle never changes, only the dash, so nothing
reflows when a fight's rate does.

**The stat rows and the spark are what the width is spent on**, and they are what the narrow
setting drops: `CombatRailResize.CombatPanelNarrowContentWidthDp` against
`CombatPanelContentWidthDp`, chosen by `CombatRailResize.ContentWidthDp`. The operator set it - the
rail was too wide for a Surface. Nothing else on the panel changes between the two widths, and the
flee pill's drawn width is the floor on how narrow either can go.

Current target: marked by emphasis **within its own slot** (border, brightness) - never by
size.

**The unknown badge.** MUD2 replaces a Creature's name with `someone` (person-shaped) or
`something` (the rest) whenever the player cannot see it - blind, in the dark, or the Creature
invisible. Blows the client cannot attribute (`CombatTracker.ResolveAnonymous`: attribute only when
exactly one candidate of the word's class is engaged, and only while the player is known unable to
see or the Creature was seen to fade) land on the word's own row, and that row is drawn as the
**unknown badge**: one per word, at the **top of the live stack**, titled by the word.

- **Size is the count.** The badge is `RosterRow.SlotSpan` slots tall - one per Unseen opponent it
  stands for. Every attacker announces itself under C08.00 ("Someone is about to attack you."), so
  the count is exact: `CombatTracker.Unseen` counts those starts and an anonymous kill or flee takes
  one away. Swings never open one.
- **Who it stands for** is its list (`RosterRow.UnseenLabel`): the named Creatures that folded into
  it, then one `???` per announced opponent nobody has named. The first entry sits on the title row,
  right-aligned where a Creature's weapon would go; each further entry has the slot below, on its own
  name line (`CombatRailView.DrawUnseenList`). A Creature folds in while the player cannot see and
  there is more than one candidate for the word - blind with a rat engaged and two `Something is
  about to attack you.` reads `rat3, ???, ???`, three slots tall. Sighted, a named Creature is
  self-evidently not the unknown and keeps its own row. A Creature of unknown kind is a candidate for
  either word; with both open it folds into the `SomeKinds.Default` word's badge.
- **Derived every refresh from what is open now** (`ParticipantRoster.Build`), never accumulated.
  When sight returns, a Creature joining by name is the opponent that announced itself blind
  (`CombatEventKind.UnseenNamed` retires the word's row once its last one is named); folded names
  un-fold on their own; an opponent that announced itself while the player could see is invisible
  itself and stays a `???` until it is killed or the encounter ends.
- **Frame**: a still dashed edge in the caution colour on every platform (`CombatRailView.DrawUnseenFrame`).
  On Windows a marquee rides just outside it: yellow and orange dots alternating along the edge,
  stepping `UnseenMarquee.StepDp` every `UnseenMarquee.StepMilliseconds`. Never drawn by the canvas
  (rule 3, Invariant #1): it is a Composition `ShapeVisual` on a sibling element, one per word,
  positioned by the visual's own offset over `CombatRailView.RowRectDp` so a badge growing by a slot
  costs no layout (`GamePage.SyncUnseenMarquees`). Android has the still frame only.
- **Never the row the cap drops.** Named live rows take the room the badges leave
  (`ParticipantRoster.MaxRows` less the badges), so an over-tall pack hides its eighth named Creature
  before it hides the unknowns. On a panel too short for the badge's full height it is drawn as tall
  as fits and its list says `+N` (`RailSlotGeometry.RowPlacement`).
- It is the exception to "one slot per opponent" and to rule 3: the badge's height changes as
  opponents announce themselves and die, because that change IS the information.

## 5. The player's own tile

Built like an opponent's and read the same way (`CombatRailView.DrawPlayerTile`): the persona's
name, the weapon lines, then the player's stamina as a full-width bar with the magic strip
immediately beneath it, then the same two stat rows and the same exchange spark - **mirrored**,
because the upper row is always what the tile's subject is taking. The tempo frame around it is the
incoming half of the same border language every opponent tile carries, its dash density pooled
across everything still swinging.

- The name is **blank until the login handshake names the persona** - never a stand-in word. Bold
  on it means the player took a blow on the tick being drawn, and nothing else.
- The player's ladder is the same seven rungs and the same two prediction lanes as an opponent's,
  pointed the other way. These are the TIGHTEST bands on the panel: the denominator is a maximum the
  game printed, where every opponent band divides by an inferred pool.
- **No status dot** anywhere on it - the bar carries its own state.
- Stamina colour follows the `colorcode()` ladder, identical to the top status strip:
  `>=100` bright green, `>=76` green, `>=36` bright yellow, `>=16` yellow, `>=6` red,
  else bright red. The rail and the strip must never disagree about the same number.
- **Magic** is purple shading blue, turning **red below 20**, notched at the quarters. It answers
  "roughly how much is left" and nothing more - a caster who needs the figure has it in the status
  strip at the top of the window. When the maximum is 0 or unknown the strip is **greyed and inert
  but still present**.
- **Out of combat the readouts go grey** (hue kept). The numbers stay true - they ride the FES
  heartbeat and the top strip still shows them in full colour.

**Weapon. In-combat only.**
- Sword icon + weapon name, or open-hand icon + `UNARMED`. **Never the word "armed"** - the
  weapon name already says it.
- **Nothing is drawn here between fights.** MUD2 has no equipment slots and no default weapon:
  a weapon is chosen while fighting, or as part of starting a fight (`kill x with y`), and
  `wield` is per-engagement. Out of combat the client does not know what is in the player's
  hands, so the space stays reserved and empty.

**Alternate weapon**, below the weapon: hotkey chip left, name right-aligned. **`Ctrl+W`**
(consistent with `Ctrl+F` flee, `Ctrl+E` chase, `Ctrl+G` follow). Registered as a keyboard
accelerator so the event is marked handled and never reaches a default close-window action.
- Sends `wield <noun>`, where the noun is the item name's final token.
- **Drawn only when there is a candidate.** Its position is fixed, so lighting up mid-fight
  displaces nothing.
- **Candidates are carried items already on file as having been fought with**, ranked by
  highest median damage per landed blow *against this creature group*; weapons with no
  record rank after every weapon that has one, but are still offered.
- Recomputed every refresh, never latched.

**The flee pill.** `FLEE` and `Ctrl+F`, centred at the **top of the tick row**, above the player's
own tile. Reserved whether drawn or not; nothing in the row moves when it lights up. Its drawn width
is the floor on how narrow the panel can be - see `CombatRailView.PillWidth`.

**Content:** `^F:  FLEE  sta:{sta}  -{cost}` - e.g. `^F:  FLEE  sta:23  -573`. The key leads
because it is the only thing that can be DONE; stamina because that is the number the decision
is actually about; the price last, **omitted when any input is missing** and `free` when MUD2
charges nothing.

**Treatment:** a **filled** chip - dark red fill, a bright red border, white bold text, a corner
radius that leaves it not fully rounded - centred on the panel. `CombatRailView` owns the values
(`PillWidth`, `PillRadius`, `PillStroke` and the two colours beside them).

Those two colours are the panel's only ones not derived from `TerminalTheme.Palette` (section 9),
because Campbell has no pure red. That exception is the part worth writing down; the hex is not.

- **Four states**, resolved by `FleePillResolver` (pure, in mudsharp, unit-tested):

  | state | when | drawn as |
  |---|---|---|
  | `Hidden` | none of the below | nothing; the slot stays reserved |
  | `Visible` | stamina <= **26.5**, or one average bad tick would reach the survival threshold | the chip, knocked back to ~55% |
  | `Caution` | stamina <= **20**, or one tick's combined damage >= stamina, or **two hits left** | full strength, **border pulses** |
  | `EscapeNow` | stamina <= **6.5** | + a faint background breath behind the text |

- **One shape across all three visible states; the escalation is brightness and motion.**

- **The price is MUD2's own arithmetic, not an estimate** (`MudSharp.Combat.FleeWorth`); see
  `docs/MUD2-flee-cost.md` for the formula and its source. **Null is not zero.**
  `FleeWorth.Cost` returns null when score, stamina or maximum is missing and the pill prints
  no price; it returns 0 below 6% and the pill prints `free` - a plain word, never `-0`, never
  a highlight.

- **Everything happens on the tick, so the damage figure is a lump, not a rate.** MUD2 resolves
  every combatant's swing on one 2.000 s boundary, so the figure tested against is the **sum of
  what each live opponent hits for when it hits** - not a hit-rate-discounted damage-per-tick.
  "Any one of them averages more than my whole stamina" is a strict subset of that sum, so there
  is one rule rather than two that could only ever agree.

- **Two hits left is a count rather than a band** - two hits from death at full health is how a
  dragon kills someone, and it is the same override that promotes the whole-panel glow
  (section 8).

- **An unmeasured creature counts as 20** (`FleePillResolver.AssumedUnknownHit`) for the combined-
  damage check, deliberately pessimistic. A creature with samples is described by its own
  samples however mild they are; a rat measured at 4 a blow contributes 4. **A measured zero is a
  measurement** - `Samples > 0, Sum == 0` contributes 0, not the assumption. This value shares a
  number with the survival threshold and is not the same quantity; if either is tuned it moves
  alone. It multiplies across creatures without samples, so a pack of unmet species alarms early
  until each is met once. Live opponents past the roster's row cap are extrapolated at the mean
  of the ones with rows.

- **It is a button.** Clicking it sends the same bare `flee` `Ctrl+F` sends, down the same path,
  and hands focus straight back to the command box. The hit target exists only while the pill is
  drawn - it is not present at `Hidden`, and its width is stated explicitly rather than filled.

- **In-combat only, and grace counts as out.** Same gate as the tick meter: nothing is attacking
  during the post-kill grace window, and an instrument saying RUN then is asking the player to
  pay a real price to escape a finished fight.

- **The pulsing element is drawn over the canvas**, unlike the panel glow and the tick fill,
  which sit behind it: the chip's fill is opaque, so a pulsing sibling behind it would be painted
  over. It sits above the canvas but below `CombatMetronomeHit`, and is `InputTransparent` with
  no gestures of its own; its platform view also has `IsHitTestVisible = false`, because
  `InputTransparent` alone does not keep a WinUI element out of the pointer path. **The canvas draws the border only in the quiet state** - at the alarm
  states the ring itself is what moves.

**Unarmed timing.** `wield` is **per-engagement, not sticky** - every encounter starts with
nothing in hand until stated. An unarmed opening is *normal*: stay calm for the first ticks, go
amber only **after damage has landed**, and never straight to red.

## 6. Tick meter and metronome

- Ember's tick, directly above the player's own tile, **pale and dim** - grey/white, low opacity. It is a
  timer, not a judgement, so **no colour coding and no label**. A small drawn metronome mark
  is permitted; text is not.
- **It moves, and it drains.** The bar starts **full** at the top of a tick and shrinks
  **leftwards** - its left end pinned, its right end travelling left - reaching empty as the next
  swing lands. A countdown, matching the health bar (lit = remaining) rather than inverting
  between two gauges on one panel.
- **Strictly linear.** Every keyframe takes an explicit linear easing, so the bar never crawls
  at the ends and races through the middle.
- MUD2's tick is exactly 2.000 s and phase-locked, so a sweep started at the fight's first swing
  stays in phase for the rest of the fight without resyncing. The canvas draws the **empty track
  only**; the fill is a Composition-animated sibling behind it (`TickSweep`).
- **In-combat only.** The whole row - track and fill - is absent between fights.
- Two exceptions only: **red at stamina <= 30**, **glow at stamina <= 20** - see "The three
  stamina thresholds" below.
- Nothing else is drawn over the tick. The opponent count is stated by the slots themselves and by
  the overflow row; a third copy centred on a width that changes with every death slides under the
  eye, which is why there is not one.

**The metronome toggle** sits at the right end of the tick row, in width taken OUT of the track
(`CombatRailView.MetronomeReserve`) rather than added beside it, so the row's overall geometry is
unchanged whether the toggle is lit or not.

- Drawn **always**, in and out of combat: it is a control, not a readout.
- Armed: lit, with the pendulum leaning. Idle: outline, pendulum upright.
- When armed, **two clicks per tick, bracketing the boundary**: a high click **N ms before** and
  a low click **N ms after**, where N is one number for both sides. **N = 50 ms**, for a 100 ms
  gap centred on the boundary with neither click on it - close enough that the two read as one
  gesture straddling the rollover.
- **The purpose is to bookmark the ROLLOVER, nothing more.** MUD2 is not a reaction game and
  there is no hotkey to hit on the beat; every decision must be typed *and transmitted* before
  the boundary, so a single on-the-beat click would announce a deadline already missed. What the
  tick delivers is a **status update** - swing lines, health rung, stamina change - and the pair
  brackets the interval in which that information lands: the high click says "it is about to
  arrive", the low click says "it has, and this is your turn now."
- **N is measured to the AUDIBLE edges of the clips, not to their files.** The pre-click's
  audible content ends at `boundary - N`; the after-click's begins at `boundary + N`, so the
  silence a listener perceives is exactly `2N` and the boundary is its midpoint. `WavProbe`
  reads the audible span from the asset and schedules `preLead = N + audibleEnd` and
  `afterOffset = max(1, N - audibleStart)`.
- **Scheduling is one alternating chain**, not two independent schedules: each beat's own job is
  to schedule the next, armed from the same anchor and in the same synchronous block as the tick
  bar, both locating the boundary through the one shared `CombatTiming`. A fixed-period timer is
  forbidden here - it schedules each firing relative to the last, so timer slop accumulates and
  the click walks off the boundary over a long fight. `CombatTiming.NextBeat` owns the
  arithmetic, returning both the delay and which click it is - the kind comes from where the beat
  falls, not from a toggle. A beat already past is **skipped, not fired late**.
- **Every beat re-checks that the fight is still on**, and stays silent if not. The chain keeps
  running through a lull - staying on the lattice, making no sound - rather than tearing down and
  having to re-derive the phase.
- **The phase is an ESTIMATE over the session's accumulated swings, not one sample per
  encounter** (`Mucka.Combat.TickPhase`). The line that flips `InCombat` is the reply to the
  player's own `kill` command, so its phase is the keystroke's rather than the server's; a swing
  line - emitted *by* the tick - is the right kind of evidence, and every one of them (not just
  the first) feeds the estimate. It is **circular**-mean over folded residuals, because the
  quantity is an angle. It is **session-scoped and never reset per encounter**; reset belongs
  only to a genuinely different lattice. **Re-anchoring does not yank the bar** - the estimate
  re-publishes only when it moves more than 15 ms, so corrections are frequent for the first few
  swings of a session and then effectively stop.
- **The click stays silent until the phase is known.** A bracket means "either side of the
  boundary"; clicking either side of a *guess* would be theatre. The bar is treated differently
  on purpose - it runs from combat start and re-aligns when the first swing arrives, because a
  briefly-wrong timer that visibly corrects itself is honest in a way a confidently-wrong sound
  is not.
- Arming mid-fight joins the bracket already running rather than starting a new one from the
  button press.
- Driven by a **thread-pool timer, never a UI-thread one** (Invariant #1). Master mute wins over
  the toggle.
- **It clicks only when armed AND there is a next swing to count down to.** The rail must be on
  screen, because the only switch is drawn on it.

**"Is there a next swing" is not the same as `InCombat`.** `CombatTracker` holds `InCombat` true
for a 5-second post-kill grace window so that a pack fight's not-yet-engaged stragglers rejoin
the same encounter instead of opening a new one. That is bookkeeping about what the *client*
knows, not a claim that anything is still attacking: kill the one zombie you were fighting and
nothing is running at all; the encounter stays open only while we wait to find out whether a
straggler exists. So **both** the bar and the click stop during grace. Leaving the empty track
drawn with its fill stopped at zero would be worse than drawing nothing: on a countdown, **empty
means the swing is due now.** So the whole row goes.

Resumption re-anchors the phase - a resumption means something has started swinging again, and
that moment is itself near a tick boundary. Neither panel visibility nor the grace flag raises
the `Live` property, so both are watched directly; the grace flag reaches the canvas as its own
bindable property.

- **The canvas takes no input.** The hit target is a separate invisible button laid over the
  drawn switch, with its tab stop cleared, and its click hands focus straight back to the command
  box. The rail itself stays `InputTransparent` with zero gesture recognizers, and every element
  laid over the canvas that must not take pointer input sets `IsHitTestVisible = false` on its
  platform view as well.
- **On by default** - the beat is the point. Session-scoped: `mucka.ini` does not hold it, so
  switching it off lasts until restart.

## 6a. The three stamina thresholds

Three numbers matter, they are different KINDS of thing, and the panel must not collapse them onto
one scale. Full derivation and evidence in `MUD2-published-mechanics.md`.

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

Outside rats, of 5 occasions at exactly 20 stamina, 3 cost the character. Small sample, lived
experience, permadeath - it outranks any formula for deciding what the panel shouts about.

So: **the stat knees explain, the survival threshold alarms.**

## 7. Damage floats

Restrained comic-book emphasis, absolutely positioned over the canvas (`Rendering/RailFloatLayer.cs`),
reserving no space, decaying fast. A float echoes the one line the game just printed and nothing
else.

**What raises one.** A blow the player landed, a blow the player took, and stamina observed going
UP. **A miss raises no float** - `RailFloatKind` names the miss kinds and nothing raises them, so a
quiet pane means nothing landed rather than nothing happened.

**Text** (`RailFloatText`). Outgoing is the game's own BRACKET, unsigned, exactly as printed
("5-9"), or the single figure MUD2 sometimes prints instead, because that side is a bucket and a
minus sign would launder a bracket into a value. Incoming is a signed figure, because MUD2 prints
absolute stamina on the hit line and the delta against the previous reading is a measurement. A
signed number on a float is a measurement; an unsigned range is a range.

**One column, whatever the direction** (`RailFloatPlacement`). Every float - outgoing, incoming,
gain, player pane or opponent pane - starts at the same x beside the row it belongs to. Splitting
the directions to opposite edges was tried in play and rejected: the rail is scanned down its badges,
and numbers alternating between the margins cut across that scan instead of riding it. Direction
stays legible from the colour and from which pane it rose off, neither of which costs a second sweep
of the eye. **Do not re-split them.** Further blows of the SAME tick zig-zag off the first,
alternating sideways and stepping up by less than a box height so they deliberately overlap
(`RailFloatPlacement.CombatFloatZigDp`, `RailFloatPlacement.CombatFloatClusterStepDp`); several
numbers piling over one pane is the sense of being hit by a pack. Reading each figure is the
encounter table's job, not this one's.

**Colour is the KIND, size is the MAGNITUDE.** Outgoing, incoming and gain each have one colour,
fixed. What varies with how hard the blow landed is the text: `RailFloatEmphasis.StepsFor` adds a
point at each of four thresholds and `RailFloatEmphasis.IsHeavy` adds weight at the top one. The
box is sized for the biggest the ladder can produce and never grows with the text, so a big number
cannot clip. For an outgoing hit the magnitude is the LOW bound of the printed bracket - the floor
the game committed to - so emphasis can never claim a blow was bigger than the wire said.

Incoming damage is **exact, not estimated** - MUD2 reports post-hit stamina on every
incoming hit, so per-attacker damage is known precisely. A stamina gain is the opposite: MUD2 has no
"+3 health" line, so a gain float is the sum of everything since the previous reading - regen, food,
a spell - reported at the moment of observation, and it says so by being a different colour rather
than by claiming a cause.

`RailFloatBudget` bounds how many are in flight at once and sheds by kind.

## 8. Signalling that lives outside the rail

**The rail's glow answers to ONE stamina threshold, 25, in combat and out.** It is the loudest
thing the client owns and it is not driven by the survival projection alone: the projection
promotes at "under 15 seconds to die", which against an ordinary zombie is true from about 30
stamina, and a full-panel flash at 30 is an alarm that gets ignored at 20. The projection still
drives everything quieter. One override survives, because it is a count rather than a forecast -
**two hits left or fewer**, which is imminent whatever the absolute stamina says, and is how a
dragon kills someone at full health. The danger does not stop when the fight does: at that
stamina a wandering NPC that would ignore a healthy player will attack you (`RATE` crossing its
threshold, computed against the stats the 40 and 30 knees have already degraded), one blow from
most creatures can kill, and fleeing still costs real points. In combat the tier may escalate
above this but never read calmer than the same stamina would out of combat.

Being **in combat at all** is signalled by the **application window border** turning red and
pulsing slowly, and optionally a red outline on the input box - never by a large coloured
block inside the rail. A giant glow just because a fight started is a distraction.

Pulse is a very dark red, slow (roughly RGB 16-24).

## 9. Rendering contract

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
- Gate on `INPUT_DIAG`: if the rail costs even 1 ms of input latency it will be switched off and
  none of this matters.
