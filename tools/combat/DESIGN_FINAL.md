# Combat Insight UI  -  Final Design Spec

Status: **AGREED. Implement as written.** Windows only. Written 2026-08-06, reconciling
`DESIGN_LIVE_A.md` (base skeleton), `DESIGN_LIVE_B.md` (harvested improvements), and
`UX_PROPOSAL.md` (origin) against the owner's explicit decisions below. Every glyph in this
document, and every glyph an implementer writes into code, is plain ASCII
(`INTERNAL.md`: "Models which use non-ascii characters in code will be rejected."). No
application code was changed to produce this document. This is not a menu of options  -  build
what is written here.

Contents: [1](#1-decisions-and-rationale) | [2](#2-surface-inventory) |
[3](#3-wireframes) | [4](#4-visual-language) |
[5](#5-the-flee-cost-ladder) | [6](#6-keybindings) |
[7](#7-performance-contract) | [8](#8-staged-implementation-plan) |
[9](#9-open-questions)

---

## 1. Decisions and rationale

Settled. Do not reopen any row in this table during implementation or review.

| # | Decision | Rationale |
| --- | --- | --- |
| D1 | The current clog window (`ClogPage`, one `Label`/`FormattedString`) is deleted outright, not refactored. | Owner verdict: "it looks like shit." Its layout, section order, and rendering approach carry no weight going forward. |
| D2 | **No live-combat surface may float.** Everything live-horizon lives docked in the main window's (widened) side panel. Only the Combat Lab (analysis, never open during combat) is a separate floating window. | Two owner reasons: (a) a secondary window steals OS focus, violating Invariant #0, and in permadeath a lost keystroke can cost the character; (b) on Windows, a secondary window is not reliably restored alongside the main one after alt-tab. Read-only construction (as `DESIGN_LIVE_A`'s "The Watch" and `DESIGN_LIVE_B`'s "HUD windlet" both proposed) fixes reason (a) but not (b)  -  so floating live surfaces are rejected regardless of interactivity. |
| D3 | The main window's docked side panel is **widened** to carry full combat detail, not squeezed into the existing 228dp rail. | Owner: "widen the main window to accommodate a proper side panel." See 2.2 for the concrete number. |
| D4 | `Ctrl+F` (flee) / `Ctrl+Shift+F` (flee in typed direction) are unchanged, already implemented, already correct. | `Pages/GamePage.xaml.cs:1315-1319`, wired via `KeyboardAccelerator` -> `GameViewModel.Flee()`/`FleeThen()` -> `_conn.SendLine` + `RequestFocus?.Invoke()`. Proven pattern; the new bindings in D5 reuse it exactly. |
| D5 | Four new bindings: `Ctrl+E` chase+attack most-recent fleer, `Ctrl+Shift+E` chase+attack first fleer of the encounter, `Ctrl+G` follow (movement only) most-recent fleer, `Ctrl+Shift+G` follow (movement only) first fleer. All four are inert (no-op, no error) until unblocked. | Full spec in section 6. |
| D6 | The flee-cost curve is 3 anchor points (>20 sta = 10%, 6.5 sta = 2.5%, <6.5 sta = free). The shape between anchors is an explicitly-labelled GUESS (linear interpolation), never presented with the same visual weight as a measured value. No automated "you should wait" advice is ever generated. | Owner: this is not a measured function. `DESIGN_LIVE_A`'s stepped ladder + explicit guess-marker is correct; `DESIGN_LIVE_B`'s smooth interpolated point value is not  -  it implies precision that does not exist. |
| D7 | Cost-framing never overrides survival-framing. Below 6.5 stamina (free flee), the alert vocabulary does NOT de-escalate to a calm tone  -  the player is 1-2 hits from permadeath regardless of what fleeing costs. | Owner: `DESIGN_LIVE_B` made exactly this mistake (a `Good` tone below 6.5 sta). Explicit rule in section 4.4. |
| D8 | Render surface for all live combat content is a single **SkiaSharp canvas** embedded in the main window's side panel, not discrete WinUI controls (`Label`/`FormattedString`/`CollectionView`) and not a second `SKCanvasView` window. | The measured failure mode: an 11-NPC pack fight rebuilt 200+ native WinUI spans per event and stalled the UI thread 2-3s. A canvas draws fixed-layout primitives whose cost never scales with participant/history count; see section 7. |
| D9 | Pulse/glow motion runs via WinUI Composition (`ElementCompositionPreview` + `ScalarKeyFrameAnimation` on `Opacity`) on a layer positioned BEHIND the Skia canvas, never via a UI-thread timer and never by animating text colour directly. | Invariant #1. Composition animation costs zero UI-thread time once started; `SKXamlCanvas` itself paints ON the UI thread on WinUI, so it must never be the thing doing the animating. |
| D10 | Historical summaries (median damage/hit-rate/duration per npc_group, per instance, per weapon) are maintained **incrementally** as each fight closes, never recomputed by rescanning the full fight corpus. | The measured failure mode: `ExcludingEncounterFrom(...).ToList()` plus three median passes over the ENTIRE corpus on every cache miss, and misses happen on every fight resolution  -  cost grows across a whole session. See section 7.3. |
| D11 | Colour semantics reuse `Rendering/TerminalTheme.Palette` (the Campbell palette) by index, promoted normal-to-bright the same way `TerminalTheme.Foreground` already promotes bold text. No new hex values anywhere in this design. | Verified against `Rendering/TerminalTheme.cs`: `Palette[0..15]` is the exact Campbell table both draft designs cited. One palette across terminal and combat surfaces, not a second one drifting apart (as `ClogPage.ToneColor`'s GitHub-dark set already has). |
| D12 | ASCII-only iconography (`#`/`.` bars, `~` estimate marker, `[v]`/`[>]` fold state, plain words for outcomes). No unicode glyphs, escaped or literal. | Per `INTERNAL.md`. Note for implementers: `ViewModels/SidePanelViewModel.cs` currently returns the literal code-point escapes `u25bc` and `u25b6` from `CombatFoldGlyph`/`PanelToggleGlyph` (triangle glyphs), plus one literal non-ASCII character in `PanelToggleGlyph`'s other branch - do not copy that pattern into any new code this design adds; fixing the existing instances is Stage 0 (section 8). |
| D13 | The Combat Lab (analysis) is a separate floating window, normal chrome, resizable  -  explicitly allowed to float because it is never open during combat. | Owner: "A separate large ANALYSIS window may still float, because it is never open during combat." |

---

## 2. Surface inventory

Two horizons only  -  Live and Analysis  -  because D2 removes the third ("peripheral, floating,
read-only") horizon both draft designs proposed.

### 2.1 Status Strip  -  main window, top bar, docked, always present

Augments the existing `<  o  i | Sta ... | Score ...` top strip. Owns: stamina/str/dex meters
with delta chips, and the single live flee-cost headline figure. Zero new window. Visible at all
times; contents dim to 20-25% opacity out of combat.

### 2.2 Combat Rail  -  main window, left side panel, docked, widened

Revives the dead `IsCombatExpanded` / `CombatFoldGlyph` / `ToggleCombatCommand` triple in
`SidePanelViewModel` (wired up today with no XAML consuming it). This is now the ONE live
combat surface beyond the Status Strip  -  it absorbs everything a floating "Watch"/"HUD" would
otherwise have carried, because D2 forbids that surface from existing at all.

Owns: target list, race/outlook, the full flee ladder, the plain-language "why" line, unarmed
and NPC-weapon alerts, the pursuit/chase block (candidates + exact pending command), recent-swing
strip, weapon-vs-history table, and (out of combat) last-fight + session totals.

**Concrete widening**: `Pages/GamePage.xaml.cs` defines `SidePanelWidthDp = 228.0`, used both as
the panel `Border`'s `WidthRequest` and as an input to `PreferredWindowWidthDp`. Change this
constant to **300dp** (adds ~72dp / ~10 monospace columns at the panel's own 12px Cascadia Mono).
This is a static, permanent widening applied once at window layout time  -  NOT a dynamic
resize-on-combat-start  -  because a window that visibly resizes itself at the exact moment a
fight begins is its own kind of distraction, and Invariant #0 demands nothing about combat
starting should cost the player anything, including a frame of layout thrash. All wireframes in
section 3 assume a 40-column interior (300dp / ~7.2px per char, minus border padding).

**Minimum width and overflow.** 40 columns is the design target, not a guarantee: a user running
the OS at higher text-scaling/DPI gets fewer physical columns out of the same 300dp. The Combat
Rail's Skia layout (7.1) enforces a hard-floor minimum interior width of **28 columns** (the
narrowest wireframe row this design draws, the pack-fight table's participant line, still fits at
that width once truncated per below) and never renders narrower than that; if the actual available
width is below 28 columns the panel clips rather than attempting a layout no wireframe here
describes. Between 28 and 40 columns, overflow is handled the same way `CombatHistoryFormatter`
already handles it today, not a new rule:

- NPC and weapon names truncate (`CombatHistoryFormatter.Truncate`/`DisplayName`'s existing
  precedent), never wrap to a second line  -  every row in this design is a fixed-height band, and
  wrapping would break that invariant.
- The pending pursuit command (`> <command>`) truncates with a trailing `...` if the batched
  command text does not fit, rather than wrapping or horizontally scrolling  -  wide content that
  scrolls inside a MAUI `ScrollView` on WinUI is a known-bad pattern this design deliberately avoids
  (see the WinUI-on-Windows feasibility notes both draft designs independently flagged).
- Numeric columns (percentages, points, hit/miss counts) are never truncated  -  if a numeric column
  would not fit at 28 columns, the row that contains it is dropped from the capped row count (5
  participant rows, 4 ladder rows) before its numbers are ever clipped. A cut-off number is worse
  than a missing row.

### 2.3 Combat Lab  -  separate floating window, analysis only

Never open during a fight by construction of what it is for (post-session review, cross-fight
rollups, hidden-modifier hunting). Four tabs: **Overview**, **Weapons & Creatures**, **Flee
Economics**, **Findings**. Normal window chrome, resizable, geometry persisted in `mucka.ini`
(same pattern as the map/raw-console windows). Fully interactive  -  this is the sanctioned
exception to Invariant #0's docking requirement, in the same class as Settings: it owns focus
while open and returns it on close.

### 2.4 What lives where

| Information | Status Strip | Combat Rail | Combat Lab |
| --- | :---: | :---: | :---: |
| Stamina, absolute + trend | yes | yes | |
| Flee-cost-right-now figure | yes (headline) | yes (full ladder) | yes (calibration) |
| Risk verdict / outlook (winning/losing) | small | yes | |
| Encumbrance str/dex delta + pulse | yes | | yes (history) |
| "Why is this going badly" plain-language line | | yes | |
| Unarmed alert | | yes | |
| NPC weapon-pickup alert | | yes | |
| Active targets, per-target race | | yes | |
| Pursuit candidates + exact pending command | | yes | |
| Recent-swing strip (silent pass visible) | | yes | |
| Weapon vs npc_group table | | yes (condensed) | yes (full matrix) |
| Session totals | | small | yes |
| Per-fight replay | | | yes |
| Flee-cost curve calibration (real data) | | | yes |
| Hidden-modifier findings | | | yes |

---

## 3. Wireframes

Monospace, `"Cascadia Mono"` exactly (matching `MauiProgram.cs`'s font registration  -  never a
CSS-style fallback list; see D12/Stage 0). Column counts are literal character counts in the boxes
below.

### 3.1 Status Strip

Idle:

```
 <  o  i | Sta 105/105 | Mag 105/105 | Str 100/100 | Dex 100/100 | Score 26375 +0 |  rec  Rain  95m
```

In combat, hurt, encumbered  -  the flee-cost figure replaces the rec/weather/reset cluster only
while a fight is live (content swap, not a resize  -  nothing reflows):

```
 <  o  i | Sta  38/105 | Mag 105/105 | Str  89/100 -11 | Dex  71/100 -29 | flee ~7%  Score 26375 |
         | ##....... |             | ########..      | #######...      |                        |
```

- Meter hairline (2px) under each stat is always present, 25% opacity when idle, so nothing
  reflows when combat starts.
- `flee ~7%`  -  the `~` prefix is MANDATORY whenever the figure sits between the two known
  anchors (see section 5). At an anchor (exactly 20+ sta, exactly 6.5 sta, or below 6.5) the `~`
  is dropped because that figure is known, not guessed.
- Str/Dex delta chips (`-11`, `-29`) reserve 5 characters right-aligned even at zero, so a
  penalty appearing mid-fight does not shift anything else.

### 3.2 Combat Rail  -  idle (40-column interior)

```
+--------------------------------------+
| COMBAT                          [>] |
|                                      |
|  no fight in progress                |
|  last: rat0        KILLED     0:24  |
|                                      |
|  session  12 fights   9 killed      |
|           1 died       2 fled       |
+--------------------------------------+
```

### 3.3 Combat Rail  -  live, single target, healthy

```
+--------------------------------------+
| COMBAT  0:14                    [v] |
|                                      |
|  falchion vs rat0                    |
|  you  [#########.........] 64%      |
|  them [#####..............] 27%     |
|                                      |
|  winning        kill 0:31           |
|                                      |
|  FLEE COST                          |
|  now            10%      -2637 pts  |
|  at 20 sta      10%      -2637 pts  |
|  at 6.5 sta     2.5%      -659 pts  |
|  below 6.5 sta       FREE           |
|                        ^ 105 sta here|
+--------------------------------------+
```

### 3.4 Combat Rail  -  pack fight (5+ NPCs, capped at 5 rows)

```
+--------------------------------------+
| COMBAT  0:52                    [v] |
|                                      |
|  dagger0 vs                          |
|   rat12    0:11   12/ 4  killed     |
|   rat14    0:09    6/ 2  killed     |
|   rat3     0:44   14/ 9  live       |
|   rat5     0:31    9/ 6  live       |
|   rat7     0:22    8/11  live       |
|   and 11 more                        |
|                                      |
|  LOSING         die 0:19  kill 1:40 |
|  low dmg: fighting bare handed       |
|                                      |
|  FLEE COST                          |
|  now            10%      -2637 pts  |
|  at 6.5 sta     2.5%      -659 pts  |
|  below 6.5 sta       FREE           |
|                     ~9% 41 sta here |
+--------------------------------------+
```

- Each row is one draw call against a fixed-layout table (max 5 rows + "and N more"), never one
  native control per NPC  -  this is the direct fix for the 200+-span pack-fight stall. See
  section 7.
- `LOSING` line's "why" (`low dmg: fighting bare handed`) uses the rule table in 3.7.

### 3.5 Combat Rail  -  unarmed, encumbered, losing, one NPC fled and pursuable

```
+--------------------------------------+
| COMBAT  0:41                    [v] |
|                                      |
|  UNARMED vs zombie4                  |
|  [picked up: fork]                   |
|  you  [##..........] 12%            |
|  them [########....] 55%            |
|                                      |
|  LOSING          die 0:18           |
|  low dmg: fighting bare handed,      |
|  and 7 items cost you 11 str now     |
|                                      |
|  FLEE COST                          |
|  now            10%      -2637 pts  |
|  at 6.5 sta     2.5%      -659 pts  |
|  below 6.5 sta       FREE           |
|                    ~8% 38 sta here  |
|                                      |
|  ZOMBIE4 FLED se, 0:04 ago           |
|  Ctrl+E: chase and re-attack         |
|  > se,k zombie4                      |
+--------------------------------------+
```

- `UNARMED` renders in the current-weapon slot in the alert colour (not the neutral label the
  formatter uses today)  -  this directly answers "unarmed combat, highlighted."
- `[picked up: fork]` is an EVENT pulse (section 4.2): flashes when observed, then settles to a
  permanent unemphasised tag, matching `CombatStatsAggregator.FormatActiveNpcs`'s existing
  `"zombie (fork)"` string.
- `> se,k zombie4` is the LITERAL command `Ctrl+E` will send  -  no weapon clause because the
  player is unarmed (see section 6.2).

### 3.6 Combat Rail  -  pursuit BLOCKED (another fight still open)

```
+--------------------------------------+
| COMBAT  1:03                    [v] |
|                                      |
|  falchion vs rat3, rat13             |
|   rat3     0:12   3/ 2  live        |
|   rat13    1:03   9/ 7  live        |
|                                      |
|  EVEN            die 0:41  kill 0:38|
|                                      |
|  FLEE COST                          |
|  now             8%      -2110 pts  |
|  at 6.5 sta     2.5%      -659 pts  |
|  below 6.5 sta       FREE           |
|                    ~8% 19 sta here  |
|                                      |
|  rat3 fled n, 0:22 ago               |
|  blocked - finish current fight      |
|  > n,k rat3    (Ctrl+E when clear)   |
+--------------------------------------+
```

- The pending command is still SHOWN (never hidden) but rendered muted/dim, with the reason
  spelled out in words, per owner instruction: "these must be inert and visibly disabled until
  the engagement fully resolves." The moment `rat13` also resolves, this block brightens and the
  hint changes to an active `Ctrl+E` prompt exactly like 3.5.

### 3.7 Combat Rail  -  post-combat (result banner + session)

```
+--------------------------------------+
| COMBAT                          [v] |
|                                      |
|  + killed zombie4                    |
|                                      |
|  last: zombie4     KILLED     0:41  |
|         28.5 dealt / 11.0 taken     |
|                                      |
|  session  13 fights  10 killed      |
|           1 died       2 fled       |
+--------------------------------------+
```

- Result banner persists until the next fight starts or the player dismisses it (no auto-erase  - 
  this was a real complaint about the old window's 8-second self-clear).

### 3.8 "Why" line rule table (deterministic, priority-ordered, one line max)

| Priority | Condition | Sentence |
| --- | --- | --- |
| 1 | current weapon is null | `low dmg: fighting bare handed` |
| 2 | strength delta <= -10 | `... and N items cost you M str right now` |
| 3 | live per-hit < 70% of this weapon's own historical median for this npc_group (n >= 3) | `WEAPON is hitting for less than usual (X vs your usual Y)` |
| 4 | dexterity delta <= -15 and live hit-rate < historical hit-rate for this weapon | `carrying N items is costing you dex, and it shows in your hit rate` |
| 5 | an `NpcWeaponEquip` fired in the last 20s for the primary target | `they're hitting harder: TARGET picked up a WEAPON partway through this` |

Only the single highest-priority active condition renders. Silent when nothing qualifies. No
formula, coefficient, or number beyond what is already on screen elsewhere  -  this is the "surface
causes, not coefficients" instruction verbatim.

### 3.9 Combat Lab  -  Flee Economics tab (980x640 window)

```
+------------------------------------------------------------------------------------------+
| COMBAT LAB     Overview   Weapons & Creatures   [ Flee Economics ]   Findings             |
+------------------------------------------------------------------------------------------+
|  Known so far (told to the client, not measured):                                        |
|    sta > 20      costs 10% of score                                                      |
|    sta = 6.5     costs 2.5% of score                                                     |
|    sta < 6.5     costs nothing                                                           |
|                                                                                            |
|  This is 3 points, not a measured curve. The live ladder interpolates a GUESS between      |
|  them until real flee events are captured (see section 8, Stage 3).                       |
|                                                                                            |
|  RECORDED FLEE EVENTS THIS CAPTURE:  0                                                    |
|                                                                                            |
|   cost %                                                                                  |
|   10 |*                                                                                    |
|      | `--..                                                                              |
|    5 |      `--..                                                                          |
|      |            `-*                                                                     |
|    0 +-------------------+---------                                                       |
|      0        10        20   sta at moment of flee                                        |
|                                          * = the only 2 known points (6.5, >20); the        |
|                                            rest of the line is a guess, not data            |
+------------------------------------------------------------------------------------------+
```

### 3.10 Combat Lab  -  Findings tab

```
+------------------------------------------------------------------------------------------+
| COMBAT LAB     Overview   Weapons & Creatures   Flee Economics   [ Findings ]              |
+------------------------------------------------------------------------------------------+
|  WORTH A LOOK                                                                              |
|   Carrying more seems to make rats hit you more often.                                     |
|     light (under 2kg)   .::.#:.       they hit you 24% of the time     9 fights           |
|     heavy (over 3kg)      .:.##:.:.   they hit you 38% of the time    11 fights           |
|   TO FIND OUT: fight 8 more rats carrying under 2kg. Same weapon if you can.                |
|                                                        [ start this trial ]                 |
+------------------------------------------------------------------------------------------+
|  TOO EARLY                                                                       (3 more)  |
|   The falchion may hit dwarves harder than the dagger0.   2 fights vs 1 fight               |
+------------------------------------------------------------------------------------------+
```

Evidence ladder (4 rungs, words only, never a p-value or "significant"): `TOO EARLY` (<5 fights
either arm, collapsed), `WORTH A LOOK` (5+, medians separate), `LOOKS REAL` (12+, spreads barely
overlap), `CONFIRMED` (30+, holds across two conditions). Every card ends in an instruction.

---

## 4. Visual language

### 4.1 Palette  -  reused, not invented

`Rendering/TerminalTheme.Palette` (verified in code  -  the Campbell theme, indices 0-15):

| Role | Palette index (normal/bright) | Hex (normal/bright) | Means |
| --- | --- | --- | --- |
| Ink | 7 / 15 | #CCCCCC / #F2F2F2 | a primary value |
| Muted | 8 (dim only) | #767676 | labels, units, sample counts |
| You | 6 / 14 | #3A96DD / #61D6D6 | belongs to the player |
| Them | 1 (normal only at rest) | #C50F1F | an opponent's identity  -  not a danger signal alone |
| Danger | 1 -> 9 | #C50F1F -> #E74856 | lethal risk. Same hue as Them, promoted  -  "the enemy" becomes "the enemy is about to kill you" by brightening the same colour |
| Load | 5 / 13 | #881798 / #B4009E | encumbrance, self-inflicted stat penalties |
| Caution | 3 / 11 | #C19C00 / #F9F1A5 | degraded, not lethal |
| Good | 2 / 10 | #13A10E / #16C60C | beating your own historical baseline |

No new hex value appears anywhere in this design. Outcomes (`KILLED`, `FLED`, `DIED`) render in
Ink as facts, not judgements; only `KilledByNpc` (player death) gets Danger-bright.

### 4.2 EVENT vs STATE  -  two kinds of emphasis, five tiers

| Tier | Applies to | Colour move | Motion | Duration | Meaning |
| --- | --- | --- | --- | --- | --- |
| T1 | STATE | normal hue | none | while true | worth noticing on your own time |
| T2 | STATE | bright hue | none | while true | worth noticing soon |
| T3 | STATE | bright hue | glow pulse, 1.2s period | while true | act now |
| E1 | EVENT | bright flash | none | ~1.5s then decays to T1/none | something changed, low stakes |
| E2 | EVENT | bright flash + one glow pulse | one pulse only | ~2.5s then settles | something changed, worth a look now (NPC armed, NPC fled) |

Rules:
- **At most one T3 element at a time**, enforced in code. If two conditions qualify, the most
  urgent (lowest time-to-die) wins the pulse; the other renders T2 (bright, static). **Tie-break
  when two candidates have an equal (or incomparably close) time-to-die: stamina always wins.**
  Stamina is the only T3-eligible signal that can directly end the encounter in death; strength/dex
  degradation and unarmed status are contributing causes, not the clock itself, so ties resolve in
  favour of the signal that is actually counting down to permadeath.
- Escalating transitions fade in over 250ms; calming transitions stop instantly.
- No motion at all outside combat, ever.
- Motion is ALWAYS a glow/opacity layer behind or around text via WinUI Composition, never the
  text's own colour (D9). `Label.TextColor`/canvas-text-colour animation is a UI-thread dependent
  property change and is not permitted.
- **Bracket-crossing signals (the last row of 4.3) have hysteresis, not a bare threshold compare.**
  Each of the two brackets (20 sta, 6.5 sta) fires its E1 crossing event AT MOST ONCE per downward
  crossing, and re-arms only once stamina rises back strictly ABOVE that same boundary. Implement
  as one latched boolean per boundary (`_below20Armed`, `_below6_5Armed`), checked and cleared on
  the downward crossing, re-set only when stamina is observed above the boundary again. Without
  this, stamina oscillating across 6.5 from repeated small hits and small regen ticks would fire the
  E1 flash on every single tick that straddles the line - the opposite of the "worth a look, once"
  meaning E1 is supposed to carry.

### 4.3 What triggers what

| Signal | Tier | Condition |
| --- | --- | --- |
| Stamina / hits-left | T3 | hits-left <= 2, or projected time-to-die < 15s and shorter than time-to-kill |
| Stamina | T2 | hits-left <= 4, or stamina < 25% of max |
| Stamina | T1 | stamina < 50% of max, in combat |
| Strength delta chip | T2 | effective strength < 50% of max |
| Strength delta chip | T1 | effective strength < 75% of max (the brief's own threshold) |
| Dexterity delta chip | T1 | any nonzero penalty, in combat |
| Unarmed | T2 | always, whenever current weapon is null and a fight is live |
| NPC weapon pickup | E2 | on `NpcWeaponEquip` for the primary target |
| NPC fled, pursuit available | E2 | on a confirmed flee with no other unresolved fight |
| NPC fled, pursuit blocked | E1 | on a confirmed flee while another fight is open (informational only) |
| Flee-cost crossing a bracket | E1 | stamina crosses 20 or 6.5 downward |

### 4.4 The non-negotiable rule: survival overrides cost-framing

Below 6.5 stamina, fleeing is free  -  but the player is also one or two hits from permadeath. **The
flee-cost line's colour/tier NEVER de-escalates to Good/calm on that basis alone.** The FLEE COST
block's tone is driven by the STAMINA tier table above (4.3), completely independent of what the
cost-percentage happens to be. A free flee at 4 stamina renders with the SAME Danger/T3 urgency as
any other reading at 4 stamina  -  the number changes (`FREE` replaces a percentage), the colour and
motion do not soften. This is the single most important rule in this section: cost information and
survival information are visually independent channels, and survival always wins the channel that
controls urgency.

**Hard floor, stated explicitly so no other rule in this document can be read to override it:** at
or below 6.5 stamina, the FLEE COST block renders at **no less than T2 Pulse Danger**, full stop,
regardless of what the stamina-tier table in 4.3 would otherwise compute from hits-left or
projected time-to-die. 4.3's table can still promote the block to T3 (e.g. hits-left <= 2), but
nothing  -  not a healthy-looking damage rate, not an opponent that has not landed a hit yet, not any
future signal this design has not anticipated  -  is permitted to render the FLEE COST block below
T2 while stamina sits at or under the free-flee threshold. An implementer who derives a calm/T1
result from 4.3's table at 6.5 stamina or below has implemented the tier table wrong, not found an
exception to this rule.

### 4.5 ASCII iconography

| Purpose | ASCII | Notes |
| --- | --- | --- |
| Meter fill / empty | `#` / `.` | Skia-drawn rectangles; text is a fallback description only |
| Estimated value | `~` prefix | e.g. `~7 hits`, `~7%` |
| Unknown / thin evidence | `?` | |
| No data | `--` | |
| Interpolated (not measured) ladder row | `~` prefix on the percentage | distinct from anchor rows |
| Current position marker | `^ ... here` | never a bare arrow glyph |
| Fold state | `[v]` / `[>]` | fixed-width, replaces triangle glyphs |
| Outcomes | `KILLED` `DIED` `FLED` `YOU FLED` `WITHDREW` | words |
| Pending pursuit command | `> <command>` | the literal text a keypress will send |
| Silent tick (swing strip) | `.` | makes the pass mechanic visible for the first time |

### 4.6 Typography and density

- One face: `"Cascadia Mono"` exactly. No bold anywhere  -  only the Regular face is registered,
  and a synthesised bold perturbs monospace advance width (the exact bug class that broke
  `ClogPage`'s column alignment once already). Emphasis comes from the tier table, never weight.
- Three sizes: 16px for at most two hero numbers per surface (stamina, flee-cost), 12px
  body/tables, 10px muted labels/sample counts.
- 4px vertical rhythm unit, 1.35 line height. Every band holds its height whether populated or
  not  -  no reflow between idle and live states. Numeric columns right-align.

---

## 5. The flee-cost ladder

### 5.1 What is actually known

Three points, from the owner, not measurements:

```
sta > 20   ->  10% of score
sta = 6.5  ->   2.5% of score
sta < 6.5  ->   0% (free)
```

Monotonically decreasing from a flat ceiling, through one interior point, to a zero floor. The
shape between 6.5 and 20 is UNKNOWN  -  could be linear, could be a smoother decay, could be another
step function nobody has hit yet.

### 5.2 The honest interpolation (guess, always labelled)

```
cost(sta) =
    10%                                    when sta > 20
    2.5 + (sta - 6.5) / (20 - 6.5) * 7.5    when 6.5 <= sta <= 20   (~0.56% per stamina point)
    0%                                      when sta < 6.5
```

Every value produced by the middle branch is presented with a `~` prefix and is NEVER shown with
the same visual weight as the two anchor rows. This is the exact convention this codebase already
uses elsewhere (median not mean, sample size always shown, no false precision).

### 5.3 The ladder  -  stepped, at most four rows, never a smooth curve in the live view

```
flee now              10%   -2637      <- current position, ALWAYS first row, ~ if between anchors
flee at 20 sta         10%   -1845      <- anchor: known, not a guess
flee at 6.5 sta        2.5%   -659      <- anchor: known, not a guess
below 6.5 sta               FREE       <- anchor: known, not a guess
```

A smooth curve is deliberately rejected for the live view  -  continuous implies a confidence that
does not exist. The full curve, with uncertainty shown as a shaded band, belongs only in the Combat
Lab's Flee Economics tab (3.9), where there is room to explain it.

### 5.4 The risk-paired second line (never advice)

The "next band" calculation has three branches because the TARGET stamina it is counting down to
changes depending which side of the two anchors the player is currently on. Pseudocode, computed
fresh every UI tick from the current live snapshot (never cached across a fight):

```
function HitsToNextBand(currentStamina, incomingDamagePerHit, opponentLandedHitsThisFight):
    # Thin-sample guard: below this, incomingDamagePerHit is one lucky/unlucky swing, not a
    # rate. Reuses the same minimum CombatOutlook already gates its own projection on
    # (CombatOutlook.MinimumOwnHits = 2), rather than inventing a second threshold.
    if opponentLandedHitsThisFight < 2 or incomingDamagePerHit <= 0:
        return SUPPRESSED     # render nothing - no guess stands in for a real rate (3.8's own rule)

    if currentStamina < 6.5:
        return SUPPRESSED     # already free; no cheaper band exists - see 4.4/D7, never show a
                               # "next band" line here, it would reintroduce cost-framing exactly
                               # where survival must be the only signal on screen

    # The two branches below differ in what "next band" MEANS, not just in the numbers:
    if currentStamina <= 20:
        targetStamina = 6.5   # branch 2: counting down to the free-threshold anchor (2.5% there)
    else:
        targetStamina = 20.0  # branch 1: counting down to where the curve STARTS to fall - see below

    staminaToLose = currentStamina - targetStamina
    hits = ceil(staminaToLose / incomingDamagePerHit)
    return (hits, targetStamina)
```

Rendered wording differs by branch, because branch 1's target does not actually change the cost
(the curve is continuous at sta=20: `cost(20) == cost(20+epsilon) == 10%`), while branch 2's target
lands exactly on the 2.5% anchor:

```
# branch 1 (currentStamina > 20) - honest about there being no saving yet:
next band in ~6 hits           (cost stays 10% until then)

# branch 2 (6.5 <= currentStamina <= 20) - lands on the 6.5 anchor, a real cheaper number:
next band in ~3 hits    2.5%   -659 pts   -- but you may not survive them: ~2 hits left
```

The risk pairing applies to both non-suppressed branches: when `hits` (from the function above) is
greater than or equal to the survivability projection's hits-left estimate (section 4.3 /
`CombatOutlook`, already implemented and tested), the line renders in Danger and reads **"but you
may not survive them"**  -  never "wait", never any imperative. This design never tells the player
what to do; it shows the arithmetic and the risk side by side and leaves the call to a human under
permadeath stakes, because the model behind "hits to next band" rests on a guessed curve (5.1) and
an automated "you should wait" instruction built on a guess is exactly the kind of false confidence
this codebase's own conventions argue against.

### 5.5 Data needed live  -  confirmed available, zero new capture required for the ladder itself

- **Current stamina**: live via the FES heartbeat and inline in every `HitByNpc` line. Already
  tracked continuously (`CombatStatsAggregator._lastKnownStamina`).
- **Total score**: live via the FES heartbeat (`GameStatsSnapshot.Score`, confirmed present in
  `mudsharp/Models/GameStatsSnapshot.cs`)  -  read directly from the current merged stats snapshot,
  not from `FightRecord` (which has no score field at all).
- **Damage rate**: derived from the current fight's own `ApproxDamageDone`/`ApproxDamageTaken`
  divided by elapsed seconds, falling back to the npc_group's historical median before enough
  swings have landed (same trust-gate pattern `CombatHistoryContext` already uses).

So the ladder itself is NOT blocked on any new data capture. What IS missing, and matters for
turning the 3-point guess into a real curve, is below.

### 5.6 Closing the loop: capturing real flee data (Stage 3, section 8)

Nothing today records what a flee actually costs. `FightRecord` has no score field, and stamina is
only snapshotted at encounter START. Add, on every confirmed `YouFled`:

- Player stamina at that instant (already tracked continuously).
- Player score immediately before sending `flee` (`GameStatsSnapshot.Score`, read live).
- Player score after the next stats reading following the flee confirmation.
- Whether the flee succeeded (`fled` vs `tried to flee`  -  see section 6.4, this event kind does
  not exist yet either).

A new JSONL line type, `flee_event`, parallel to `fights.jsonl`. The Flee Economics tab (3.9)
plots these against the 3-anchor guess; once there are enough, the LIVE ladder should prefer the
nearest empirical observation over the interpolated formula, falling back to the guess wherever
there is no nearby observation  -  the same instance-over-group preference `CombatHistoryContext`
already applies elsewhere.

---

## 6. Keybindings

### 6.1 Unchanged (D4)

`Ctrl+F` = `_vm.Flee()` (sends `flee`). `Ctrl+Shift+F` = `_vm.FleeThen()` (sends `flee <typed
direction>`, or bare `flee` if the input box is empty). Both already implemented at
`Pages/GamePage.xaml.cs:1315-1319`, both already call `RequestFocus?.Invoke()` after
`_conn.SendLine(...)`. Do not touch this code.

### 6.2 New bindings and exact commands sent

All four new bindings reuse the identical two-step pattern every existing hotkey in
`RegisterHotkeyAccelerators` uses: `_conn.SendLine(...)` then `RequestFocus?.Invoke()`. All four
are no-ops (focus still returns, nothing sent) when there is no eligible candidate.

| Binding | Targets | Sends |
| --- | --- | --- |
| `Ctrl+E` | the MOST RECENT NPC to have fled this encounter | `<dir>,k <name>` if unarmed, else `<dir>,k <name> wi <weapon>` |
| `Ctrl+Shift+E` | the FIRST NPC to have fled this encounter | same batched form, that candidate's own direction |
| `Ctrl+G` | the MOST RECENT NPC to have fled | `<dir>` only  -  movement, no attack |
| `Ctrl+Shift+G` | the FIRST NPC to have fled | `<dir>` only  -  movement, no attack |

- `<dir>` is the MUD2 movement abbreviation (see 6.4), never the full word the flee line used.
- `<weapon>` is the weapon CURRENTLY in hand at the moment the key is pressed (read from the live
  encounter snapshot's current weapon), NOT the weapon that was in hand when the NPC fled  -  the
  domain instruction is explicitly "re-engage with the weapon already in hand."
- If the player is unarmed, the `wi <weapon>` clause is omitted entirely (`<dir>,k <name>`), not
  sent as `wi unarmed` or similar.

### 6.3 Data model: an ordered fled-candidate list per encounter

`CombatStatsAggregator` gains an encounter-scoped, chronologically-ordered list:

```
FledCandidate(string NpcName, string Direction, DateTime WhenUtc)
```

Appended on every confirmed (not "tried to") flee, in the order they occurred. Cleared only when a
NEW encounter begins (`BeginEncounter`), not when the current one closes  -  "most recent" and
"first ... of the last engagement" both need to remain answerable in the window right after a
multi-NPC fight resolves, which is exactly the moment pursuit becomes legal.

- **Most recent** = last element of the list.
- **First of the encounter** = first element of the list.
- A `FledCandidate` expires (removed from consideration, not necessarily from display) 15 seconds
  after `WhenUtc`  -  see section 9 for why this number is a recommended default, not a measurement.

### 6.4 Prerequisite parser work (blocking  -  must land before ANY of section 6.2 can function)

None of these four bindings can be built correctly today. This is Stage 2 in section 8, and it
precedes the Combat Rail's pursuit block and all four keybindings in the dependency graph there  -
not a footnote, a numbered stage of its own that nothing in section 6 can skip.

**1. `NpcFled`'s regex captures no direction.** Confirmed at `mudsharp/Combat/CombatTracker.cs:60`:

```
@"^The (?<npc>.+?) has fled by going \w+\.$"
```

`\w+` matches the direction word but does not capture it. Add a named group:
`@"^The (?<npc>.+?) has fled by going (?<dir>\w+)\.$"`, then convert the captured word through the
full-word -> MUD2-abbreviation table below.

**Direction word -> MUD2 abbreviation table** (the flee line always names the direction in full;
the movement command needs the abbreviation MUD2's own movement parser accepts  -  do not derive it
by truncation, several of these are not first-letter prefixes):

| Word | Abbreviation | Word | Abbreviation |
| --- | --- | --- | --- |
| north | n | northeast | ne |
| south | s | northwest | nw |
| east | e | southeast | se |
| west | w | southwest | sw |
| up | u | in | in |
| down | d | out | out |

**Unrecognised direction word**: if the captured word is not a key in this table (an unseen MUD2
direction, or a flee into water/a portal that names something else entirely  -  flagged as an open
unknown in `MECHANICS_NOTES.md`), **do not guess an abbreviation**. Treat the candidate as having no
usable direction: it is still recorded (so it shows up in history/session totals) but is excluded
from the fled-candidate list that backs the pursuit block and the four keybindings, and the Combat
Rail shows the NPC name with no pending command rather than a guessed one. Sending a wrong movement
command mid-combat is worse than offering no pursuit at all.

**2. "Tried to flee" is not distinguished from a real flee at all.** There is no regex for it today.
Per `MECHANICS_NOTES.md`, the existing `NpcFled` pattern almost certainly does not match a "tried to
go" line either (its wording differs and `NpcFled` requires the literal `"has fled by going"`), which
today is "correct" only by the accident of not matching anything, not by any deliberate handling  -
**this needs a regression test either way**, confirming both that the real-flee regex does not
misfire on a failed attempt, AND that the new failed-flee regex fires correctly on it. Add:

```
@"^The (?<npc>.+?) tried to flee by going (?<dir>\w+)\.$"
```

matched to a distinct `CombatEventKind.NpcFleeFailed` (or an equivalent flag on the existing kind).
A failed flee still disengages the NPC from the current exchange (per the domain notes, it leaves
the player open to instant re-engagement  -  the NPC is still IN the room, just no longer committed
to this specific attack), but it is **never** added to the fled-candidate list and **never** offered
as a pursuit target: the NPC never left, so there is nowhere to chase it to.

**3. The fled-candidate list itself (6.3) does not exist.** New state on `CombatStatsAggregator`,
populated only by item 1's confirmed-flee path, never by item 2's failed-flee path.

All three land together in Stage 2 (section 8) before the Combat Rail's pursuit block or any of
the four keybindings ship.

### 6.5 Stale / unknown / unreachable handling

- **No candidate pending** (nothing has fled, or the only candidate expired): the binding is a
  pure no-op. Focus still returns to the command box (so an accidental press is never surprising),
  nothing is sent, and the Combat Rail shows nothing new.
- **Pursuit blocked** (any other fight in the encounter still unresolved): the binding is INERT  - 
  visibly so. The Combat Rail shows the candidate's line and the exact pending command, but dimmed,
  with the reason spelled out (`blocked - finish current fight`, see 3.6). Pressing the key while
  blocked does nothing and returns focus, exactly like the no-candidate case. This directly
  implements the owner's instruction: "these must be inert and visibly disabled until the
  engagement fully resolves."
- **Unreachable / server refuses the move**: MUD2 gives no confirmation line the client currently
  parses for "you can't go that way." A batched `<dir>,k <name>` where the movement sub-command
  fails will still execute the `k <name>` sub-command on the next server turn, in whatever room the
  player is actually still in  -  which may attack nothing, or (worse) attack an unrelated NPC that
  happens to share the fled NPC's name. This is a known, currently-unfixable limitation (no
  regression path exists without new parser work  -  flagged in section 9), same class of risk
  `$clog eval`'s existing command batching already accepts.
- **Show the command before commit**: every state in sections 3.5/3.6 displays the literal `>
  <command>` text the corresponding keypress will send, updated live as the current weapon
  changes, BEFORE the key is ever pressed. This satisfies the owner's explicit requirement in full.

---

## 7. Performance contract

This is not a later optimisation pass  -  it is why D1/D8/D9/D10 exist. The measured failure: an
11-NPC pack fight rebuilt 200+ native WinUI spans per combat event, saturating the UI thread and
delaying combat text 2-3 seconds; the previous throttle/row-cap retrofit is still insufficient
because it bounds render FREQUENCY, not render COST, and does nothing about the history-lookup
cost growing across a session.

### 7.1 Render surface

A single `SkiaSharp` `SKCanvasView` embedded directly inside the Combat Rail's content area in the
main window (D8)  -  not a `Label`/`FormattedString` (today's approach, and the direct cause of the
stall: one native `Run` per styled span, full teardown-plus-remeasure on every render), not a
`CollectionView` (poor virtualization at scale on WinUI), and not a second window. All text, bars,
and the ladder are Skia draw calls against a FIXED layout: a capped number of rows (5 participant
rows + "and N more", 4 ladder rows, 1 why-line, 1 pursuit block) whose draw-call count depends only
on that fixed cap, never on total participant count or total historical fight count. On WinUI,
`SKXamlCanvas` paints ON the UI thread  -  acceptable here specifically because the draw-call count
is bounded and the canvas is only invalidated on genuine state change (7.2), never per-frame.

Pulse/glow (D9): a WinUI `Border`/`Rectangle` positioned BEHIND the canvas in the same grid cell
(canvas background transparent), driven by `ElementCompositionPreview.GetElementVisual` +
`ScalarKeyFrameAnimation` on `Opacity`, started/stopped only on tier transitions (4.2), torn down in
`OnHandlerChanged` when `Handler is null` exactly as the current `ClogPage` already does for its
subscriptions. This is the ONLY mechanism producing continuous motion anywhere in this design;
`SKXamlCanvas` itself is never asked to animate.

**The recent-swing ring buffer bound is 6, not an arbitrary "fixed size".** Confirmed in
`mudsharp/Combat/FightAccumulator.cs`: `RecentSwingCapacity = 6`, backing two fixed `SwingOutcome[]`
arrays (`_yourRecent`/`_theirRecent`) per fight, already O(1) to write and already capacity-capped
regardless of how long the fight runs. The recent-swing strip in the Combat Rail (3.3-3.5's wireframes
show it condensed; a full strip matches this exactly) never draws more than 6 columns per side  -
this existing constant is reused as-is, not redesigned.

**Minimal `PulseLayer` sketch.** Built once in Stage 1, reused by every later stage that needs a
tier-driven pulse (4.2). Teardown is not optional: this codebase already has a live crash precedent
for exactly this class of bug  -  `ClogPage`'s own remarks describe a page that stayed subscribed
after its hosting window closed, so the next combat line rendered into already-destroyed WinUI
objects and took the whole process down with `RO_E_CLOSED` (`0x80000013`). A leaked, still-running
Composition animation on a torn-down visual is the identical failure shape, so `PulseLayer` follows
the identical discipline `ClogPage.Detach()` already established:

```csharp
internal sealed class PulseLayer
{
    private CompositionScalarAnimation? _anim;
    private Visual? _visual;
    private readonly FrameworkElement _host;   // the Border/Rectangle sitting behind the canvas

    private PulseLayer(FrameworkElement host)
    {
        _host = host;
        _host.Unloaded += (_, _) => Stop();          // belt-and-braces alongside OnHandlerChanged
    }

    public static PulseLayer Attach(FrameworkElement host) => new(host);

    /// <summary>Starts (or restarts, if the period differs) the glow for the given tier. T1/E1-none
    /// callers should call Stop() instead - this method is only for T3/E2's actual pulsing tiers.</summary>
    public void SetTier(PulseTier tier)
    {
        if (tier is PulseTier.None or PulseTier.StaticBright) { Stop(); return; }

        _visual ??= ElementCompositionPreview.GetElementVisual(_host);
        var compositor = _visual.Compositor;
        _anim = compositor.CreateScalarKeyFrameAnimation();
        _anim.InsertKeyFrame(0.0f, 1.0f);
        _anim.InsertKeyFrame(0.5f, tier == PulseTier.T3 ? 0.25f : 0.45f);
        _anim.InsertKeyFrame(1.0f, 1.0f);
        _anim.Duration = TimeSpan.FromMilliseconds(tier == PulseTier.T3 ? 1200 : 2500);
        _anim.IterationBehavior = tier == PulseTier.T3
            ? AnimationIterationBehavior.Forever
            : AnimationIterationBehavior.Count;   // E2: "one pulse only" per 4.2's tier table
        if (tier != PulseTier.T3) _anim.IterationCount = 1;
        _visual.StartAnimation("Opacity", _anim);
    }

    /// <summary>Stops and detaches the animation. MUST be called from the host page's
    /// OnHandlerChanged when Handler is null - the same place ClogPage.Detach() already runs from -
    /// never left to GC, since a live Composition animation on a destroyed visual is exactly the
    /// RO_E_CLOSED crash class described above.</summary>
    public void Stop()
    {
        _visual?.StopAnimation("Opacity");
        _anim = null;
    }
}
```

Wiring: the Combat Rail's page-equivalent (whatever hosts the new `SKCanvasView` in the widened side
panel) calls `_pulse.Stop()` from its own `OnHandlerChanged(Handler is null)` path, alongside
whatever else it already tears down there  -  one more line in an existing method, not a new
lifecycle hook.

### 7.2 Work budget: per-event, per-second, per-encounter

| When | What runs | Cost |
| --- | --- | --- |
| **Per combat event** (every hit/miss/flee/weapon-equip line  -  fires on the Feed thread, MUD2's own tick is ~2s but a pack fight can emit several lines per tick) | Update the relevant `FightAccumulator` counters and fixed-size swing ring buffer (already O(1) in current code); append to the fled-candidate list on a confirmed flee; set a dirty flag. NOTHING ELSE. | O(1), independent of accumulated history size |
| **Per second** (UI-thread tick, matching or slightly below the existing `ClogRenderGate`'s ~4-5Hz cap) | If dirty: rebuild the small immutable snapshot record (bounded by the fixed caps in 7.1); structurally diff against the last-rendered snapshot (same discipline as `ClogLine.SequenceEquals` today); if changed, call `InvalidateSurface()` once. | Bounded by fixed layout size, never by history or participant count |
| **Per encounter** (fight close) | Append the `FightRecord` (and new `flee_event` rows) to their JSONL files off-thread; update the in-memory incremental history index for that npc_group/instance/weapon bucket (7.3) | File I/O and index update happen off the UI thread; only the finished small summary object is marshalled back |

### 7.3 History summaries stay O(1)-ish: incremental index, not full rescans

Today's cost: `FightHistory.ExcludingEncounterFrom(...).ToList()` plus three median-computing
passes over the ENTIRE fight corpus, and this happens on every cache miss, which happens on every
fight resolution  -  so the cost grows for the whole session.

Replace with an in-memory `HistoryIndex` maintained incrementally:

- **Startup**: one off-thread load of `fights.jsonl`, building per-npc_group, per-instance, and
  per-(weapon, npc_group) buckets, each holding a small sorted list of the values needed for its
  medians (damage-per-hit, duration, hit-rate, kill-damage-for-pool). One-time cost, off the UI
  thread, same pattern `FightHistoryStore.LoadAsync` already follows.
- **On fight close**: insert this one fight's values into the handful of buckets it belongs to
  (binary-search insertion into each bucket's sorted list  -  cheap at realistic bucket sizes of
  dozens to low hundreds, and this runs once per fight close, never once per UI tick or per combat
  event).
- **On encounter start**: look up (O(1) dictionary access) the `CombatHistoryContext` for the new
  primary target's npc_group/instance and CACHE it for the whole encounter's duration  -  read-only
  during the fight, never re-queried per event or per tick.
- **Self-comparison is structurally impossible, not filtered out**: because the index is only
  updated when a fight fully closes and flushes, the in-progress encounter's own rows are never in
  the index to begin with  -  `ExcludingEncounterFrom`'s runtime filter is no longer needed, it is
  replaced by an update-ordering guarantee.

### 7.4 What must never happen on the UI thread

- File I/O of any kind (loading or appending `fights.jsonl`, `flee_event` rows, `items.jsonl`).
- A rescan of the full fight corpus for any median or summary.
- Native view creation/teardown proportional to participant count (the old `Label`/`FormattedString`
  approach)  -  replaced entirely by bounded Skia draw calls.
- Any `Task.Wait()`/`.Result` or synchronous blocking call.
- Repeating timers driving opacity/colour (Invariant #1)  -  all continuous motion is Composition
  (7.1), started/stopped on transitions only.

### 7.5 What must never happen on the Feed thread

- Anything that blocks on the UI thread (no synchronous dispatch, no waiting on a UI-thread result).
- File I/O (JSONL appends are queued/fire-and-forget onto a background writer, exactly as
  `ClogWriter` already does for per-encounter clogs).
- History-index rescans or lookups beyond the O(1)/O(log n) incremental update in 7.3  -  the Feed
  thread's job for combat is purely: classify the line, update counters, set dirty. It never reads
  history at all; only the UI-thread tick (7.2) reads the cached `CombatHistoryContext`.

---

## 8. Staged implementation plan

**Correction against an earlier draft of this document: "every stage after Stage 0 is independently
shippable" was not true and is withdrawn.** Some stages genuinely have no dependency on any other
and can start the same day; others are hard-blocked on a specific earlier stage's code existing, and
one piece of infrastructure (`PulseLayer`, Stage 1) is a dependency nearly everything downstream
reuses rather than reimplements. The dependency graph below is the authoritative statement of what
can start when  -  read it before assigning stages to parallel workstreams.

### 8.1 Dependency graph

```
Stage 0  (fix what is broken)                    \
Stage 1  (Status Strip + PulseLayer)               \
Stage 2  (parser prereqs for pursuit)                +--  no dependencies. all four can
Stage 3  (flee_event capture)                      /       start the same day, in parallel,
Stage 5  (incremental history index)              /        by different people/workstreams
Stage 6  (Combat Lab: Overview + Weapons&Creatures)/

Stage 1 ------------------------------+
                                        \
Stage 5 (recommended, not a hard block) -+--> Stage 4a (Combat Rail: core - targets, race,
                                        /       flee ladder, why-line)
Stage 0 (needs the widened panel) ----+

Stage 2 (HARD BLOCK - cannot be skipped) --+
                                             \
Stage 4a (extends the already-shipped rail) -+--> Stage 4b (pursuit block + Ctrl+E /
                                                    Ctrl+Shift+E / Ctrl+G / Ctrl+Shift+G)

Stage 3 (soft block - code can be written    --+
  immediately, but the tab has nothing REAL      \
  to show until this has run for a while)          +--> Stage 7 (Combat Lab: Flee
Stage 5 (soft - Findings' rollups benefit,        /       Economics + Findings)
  not required) ---------------------------------+

Stage 8 (deeper capture, ongoing) -- blocks nothing above; nothing above blocks it either
```

Reading it plainly:

- **Can start immediately, in parallel, day one, on separate workstreams**: Stage 0, Stage 1
  (Status Strip meters and the `PulseLayer` skeleton specifically  -  the reviewer's own examples),
  Stage 2 (parser prerequisites), Stage 3 (flee_event capture), Stage 5 (incremental history index),
  and Stage 6 (Combat Lab: Overview + Weapons & Creatures, built entirely on rollups that already
  exist over `fights.jsonl`  -  the third of the reviewer's own examples). Six of nine stages have
  zero code dependency on any other stage in this plan.
- **Hard-blocked, cannot be reordered**: Stage 4b (the pursuit block and all four new keybindings)
  cannot exist before Stage 2 lands  -  there is no eligible fled-candidate direction, no
  `NpcFleeFailed` distinction, and no candidate list for the keybindings to read. This is not a
  scheduling preference, it is a code dependency: section 6.2's four bindings are defined entirely
  in terms of state Stage 2 creates.
- **Soft-blocked (code is independent, USEFUL CONTENT is not)**: Stage 7's Flee Economics tab can be
  written the day Stage 3 starts, but it has nothing but the 3-anchor guess to plot until real
  `flee_event` rows have accumulated from actual play  -  the earlier Stage 3 starts capturing, the
  sooner Stage 7 has anything beyond the guess to show.
- **Recommended-but-not-required ordering**: Stage 5 (incremental history index) should land
  before or alongside Stage 4a, because Stage 4a's "why" line (rule 3 in 3.8) and weapon table both
  read history, and shipping the new Combat Rail on top of the OLD full-corpus-rescan behaviour
  would reintroduce the exact perf bug this design exists to fix, even though nothing in Stage 4a's
  own code requires Stage 5 to compile or run.
- **`PulseLayer` (built in Stage 1) is reused, not reimplemented, by Stage 4a and Stage 4b's tier
  pulsing.** Building it as a one-time shared helper (7.1) is why Stage 1 is listed first among the
  "can start immediately" group rather than folded into Stage 4  -  every later stage that needs a
  pulse calls into it instead of writing its own Composition boilerplate.

### Stage 0  -  fix what is already broken (half a day)

- Delete `ClogPage`/`ClogDisplay`'s WinUI-`Label` rendering path entirely (D1).
- **Required, not an aside: strip the non-ASCII escape-sequence glyphs (`u25bc`/`u25b6`) and the
  literal non-ASCII character currently in `ViewModels/SidePanelViewModel.cs`** (`CombatFoldGlyph`,
  `PanelToggleGlyph`, and siblings for Online/Inventory/Map/ItemsHere)  -  replace with `[v]`/`[>]`
  per D12. This is not a nice-to-have cleanup adjacent to the work: **the new Combat Rail is being
  built in this exact file and its exact fold-glyph convention.** Leaving the old glyphs in place
  means the same file ships two different fold-glyph systems side by side, and a variable-width
  glyph mixed with fixed-width monospace ASCII is the identical failure mode that broke `ClogPage`'s
  column alignment once already (INTERNAL.md's rule exists for exactly this reason, not just style).
- Fix the CSS-style font fallback list still present in `MappingPage.cs`/`RawConsolePage.cs`
  (unrelated to combat, but the same font-registration bug class this design depends on getting
  right  -  see D12/4.6).
- Widen `SidePanelWidthDp` from 228 to 300 (D3/2.2) and confirm `PreferredWindowWidthDp` picks it
  up without further changes.

### Stage 1  -  Status Strip + render/pulse infrastructure (2-3 days)

No dependencies  -  can start the same day as Stage 0/2/3/5/6.

- Meters, delta chips (Skia-drawn rectangles, never text substitutes).
- Build the `SKCanvasView` + `PulseLayer` helper described in 7.1 ONCE  -  every later stage that
  needs a tier pulse (4a, 4b) reuses this instance rather than writing its own.
- The flee-cost headline figure using the 3-anchor interpolation (5.2)  -  needs zero new capture.

Delivers: encumbrance pulse requirement, stamina visibility, a first (guessed) flee number, zero
new windows, zero new data, and the rendering foundation every later stage builds on.

### Stage 2  -  parser prerequisites for pursuit (2-3 days)

No dependencies  -  can start the same day as Stage 0/1/3/5/6. **Hard-blocks Stage 4b.**

- `NpcFled` direction capture group + the full word-to-abbreviation table (6.4).
- `NpcFleeFailed` event kind, matched from its own regex, tested distinctly from a real flee, with a
  regression test covering both directions of the ambiguity (6.4 item 2).
- The ordered fled-candidate list (6.3) on `CombatStatsAggregator`.

Nothing in the Combat Rail's pursuit block or any of the four new keybindings (Stage 4b) can ship
before this stage  -  it is the hard prerequisite for section 6 in full, not a parallel option.

### Stage 3  -  flee_event capture (2-3 days)

No dependencies  -  can start the same day as Stage 0/1/2/5/6. Independent of Stage 2 (this captures
the PLAYER'S own flee, not an NPC's, so it does not need Stage 2's NPC-direction work at all).

- `flee_event` JSONL capture (5.6): stamina-at-flee, score-before/after, success/failure.

Soft-blocks Stage 7's Flee Economics tab: that tab's code has no dependency on this stage, but its
content is just the 3-anchor guess until rows from this stage have accumulated from real play.

### Stage 4a  -  Combat Rail: core (3-4 days)

Depends on Stage 1 (`PulseLayer`) and Stage 0 (widened panel). Recommended, not required, to follow
Stage 5 (see 8.1's "recommended-but-not-required" note  -  ships correctly without it, just inherits
the old perf bug in its history-dependent rows until Stage 5 lands).

- Revive `IsCombatExpanded` against the widened panel.
- Targets, race bars, the "why" line (3.8), the flee ladder (5.3-5.4).

Delivers: the flee-decision aid nothing today provides, and the targets/race view, without the
pursuit feature (that is 4b).

### Stage 4b  -  Combat Rail: pursuit (1-2 days, extends 4a)

**Hard-blocked on Stage 2.** Also depends on Stage 4a (extends the rail 4a already shipped).

- Pursuit block: candidates, exact pending command, blocked/available states (3.5/3.6).
- `Ctrl+E`/`Ctrl+Shift+E`/`Ctrl+G`/`Ctrl+Shift+G` (6.2), built on Stage 2's fled-candidate data.

Delivers: the loudest single complaint in `MECHANICS_NOTES.md` ("very annoying having to try and
find the flee message...") answered in full.

### Stage 5  -  incremental history index (2-3 days)

No dependencies  -  can start the same day as Stage 0/1/2/3/6. Independently valuable even before
Stage 4a ships, since it fixes the "lag returns over a session" symptom regardless of which surface
reads it, and is recommended to land before or alongside Stage 4a so the new rail does not inherit
the fixed perf bug in its own history-dependent content.

- Replace `ExcludingEncounterFrom(...).ToList()` + full rescans with the incremental `HistoryIndex`
  (7.3).

### Stage 6  -  Combat Lab: Overview + Weapons & Creatures (1-1.5 weeks)

No dependencies  -  can start the same day as Stage 0/1/2/3/5.

- Built entirely on data that already exists in `fights.jsonl` via `FightHistory.Summarize` /
  `SummarizeInstance` / `SummarizeByWeapon`  -  no new capture needed for these two tabs.

### Stage 7  -  Combat Lab: Flee Economics + Findings (1 week)

Code has no hard dependency; content is soft-blocked on Stage 3 (see 8.1) and benefits from, but does
not require, Stage 5's rollups for the Findings tab.

- The curve plot against real `flee_event` rows  -  the earlier Stage 3 starts capturing, the sooner
  this tab has anything beyond the 3-anchor guess to show. Findings card model, evidence ladder,
  trial helper.

### Stage 8  -  deeper capture, ongoing, explicitly blocked items (blocks nothing above; nothing above blocks it)

1. **Per-swing event stream in `fights.jsonl`.** Only aggregates persist today. Blocks swing-by-swing
   replay and blocks making the silent pass tick visible  -  the mechanic the owner is specifically
   curious about.
2. **Wield-refusal capture.** `"You cannot use the X to fight now!"` is parsed nowhere (zero
   observations). The stamina-gated strength threshold has no data without it.
3. **Stats snapshot at every weapon change and joiner start**, not only at encounter start  -  a
   fight that switches weapons mid-way misattributes all damage to the starting weapon.
4. **Held vs stowed inventory split.** The dexterity-vs-strength carry-load claim cannot be tested
   quantitatively without knowing which items were in a bag.
5. **Invisibility and sleep state.** Neither is modelled; sleep in particular silences ALL
   client-visible combat resolution, so any swing-level analysis will misread a sleep window as an
   impossible instantaneous stamina drop unless those fights are flagged and excluded.
6. **A character/persona field.** Alts currently pool together in every rollup.
7. **`raw_strength`/`raw_dexterity` freshness.** Stale-by-construction relative to the FES
   heartbeat (only refreshed by `sc`)  -  any comparison using them should say so.
8. **NPC value/points per group**, cached once outside combat (already recommended in
   `MECHANICS_NOTES.md`).

---

## 9. Open questions

Each stated as a question with a recommended default, so implementation is never blocked on an
answer only the owner can give.

1. **How long should a fled candidate stay pursuable?** Nobody has measured how long before an NPC
   wanders further. *Default: 15 seconds from the flee event, matching `DESIGN_LIVE_A`'s guess.
   Revisit once `flee_event`/pursuit-attempt data exists to check against.*
2. **Should the Combat Lab forcibly close or grey out if a fight starts while it happens to be
   open?** The owner's stated invariant is that it is "never open during combat" as a workflow
   fact, not necessarily an enforced one. *Default: no enforcement  -  leave it open but stale (it
   already never receives live updates); add a one-line banner "combat in progress in the main
   window" if the encounter starts while the Lab is focused, purely informational, no forced
   close/no auto-refocus that could itself violate Invariant #0 for a window that owns its own
   focus while open.*
3. **What happens when a batched pursuit command's movement leg fails** (owner flagged "unreachable"
   as a case to handle, but MUD2's refusal text for a blocked exit is not currently parsed)?
   *Default: accept the known risk described in 6.5 (the `k <name>` sub-command may run in the
   wrong room) for v1; add a `CombatEventKind` for movement refusal and abort the batched follow-on
   only if/when that text is captured and confirmed  -  track as a Stage 8 candidate.*
4. **Exact widened side-panel width.** 300dp (2.2) is a concrete recommendation sized to the
   wireframes in section 3, not a measurement of what "enough room" means on every display scale
   setting. *Default: 300dp; treat as adjustable in one place (`SidePanelWidthDp`) if real usage at
   different DPI settings shows the 40-column interior is too cramped or too generous.*
5. **Should the live ladder ever show a fifth row** (e.g. a second interpolated point between the
   current position and the next anchor, for a very slow multi-band fight)? Section 5.3 caps at
   four rows deliberately. *Default: no  -  cap holds at four. A wall of numbers defeats the point of
   a decision aid; the Combat Lab's Flee Economics tab is where more resolution belongs.*
6. **Does `Ctrl+G`/`Ctrl+Shift+G` (follow, no attack) still respect the pursuit-blocking rule even
   though it sends no attack command?** The domain rule ("cannot travel while fighting") is about
   movement, not attacking, so it should apply identically. *Default: yes  -  treat all four bindings
   as gated by the same "all fights in this encounter resolved" check; there is no reading of the
   domain notes where movement-only pursuit is exempt from the travel-while-fighting restriction.*
