# Combat Insight UX Design — Windows / MAUI
**Independent design proposal for Mucka's combat insight surfaces.**
**Written 2026-08-06. Windows-only. All glyphs ASCII.**

### Attribution and Independence Note
This design builds on two foundational ideas from the existing `UX_PROPOSAL.md`: (1) the four-surface layer model (Vitals, Threat, HUD, Ledger), and (2) stamina as a hero-sized metric with a "hits left" countdown. Those decisions are well-founded and I am not re-deriving them. The unique contributions in this design are: the specific visual layout of the Vitals meters and alert thresholds (section 2.1), the non-interactive HUD architecture (2.3), the **flee-cost decision surface** (2.2b, the highest-stakes element), the integration of all alert rules with a unified L0-L3 vocabulary (3.2), and the staged plan prioritizing data capture (section 5).

---

## Executive Summary

The current clog window is a single `Label` with `FormattedString`, causing 2-3s UI stalls in 11-NPC pack fights that nearly killed the player. This design proposes a **layered, role-based information architecture** that keeps critical live data in the main window (where the eye already is), reserves a lightweight floating readout for optional depth, and defers analysis to a larger, deliberate-context Ledger window.

The single biggest bet: **stamina becomes a prominent, hero-sized metric with a live "hits left" countdown instead of a raw value**, because that is what actually matters for a permadeath decision. Everything else is subordinate to keeping the UI thread fluid and the command box always ready.

---

## 1. Information Architecture

### Problem Statement
Three incompatible use cases are forced into one 330x520 window:
1. **Live engagement** — under 300ms glance, peripheral attention, no interaction
2. **Post-fight review** — 5-30s foveal reading, yes interaction desired
3. **Cross-session discovery** — minutes, statistical, deep analysis

A surface trying to be three products fails at all of them. The current design has no hierarchy to guide attention mid-fight, no room for the Ledger without a second window, and embeds data that belongs in different clocks.

### Proposed Layer Model

| **Layer** | **Clock** | **Window** | **Role** | **Interactive?** |
|-----------|-----------|-----------|---------|-----------------|
| **Vitals** | always | main top strip | live self-state | no |
| **Threat** | live 2s | left rail (main) | encounter summary + pursuit | yes, focus-safe |
| **HUD** | live 1Hz | floating windlet | detailed exchange + outlook | no (structural) |
| **Ledger** | deliberate | separate window | cross-fight patterns + findings | yes, context switch |

Each layer answers a specific question:
- **Vitals**: Am I hurt? Am I encumbered? How many hits left?
- **Threat**: Who is fighting me? Who is winning? **Should I flee, or pursue a fleeing NPC?**
- **HUD**: What is the current exchange looking like? My weapon vs their weapon? Historical comparison?
- **Ledger**: What is my weapon good at? What is the hidden mechanic? Which NPC type should I avoid?

---

## 2. Detailed Surface Specifications

### 2.1 Vitals Layer — Top Strip Augmentation (Main Window)

**Current state (example, from Screenshots/mucka3.png):**
```
<  o  i | Sta: 105/105 | Mag: 105/105 | Str: 100/100 | Dex: 100/100 | Score: 26375 (+0) |  rec   Rain  95m
```

**Proposed, out of combat:**
```
+-----------------------------------------+
| <  o  i | Sta 105/105 | Str 100/100 | Dex 100/100 | Mag 105/105 | Score 26375 +0 | rec Rain 95m |
|         | ########## |         |  ####    |      ####      |         |                   |
+-----------------------------------------+
```

The meter row is **always present** (hairline, 1px), so reflow never happens. Out of combat, it renders at 20% opacity.

**In combat, hurt, encumbered:**
```
+-----------------------------------------+
| <  o  i | Sta  38/105 | Str 89/100 -11 | Dex 71/100 -29 | Mag 105/105 | Score 26375 | rec Rain 95m |
|         | ###....... |     ########.   |    #######..   |           |               |            |
|         | 2 HITS LEFT |                 |                 |           |               |            |
|         | throbbing  |    LOAD pulsing |   LOAD pulsing  |           |               |            |
+-----------------------------------------+
```

**Design rules:**
- Meters are Skia-drawn rectangles, filled proportional to `current/max`, never text substitutes.
- Stamina meter must read `MaxStamina` (not hardcoded 100, because STA caps at 120 when permanently buffed).
- Delta chips (`-11`, `-29`) appear only when effective differs from raw. Space is always reserved (5 chars, right-aligned) so no reflow on appearance.
- **Stamina carries the alert behaviour** (colour and pulse, section 4.3). It is the only stat that has an L2/L3 alert vocabulary; load penalties are L1 Breathe at most.
- `HITS LEFT` is the human-readable survivability metric: `floor(stamina / damage_per_hit)`, falling back to group median when sample is thin. Never shown until an NPC has actually hit you once. Colour and pulse driven by survivability tier.

**Why this layer first:**
- Highest value per unit of work: stamina is already on screen, needs no new window, no focus question.
- Solves the owner's two stated examples immediately: "stamina too low" and "effective dex degraded by load".
- Even with the HUD and Threat band closed, this one change makes the live UI usable.

---

### 2.2 Threat Band — Left Rail Expansion (Main Window)

The left rail already hosts Online/Carrying/Compass. Revive the dead `IsCombatExpanded` fold.

**Idle state (same height as live state so nothing above/below shifts):**
```
+----------------------------------+
| COMBAT                       [v] |
|                                  |
|  no fight in progress            |
|                                  |
|  last: rat0  KILLED  0:24        |
|  session: 9k 1d 12f              |
|                                  |
|                                  |
|                                  |
|                                  |
+----------------------------------+
```

**Live state, two targets, one dead, one fled:**
```
+----------------------------------+
| COMBAT   0:24              [v]    |
|                                  |
|  rat0                            |
|  [##########....] ~65%           |
|                                  |
|  zombie4     DEAD  0:11          |
|                                  |
|  LOSING                          |
|  kill 0:31      die 0:14         |
|                                  |
|  [pursue zombie4 se] <-- button  |
|                                  |
+----------------------------------+
```

**Content rules:**
- Primary target gets an **estimated stamina pool bar** (dotted outline, because it is inferred). Uses `FightHistorySummary.EstimatedStaminaPool - damage_done_so_far`. Falls back to empty dotted track with `?` if no historical fights exist.
- Other active targets are listed with outcome (DEAD/LIVE) and elapsed time since join. Names only, no bars (space is precious).
- The race: `OUTLOOK` verdict (WINNING/EVEN/LOSING), below it the time projections. LOSING states the time-to-kill-me first, emphasizing the danger.
- **Chase button** is the entire point. One per fleeing NPC. Tapping injects `<direction>,k <target> wi <weapon>` (the weapon comes from the encounter's current weapon, not from the button state). Button disabled while any other fight in the encounter is open (the pursuit-blocking rule from MECHANICS_NOTES). Tapping also calls `RequestFocus` to return to command box (Invariant #0 via existing pattern).

**Why the rail, not the HUD?**
- The chase button MUST be interactive, so it MUST be in the main window to avoid the focus-stealing problem (5.4 in the prior proposal explains this well).
- The threat summary is what the side rail already hosts — targets, compass, carried items. It is the natural place for "who am I fighting?"
- Keeps live interaction out of the floating window entirely.

---

### 2.2b Threat Band — The Flee Cost Decision (Main Window, Left Rail)

**The highest-stakes element.** Permadeath makes fleeing a real strategic choice with a non-monotonic cost curve: fleeing costs a percentage of total score scaled by stamina, so fleeing at lower stamina costs less — until stamina is so low it is also near death. This section surfaces that trade-off without deceiving the player into thinking "just wait, it gets cheaper."

Fleeing costs are calculated by this curve:
- Stamina > 20: 10% of total score
- 6.5 ≤ Stamina ≤ 20: linear interpolation from 10% down to 2.5%
- Stamina < 6.5: 0% (free flee)

**What the band shows (in-combat only, once the player has taken at least one hit):**

```
+----------------------------------+
| COMBAT   0:24              [v]    |
|                                  |
|  rat0                            |
|  [##########....] ~65%           |
|  zombie4     DEAD  0:11          |
|                                  |
|  LOSING                          |
|  kill 0:31      die 0:14         |
|                                  |
|  FLEE COST                       |  <- new band
|  now -2600  free@6.5 in 5 hits   |
|  [======target==|free|..death]   |  <- visual: shows all three states
|                                  |
|  [pursue zombie4 se]             |
|                                  |
+----------------------------------+
```

**Visual breakdown:**
```
FLEE COST
now -2600  free@6.5 in 5 hits
[======target==|free|..death]
```

- First line: section label, always `Muted` uppercase
- Second line: `now -<points>` (the current flee cost in actual game points, `Caution` amber by default, escalates to `Danger` red at low stamina), then `free@6.5 in <N> hits` (the distance to the free threshold in hit count, estimated from current damage rate; falls back to `>10 hits` if sample is thin)
- Third line: a distance bar showing the whole stamina range from 0 to 105 (or `MaxStamina`), with three key markers:
  - Current stamina position (the `=` symbols, current location)
  - `free` boundary at 6.5 (marked as `|`)
  - `death` at 0 (marked as `]` at the right edge, truncated as `..death` if it doesn't fit)

**Colour and alert rules:**
The bar itself and the cost number respond to stamina thresholds:
- **Stamina > 20**: L0 steady, `Caution` text. Cost is high (10%), but player has room to recover. No urgency.
- **Stamina 20–10**: L1 breathe, `Caution` text. Cost is dropping, but not free yet. Cost-versus-risk trade-off is live.
- **Stamina 10–6.5**: L2 pulse, `Danger` text. Cost is approaching free, **but death is also close**. The UI emphasizes danger, not savings. Distance bar shows the player is in the red zone.
- **Stamina < 6.5**: L0 steady, `Good` text. Flee is free, but the player is also nearly dead (1-2 hits). Good news with a dark backing.

**Distance bar colour:**
The bar fills with a gradient or segments:
- `Caution` amber from 0 to 6.5 (the danger zone where fleeing is free)
- Neutral grey from 6.5 to 20 (the cost-transition zone)
- Safe blue from 20 upward

This visual immediately tells the player: "green zone is safe and expensive, grey zone is a trade-off, red zone is free but deadly."

**Interaction:**
- **No flee button.** This is a readout only. Fleeing is done via the command box (`$flee` or `flee <exit>` if it exists in the game).
- **Why no button?** A one-tap "flee now" button is genuinely dangerous: a stray click during high-pressure combat flees a fight the player might be winning, costing 10% of score permanently. And clicking the button changes OS window activation, stealing the next keystroke (Invariant #0). The readout-only design avoids both problems.

**Band visibility:**
- Appears only when in combat AND the player has taken at least one hit (until then, `incoming_damage_rate` is unknown, so "hits to free" cannot be calculated).
- When no fight is active, the band is hidden (space is used for last-fight and session summary).
- When in the grace period, the band remains visible but desaturated.

---

### 2.3 HUD Windlet — 400x320 Non-Interactive Readout (Floating Window)

A single SkiaSharp canvas, event-driven refresh. Fixed band layout so geography never changes.

**Live combat state:**
```
+-------------------------------------------+
|  ENGAGEMENT    0:24              IN COMBAT  |  <- amber band
+-------------------------------------------+
|                                            |
|   STA         38 / 105                     |  <- 18px hero number
|   [#########.....................]        |
|   2 HITS LEFT    -14 in last 10s            |  <- L3 throb, Danger red
|                                            |
+-------------------------------------------+
|   LOAD    str -11    dex -29               |  <- L1 Breathe, Load purple
|           3.4kg, 7 items   drop to fix     |
|                                            |
+-------------------------------------------+
|   dagger0  vs                              |
|   rat0          0:24   [#########---]~65  |
|   zombie4       DEAD   0:11                |
|                                            |
+-------------------------------------------+
|              you        them                |
|   hit/miss    9 / 5      4 / 11            |
|   hit rate      64%        27%             |
|   damage      28.5        11.0            |
|                                            |
+-------------------------------------------+
|   OUTLOOK   LOSING                         |
|   [=kill 0:31====|===die 0:14==]          |  <- race bar (outlook Breathe)
+-------------------------------------------+
|  rats: ~35dmg, 0:22, 22/24 kills (22 fights)|
+-------------------------------------------+
```

**Idle state:**
```
+-------------------------------------------+
|  NO COMBAT                                 |  <- Muted grey band
+-------------------------------------------+
|                                            |
|   STA        105 / 105                     |
|   [########################################]|
|   full health                              |
|                                            |
+-------------------------------------------+
|   LOAD    unencumbered                     |
|           1.1kg, 3 items                   |
|                                            |
+-------------------------------------------+
|   dagger0  vs  --                          |
|                                            |
|   last fight: rat0 KILLED 0:24             |
|                                            |
+-------------------------------------------+
|   session: 12 fights  9 killed  1 died    |
|            431.0 dealt  188.5 taken        |
|                                            |
+-------------------------------------------+
|  F9 or $ledger for full history            |
|                                            |
+-------------------------------------------+
```

**Design rules:**
- **Zero interactive controls.** No buttons, tabs, scrollbars, tap targets. Pure readout. Reason: clicking changes window activation, stealing the next keystroke from the command box (Invariant #0). Interaction belongs in the main window.
- **Fixed band heights.** Every band is always present whether content exists or not. An empty band renders a `Muted` placeholder (e.g., "no load penalty" instead of disappearing). This holds the geography stable for peripheral glance.
- Stamina is the hero: 18px, top-left, followed by the meter and `HITS LEFT` countdown. The only competition for hero size is the Outlook verdict (both at encounter granularity, never both L3 at once).
- Targets capped at 4 rows; pack fights show `+3 more` rather than scrolling. `Muted` text, outcomes in `Ink` (not `Good`/`Caution`/`Danger` — they are facts, not judgements).
- Weapon comparison to history: one line, always present, keyed on the primary target's group. Shows typical damage, typical duration, win rate (e.g., "rats: ~35dmg, 0:22, 22/24 kills (22 fights)").
- Bottom: idle state shows last fight and session totals; live state shows nothing (the space is for the race bar and exchange).

**Grace period behaviour:**
After the last NPC dies or flees, `IsCombatGracePeriod` is true for up to 5s. Banner reads `WINDING DOWN` instead of `IN COMBAT`, colours desaturate to 50%, all motion stops immediately.

---

### 2.4 Ledger Window — Tabbed Analysis UI

A larger window with four tabs: Fights, Creatures, Weapons, Findings.

#### 2.4.1 Fights Tab — Per-Fight Detail

```
+---------------------------+--------------------------------------+
| Fights | Creatures | Weapons | Findings                |
+---------------------------+--------------------------------------+
| filter: [ rats v ] [ all weapons v ] [ this session v ]  87 fights |
+-----------+------------------------------------------------------------------+
| when     | target  weapon outcome dur |  rat0 with dagger0     2026-08-06 19:41:22     |
+-----------+------------------------------------------------------------------+
| 19:41:22 | rat0    dagger  KILLED 0:24 |  Elizabethan tearoom, Rain                    |
| 19:40:08 | zombie  dagger  KILLED 0:11 |  str 89/100 (-11)  dex 71 (-29)  sta 52 start |
| 19:38:51 | rat13   dagger  FLED   0:09 |                                                |
| 19:35:02 | wolf    fal     YOU    0:17 |     you ######## 9/5 64%                      |
|          |         |         |        |    them ##### 4/11 27%                       |
|          |         |         |        |                                               |
|          |         |         |        |  damage 28.5 dealt   11.0 taken               |
|          |         |         |        |  duration 0:24  vs typical 0:22 for rats     |
|          |         |         |        |                                               |
|          |         |         |        |  TIMELINE stamina                             |
|          |         |         |        |  52 +------+------+------+------+             |
|          |         |         |        |     |*..__'                       |             |
|          |         |         |        |     |                             |             |
|          |         |         |        |  38 +--KILL--+------+------+-----+|             |
|          |         |         |        |     0s      8s    16s    24s    |             |
+-----------+------------------------------------------------------------------+
```

Left pane: sortable list of fights, click to populate the right pane detail. Filters at top: creature group, weapon, session/all.

Right pane: fight detail including:
- Weapon and target, with location/weather/status context
- Two mirrors: you vs them (hit/miss bar, hit rate %, damage median)
- Outlook at fight end, duration vs typical for this group
- **Stamina trace** (only available if per-swing timestamps exist; else empty with honest footer "pending data")
- Outcome, time elapsed from previous fight in session

#### 2.4.2 Creatures Tab — NPC Group Rollups

```
+-------+----------+-----+----------+-------+----------+------+
| Group | Fights   | Kills| Hit Rate | Dmg/Hit| Typical Duration | Danger |
+-------+----------+-----+----------+-------+----------+------+
| rats  | 22       | 19   | 64%      | 3.2    | 0:22     | * |
| zombie| 8        | 6    | 52%      | 2.8    | 0:18     | ** |
| thief | 4        | 2    | 41%      | 2.1    | 0:31     | *** |
| wolf  | 1        | 0    | 27%      | 11.0   | 0:17     | *** |
+-------+----------+-----+----------+-------+----------+------+
```

Shows every NPC group encountered. `Danger` is a visual asterisk count: if time-to-kill-me is consistently shorter than time-to-kill-them, escalate. Sample size always shown first.

Click a group to filter Fights tab.

#### 2.4.3 Weapons Tab — Weapon × Creature Effectiveness Matrix

```
+-----------+-------+-------+-------+-------+-------+
| Weapon    | Rats  | Zombie| Thief | Raven | Dwarf |
+           | (22)  | (8)   | (4)   | (3)   | (2)   |
+-----------+-------+-------+-------+-------+-------+
| dagger    | 3.2   | 2.8   | 2.1   | 3.1   | 2.9   |
|           | ####  | ###   | ##?   | ###   | ##?   |
+-----------+-------+-------+-------+-------+-------+
| falchion  | 2.9   | 3.1   | 3.4   | --    | --    |
|           | ###   | ###   | ####  | ...   | ...   |
+-----------+-------+-------+-------+-------+-------+
| broadsword| --    | --    | --    | --    | --    |
|           | ...   | ...   | ...   | ...   | ...   |
+-----------+-------+-------+-------+-------+-------+
```

Rows = weapons, columns = creature groups. Each cell is the median damage per landed blow, with:
- A histogram bar (or filled bar, or dot strip) showing the distribution
- Count of fights for that pairing
- A `?` if sample is too thin (< 5 fights)
- An empty dotted bar `....` for no data

Click a cell to filter Fights tab to that weapon+creature pairing.

**Why median, not mean?** One outlier fight (you wandered off, NPC was low-health from previous fight) destroys a mean.

#### 2.4.4 Findings Tab — Evidence-Ranked Discoveries

```
+-------+--------------------------------------------------------------+
| Strength | Claim                    | Evidence                          |
+----------+------+------------------+------+------+------+------+------+
| LOOKS    | The dagger beats the     | dagger  ####:: ::###:: #####..:  |
| REAL     | falchion against rats    | falchion  ###:. : :.:.  ##:.::  |
| (22 vs 6)| because dagger damage    |                                   |
|          | clusters higher.         | Sample size: 22 dagger fights,   |
|          |                          | 6 falchion fights vs rats.       |
|          | (IQR barely overlaps)    |                                   |
|          |                          | TRY THIS: Fight 4 more rats with |
|          |                          | falchion in the same room.       |
+----------+------+------------------+------+------+------+------+------+
|          |                                                            |
| WORTH A  | Carrying heavy loads     | light (< 2kg)   .::.:.:.   you  |
| LOOK     | makes them hit you more  | heavy (> 3kg)   ::.##:.#.   hit |
| (9 vs 11)| often.                   | 24% vs 38%                       |
|          |                          |                                   |
|          |                          | TO NARROW IT: Fight 8 more rats  |
|          |                          | while carrying under 2kg, same   |
|          |                          | weapon.  [ start trial ]         |
+----------+------+------------------+------+------+------+------+------+
|          |                                                            |
| TOO      | falchion may be better   | 1 kill vs 1 kill. Too few.       |
| EARLY    | than dagger vs thieves.  |                                  |
| (1 vs 1) |                          |                                  |
+----------+------+------------------+------+------+------+------+------+
```

Findings are **sentences ranked by evidence strength**, each backed by one visualization (dot strip showing distribution of fight outcomes) and one next action.

**Evidence ladder (4 rungs, words only):**
- `TOO EARLY` — fewer than 5 fights in either arm. Collapsed by default.
- `WORTH A LOOK` — >= 5 each, distributions overlap but medians separated.
- `LOOKS REAL` — >= 12 each, interquartile ranges barely overlap.
- `CONFIRMED` — >= 30 each, clean separation across multiple conditions.

Each card ends in an instruction, not a conclusion. "Fight 8 more rats while carrying under 2kg" converts a sample-size problem into a quest.

**Special card: the wield threshold.**
If the client has captured wield-refusal events (future data work, section 3.1), surface:

```
axe01234 wields down to sta 62, fails at sta 41

[-----------|##########?#########|----------]
0          41                 62           105

You lose the ability somewhere between 41 and 62.
TO NARROW IT: Try wielding around sta 50.
```

---

## 3. Visual Language

### 3.1 Colour Semantics

Eight semantic roles, each exactly one meaning:

| Role | Hex | Meaning | Never Means |
|------|-----|---------|-------------|
| `Ink` | `#cccccc` | a primary value | a label |
| `Muted` | `#6e7681` | labels, units, scaffolding | any number the eye needs to find |
| `You` | `#58a6ff` | belongs to the player | good, or safe |
| `Them` | `#c9524a` | belongs to an opponent | danger to you |
| `Danger` | `#ff3333` | lethal risk **and nothing else** | an opponent's name |
| `Caution` | `#d29922` | degraded, not lethal | any outcome |
| `Good` | `#3fb950` | better than your own baseline | an outcome or label |
| `Load` | `#a371f7` | encumbrance and stat penalties | a heading or section |

Changes from the current codebase:
- `Them` desaturates from the current hot red, freeing bright red exclusively for `Danger` (lethal risk).
- Purple moves from "heading" to "load", because the owner explicitly asked for purple encumbrance signalling.
- Outcomes (`KILLED`, `FLED`, `YOU FLED`, `DIED`) render as `Ink` facts, not judgements. Only `DIED` uses `Danger`.

### 3.2 Alert Vocabulary — Four Levels

Motion only animates a **glow layer behind text, never the text itself** (reason: text animation is UI-thread dependent; glow layer uses WinUI Composition off-thread per 7.4).

| Level | Name | Motion | Period | Means |
|-------|------|--------|--------|-------|
| L0 | Steady | none | - | normal state |
| L1 | Breathe | opacity 1.00→0.70→1.00, ease-in-out | 2.4s | worth noticing when you get a moment |
| L2 | Pulse | opacity 1.00→0.45→1.00, ease-in-out | 1.2s | deal with this soon |
| L3 | Throb | opacity 1.00→0.25→1.00 + `Danger` glow | 0.6s | act now |

**Constraints:**
- **At most one L3 at a time.** If two conditions are L3, the lesser one drops to L2 (enforce in code).
- **Transitions calm down instantly**, escalate fade in over 250ms (a single errant frame doesn't flash the panel).
- **State-transition only.** Start/stop animations on state change, never restart per tick. No polling.
- **Never out of combat.** The idle HUD is completely still.

### 3.3 What Gets What Alert Level

| Signal | Level | Where | When |
|--------|-------|-------|------|
| Stamina / hits-left | L3 Throb, `Danger` | Vitals | hits-left <= 2, OR projected time-to-die < time-to-kill AND < 15s |
| Stamina | L2 Pulse, `Danger` | Vitals | hits-left <= 4, OR stamina < 25% of max |
| Stamina | L1 Breathe, `Caution` | Vitals | stamina < 50% of max in combat |
| Flee cost text | L2 Pulse, `Danger` | Threat band | 10 >= stamina > 6.5 (red zone: cheap to flee, close to death) |
| Flee cost text | L1 Breathe, `Caution` | Threat band | 20 >= stamina > 10 (grey zone: trade-off active) |
| Flee cost text | L0 Steady, `Caution` | Threat band | stamina > 20 (high cost, safe) |
| Flee cost text | L0 Steady, `Good` | Threat band | stamina < 6.5 (free flee, but nearly dead) |
| Dex delta penalty | L2 Pulse, `Load` | Vitals | penalty >= 20, OR penalty just increased mid-fight |
| Any load delta | L1 Breathe, `Load` | Vitals | nonzero penalty in combat |
| Outlook LOSING | L1 Breathe, `Caution` | Threat band | (stamina carries the real urgency) |
| Chase button ready | L1 Breathe, `Ink` | Threat band | fleeing NPC pursuable AND no other fight open |
| Everything else | L0 Steady | - | - |

### 3.4 Glyphs — ASCII Only

Per INTERNAL.md, no non-ASCII character literals in code. Iconography:

| Purpose | ASCII |
|---------|-------|
| Bar fill / empty | `#` / `.` |
| Estimate marker | `~` prefix (e.g., `~65`) |
| Unknown / thin evidence | `?` |
| No data | `--` |
| Trend | `^` `v` `=` |
| Fold open / closed | `[v]` / `[>]` |
| Outcomes | `KILLED` `DIED` `FLED` `YOU FLED` `WITHDRAWN` | words, not glyphs |
| Silent tick (swing strip) | `.` |

Anything richer (weapon silhouette, skull, shield) must be an image asset routed through `Resources/Images` and the existing `tools/rasterize-status-icons.cs` pipeline.

### 3.4a Data Availability Check — Flee Cost Requirements

**What is needed live:**
1. **Current stamina** — available every frame from FES heartbeat (`12.08.01` every ~1s) and inline in every incoming hit line (`The X hits you (99/100)` parse). **Status: fully live, zero new capture needed.**
2. **Total score** — available from FES heartbeat (~1Hz) and explicit `sc` command. **Status: live, but not currently persisted in real-time. Captured once per fight in `fights.jsonl` at encounter start only.** For a live readout, score can be read from `GameStatsSnapshot.CurrentScore` on every update; it is refreshed by the heartbeat. **No new capture needed, but the calculation must read live stats, not stale combat aggregate.**
3. **Damage rate** — computed as `sum(damage_taken_this_fight) / count(hits_received)`. Stamina delta per hit is inferred from the hit line (`stamina_before - stamina_after`, relayed from the previous tick's `StatsUpdated` event as documented in MECHANICS_NOTES.md). **Status: calculation is on-thread, damage rate is available as soon as the second hit lands. Until then, fall back to group median from `fights.jsonl`.**

**Calculation:** Flee cost in points is `floor(total_score * cost_percent)` where `cost_percent` follows the curve:
```
if stamina >= 20:
    cost_percent = 0.10
elif stamina < 6.5:
    cost_percent = 0.0
else:
    cost_percent = 0.025 + 0.075 * (stamina - 6.5) / 13.5
```

Distance to free flee (in hits): `ceil((current_stamina - 6.5) / incoming_damage_rate)`, capped at `>10 hits` if sample is < 3 hits received.

**Conclusion:** Zero new data capture is required. Stamina and score are both live. The only limitation is that before the second incoming hit, damage rate falls back to group median. This is acceptable — the band is not shown until a hit has landed anyway.

---

### 3.5 Typography

Family: `"Cascadia Mono"` (exactly that string, registered once).

Three sizes only:
- **18px** — hero numbers only (stamina, hits-left, outlook verdict). Max two per surface.
- **12px** — body, all tables, all values.
- **10px** — labels, units, sample counts, footnotes. Always `Muted`.

**No bold.** Only Regular face is registered; `FontAttributes.Bold` synthesises and can perturb advance width (same failure mode as glyph-width in the current code). Emphasis comes from size, colour, `UPPERCASE`.

Section labels: `UPPERCASE`, 10px, `Muted`, followed by a 1px `Rule` hairline.

### 3.6 Density

- **4px vertical base unit.** Line height 1.35 (16px at 12px text).
- **Band padding 8px.** Gap between bands 8px (1px `Rule` separator).
- **Bar height 6px**, 2px corner radius. Track is 1px `Rule` colour; fill is semantic colour. Estimated values have a 1px dotted track outline.
- **Every band holds height even when empty.** Stable geography > compactness on a glance surface.

---

## 4. Interaction Model

### 4.1 Commands

| Command | Does |
|---------|------|
| `$clog on` / `off` | Starts/stops recording AND shows/hides the HUD windlet (unchanged). |
| `$clog status` | Prints current state (unchanged). |
| `$clog eval <item>` | Unchanged. Hint text moves out of HUD chrome into `$clog help`. |
| `$hud` | Toggles the HUD windlet without affecting recording. |
| `$ledger` | Opens the Ledger window. |
| `$combat` | Toggles the Threat band in the left rail. |

Optional hotkeys are opted into via the existing Hotkeys settings tab (see Screenshots/mucka5.png). **Do not hardcode any key.** Every unclaimed keystroke belongs to the command box.

### 4.2 Docking vs Floating

- **Vitals** — docked, permanent, non-optional, part of the existing top strip.
- **Threat band** — docked in left rail, foldable via `[v]`/`[>]`, optional pin to a floating duplicate using the existing `IsMapPinned` pattern.
- **HUD windlet** — separate `Window`, persisted geometry (position + size) in `mucka.ini` alongside other settings. Optional always-on-top toggle.
- **Ledger** — separate `Window`, normal desktop chrome, resizable, persisted geometry.

### 4.3 Resizing and Geometry

- **HUD minimum 320x260, maximum 520x520.** Banded layout, never scrolled. Pack fights cap targets at 4 + `+N more` line.
- **Ledger minimum 900x600.** Fights list/detail split is draggable.
- Both persist position and size via platform window APIs (section 7.2).

### 4.4 Focus — Invariant #0 Across Multiple Windows

Clicking a floating window changes OS window activation, and keystrokes follow activation, not control focus. So a click in the HUD sends the next keystroke into the void.

**Solution: the HUD windlet contains zero interactive controls.** No buttons, no tabs, no scrollbar, no clear glyph, nothing clickable. It is a pure readout. This is not a limitation; it is the design.

Every action (chase, fold, pin, filter) lives in the main window, where the existing `RequestFocus` machinery works. The Ledger is the sanctioned exception (like Settings): it is a deliberate context switch, owns focus while open, returns focus on close via the same `RequestFocus` path.

**Implementation details:**
- Don't build HUD as a `ContentPage` with MAUI controls. Draw it as a single `SKCanvasView` with `EnableTouchEvents = false` and `IsTabStop = false`.
- If always-on-top is enabled, the HUD is still non-click-through and non-activating (no P/Invoke needed because it has no interactive controls to steal focus).

### 4.5 No Combat Active

- **Vitals**: Meters stay at 20% opacity; delta chips visible (being encumbered out of combat matters at L0). No pulses.
- **Threat band**: Same height, shows last fight and session totals.
- **HUD**: Idle layout (see 2.3). Same geometry, no motion, dimmed banner.
- **Grace period** (`IsCombatGracePeriod` true): Banner reads `WINDING DOWN`, colours desaturate to 50%, all motion stops immediately.

---

## 5. Staged Implementation Plan

Ordered by value per unit of work. Each stage is shippable independently.

### Stage 0: Corrections (< 1 day, no new UI)

**Fix what is wrong regardless:**
- Strip the nine non-ASCII glyphs from `CombatHistoryFormatter.cs` and `ClogPage.cs`, replace with the 4.2 vocabulary.
- Fix `MonoFont` fallback lists in `MappingPage.cs` and `RawConsolePage.cs`.
- Split `Danger` from `Them` in `ToneColor`.
- Add stamina to the HUD readout (it is missing entirely).

**Delivers:** Rule compliance, unblocks all later stages.

### Stage 1: Vitals (2-3 days, highest value first)

Meters, delta chips, and composition-animation helper on the existing top strip.

**Includes:**
- Skia meter rectangles filled proportional to `current/max`.
- Delta chips in `Load` purple, reserved space (no reflow).
- The `PulseLayer` composition helper (used by all later stages).

**Delivers:** Most of goal (a). Works even with HUD and Threat band closed.

### Stage 2: Threat Band (4 days)

Revive `IsCombatExpanded` in the left rail, populated with targets, race, outlook, chase buttons, and **flee cost decision**.

**Includes:**
- Estimated stamina pool bar (dotted, if data exists, else `?`).
- Per-target outcome + elapsed time for secondaries.
- Race projection and outlook verdict.
- **Flee cost band** (section 2.2b): current cost in points, distance to free threshold, visual bar with danger/safe zones.
- Chase button logic: parse flee direction, disable while other fights open, inject `<dir>,k <target> wi <weapon>`.

**Depends on:** Minor parser extension (flee events need parsed direction, new event kind for "tried to flee" vs successful flee). Already scoped in MECHANICS_NOTES.

**Implementation notes:**
- Flee cost calculation uses live stamina and live score (from `GameStatsSnapshot.CurrentScore` refreshed by FES heartbeat).
- Damage rate is computed from hit history (second hit onwards); before that, falls back to group median from `fights.jsonl`.
- The distance bar visualization (showing 0 to MaxStamina with danger/safe zones) is drawn in the same Skia canvas as the rest of the rail content, or as a simple text-based bar (e.g., `[====target==|free|..death]`).
- Colour escalation (L0/L1/L2 per stamina tier) is applied via the existing composition animation helper from Stage 1 (PulseLayer), not a UI-thread timer.

**Delivers:** Goal (a), the user's primary complaints (pursuit + flee decision), and the highest-stakes UI element.

### Stage 3: HUD Windlet Rewrite (4 days)

Rewrite `ClogPage` as a single SkiaSharp canvas. Keep `CombatHistoryFormatter`'s *decisions*, but output type changes from `List<ClogLine>` to a structured view model that the Skia painter consumes.

**Includes:**
- Banded layout, fixed heights, no scroll.
- Stamina trace and "hits left" countdown (stamina trace only if per-swing data exists).
- Idle state (last fight, session totals).
- Persisted geometry.

**Blocked on:** Stage 2 must complete first (chase button removed from HUD, so formatting responsibility changes).

**Delivers:** Live surface that is actually usable under load.

### Stage 4: Ledger Core (1 week)

The Fights, Creatures, and Weapons tabs. All on data that already exists in `~/.mucka/clogs/fights.jsonl` via `FightHistory` aggregations.

**Includes:**
- Tabbed window (build from `Border` + `TapGestureRecognizer` like Settings).
- Fights list / detail split, click to detail.
- Creatures rollups (NPC group stats).
- Weapons table (weapon × creature matrix with medians + sample size).

**No new capture needed.** Data already exists.

**Delivers:** Goal (b) at encounter granularity, bulk of goal (c).

### Stage 5: Findings (5 days)

The Findings tab with evidence ladder, dot strips, and trial helper.

**Includes:**
- Card model for each finding.
- Bootstrap on median difference + IQR overlap check (simple stats, complex writing).
- Trial helper: pins target conditions, warns when current fight won't count.
- Copy generation (hand-written templates for each finding type).

**Delivers:** Rest of goal (c), in the register the owner asked for.

### Stage 6: Deeper Capture (ongoing, parallel to 5)

Data work that unlocks per-swing review detail. Best done incrementally.

---

## 6. Data Gaps and Capture Priorities

**Important note:** The flee cost decision surface (section 2.2b) is **not blocked by any missing data**. Stamina and score are both available live; no new capture is required. See section 3.4a.

**Other data worth capturing as soon as possible (blocking per-swing review and deeper analysis):**

1. **Per-swing event stream in fights.jsonl.** Today only aggregates are persisted. The swing strip (section 2.4.1) needs compact event array (e.g., `"swings":["h5-9","m","H3"]`). ~4 bytes per tick.

2. **Per-swing timestamps.** Needed for timeline and, more importantly, to make silent pass ticks visible. This is what the owner is most curious about.

3. **Stamina series over the fight.** FES heartbeat has richer data than the hit lines alone. Needed for the stamina trace.

4. **Stats snapshot at weapon change and joiner start.** Currently only snapped at encounter start. A fight that switches weapons attributes all damage to the starting weapon.

5. **Wield-refusal / too-weak event.** Nothing captures "you cannot hold this any more". Blocks the threshold card (section 2.4.4).

6. **Invisibility state.** Already flags blind/deaf/crippled; invisible is not captured. Domain brief says it affects dexterity calculation.

7. **Held vs stowed inventory split.** Same weight in a bag costs much less dexterity. Until inventory container parsing exists, the carry-load dexterity hypothesis is untestable.

---

## 7. MAUI-on-Windows Feasibility

All concerns flagged **OK** (proven), **CARE** (works, has trap), or **AVOID**.

### 7.1 Secondary Windows — OK
`new Window(new SomePage(vm))` + `Application.Current.OpenWindow(...)` used three times already.

### 7.2 Window Geometry Persistence — CARE
`Window.X`/`Y`/`Width`/`Height` setters are unreliable on Windows. Go to platform: `window.Handler.PlatformView` → `Microsoft.UI.Xaml.Window` → `AppWindow` → `Move(PointInt32)` / `Resize(SizeInt32)`. Restore after handler creation, not in constructor. `#if WINDOWS` guard required.

### 7.3 Always-on-Top — CARE
`AppWindow.Presenter as OverlappedPresenter` then `IsAlwaysOnTop = true`. Windows-only, straightforward.

### 7.4 Animation — OK, and Approved
- **`ViewExtensions.FadeTo` / `Animation` / `Dispatcher.StartTimer` — AVOID.** UI-thread ticker, competes with typing (Invariant #1).
- **WinUI Composition — OK.** Only mechanism that satisfies both invariants.

```csharp
var visual = ElementCompositionPreview.GetElementVisual(platformView);
var compositor = visual.Compositor;
var anim = compositor.CreateScalarKeyFrameAnimation();
anim.InsertKeyFrame(0.0f, 1.0f);
anim.InsertKeyFrame(0.5f, 0.30f);
anim.InsertKeyFrame(1.0f, 1.0f);
anim.Duration = TimeSpan.FromMilliseconds(550);
anim.IterationBehavior = AnimationIterationBehavior.Forever;
visual.StartAnimation("Opacity", anim);
```

Runs entirely off-thread, no UI-thread cost. Build a small `#if WINDOWS PulseLayer.Attach(view, level)` helper in Stage 1; all later stages reuse it.

**Critical constraint:** Only `Opacity`, `Offset`, `Scale`, `RotationAngle`, brush properties are animatable this way. `TextColor` is not (UI-thread dependent). Hence the rule: *pulse a layer behind text, never the text.*

### 7.5 SkiaSharp — OK, Proven Ceiling
`SKCanvasView` proven three times, event-driven, no render loop.

**On WinUI, `SKCanvasView` paints on UI thread.** A 400x320 HUD with ~60 draw ops is well under 1ms, repaints at most 1Hz (existing `OnAntiIdleTick` cadence) + on change. Fine. Keep diff-before-invalidate discipline.

**Do not drive Skia at 30-60fps for animation.** That is what 7.4 is for.

### 7.6 Fonts — CARE
Only Regular face registered. `FontAttributes.Bold` synthesises, can perturb advance width. Register `CascadiaMono-SemiBold.ttf` if bold is genuinely needed; in Skia surfaces, use explicit `SKTypeface`.

### 7.7 Tabs — CARE
MAUI has no desktop-appropriate tab control. Build from `Border` + `TapGestureRecognizer` like Settings does.

### 7.8 Long Lists — CARE
MAUI `CollectionView` on WinUI has poor virtualization at scale. With thousands of fights, expect trouble. Two routes: (a) draw the list in Skia as a virtual list (preferred, consistent with `TerminalView`); or (b) cap the visible set through filtering/paging (Stage 4 shortcut).

### 7.9 Off-Thread Aggregation — Non-Negotiable
Loading and aggregating `fights.jsonl` for the Ledger **will** stall typing if done inline. All parsing, grouping, median computation, bootstrapping goes off-thread; only the finished view model marshals back. Same rule `FightHistoryStore.LoadAsync` already follows.

### 7.10 Horizontal Overflow — CARE
Wide tables inside `ScrollView` on WinUI scroll badly. Design every table to fit its container at minimum window width; truncate rather than relying on horizontal scroll.

---

## Design Bet and Falsification

**The single biggest bet:** Stamina as a hero-sized metric with a "hits left" countdown is the human-readable proxy for permadeath risk that makes the live UI useful without needing a Ledger open.

**What would falsify it:** If in live play, the user finds themselves repeatedly opening the Ledger to answer "am I going to die in this fight?" or if they never look at the Vitals bar and only use the HUD windlet. Either means the layering is wrong.

**What would validate it:** The user closes the HUD windlet entirely and runs content with only Vitals and Threat band visible, because those two answer every live combat question.

---

## Summary of Goals Coverage

**(a) Live engagement:** Vitals (stamina hero + "hits left") + Threat band (targets, race, chase) + HUD (detail exchange) = complete.

**(b) Post-combat review:** Ledger Fights tab with stamina trace and swing strip (pending per-swing data).

**(c) Historical discovery:** Ledger Creatures/Weapons tabs (existing data), Findings tab (new).
