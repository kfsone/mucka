# Combat Insight UI  -  Final Design Spec

Status: **AGREED. Implement as written.** Windows only. Written 2026-08-06, reconciling
`DESIGN_LIVE_A.md` (base skeleton), `DESIGN_LIVE_B.md` (harvested improvements), and
`UX_PROPOSAL.md` (origin) against the owner's explicit decisions below. Every glyph in this
document, and every glyph an implementer writes into code, is plain ASCII
(`INTERNAL.md`: "Models which use non-ascii characters in code will be rejected."). No
application code was changed to produce this document. This is not a menu of options  -  build
what is written here.

**AMENDMENT (2026-08-07)  -  read this before implementing sections 3 and 5.** The first
implementation pass built the render MECHANISM this document specifies (a real `SKCanvasView`,
D8) but never actually composed the CONTENT for it - it drew the old text formatter's output
verbatim, so the survivability read this whole design exists to deliver was never drawn as its own
thing (the owner, after a 14-rat fight: "I had no idea how close I was to dying or losing this
fight (sta down to 20)"), and the flee-cost ladder (section 5), meant as a decision aid, shipped as
a PERMANENT block occupying half the panel (owner: "the bottom half of this page is filled with a
list of the cost to flee???? That's a bit like a big shiny poster at a Hematology clinic labelled
'How soon you'll be dead! Get to know your cancers'."). This amendment supersedes the affected
parts of sections 3 and 5 with:
- A new **threat indicator** (D14, section 4.7) as the panel's organising element - a bold, glowing
  "DEATH IN &lt;n&gt;S" style headline - replacing the flee ladder in that role.
- The flee-cost ladder (section 5) demoted from a permanent 4-row block to **at most one line**,
  shown only when fleeing is a live decision. The underlying 3-anchor math (D6/5.1/5.2) and the hard
  floor (D7/4.4) are UNCHANGED and remain settled - only the RENDERING changed, not the model.
- An **opposition roster** (section 3, amended wireframes) that always states the live/dead split
  and total count, never a truncated name list with no breakdown (the "5 dead rats and 9 more"
  failure case).
Everything else in this document (D1-D13, sections 1-2's surface inventory, section 6's keybindings,
section 7's performance contract, section 8's staged plan) is unaffected and remains settled.

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
| D2 | **No live-combat surface may float.** Everything live-horizon lives docked in the main window, in a new, additional panel on the RIGHT edge  -  never in a second window. Only the Combat Lab (analysis, never open during combat) is a separate floating window. | Two owner reasons: (a) a secondary window steals OS focus, violating Invariant #0, and in permadeath a lost keystroke can cost the character; (b) on Windows, a secondary window is not reliably restored alongside the main one after alt-tab. Read-only construction (as `DESIGN_LIVE_A`'s "The Watch" and `DESIGN_LIVE_B`'s "HUD windlet" both proposed) fixes reason (a) but not (b)  -  so floating live surfaces are rejected regardless of interactivity. |
| D3 | **CORRECTED  -  supersedes an earlier draft of this document.** The Combat Rail is NOT the existing left-edge side panel widened. It is a **new, separate, additional panel docked on the RIGHT edge** of the main window, with its own width constant. The existing left panel (Online/Items/Map, `SidePanelWidthDp` = 228dp) is untouched  -  same width, same content, forever. Showing the Combat Rail widens the WINDOW by the new panel's own width; the terminal and the left panel never resize, never reflow. The window never resizes itself automatically  -  not when a fight starts, not when it ends  -  only on the explicit `$clog on`/`$clog off` toggle (see 2.2). | Owner correction: an earlier draft of this document said to widen the existing 228dp rail to 300dp and dock the combat surface there. That is wrong  -  that panel is the player's existing Online/Items/Map panel and must remain exactly as it is. This is a PvP / asynchronous game: "you don't want to be playing around with your live window in a way that might get you killed." A working layout that changes shape under the player's hands is itself a hazard, whether the change comes from a keypress that touches the wrong panel or from the window quietly resizing itself the moment a fight begins. The new panel is strictly additive, appears only on a deliberate keypress, and never grows or shrinks on its own. |
| D4 | `Ctrl+F` (flee) / `Ctrl+Shift+F` (flee in typed direction) are unchanged, already implemented, already correct. | `Pages/GamePage.xaml.cs:1315-1319`, wired via `KeyboardAccelerator` -> `GameViewModel.Flee()`/`FleeThen()` -> `_conn.SendLine` + `RequestFocus?.Invoke()`. Proven pattern; the new bindings in D5 reuse it exactly. |
| D5 | Four new bindings: `Ctrl+E` chase+attack most-recent fleer, `Ctrl+Shift+E` chase+attack first fleer of the encounter, `Ctrl+G` follow (movement only) most-recent fleer, `Ctrl+Shift+G` follow (movement only) first fleer. All four are inert (no-op, no error) until unblocked. | Full spec in section 6. |
| D6 | The flee-cost curve is 3 anchor points (>20 sta = 10%, 6.5 sta = 2.5%, <6.5 sta = free). The shape between anchors is an explicitly-labelled GUESS (linear interpolation), never presented with the same visual weight as a measured value. No automated "you should wait" advice is ever generated. | Owner: this is not a measured function. `DESIGN_LIVE_A`'s stepped ladder + explicit guess-marker is correct; `DESIGN_LIVE_B`'s smooth interpolated point value is not  -  it implies precision that does not exist. |
| D7 | Cost-framing never overrides survival-framing. Below 6.5 stamina (free flee), the alert vocabulary does NOT de-escalate to a calm tone  -  the player is 1-2 hits from permadeath regardless of what fleeing costs. | Owner: `DESIGN_LIVE_B` made exactly this mistake (a `Good` tone below 6.5 sta). Explicit rule in section 4.4. |
| D8 | Render surface for all live combat content is a single **SkiaSharp canvas** embedded in the new Combat Rail panel on the main window's right edge, not discrete WinUI controls (`Label`/`FormattedString`/`CollectionView`) and not a second `SKCanvasView` window. | The measured failure mode: an 11-NPC pack fight rebuilt 200+ native WinUI spans per event and stalled the UI thread 2-3s. A canvas draws fixed-layout primitives whose cost never scales with participant/history count; see section 7. |
| D9 | Pulse/glow motion runs via WinUI Composition (`ElementCompositionPreview` + `ScalarKeyFrameAnimation` on `Opacity`) on a layer positioned BEHIND the Skia canvas, never via a UI-thread timer and never by animating text colour directly. | Invariant #1. Composition animation costs zero UI-thread time once started; `SKXamlCanvas` itself paints ON the UI thread on WinUI, so it must never be the thing doing the animating. |
| D10 | Historical summaries (median damage/hit-rate/duration per npc_group, per instance, per weapon) are maintained **incrementally** as each fight closes, never recomputed by rescanning the full fight corpus. | The measured failure mode: `ExcludingEncounterFrom(...).ToList()` plus three median passes over the ENTIRE corpus on every cache miss, and misses happen on every fight resolution  -  cost grows across a whole session. See section 7.3. |
| D11 | Colour semantics reuse `Rendering/TerminalTheme.Palette` (the Campbell palette) by index, promoted normal-to-bright the same way `TerminalTheme.Foreground` already promotes bold text. No new hex values anywhere in this design. | Verified against `Rendering/TerminalTheme.cs`: `Palette[0..15]` is the exact Campbell table both draft designs cited. One palette across terminal and combat surfaces, not a second one drifting apart (as `ClogPage.ToneColor`'s GitHub-dark set already has). |
| D12 | ASCII-only iconography (`#`/`.` bars, `~` estimate marker, `[v]`/`[>]` fold state, plain words for outcomes). No unicode glyphs, escaped or literal. | Per `INTERNAL.md`. Note for implementers: `ViewModels/SidePanelViewModel.cs` currently returns the literal code-point escapes `u25bc` and `u25b6` from `CombatFoldGlyph`/`PanelToggleGlyph` (triangle glyphs), plus one literal non-ASCII character in `PanelToggleGlyph`'s other branch - do not copy that pattern into any new code this design adds; fixing the existing instances is Stage 0 (section 8). |
| D13 | The Combat Lab (analysis) is a separate floating window, normal chrome, resizable  -  explicitly allowed to float because it is never open during combat. | Owner: "A separate large ANALYSIS window may still float, because it is never open during combat." |
| D14 | **NEW (2026-08-07 amendment).** The panel's organising element is a threat indicator (a bold, colour-and-glow-escalating "DEATH IN &lt;n&gt;S" style headline), not the flee-cost ladder. The ladder is demoted to at most one line, shown only when fleeing is a live decision (losing, or stamina already degraded). The opposition list always states the live/dead split and total count alongside its capped row list. | Owner, after a 14-rat fight where the panel never surfaced how close he was to dying: "rather than a flee decision, we need a threat indicator gauge of some kind -- or a 'DEATH IN &lt;n&gt;S' label or something simple. Bold text. Gently glowing at first getting angrier as it gets likelier." And, on the ladder's prominence: "the bottom half of this page is filled with a list of the cost to flee???? That's a bit like a big shiny poster at a Hematology clinic labelled 'How soon you'll be dead! Get to know your cancers'." Prominence is not permanence. |

---

## 2. Surface inventory

Two horizons only  -  Live and Analysis  -  because D2 removes the third ("peripheral, floating,
read-only") horizon both draft designs proposed.

### 2.1 Status Strip  -  main window, top bar, docked, always present

Augments the existing `<  o  i | Sta ... | Score ...` top strip. Owns: stamina/str/dex meters
with delta chips, and the single live flee-cost headline figure. Zero new window. Visible at all
times; contents dim to 20-25% opacity out of combat.

### 2.2 Combat Rail  -  main window, NEW right-edge panel, docked, own width, toggled

**CORRECTED  -  supersedes an earlier draft of this document that said to revive
`IsCombatExpanded` / `CombatFoldGlyph` / `ToggleCombatCommand` as a fold WITHIN the existing left
rail.** That was wrong for the reason given at D3: the left rail is the player's existing
Online/Items/Map panel and must not carry combat detail, must not change width, and must not
change content. The Combat Rail is instead a **new, separate panel docked on the RIGHT edge** of
the main window, with its own visibility flag and its own width constant (implementers: this
reuses the `IsCombatExpanded` name's INTENT  -  "is the combat surface showing"  -  but the
property itself belongs to the new panel's own state, not to a fold inside `SidePanelViewModel`'s
left-rail section group). This is now the ONE live combat surface beyond the Status Strip  -  it
absorbs everything a floating "Watch"/"HUD" would otherwise have carried, because D2 forbids that
surface from existing at all.

Owns: the threat indicator (D14/4.7), the opposition roster (live/dead split + count, capped row
list), race/outlook, at most one condensed flee line (D14/5, shown only when fleeing is a live
decision), the plain-language "why" line, unarmed and NPC-weapon alerts, the pursuit/chase block
(candidates + exact pending command), recent-swing strip, weapon-vs-history table, and (out of
combat) last-fight + session totals.

**Concrete sizing and toggle.** The new panel has its own width constant, **300dp** (~10 more
monospace columns than the left rail, at the panel's own 12px Cascadia Mono), defined next to
(never merged into) `Pages/GamePage.xaml.cs`'s existing `SidePanelWidthDp = 228.0`  -  that constant
is the LEFT panel's and stays at 228 forever. Showing the panel (`$clog on`, or its own toggle)
widens the main window by exactly this panel's width via `AppWindow.Resize`; hiding it
(`$clog off`) shrinks the window back by the same amount. The terminal's column count and the left
panel's width are never touched by this toggle. **The window never resizes itself outside this one
explicit toggle**  -  not when a fight starts, not when it ends, not on any combat event  -  because
a window that visibly changes shape at the exact moment a fight begins, with no keypress behind it,
is its own kind of distraction, and in this permadeath PvP game an unexpected layout change is a
hazard the owner explicitly called out. Once shown, the panel simply populates and updates as
combat happens; it does not appear, grow, or shrink on its own. All wireframes in section 3 assume
a 40-column interior (300dp / ~7.2px per char, minus border padding).

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
  participant rows; the flee line, when shown at all, is a single row - D14) before its numbers are
  ever clipped. A cut-off number is worse than a missing row.

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
| Threat indicator ("DEATH IN &lt;n&gt;S", D14) | | yes (headline) | |
| Opposition roster (live/dead split + count) | | yes | |
| Stamina, absolute + trend | yes | yes (gauge) | |
| Flee-cost-right-now figure | yes (headline) | yes (single line, conditional - D14) | yes (calibration) |
| Risk verdict / outlook (winning/losing) | small | yes (supporting detail) | |
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
| COMBAT  0:14                          |
|                                        |
| falchion vs rat0                      |
|                                        |
| WINNING                               |   <- D14: 16px, BOLD, calm green, no glow
|                                        |
| sta ###############... 92/105         |   <- always visible, right under the headline
| winning        kill 0:31              |   <- supporting detail, small/muted
|                                        |
| 1 enemy   1 live   0 down             |   <- D14: count + split BEFORE the row list
|  rat0                       live      |   <- current target: brightest, bold
+----------------------------------------+
```

### 3.4 Combat Rail  -  pack fight (14 NPCs, capped at 5 rows)

```
+--------------------------------------+
| COMBAT  0:52                          |
|                                        |
| dagger0 vs rat3                       |
|                                        |
| DEATH IN 19S                          |   <- D14: 16px, BOLD, bright red, GLOW PULSING
|                                        |
| sta #####........... 31/105           |
| losing        die 0:19   kill 1:40    |
|                                        |
| 14 enemies   9 live   5 down          |   <- the direct fix for "5 dead rats and 9 more"
|  rat3                       live      |   <- current target, brightest/bold
|  rat5                       live      |
|  rat7                       live      |
|  rat9                       live      |
|  rat11                      live      |   <- live sorts first; with 9 live > the 5-row cap,
|  and 9 more (4 still up)              |      ALL 5 shown rows are live - none of the 5 dead
|                                        |      NPCs get a row at all here (they are entirely
| low dmg: fighting bare handed         |   <- why-line     inside the 9-more hidden tail).
|                                        |
| flee now  ~9%  -1845 pts              |   <- single line, only because losing/low sta
+--------------------------------------+
```

See 3.4a below for the mixed case (a live count small enough that a dead row DOES earn a slot in
the capped list, showing the dim/struck-through treatment directly).

- Each row is one draw call against a fixed-layout table (max 5 rows + "and N more"), never one
  native control per NPC  -  this is the direct fix for the 200+-span pack-fight stall. See
  section 7.
- Live participants always sort ahead of resolved ones before the cap is applied (matches the
  pre-existing `CombatHistoryFormatter.OrderedTargets`/`Build_ParticipantCapDropsResolvedFightsBeforeLiveOnes`
  convention exactly - not a new rule). When live count alone exceeds the row cap, as here, no
  resolved row can appear in the list at all; the header count and the hidden-tail line are the
  ONLY things that still convey the 5 dead. This is intentional (a pack fight's still-live targets
  are never the ones sacrificed to make room), but is worth knowing before assuming every state
  shows a dead row.
- `and 9 more (4 still up)` - the roster's hidden-tail line ALWAYS distinguishes "more, still
  fighting" from "more, already down" (`ParticipantRoster.HiddenLiveCount`/`HiddenResolvedCount`) -
  the exact information the reported "5 dead rats and 9 more" case could not convey.
- `low dmg: fighting bare handed` uses the rule table in 3.8.

### 3.4a Combat Rail  -  pack fight, mixed (14 NPCs, 4 live - a dead row now earns a slot)

Same encounter size as 3.4, different composition (4 live, 10 already resolved instead of 9/5) -
small enough that the row cap has room for a resolved row, which is where the live/dead VISUAL
treatment (dim, struck-through vs bright, bold) actually becomes visible in the list itself rather
than only in the header counts:

```
+--------------------------------------+
| COMBAT  0:52                          |
|                                        |
| dagger0 vs rat3                       |
|                                        |
| DEATH IN 19S                          |
|                                        |
| sta #####........... 31/105           |
| losing        die 0:19   kill 1:40    |
|                                        |
| 14 enemies   4 live   10 down         |
|  rat3                       live      |   <- current target: brightest, bold
|  rat5                       live      |   <- other live: bright, normal weight
|  rat7                       live      |
|  rat9                       live      |
|  rat12          (dim, struck through) killed
|  and 9 more                           |   <- all 9 hidden are already down - no "(N still up)"
+--------------------------------------+
```

- `rat12`'s row renders dim and struck through (`RosterRow.IsLive == false`) exactly like every
  other resolved row - the owner's instruction: "the dead npcs need to be clearer - dimmer, the
  active ones brighter/bolder". The other 9 resolved NPCs never get a row at all (the cap is 5),
  but since none of THEM are live either, the hidden-tail line correctly omits the "still up"
  qualifier entirely.

### 3.5 Combat Rail  -  unarmed, encumbered, losing, one NPC fled and pursuable

```
+--------------------------------------+
| COMBAT  0:41                          |
|                                        |
| UNARMED vs zombie4                    |   <- UNARMED renders bold, Danger-toned
|                                        |
| DEATH IN ~2 HITS                      |   <- D14: 16px, BOLD, bright red, GLOW PULSING
|                                        |
| sta ##.............. 12/105           |
| losing         die --   kill --       |
|                                        |
| 1 enemy   1 live   0 down             |
|  zombie4                    live      |
|                                        |
| low dmg: fighting bare handed         |   <- why-line (priority 1 - no weapon)
| load str -11  200g 7obj               |   <- encumbrance, always-visible per owner's list
|                                        |
| flee now  FREE                        |   <- FREE, but tone stays no calmer than the tier above
|                                        |
| ZOMBIE4 FLED se, 0:04 ago             |
| Ctrl+E: chase and re-attack           |
| > se,k zombie4                        |
+--------------------------------------+
```

- `UNARMED` renders in the current-weapon slot in the alert colour, bold  -  this directly answers
  "unarmed combat, highlighted."
- `flee now  FREE` renders in Danger tone, never Good/calm  -  D7/4.4's hard floor: a free flee at
  12 stamina is not good news, and the colour must say so even though the number is friendly.
- `> se,k zombie4` is the LITERAL command `Ctrl+E` will send  -  no weapon clause because the
  player is unarmed (see section 6.2).

### 3.6 Combat Rail  -  pursuit BLOCKED (another fight still open)

```
+--------------------------------------+
| COMBAT  1:03                          |
|                                        |
| falchion vs rat3                      |
|                                        |
| STEADY                                |   <- D14: calm, no elevated tier from either target
|                                        |
| sta ################ 88/105           |
| too close      die 0:41   kill 0:38   |
|                                        |
| 2 enemies   2 live   0 down           |
|  rat3                        live      |
|  rat13                       live      |
|                                        |
| rat3 fled n, 0:22 ago                 |
| blocked - finish current fight        |
| > n,k rat3    (Ctrl+E when clear)     |
+--------------------------------------+
```

- The pending command is still SHOWN (never hidden) but rendered muted/dim, with the reason
  spelled out in words, per owner instruction: "these must be inert and visibly disabled until
  the engagement fully resolves." The moment `rat13` also resolves, this block brightens and the
  hint changes to an active `Ctrl+E` prompt exactly like 3.5.

### 3.7 Combat Rail  -  post-combat (result banner + session)

```
+--------------------------------------+
| COMBAT  0:41                          |
|                                        |
| falchion vs zombie4                   |   <- weapon/target context still shown (never gated
|                                        |      on InCombat - matches the old headline's own rule)
| sta ###############... 94/105         |   <- still visible; no threat/outlook lines - projecting
|                                        |      a FINISHED fight's death clock would be a lie
| 1 enemy   0 live   1 down             |
|  zombie4              dim,struck  killed
|                                        |
| + killed zombie4                      |   <- review tail starts here (D14: BuildReview)
|                                        |
| last: zombie4     KILLED     0:41     |
|        28.5 dealt / 11.0 taken        |
|                                        |
| session  13 fights  10 killed         |
|          1 died       2 fled          |
+--------------------------------------+
```

- Result banner persists until the next fight starts or the player dismisses it (no auto-erase  - 
  this was a real complaint about the old window's 8-second self-clear).
- No threat indicator, outlook detail, why-line, or flee line post-combat - all four are survival
  PROJECTIONS, which go silent once `InCombat` is false (D14). The roster and stamina gauge stay,
  since they describe fact rather than projecting one.

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
flee-cost line's colour/tier NEVER de-escalates to Good/calm on that basis alone.** ("The FLEE COST
block" below means the single condensed line D14/section 5 renders live, `FleeSummaryLine` -  this
rule predates that rendering change and applies to it unaltered.) That line's tone is driven by the
STAMINA tier table above (4.3), completely independent of what the
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

- One face: `"Cascadia Mono"` exactly.
- **AMENDED (2026-08-07, D14).** This section originally said "no bold anywhere - only the Regular
  face is registered, and a synthesised bold perturbs monospace advance width... emphasis comes
  from the tier table, never weight." That is correct for MAUI `Label`/`FormattedString` (a
  SYNTHESISED bold FONT does perturb advance width - the exact bug class that broke `ClogPage`'s
  column alignment once already) but the Combat Rail is a Skia canvas, not a `Label`. On a canvas,
  bold is faked by stroke-and-fill (draw the glyph fill, then stroke its outline at a small width)
  rather than by substituting a differently-metriced bold typeface - this does NOT change the
  glyph's measured advance width, so the column grid stays intact. `Rendering/TerminalView.cs`
  already uses exactly this technique for intense terminal text
  (`SKPaintStyle.StrokeAndFill` + a stroke width derived from the font size -
  `TerminalFont.BoldStrokeWidth`), proven elsewhere in this codebase before this design used it.
  The owner explicitly asked for "bold text" on the threat indicator; this is how that is achieved
  without reopening the old bug. Emphasis on the Combat Rail therefore comes from BOTH the tier
  table's colour/motion AND weight (bold) for the small, deliberately limited set of elements listed
  in 4.7 - never weight alone, and never on ordinary body/review text.
- Three sizes: 16px for the one threat-indicator headline (4.7), 12px body/tables/opposition roster,
  10px muted labels/sample counts/outlook detail.
- 4px vertical rhythm unit, 1.35 line height. Every band holds its height whether populated or
  not  -  no reflow between idle and live states. Numeric columns right-align.

### 4.7 The threat indicator (D14) - the panel's organising element

**Supersedes the flee-cost ladder's role as the thing the eye lands on first (D14).** A single, bold,
16px headline at the top of the Combat Rail, directly under the weapon/target line and above
everything else live - "big, bold, unmissable", per the owner. Glow escalates with likelihood: gentle
at first, angrier as death approaches. The label and its tier are resolved by
`MudSharp.Combat.ThreatIndicator.Resolve` (mudsharp/Combat/ThreatIndicator.cs), deliberately
NOT a second set of numeric thresholds - it reuses `CombatTierResolver.StaminaTier` (4.3), the same
tier already driving the shared pulse layer, so the headline and the glow can never disagree about
how urgent the moment is.

| Tier | Colour move | Motion | Trigger (from `CombatTierResolver.StaminaTier`, 4.3) | Example label |
| --- | --- | --- | --- | --- |
| Critical | bright Danger red | glow pulse (T3 - the only pulsing tier) | hits-left <= 2, or a sub-15s die projection faster than the kill | `DEATH IN 14S` (prefers a seconds figure; falls back to `DEATH IN ~N HITS` when only a hit count is known) |
| Danger | bright Danger red, static | none | hits-left <= 4, or stamina < 25% of max | `~4 HITS FROM DEATH` (or `STAMINA LOW` with no hit figure) |
| Caution | normal-bright Caution amber, static | none | stamina < 50% of max (T1); OR stamina is healthy but the outlook verdict alone already reads Losing | `STAMINA DROPPING` (T1) or `LOSING` (outlook-only) |
| Safe | calm Good green, static | none | nothing elevated | `WINNING` or `STEADY` |
| Idle | not drawn | none | no encounter, or the encounter has ended (`!InCombat`) - projecting a finished fight's death clock would be a lie | (nothing) |

The stamina gauge (an ASCII `#`/`.` bar, per 4.5) is drawn immediately below the headline, always
visible whenever a stamina reading exists - it is the direct input to the indicator above it, and
its absence from the first implementation is the exact incident that prompted this amendment (owner,
after a 14-rat fight: "I had no idea how close I was to dying or losing this fight (sta down to
20)"). See 4.5's own `#`/`.` convention; no new iconography.

**The complete list of elements that render bold** (Rendering/CombatPanelCanvasView.cs): the threat
headline itself; `UNARMED` in the weapon/target line, whenever no weapon is in hand (owner's
standing requirement - "unarmed combat highlighted"); the opposition roster's count header
(`N enemies   N live   N down`); and the current-target row within the roster (the one live NPC the
player is actually trading blows with - "the active ones brighter/bolder"). Nothing else on the
panel is bold - ordinary body/review text stays Regular weight exactly as 4.6 originally specified.

---

## 5. The flee-cost ladder

**AMENDED (2026-08-07, D14) - read this before implementing 5.3/5.4.** Everything below in 5.1/5.2
(the 3-anchor model, the honest linear-interpolation guess) is UNCHANGED and still exactly how the
cost figure is computed (`MudSharp.Combat.FleeCostLadder`, untouched by this amendment - still
directly unit-tested by `FleeCostLadderTests`). What changed is RENDERING ONLY: the live Combat Rail
no longer draws the full 4-row ladder (5.3) or the risk-paired second line (5.4) as a PERMANENT
block. The owner's own words: "the bottom half of this page is filled with a list of the cost to
flee???? That's a bit like a big shiny poster at a Hematology clinic labelled 'How soon you'll be
dead! Get to know your cancers'." Prominence is not permanence.

The live Combat Rail now renders **at most one line** (`SidePanelViewModel.FleeSummaryLine` /
`BuildFleeSummaryLine`), and only when fleeing is actually a live decision - losing on the outlook
projection, OR stamina already sits at CombatTierResolver.StaminaTier T1 or above (i.e. anything the
threat indicator, 4.7, would already be flagging as at least Caution). Silent otherwise - a flee
line on a healthy, winning fight would be exactly the "always-present row nobody reads" 3.8's own
framing rule already warns against. The single line still shows the "now" figure with D6's honest
`~` prefix when interpolated, and D7/4.4's hard floor still applies: the line's COLOUR never reads
calmer than Warn once stamina sits at or under the free-flee threshold, however cheap or FREE the
number itself has become.

5.3's stepped 4-row table and 5.4's risk-paired "next band" line are **not deleted from the design,
only from the LIVE view** - the underlying calculations (`FleeCostLadder.BuildLadder`,
`FleeCostLadder.HitsToNextBand`) remain exactly as specified below and remain available for the
Combat Lab's Flee Economics tab (3.9, Stage 7), which has room to show the full curve without
competing with anything live for the eye. Read 5.3/5.4 below as "what the full model can produce",
not as "what the Combat Rail renders every frame."

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

### 5.3 The ladder  -  stepped, at most four rows, never a smooth curve (full model - Combat Lab only after D14; the live Combat Rail renders a single line instead, see the amendment above)

```
flee now              10%   -2637      <- current position, ALWAYS first row, ~ if between anchors
flee at 20 sta         10%   -1845      <- anchor: known, not a guess
flee at 6.5 sta        2.5%   -659      <- anchor: known, not a guess
below 6.5 sta               FREE       <- anchor: known, not a guess
```

A smooth curve is deliberately rejected for the live view  -  continuous implies a confidence that
does not exist. The full curve, with uncertainty shown as a shaded band, belongs only in the Combat
Lab's Flee Economics tab (3.9), where there is room to explain it.

### 5.4 The risk-paired second line (never advice; computation retained, no longer rendered live after D14 - see the amendment above)

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
and gauges are Skia draw calls against a FIXED layout: a capped number of rows (the threat headline;
the stamina gauge; the opposition roster's 5 participant rows + a hidden-count footer, D14; the
condensed flee line, at most 1 row after D14; 1 why-line; 1 pursuit block) whose draw-call count
depends only on that fixed cap, never on total participant count or total historical fight count.
On WinUI, `SKXamlCanvas` paints ON the UI thread  -  acceptable here specifically because the
draw-call count is bounded and the canvas is only invalidated on genuine state change (7.2), never
per-frame.

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

Wiring: the Combat Rail's page-equivalent (whatever hosts the new `SKCanvasView` in the new right-edge
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
Stage 0 (creates the new panel + toggle) --+

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
- Add the new Combat Rail panel (own width constant, e.g. `CombatPanelWidthDp = 300.0`, defined
  separately from  -  and never merged into  -  the left rail's own `SidePanelWidthDp`, which stays
  at 228 untouched) and its show/hide toggle wired to `$clog on`/`$clog off`, resizing the window by
  exactly that width via `AppWindow.Resize` on toggle only (D3/2.2). Do NOT change
  `SidePanelWidthDp` or the left panel's `Border.WidthRequest`  -  that panel is explicitly out of
  scope for this design.

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

Depends on Stage 1 (`PulseLayer`) and Stage 0 (the new panel + toggle). Recommended, not required, to follow
Stage 5 (see 8.1's "recommended-but-not-required" note  -  ships correctly without it, just inherits
the old perf bug in its history-dependent rows until Stage 5 lands).

- Build out the new Combat Rail panel's content against the show/hide state from Stage 0.
- Targets, race bars, the "why" line (3.8), the threat indicator and opposition roster (4.7, D14),
  and the condensed flee line (5, D14 - superseded from the originally-planned full ladder).

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
4. **Exact Combat Rail panel width.** 300dp (2.2) is a concrete recommendation sized to the
   wireframes in section 3, not a measurement of what "enough room" means on every display scale
   setting. *Default: 300dp; treat as adjustable in one place (the new panel's own width constant,
   separate from the left rail's `SidePanelWidthDp`) if real usage at different DPI settings shows
   the 40-column interior is too cramped or too generous.*
5. **Should the live ladder ever show a fifth row** (e.g. a second interpolated point between the
   current position and the next anchor, for a very slow multi-band fight)? Section 5.3 caps at
   four rows deliberately. *Default: no  -  cap holds at four. A wall of numbers defeats the point of
   a decision aid; the Combat Lab's Flee Economics tab is where more resolution belongs.*
6. **Does `Ctrl+G`/`Ctrl+Shift+G` (follow, no attack) still respect the pursuit-blocking rule even
   though it sends no attack command?** The domain rule ("cannot travel while fighting") is about
   movement, not attacking, so it should apply identically. *Default: yes  -  treat all four bindings
   as gated by the same "all fights in this encounter resolved" check; there is no reading of the
   domain notes where movement-only pursuit is exempt from the travel-while-fighting restriction.*
