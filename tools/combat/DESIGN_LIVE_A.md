# Combat insight UX - Design A

Windows only. Written 2026-08-06. Independent design pass; does not reference or account for any
parallel proposal. Everything here, including every glyph proposed for use in code, is plain ASCII
(`INTERNAL.md`: "Models which use non-ascii characters in code will be rejected"). No application
code was changed to produce this document.

Contents:

1. [Information architecture](#1-information-architecture)
2. [Wireframes](#2-wireframes)
3. [Visual language](#3-visual-language)
4. [The flee-decision surface](#4-the-flee-decision-surface)
5. [Interaction model](#5-interaction-model)
6. [Staged implementation plan](#6-staged-implementation-plan)
7. [MAUI-on-Windows feasibility](#7-maui-on-windows-feasibility)

---

## 0. Starting position

Three facts drove every decision below more than anything else:

1. **The window is not the eye.** During a fight the player's eye is on the main terminal, reading
   prose. Anything that requires a saccade to a different screen region, let alone a different
   window, has already cost something. So the highest-value real estate is the strip the eye is
   already resting near - the top of the main window - and everything else is there for players who
   want more, not because it is required.
2. **This client already solved "act without touching the mouse."** `GamePage.xaml.cs` registers
   `Ctrl+F` (flee), `Ctrl+Shift+F` (flee in the currently-typed direction), and `Ctrl+1`-`Ctrl+5`
   (session command aliases) as `KeyboardAccelerator`s that call straight into
   `GameViewModel.Flee()` / `FleeThen()` / `SendControlAlias()` - each of which does
   `_conn.SendLine(...)` then `RequestFocus?.Invoke()`. That is a proven, tested, zero-click pattern
   for "the player's hands never leave the keyboard, and focus is guaranteed to end up back in the
   command box." The pursuit/chase feature in this design is built as one more binding in that same
   family, not as a button in a panel. A rail button is offered too, because it costs nothing extra
   and some sessions will prefer it - but it is the SECONDARY path, wired to call the identical
   command as the hotkey, never a different one.
3. **The flee-cost curve is the actual point of the live view.** Stamina bars and hit-rate tables are
   necessary but not sufficient - MUD2 already prints your stamina, and the player already knows
   roughly how a fight is going from reading the room. What nothing in the client currently computes
   is "what does leaving cost me right now, and does it cost less if I wait." That is genuinely
   counter-intuitive (waiting can be *cheaper*), it is irreversible once decided wrong (permadeath),
   and it is completely unserved today. Section 4 gets the most design attention in this document
   for that reason.

---

## 1. Information architecture

### 1.1 Three horizons, four surfaces

| Horizon | Clock | Where the eye is | Interaction rights |
| --- | --- | --- | --- |
| **Live** | every 2s tick | peripheral, glance only | keyboard only (see 0.2); no window may require a click |
| **Aftermath** | seconds after a fight ends | foveal, brief | one dismissible card, no modal |
| **Lab** | minutes, between sessions | foveal, deliberate | fully interactive, may take focus like Settings does |

Four surfaces carry these three horizons:

- **Status Strip** - augments the existing main-window top bar (`Sta / Mag / Str / Dex / Score`).
  Live horizon. Always present, zero new window, zero focus risk. Carries the single most important
  new number in this whole design: the live flee-cost figure.
- **Combat Rail** - revives the dead `IsCombatExpanded` / `CombatFoldGlyph` / `ToggleCombatCommand`
  triple in `SidePanelViewModel` (currently wired up with no XAML consuming it - a docked section
  that was planned and abandoned). Live horizon, in the main window, so every existing
  `RequestFocus` guarantee already covers it. Carries targets, the race, the flee ladder in full,
  and the pursuit list.
- **The Watch** - a small floating window, the direct successor to today's clog window's live
  duty. Optional, for players who want combat state visible even when the main window is not in
  view (a second monitor, or simply more peripheral space). Read-only by construction - see 5.4 for
  why that is forced by the platform, not a style choice.
- **Combat Lab** - a large, fully interactive window. Aftermath and Lab horizons: a per-fight replay
  pane, cross-fight rollups, and a dedicated tab for calibrating the flee-cost curve itself against
  real play (see 4.5). This is where `$clog`'s recording (already unconditional via
  `FightHistoryStore`/`fights.jsonl`) finally gets a screen of its own.

### 1.2 What lives where

| Information | Status Strip | Combat Rail | The Watch | Combat Lab |
| --- | :---: | :---: | :---: | :---: |
| Stamina, absolute + trend | **yes** | | yes | |
| Flee-cost-right-now figure | **yes** | **yes**, in full ladder | yes | yes, calibration |
| Risk verdict (Safe/Caution/Danger/Flee) | small | **yes** | yes | |
| Encumbrance str/dex delta + pulse | **yes** | | yes | yes |
| "Why is this going badly" plain-language line | | **yes** | yes | |
| Unarmed alert | | **yes** | yes | |
| NPC weapon-pickup alert | | **yes** | yes | |
| Active targets, per-target race | | **yes** | yes | |
| Pursuit candidates + chase hotkey hint | | **yes** | small | |
| Session totals | | small | yes | yes |
| Per-fight replay, swing strip | | | | **yes** |
| Weapon x creature matrix | | | | **yes** |
| Flee-cost curve calibration | | | | **yes** |
| Hidden-modifier findings | | | | **yes** |

### 1.3 Why not one merged panel

An earlier instinct was to fold the Rail and the Watch into one thing (either always docked, or
always floating). Rejected: they serve different players in different moments. The Rail is *in* the
window with the terminal, so it is free (no window-management cost, no activation risk, foldable in
place) - it should carry everything actionable. The Watch exists purely for the player who wants
combat state visible while the main window is not focused or not on the active monitor; it must
therefore tolerate being looked at without being interacted with. Merging them would force the Rail's
content to also survive the Watch's zero-interactivity constraint, which would mean deleting the
pursuit hotkey hint's clickable variant for no reason, or force the Watch to become interactive,
reopening the activation problem for no gain. Keeping them separate costs one more view class and
buys both the free actionable surface and the safe peripheral one.

---

## 2. Wireframes

All wireframes are monospace, Cascadia Mono, 0.6em advance (7.2px/char at 12px). Character-cell
column counts are noted per box.

### 2.1 Status Strip (main window, full width, unchanged height)

Idle:

```
 <  o  i | Sta 105/105 | Mag 105/105 | Str 100/100 | Dex 100/100 | Score 26375 +0 |  rec  Rain  95m
```

In combat, hurt, encumbered, and the flee-cost figure live (the new element, boxed for emphasis
below - it renders inline, no box glyphs in the real UI):

```
 <  o  i | Sta  38/105 |>Mag 105/105<| Str  89/100 -11 | Dex  71/100 -29 | flee 4%  Score 26375 |
         | ##....... |             | ########..      | #######...      | [xxxxxxxxxxxxxxxxx]  |
                                          ^^^^^^^^            ^^^^^^^^     ^^^^^^^^^^^^^^^^^^^^
                                          str breathing        dex pulsing  NEW: cost of fleeing
                                          (load, L1)            (load, L2)   right now, always live
```

- The meter row underneath each stat is a hairline (2px), present at all times (25% opacity when
  idle) so nothing reflows when combat starts.
- `flee 4%` replaces the `rec`/weather/reset cluster's usual position ONLY while in combat (that
  cluster is not useful mid-fight and the space is not otherwise spent); it reverts the moment
  combat ends. This is a content swap, not a resize, so it costs no layout thrash.
- The number is `Signed`-free (always shown as a positive cost) and is exactly the figure detailed
  in section 4: percentage of total score lost if you flee at this instant.

### 2.2 Combat Rail (main window, left dock, ~28 columns)

Idle - same footprint as live, so nothing above/below the rail moves when a fight starts:

```
+----------------------------+
| COMBAT                [>] |
|                            |
|   no fight in progress     |
|   last: rat0   KILLED 0:24 |
|   session  12f 9k 1 died   |
|                            |
|                            |
+----------------------------+
```

Live, single target, healthy:

```
+----------------------------+
| COMBAT  0:14           [v] |
|                            |
|  falchion vs rat0          |
|  you  [#########...] 64%   |
|  them [#####.......] 27%   |
|                            |
|  winning   kill 0:31       |
|                            |
|  flee now      10%  -2637  |
|  flee at 60 sta 10%  -2637 |
|  flee at 20 sta ~7%  -1845 |
|  flee at 6.5    2.5%  -659 |
|  below 6.5 sta      FREE   |
|                     ^ here |
+----------------------------+
```

Live, unarmed, encumbered, losing, one NPC has just fled (pursuit available):

```
+----------------------------+
| COMBAT  0:41           [v] |
|                            |
|  UNARMED vs zombie4        |
|  [picked up: fork]         |
|  you  [##..........] 12%   |
|  them [########....] 55%   |
|                            |
|  LOSING    die 0:18        |
|  low dmg: fighting bare    |
|  handed, and 7 items cost  |
|  you 11 str right now      |
|                            |
|  flee now      10%  -2637  |
|  flee at 15 sta ~8%  -2110 |
|  flee at 6.5    2.5%  -659 |
|  below 6.5 sta      FREE   |
|              ^ here (38)   |
|                            |
|  rat0 fled se, 0:04 ago    |
|  Ctrl+G to chase           |
|  > se,k rat0 wi falchion   |
+----------------------------+
```

Notes:

- `UNARMED` renders where the weapon name normally sits, in the alert colour (not the neutral
  "unarmed" label the current formatter uses) - see 3.2. This directly answers the brief's
  "unarmed combat, highlighted" requirement: today it is not distinguished from a real weapon name
  at all.
- `[picked up: fork]` is an EVENT pulse (3.4): it flashes for a few seconds when the line
  `The zombie4 has started to use the fork to fight!` is observed, then settles into a permanent,
  unemphasised tag the way it already does in `CombatStatsAggregator.FormatActiveNpcs` (`"zombie
  (fork)"`). The point of the flash is that this line is easy to miss in scrolling prose and, per
  the brief, "significantly changes the fight" - it deserves one moment of emphasis it does not get
  today.
- The "why" line (`low dmg: ...`) is the plain-language performance-guidance requirement - see 2.5
  for the rule table that generates it.
- The flee ladder is always four rows: your current stamina, the >20 ceiling, the 6.5 floor, and
  one interpolated row nearest your current stamina. Never more than four - a wall of numbers is
  the opposite of a decision aid. Full detail in section 4.
- The pursuit block only appears while a candidate is pending, is suppressed automatically while any
  other fight in the encounter is unresolved (per `MECHANICS_NOTES.md`'s caveat 2), and shows the
  EXACT text that Ctrl+G will send, so there is no mystery about what a keypress does before doing
  it.

### 2.3 The Watch (floating window, 360x280, read-only)

Live:

```
+----------------------------------------+
|  IN COMBAT                    0:41     |
+----------------------------------------+
|  STA        38 / 105                   |
|  [#########.......................]    |
|  about 3 hits left                     |
+----------------------------------------+
|  UNARMED  vs  zombie4 (fork)            |
|  low dmg: fighting bare handed, and 7   |
|  items cost you 11 str right now       |
+----------------------------------------+
|  flee now   10%   flee at 6.5sta  2.5%  |
|  you are at 38 sta -> about 7%          |
+----------------------------------------+
|  rats: usually 0:22, ~35 dmg to kill    |
+----------------------------------------+
```

Idle - identical band geometry, dimmed, no motion:

```
+----------------------------------------+
|  NO COMBAT                             |
+----------------------------------------+
|  STA       105 / 105                   |
|  [######################################]
|  healthy                               |
+----------------------------------------+
|  last fight: rat0  KILLED  0:24  28.5  |
+----------------------------------------+
|  session  12 fights  9 killed  1 died  |
|           2 fled     431 dealt 4:12    |
+----------------------------------------+
|  no combat data - type $watch to hide  |
+----------------------------------------+
```

Every band holds its height whether populated or not (same reasoning as the Rail): a glance surface
earns nothing from saving four vertical pixels at the cost of every number moving between frames.

### 2.4 Combat Lab (large window, 980x640)

Four tabs: **Overview**, **Weapons & Creatures**, **Flee Economics**, **Findings**. Overview and
Weapons&Creatures play the same role the reference materials describe for a fights list and a
weapon-by-creature matrix (both buildable today from `fights.jsonl` with no new capture); the two
tabs given full wireframes below are the ones this design adds emphasis to.

#### Flee Economics tab - calibrating the curve itself

```
+------------------------------------------------------------------------------------------+
| COMBAT LAB     Overview   Weapons & Creatures   [ Flee Economics ]   Findings             |
+------------------------------------------------------------------------------------------+
|  Known so far (from what you have told the client, not measured):                        |
|   sta > 20        costs 10% of score                                                      |
|   sta = 6.5        costs 2.5% of score                                                    |
|   sta < 6.5        costs nothing                                                          |
|                                                                                            |
|  This is 3 points, not a measured curve. The client cannot yet log what a flee actually    |
|  costs, so the ladder shown live is an interpolated GUESS between those points.            |
|                                                                                             |
|  RECORDED FLEES THIS CAPTURE:  0                                                           |
|                                                                                            |
|  TO FIND OUT: next time you flee, note your stamina just before and your score just        |
|  before/after in $clog eval or the chat log. Once the client captures this automatically    |
|  (see section 6, stage 2) this tab will plot real points here instead of the 3-point        |
|  guess, and the live ladder will read from measured data instead of interpolation.          |
|                                                                                            |
|   cost %                                                                                  |
|   10 |*                                                                                    |
|      | `--..                                                                              |
|    5 |      `--..                                                                          |
|      |            `-*                                                                     |
|    0 +-------------------+---------                                                       |
|      0        10        20   sta at moment of flee     * = the only 2 anchor points        |
|                                                          known (6.5 and >20); everything    |
|                                                          else on this line is a guess       |
+------------------------------------------------------------------------------------------+
```

#### Findings tab - same evidence-ladder discipline as the rest of the Lab

```
+------------------------------------------------------------------------------------------+
| COMBAT LAB     Overview   Weapons & Creatures   Flee Economics   [ Findings ]              |
+------------------------------------------------------------------------------------------+
|  WORTH A LOOK                                                                              |
|                                                                                            |
|  Carrying more seems to make rats hit you more often.                                       |
|    light (under 2kg)   .::.#:.       they hit you 24% of the time     9 fights             |
|    heavy (over 3kg)      .:.##:.:.   they hit you 38% of the time    11 fights             |
|                                                                                            |
|  TO FIND OUT: fight 8 more rats carrying under 2kg. Same weapon if you can.                 |
|                                                        [ start this trial ]                 |
+------------------------------------------------------------------------------------------+
|  TOO EARLY                                                                       (3 more)  |
|  The falchion may hit dwarves harder than the dagger0.   2 fights vs 1 fight               |
+------------------------------------------------------------------------------------------+
```

Rungs: `TOO EARLY` (under 5 fights either side, collapsed), `WORTH A LOOK` (5+, medians separate,
spreads still overlap), `LOOKS REAL` (12+, spreads barely overlap), `CONFIRMED` (30+, holds across
two conditions). Never a p-value, an interval, or the word "significant" - a word, a dot strip, a
sample count, and an instruction, every time.

### 2.5 The "why" line: rule table, not statistics

Deterministic, priority-ordered, computed from state the client already has (no new capture
required for the first four rows):

| Priority | Condition | Sentence |
| --- | --- | --- |
| 1 | current weapon is null | `low dmg: fighting bare handed` |
| 2 | strength delta <= -10 | `... and N items cost you M str right now` |
| 3 | this weapon's live per-hit < 70% of its own historical median for this npc_group (n >= 3) | `WEAPON is hitting for less than usual (X vs your usual Y)` |
| 4 | dexterity delta <= -15 and live hit-rate < historical hit-rate for this weapon | `carrying N items is costing you dex, and it shows in your hit rate` |
| 5 | an `NpcWeaponEquip` fired in the last 20s for the primary target | `they're hitting harder: TARGET picked up a WEAPON partway through this` |

Only the single highest-priority active condition renders (one line, never a stacked list - a
glance surface gets one sentence, not a report). Silent when nothing qualifies. This is exactly the
"surface causes, not coefficients" instruction: no formula is shown, ever - only the plainest true
sentence the current state supports.

---

## 3. Visual language

### 3.1 One palette, not a new one

`Rendering/TerminalTheme.cs` already carries the Campbell palette used by the terminal itself (the
project's own north star, "Clio in Windows Terminal", already lives here). The combat surfaces
should be chromatically part of the same terminal, not a fourth palette bolted on next to it (today
there are already two: Campbell in the terminal, a separate GitHub-dark set in `ClogPage.ToneColor`
- that drift is itself worth stopping). This design reuses `TerminalTheme.Palette` directly, index
by index, and reuses the SAME mechanic `TerminalTheme.Foreground` already implements for bold text:
promoting a normal-intensity colour to its bright variant. Urgency escalation in this design IS that
promotion - normal hue at rest, bright hue once it matters, bright hue plus a glow pulse once it is
urgent. No new colour infrastructure, just a semantic layer over what already exists.

| Role | Palette index (normal / bright) | Hex (normal / bright) | Means |
| --- | --- | --- | --- |
| Ink | 7 / 15 | #CCCCCC / #F2F2F2 | a primary value |
| Muted | 8 (dim only, never brightened) | #767676 | labels, units, sample counts |
| You | 6 / 14 | #3A96DD / #61D6D6 | belongs to the player |
| Them | 1 (normal only at rest) | #C50F1F | an opponent's identity - NOT a danger signal by itself |
| Danger | 1 -> 9 | #C50F1F -> #E74856 | lethal risk to the player. Same hue as Them, promoted - "the
  enemy" becomes "the enemy is about to kill you" by literally getting brighter, which is the exact
  relationship between the two ideas |
| Load | 5 / 13 | #881798 / #B4009E | encumbrance, self-inflicted stat penalties (the purple the
  brief asks for) |
| Caution | 3 / 11 | #C19C00 / #F9F1A5 | degraded but not lethal |
| Good | 2 / 10 | #13A10E / #16C60C | beating your own historical baseline |

Deliberate choices worth stating:

- **Them and Danger share a hue.** An opponent's name is always the normal red; the moment that
  opponent is about to kill you, the SAME red brightens. This removes the failure mode the current
  `ClogPage.ToneColor` has (the opponent's identity and "you are dying" are literally the same
  `#f85149` today, so there is nothing left to escalate to) without inventing a new colour - the
  escalation IS the promotion.
- **Outcomes (`KILLED`, `FLED`, `DIED`) render in Ink, not a judgement colour.** They are facts.
  Only `KilledByNpc` (player death) gets Danger-bright; everything else is just information.
- **No new hex value appears anywhere in this document.** Every colour above already exists in
  `TerminalTheme.Palette`.

### 3.2 Alert vocabulary: EVENT vs STATE, and three tiers of each

Two kinds of thing want emphasis, and they are not the same:

- **STATE** - a condition that is true right now and stays true (low stamina, encumbrance, being
  unarmed). Emphasis should persist exactly as long as the condition does, and stop the instant it
  clears.
- **EVENT** - something that just happened once (an NPC picked up a weapon, an NPC fled). Emphasis
  should flash briefly even though the resulting *state* may persist quietly afterward (the weapon
  tag stays visible; the flash does not). Without this distinction, either events get no emphasis at
  all (today's bug - the NPC weapon line is easy to miss) or every state re-flashes forever (noisy
  and also a violation of "at most one L3 element").

| Tier | Applies to | Colour move | Motion | Duration | Meaning |
| --- | --- | --- | --- | --- | --- |
| T1 | STATE | normal hue | none | while true | worth noticing on your own time |
| T2 | STATE | bright hue | none | while true | worth noticing soon |
| T3 | STATE | bright hue | glow pulse, 1.2s period | while true | act now |
| E1 | EVENT | bright hue flash | none | 1.5s then decays to T1/none | something changed, low stakes |
| E2 | EVENT | bright hue flash + one glow pulse | one pulse only | ~2.5s then settles | something changed, worth a look now (NPC armed, NPC fled) |

Rules carried over from first principles, restated because they are non-negotiable given Invariant
#1:

- Motion is **only** a glow/opacity layer behind or around text, via WinUI Composition
  (`ElementCompositionPreview` + `ScalarKeyFrameAnimation` on `Opacity`), never on the text itself
  (`Label.TextColor` animation is a dependent, UI-thread property and would violate the invariant
  the same way a `Dispatcher` timer would - see 7.4).
- **At most one T3 element at a time**, enforced in code. If two conditions qualify, the most urgent
  (lowest projected time-to-die) wins the pulse; the other renders at T2 (bright, static).
- Alarming transitions fade in over 250ms (one noisy tick should not flash the panel); calming
  transitions stop instantly.
- No motion at all outside combat, ever.

### 3.3 What triggers what

| Signal | Tier | Condition |
| --- | --- | --- |
| Stamina / hits-left | T3 | hits-left <= 2, or projected time-to-die < 15s and shorter than time-to-kill |
| Stamina | T2 | hits-left <= 4, or stamina < 25% of max |
| Stamina | T1 | stamina < 50% of max, in combat |
| Strength delta chip | T2 | effective strength < 50% of max |
| Strength delta chip | T1 | effective strength < 75% of max (the brief's own threshold) |
| Dexterity delta chip | T1 | any nonzero penalty, in combat |
| Unarmed | T2 | always, whenever the current weapon is null and a fight is live |
| NPC weapon pickup | E2 | on `NpcWeaponEquip` for the primary target |
| NPC fled, pursuit ready | E2 | on `NpcFled` with a parsed direction and no other unresolved fight |
| NPC fled, pursuit blocked | E1 | on `NpcFled` while another fight is still open (informational only) |
| Flee-cost crossing a bracket | E1 | your stamina crosses 20 or 6.5 downward (the number just got cheaper) |
| Outlook `LOSING` | T1 | (the stamina tier already carries the real urgency) |

### 3.4 Iconography, ASCII only

| Purpose | ASCII | Notes |
| --- | --- | --- |
| Meter fill / empty | `#` / `.` | text fallback; real bars are Skia rectangles |
| Estimated value | `~` prefix | e.g. `~7 hits` |
| Unknown / thin evidence | `?` | |
| No data | `--` | |
| Interpolated (not measured) row in the flee ladder | `~` prefix on the percentage | distinct from
  the anchor rows, which show a bare number |
| Current position marker | `^ here` under the row, or `>` beside it | never a bare arrow glyph |
| Fold state | `[v]` / `[>]` | fixed-width, replaces triangle glyphs |
| Outcomes | `KILLED` `DIED` `FLED` `YOU FLED` `WITHDREW` | words |
| Pending pursuit | `> <command>` | the literal text Ctrl+G will send, shown before it is sent |
| Silent combat tick (Lab swing strip) | `.` | makes the pass mechanic visible for the first time |

Anything beyond this (a weapon silhouette, a skull) is an image asset routed through
`Resources/Images` / `tools/rasterize-status-icons.cs`, never a character literal. Nothing in this
design requires one to ship a first version.

### 3.5 Typography and density

- One face: `"Cascadia Mono"` exactly, matching `MauiProgram.cs`'s registration. Never a CSS-style
  fallback list (`MappingPage.cs` and `RawConsolePage.cs` still carry that bug - worth fixing
  regardless of this design, see Stage 0).
- No bold anywhere: only the Regular face is registered, and a synthesised bold in a monospace face
  perturbs advance width - the exact bug class that broke `ClogPage`'s column alignment once
  already. Emphasis comes from the tier table above (colour + motion), never from weight.
- Three sizes: 16px for at most two hero numbers per surface (stamina, flee-cost), 12px body/tables,
  10px muted labels and sample counts.
- 4px vertical rhythm unit, 1.35 line height. Every band holds its height whether populated or not.
  Numeric columns right-align.

---

## 4. The flee-decision surface

This gets the most attention in the document because it is the highest-stakes and least-served
element in the brief: permadeath on one side, a real irreversible score cost on the other, and a
genuinely counter-intuitive shape (waiting can be *cheaper*) that nothing today computes at all.

### 4.1 What is actually known

Three data points, from the user, not measurements:

```
sta > 20   ->  10% of score
sta = 6.5  ->   2.5% of score
sta < 6.5  ->   0% (free)
```

That is a monotonically decreasing step from a flat ceiling, through one interior point, to a floor
of zero below a threshold. It is NOT a measured curve, and the shape between 6.5 and 20 is unknown -
it could be linear, could be a smoother decay, could itself be another step function with more
thresholds nobody has hit yet. This matters for how confidently the UI is allowed to draw it (4.3).

### 4.2 The honest interpolation

For a live estimate between the two known interior anchors (20 and 6.5), use linear interpolation
as the least-committal guess:

```
cost(sta) =
    10%                                    when sta > 20
    2.5 + (sta - 6.5) / (20 - 6.5) * 7.5    when 6.5 <= sta <= 20      (~0.56% per stamina point)
    0%                                     when sta < 6.5
```

This is presented to the player as a GUESS, explicitly, every time it renders - never as a fact with
the same visual weight as the two anchor points. See 4.3.

### 4.3 The ladder, and why it is stepped rather than a smooth line in the live UI

The live surfaces (Status Strip, Rail, Watch) show a **ladder of at most four rows**, not a
continuous graph:

```
flee now             10%   -2637      <- your current position, ALWAYS the first row
flee at 20 sta       10%   -1845      <- anchor: known, not a guess
flee at 6.5 sta       2.5%  -659      <- anchor: known, not a guess
below 6.5 sta        FREE            <- anchor: known, not a guess
```

The current-position row uses the interpolated formula from 4.2 when between anchors, and is
visually marked as an ESTIMATE (the `~` prefix from 3.4) whenever it is not itself sitting exactly
on an anchor. A smooth curve was deliberately rejected for the live view: a continuous line implies
a confidence in the interpolation that does not exist, and the brief's own standing rule elsewhere
in this codebase is "never imply precision you don't have" (median not mean, sample size always
shown, no p-values). A stepped ladder with an explicit guess-marker is the honest version of the
same idea applied to this specific number. The full curve, with its uncertainty visualised as a
shaded band, belongs in the Combat Lab's Flee Economics tab (2.4) where there is room to explain it
properly - not in a three-second glance.

### 4.4 The actual decision: fight on, flee now, or flee after taking a bit more

The Rail's ladder is paired with the survivability projection (`CombatOutlook`, already
implemented and tested) to produce one more line that is the whole point of this surface:

```
flee now         10%  -2637 pts
wait for ~2 more hits   ~7%  -1845 pts   -- but you may not survive them: ~3 hits left
```

Concretely: the client already knows (a) your current stamina, (b) the opponent's observed
damage-per-landed-hit this fight (or the historical median, per `FightHistorySummary`), and
therefore (c) roughly how many more hits bring you to the next cheaper flee-cost band, AND
separately (d) the "hits-left" survivability estimate from section 3.3. When (c) is greater than or
equal to (d) - i.e. reaching a cheaper flee band would take about as many hits as killing you - the
second line renders in Danger and is suppressed from ever reading as a suggestion; the wording is
deliberately "but you may not survive them," never "wait." This design refuses to ever tell the
player to wait; it only ever shows the arithmetic and the risk side by side, in the plainest
possible terms, and lets a human make the call under permadeath stakes. That is a deliberate
restraint: the model behind (c) is a rough estimate against a hidden formula (see 4.1), and an
automated "you should wait" instruction built on a guessed curve is exactly the kind of false
confidence this whole codebase's existing conventions (median-not-mean, no significance claims)
argue against.

### 4.5 Closing the loop: capturing real flee data

Nothing today records what a flee actually costs. This is the single most valuable new capture in
this entire design, because it converts a three-point guess into a real, growing, empirical curve -
exactly the same insight that made the fight-history store possible for the "are you winning"
projection (`STATS_DESIGN.md`: "history is what makes projection possible at all").

**What to add** (none of it exists today - `live_fights` has no score field at all, and stamina is
only captured at fight START):

- On every `YouFled` event: the player's stamina at that instant (already tracked continuously by
  `CombatStatsAggregator._lastKnownStamina`), the player's score immediately before sending `flee`
  (`GameViewModel._score` already exists and is live), and the score after the next `qs`/heartbeat
  reading following the flee confirmation.
- A new JSONL line type, `flee_event`, parallel to `fights.jsonl`'s per-fight rows: timestamp,
  stamina-at-flee, max-stamina, score-before, score-after, whether the flee succeeded (`fled` vs
  `tried to flee`).
- The Flee Economics tab (2.4) plots these as real points against the 3-anchor guess, and once
  there are enough (a handful is enough to start narrowing the shape - this is not a
  statistics-heavy fit, just "does the observed point sit near the guessed line or not"), the LIVE
  ladder should prefer the empirical nearest-neighbour figure over the interpolated formula,
  falling back to the guess wherever there is no nearby observation yet. This is the same
  instance-over-group preference pattern `CombatHistoryContext` already uses elsewhere in the
  codebase (prefer specific measured data once there is enough of it, fall back to the general
  model before that) - reused here rather than invented fresh.

### 4.6 Multiple fleeing NPCs, failed flees, and expiry

- **Failed flee ("tried to go") is not currently parsed at all.** `CombatTracker`'s `NpcFled` regex
  only matches `"The X has fled by going <dir>."`; per the audit note in `MECHANICS_NOTES.md`, a
  "tried to go" line likely does not match anything today, which is safe by accident, not by
  design. This design needs a distinct `CombatEventKind.NpcFleeFailed` (or a bool flag) added to
  `CombatModels.cs`, matched from a new regex, and explicitly NOT offered a pursuit action - the
  NPC never left, and offering to chase it would try to move the player out of a fight that is
  still live.
- **Direction is not currently captured either.** `NpcFled`'s regex uses `\w+` for the direction
  without a capture group. Needs a named group and a direction-word -> MUD2 abbreviation table
  (`"southeast"` -> `"se"`, not first-letter truncation - `MECHANICS_NOTES.md` already flags this
  exactly).
- **Multiple fleeing NPCs in different directions**: the Rail lists each pending candidate as its
  own line with its own suggested command; Ctrl+G always targets the single most-recently-fled
  candidate (the one the player is most likely still tracking mentally); anything older is reachable
  by clicking its line in the Rail (a main-window click, already safe). This avoids inventing a
  second hotkey scheme for an edge case the domain notes describe as uncommon (pursuit is blocked
  entirely while any other fight is open, which is also the common multi-NPC case).
- **Expiry**: a pending candidate fades from E2 back to a plain informational line after 15 seconds
  (a guess - "how long before it wanders further" is an open question the mechanics notes flag as
  unresolved) and is removed entirely once the encounter itself closes. It never auto-executes and
  never disappears the instant it is offered - the player types or presses at their own pace.

---

## 5. Interaction model

### 5.1 Commands

| Command | Does |
| --- | --- |
| `$clog on` / `off` | unchanged: starts/stops recording. Recording and display are decoupled below, unlike today. |
| `$clog status` | unchanged |
| `$clog eval <itemid>` | unchanged |
| `$watch` | toggles The Watch floating window, independent of recording |
| `$rail` | toggles the Combat Rail fold (mirrors clicking `[v]`/`[>]`) |
| `$lab` | opens the Combat Lab |

No behaviour is hidden behind a command that has no visible alternative - `[v]`/`[>]` in the Rail's
own header does the same thing `$rail` does, for a player who prefers the mouse.

### 5.2 Keyboard, built on the existing accelerator table

`GamePage.xaml.cs`'s `RegisterHotkeyAccelerators` already owns `Ctrl+F`, `Ctrl+Shift+F`, `Ctrl+1`-`5`,
`Ctrl+D`, `Ctrl+Shift+D`, `Ctrl+L`, `Ctrl+`` ``, `PageUp`/`PageDown`. This design adds exactly one:

- **`Ctrl+G`** ("go" / "give chase") - sends the current most-recent pursuit candidate's exact
  batch command (e.g. `se,k zombie4 wi falchion`) via `_conn.SendLine`, then `RequestFocus?.Invoke()`
  - the identical two-step pattern `Flee()` already uses. No-op (does nothing, focus still returns)
  when no candidate is pending, so an accidental press mid-fight is inert rather than surprising.
  This is a genuinely new capability - not a rebinding of anything existing - and it is the only new
  global key this design proposes.

Everything else actionable (fold toggle, pin toggle, clicking a Rail candidate line) already flows
through `SidePanelViewModel`'s existing `Command` + `RequestFocus` pattern and needs no new
keyboard plumbing.

### 5.3 Docking vs floating

- **Status Strip** - docked, permanent, always on.
- **Combat Rail** - docked in the left rail (same slot the dead `IsCombatExpanded` code already
  targets), foldable, and pinnable to a floating window using the existing `IsMapPinned` /
  `IsFloatingMapVisible` pattern already proven for the map.
- **The Watch** - a separate `Window`, geometry persisted in `mucka.ini`, optional always-on-top
  (see 7.3).
- **Combat Lab** - a separate `Window`, normal chrome, resizable, geometry persisted.

### 5.4 Focus: why The Watch has nothing to click

Clicking any control in a *different* top-level window changes OS window activation, and keystrokes
follow activation, not control focus - `GamePage.FocusInput()` cannot fix this because the problem
is one level higher than any control's focus state. Re-activating the main window on every stray
click elsewhere would produce visible z-order flicker and is not an acceptable trade.

Therefore The Watch is drawn as a single `SKCanvasView` with `EnableTouchEvents = false` and no
`Button`/`Entry`/tab-stop controls of any kind - there is nothing in it to click, so there is no way
for it to violate Invariant #0. Every actionable element in this design (the chase hotkey, the Rail's
click-to-select-a-candidate, the fold toggle) lives in the main window instead, where the existing
`RequestFocus` + root `PointerReleased` safety net already covers it. The Combat Lab is the one
sanctioned exception, in the same class as Settings: a deliberate context switch that owns focus
while open and returns it on `Esc` or close.

### 5.5 No combat active

- Status Strip: meters stay at 25% opacity, delta chips stay visible if encumbered (still worth
  knowing at rest), the flee-cost slot reverts to the normal weather/reset cluster.
- Combat Rail: same footprint, shows last fight + session totals.
- The Watch: idle layout (2.3), fully still.
- Post-kill grace (`IsCombatGracePeriod`, already implemented and tested): banner reads "winding
  down", all T2/T3 motion stops immediately, colours desaturate. The player is out of danger; the
  UI should visibly agree before the encounter formally closes.

---

## 6. Staged implementation plan

Ordered by value per unit of effort. Every stage after Stage 0 is independently shippable.

### Stage 0 - fix what is already broken (half a day)

- Split `Them` from `Danger` in whatever replaces `ClogPage.ToneColor`, so an opponent's identity and
  "you are dying" are no longer the same colour with nothing left to escalate to.
- Strip the non-ASCII glyph literals already flagged as a rule violation in
  `CombatHistoryFormatter.cs` / `ClogPage.cs`.
- Fix the CSS-style font fallback list still present in `MappingPage.cs` / `RawConsolePage.cs`.
- Surface stamina somewhere the player is already looking (today it lives only in
  `CombatStatDeficits`, consumed only by the projection, never displayed on its own).

### Stage 1 - Status Strip (2-3 days)

Meters, delta chips, and the T1-T3 tier mechanism (built once, as a small `PulseLayer.Attach(view,
tier)` helper over WinUI Composition - see 7.4 - reused by every later stage). The flee-cost figure
using the 3-anchor interpolation from 4.2, since it needs no new capture at all to show something
useful immediately.

Delivers: the encumbrance pulse requirement, the stamina-visibility gap, and a first (guessed) flee
number, all with zero new windows and zero new data.

### Stage 2 - flee/pursuit data capture (2-3 days, can run in parallel with Stage 1)

- Add `flee_event` capture (4.5): stamina-at-flee, score-before/after, success/failure. This is the
  highest-value new data item in the whole design because every day without it is a flee whose real
  cost is lost forever.
- Add the `NpcFleeFailed` event kind and fix `NpcFled`'s direction capture group + the
  word-to-abbreviation table (`MECHANICS_NOTES.md` already specifies this precisely).

### Stage 3 - Combat Rail (3-4 days)

Revive `IsCombatExpanded`. Targets, race bars, the flee ladder (4.3), the "why" line (2.5), the
pursuit list, and `Ctrl+G` (5.2).

Delivers: the rest of goal (a) - this is the loudest single complaint in `MECHANICS_NOTES.md`
("very annoying having to try and find the flee message...") finally answered, plus the flee
decision aid that nothing today provides at all.

### Stage 4 - The Watch (4-5 days)

Rewrite the clog window as the read-only, Skia-drawn, fixed-geometry surface in 2.3. Port
`CombatHistoryFormatter`'s content decisions (median-not-mean, sample-size-first, refuse-to-project-
early) rather than rewriting them; change only the rendering target from one `Label` to a structured
model a Skia painter consumes.

### Stage 5 - Combat Lab: Overview + Weapons & Creatures (1-1.5 weeks)

Built entirely on `fights.jsonl` data that already exists via `FightHistory.Summarize` /
`SummarizeInstance` / `SummarizeByWeapon` - no new capture needed for these two tabs.

### Stage 6 - Combat Lab: Flee Economics + Findings (1 week)

The curve plot against real `flee_event` rows (blocked on Stage 2 having run for a while first -
the earlier that capture starts, the sooner this tab has anything to show), and the Findings card
model / evidence ladder / trial helper.

### Stage 7 - deeper capture, ongoing, explicitly blocked items

Flagged in priority order. None of these block Stages 1-6; all of them make Stage 6 (and any future
swing-level review) better:

1. **Per-swing event stream in `fights.jsonl`.** Today only aggregate counters persist per fight.
   Blocks any swing-by-swing replay and blocks making the silent pass tick visible at all - the
   mechanic the brief specifically calls out as the owner's own curiosity.
2. **Wield-refusal capture.** "You cannot use the X to fight now!" is parsed nowhere. The
   stamina-gated strength threshold the brief names explicitly has zero observations without it.
3. **Stats snapshot at every weapon change and joiner start**, not only at encounter start - already
   flagged in `MECHANICS_NOTES.md`. Without it a fight where you switched weapons mid-way
   misattributes all damage to the starting weapon.
4. **Held vs stowed inventory split.** The dexterity-vs-strength claim about bags cannot be tested
   quantitatively without knowing which carried items were stowed.
5. **Invisibility and sleep state.** Neither is modelled; sleep in particular silences ALL
   client-visible combat resolution (confirmed live), so any swing-level analysis will misread a
   sleep window as an impossible instantaneous stamina drop unless those fights are flagged and
   excluded.
6. **A character/persona field.** Alts currently pool together in every rollup; there is no way to
   separate them.
7. **`raw_strength`/`raw_dexterity` freshness.** These come only from the `sc` command and are
   stale-by-construction relative to the FES heartbeat - any comparison using them should say so.
8. **NPC value/points per group**, cached once outside combat, per the existing recommendation
   already written down in `MECHANICS_NOTES.md`.

---

## 7. MAUI-on-Windows feasibility

Each item flagged **OK** (already proven in this codebase), **CARE** (works, has a known trap), or
**AVOID** (do not do this).

### 7.1 Secondary windows - OK

`new Window(...)` + `Application.Current.OpenWindow(...)` is already used three times in this
codebase (`_clogWindow`, `_rawConsoleWindow`, `_mapWindow`). The Watch and the Combat Lab are a
fourth and fifth instance of an already-proven pattern.

### 7.2 Window geometry - CARE

`GamePage.xaml.cs` already reaches past MAUI's unreliable `Window.X/Y/Width/Height` setters to the
platform `AppWindow` (`nativeWindow.AppWindow.Resize(...)`) for its own resize logic - reuse that
exact path for persisting/restoring The Watch's and the Lab's geometry, guarded with `#if WINDOWS`
as `ClogPage` already is in its entirety.

### 7.3 Always-on-top - CARE; non-activating - AVOID

`AppWindow.Presenter as OverlappedPresenter` then `IsAlwaysOnTop = true` is a straightforward,
Windows-only addition for The Watch. A genuinely non-activating window (`WS_EX_NOACTIVATE`) is NOT
exposed by `AppWindow` and needs raw `SetWindowLong` P/Invoke with its own hit-testing
complications - this design avoids ever needing it by making The Watch non-interactive in the first
place (5.4). Do not reach for `WS_EX_NOACTIVATE` if a future change makes The Watch clickable again
without re-reading this section.

### 7.4 Animation - the mechanism the whole tier system depends on

`ViewExtensions.FadeTo` / `Animation` / `Dispatcher.StartTimer` all run on the UI thread and are
**AVOID** outright under Invariant #1 - this is exactly why `ClogPage`'s author correctly refused to
add a pulse and settled for a static border colour.

WinUI Composition is **OK** and is the sanctioned mechanism this entire tier system (3.2) is built
on:

```
var visual = ElementCompositionPreview.GetElementVisual(platformView);
var compositor = visual.Compositor;
var anim = compositor.CreateScalarKeyFrameAnimation();
anim.InsertKeyFrame(0.0f, 1.0f);
anim.InsertKeyFrame(0.5f, 0.30f);
anim.InsertKeyFrame(1.0f, 1.0f);
anim.Duration = TimeSpan.FromMilliseconds(1200);   // 1.2s for T3, per 3.2
anim.IterationBehavior = AnimationIterationBehavior.Forever;
visual.StartAnimation("Opacity", anim);
```

This runs entirely on the compositor - zero UI-thread cost while active, no timer, no per-frame
managed code, satisfying Invariant #1 by construction. Constraints that follow directly:

- Only `Opacity`/`Offset`/`Scale`/`RotationAngle`/brush properties on a `SpriteVisual` are
  animatable this way. A MAUI `Label.TextColor` is NOT - animating it is a dependent, UI-thread
  property change, which is exactly why 3.2 insists the glow is a layer behind the text, never the
  text's own colour.
- Start/stop only on tier transitions, never polled.
- Tear down in `OnHandlerChanged` when `Handler is null`, exactly as `ClogPage` already does for its
  event subscriptions - a leaked forever-animation on a destroyed window is a real, silent leak.
- Build this once as `PulseLayer.Attach(view, tier)` in Stage 1; every later stage reuses it rather
  than reimplementing.

### 7.5 SkiaSharp - OK, with a known ceiling

`SKCanvasView` is proven three times already (`TerminalView`, `RadarCompassView`, `SwampSeamView`),
all event-driven via `InvalidateSurface()` with no render loop. On WinUI it is backed by
`SKXamlCanvas`, which **paints on the UI thread** - fine for The Watch's low draw-op count at
low Hz (the existing `OnAntiIdleTick` cadence plus on genuine change), not fine as a substitute for
7.4's animation mechanism. Keep the existing diff-before-invalidate discipline (`ClogLine.
SequenceEquals` today; an equivalent for whatever view model replaces it) so an unchanged frame
costs nothing.

### 7.6 Keyboard accelerators - OK, already proven for exactly this use case

`GamePage.xaml.cs`'s `RegisterHotkeyAccelerators` already registers a dozen-plus
`Microsoft.UI.Xaml.Input.KeyboardAccelerator`s at the window root, each marking `e.Handled = true`
and gated on `_isFkeyEditorOpen`/scrollback state. `Ctrl+G` (5.2) is one more entry in that same
table, calling one more `GameViewModel` method that follows the identical `SendLine` +
`RequestFocus` two-step every existing hotkey in that file already uses. This is the lowest-risk
piece of this entire design to implement, because it is not a new mechanism - it is the same
mechanism, called one more time.

### 7.7 Fonts - CARE

Only `CascadiaMono.ttf` Regular is registered in `MauiProgram.cs`. `FontAttributes.Bold` synthesises
and can perturb monospace advance width. Control weight explicitly via `SKTypeface` in Skia surfaces
(as `Rendering/TerminalFont.cs` already does) and avoid `FontAttributes.Bold` entirely on MAUI
control surfaces - emphasis in this design comes from the tier table (3.2/3.3), never from weight.

### 7.8 Long lists in the Combat Lab - CARE

`CollectionView` on WinUI has a poor track record for virtualization/measure correctness at scale.
For a fight list that could reach thousands of rows, either draw it in Skia as a virtual list
(consistent with `TerminalView`'s own approach) or cap the visible set through filtering/paging with
plain `CollectionView` as a cheaper first pass. Prefer the former for visual consistency; the latter
is an acceptable Stage 5 shortcut.

### 7.9 Off-thread aggregation - CARE, non-negotiable

The Lab and the game window share one UI thread and one dispatcher. Loading and aggregating
`fights.jsonl`, and especially any Findings-tab bootstrap statistics, **will** stall typing in the
main window if done inline. All of it runs off-thread, with only the finished view model marshalled
back - the same rule `FightHistoryStore.LoadAsync` already follows.

### 7.10 Multi-window focus - see 5.4

The single most important platform fact behind this whole design: control focus and window
activation are different things on Windows, `RequestFocus` only ever addresses the first, and the
only way to fully sidestep the second for a floating window is to give it nothing to click.

---

## Appendix: goals (a) and (b), and what serves each

**(a) Live engagement.** Status Strip (2.1) carries the always-on encumbrance/stamina/flee-cost
signals where the eye already is; the Combat Rail (2.2) adds targets, the race, the "why" line, the
full flee ladder, and the pursuit hotkey; The Watch (2.3) is the same information for players who
want it visible outside the main window. The tier system (3.2/3.3) is what makes any of this
peripheral rather than something that must be read. Both of the brief's named examples are directly
implemented: unarmed combat renders as an alert rather than a neutral label, and the strength/dex
delta chips pulse at exactly the 75%/50% thresholds specified.

**(b) Analysis.** The Combat Lab's Overview and Weapons & Creatures tabs give the cross-fight
rollups from data that already exists; Findings applies the same "sentence, picture, sample size,
instruction" discipline the codebase's own comments already argue for (median not mean, no false
precision). The Flee Economics tab is this design's one genuinely new analytical surface: it turns
the highest-stakes, least-served number in the whole brief from a three-point guess into a curve
that gets better every session the client plays.
