# Combat Panel Design

Appearance and content of the rail are specified in `combat-rail-spec.md`; this document keeps the
decisions, the window policy, the alert tiers, the keybindings and the performance contract.

## Decisions

D1. There is no separate clog window; live combat content is the Combat Rail (D3) and nothing else.

D2. No live-combat surface may float. Everything live-horizon lives docked in the main window, in
    a new, additional panel on the RIGHT edge - never in a second window. Only an analysis view
    (never open during combat) may be a separate floating window.

D3. The Combat Rail is a separate panel docked on the RIGHT edge of the main window, with its own
    width constant. The existing left panel (Online/Items/Map, `SidePanelWidthDp` = 228dp) is
    untouched - same width, same content. Showing the Combat Rail widens the WINDOW by the panel's
    own width; the terminal and the left panel never resize, never reflow. The window never
    resizes itself automatically - not when a fight starts, not when it ends - only on the explicit
    show/hide toggle.

D4. `Ctrl+F` (flee) and `Ctrl+Shift+F` (flee in the typed direction) are the flee bindings.

D5. Flee cost is shown as the flee pill (`combat-rail-spec.md`, section 5), priced by the formula
    in `MUD2-flee-cost.md`. No automated "you should wait" advice is ever generated.

D6. Cost-framing never overrides survival-framing. Below `CriticalStaminaThreshold` (6.5 stamina,
    where a flee is free) the alert tier does NOT de-escalate to a calm tone - the player is 1-2
    hits from permadeath regardless of what fleeing costs. See "Hard floor" below.

D7. Render surface for all live combat content is a single SkiaSharp `SKCanvasView` embedded in
    the Combat Rail panel, not discrete WinUI controls (`Label`/`FormattedString`/`CollectionView`)
    and not a second `SKCanvasView` window.

D8. Pulse/glow motion runs via WinUI Composition (`ElementCompositionPreview` +
    `ScalarKeyFrameAnimation` on `Opacity`) on a layer positioned BEHIND the Skia canvas, never via
    a UI-thread timer and never by animating text colour directly.

D9. Historical summaries (median damage/hit-rate/duration per npc_group, per instance, per weapon)
    are maintained incrementally as each fight closes, never recomputed by rescanning the full
    fight corpus.

D10. Colour semantics reuse `Rendering/TerminalTheme.Palette` (the Campbell palette) by index,
     promoted normal-to-bright the same way `TerminalTheme.Foreground` promotes bold text. Any hex
     value outside the palette is a named exception in `combat-rail-spec.md`.

D11. ASCII-only iconography (`#`/`.` bars, `~` estimate marker, `[v]`/`[>]` fold state, plain words
     for outcomes). No unicode glyphs, escaped or literal.

D12. Flee decisions are ABSOLUTE: incoming damage per tick against the player's actual stamina. No
     fraction-of-maximum scaling and no creature rung ladder takes part in them.

D13. Cognitive-load tenets for the whole live combat panel: (a) the player's attention belongs on
     the terminal window directly above the input box, not buried in the panel; (b) the panel must
     impose minimal cognitive load - fixed, pre-allocated layout with no reflow (new elements may
     appear, everything else holds still), unambiguous colour, alarms that work without reading.

## Window policy

The panel's width is `Mucka.Util/Mucka.Combat/CombatRailResize.cs`: `CombatPanelContentWidthDp` (376) plus
the border stroke on each side (`CombatPanelWidthDp`). `SidePanelWidthDp` = 228 is the LEFT panel's
constant and is never merged with it. The toggle is `SidePanelViewModel.ToggleCombatPanelCommand`
(overflow menu), persisted as `ClientSettings.ShowCombatRail`. Showing the panel widens the main
window by exactly the panel width via `AppWindow.Resize`; hiding it shrinks the window back by the
same amount. The terminal's column count and the left panel's width are never touched by the
toggle. The window never resizes itself outside this one explicit toggle. Once shown, the panel
populates and updates as combat happens; it does not appear, grow, or shrink on its own.

## Roster rules

- Rows are capped at `ParticipantRoster.MaxRows` (8); the number actually drawn is derived from the
  window height (`RailSlotGeometry`).
- Live participants always sort ahead of resolved ones before the cap is applied. When live count
  alone exceeds the cap, no resolved row appears; the header count and the hidden-tail line are the
  only things that still convey the dead.
- The hidden-tail line always distinguishes "more, still fighting" from "more, already down"
  (`RosterPlan.HiddenLiveCount`).
- The current target row is brightest and bold; other live rows bright, normal weight; resolved
  rows dim and struck through.
- Bold is used for exactly: the urgency headline, `UNARMED` when no weapon is in hand, the roster
  count header, and the current-target row. Nothing else on the panel is bold.

## After a fight

- The result banner persists until the next fight starts or the player dismisses it. No timed
  self-clear.
- No urgency tier, outlook detail, or flee pricing renders post-combat; those are survival
  projections and go silent when `InCombat` is false. The roster and the stamina gauge stay, since
  they describe fact rather than projecting one.

## Palette roles

`Rendering/TerminalTheme.Palette`, indices 0-15 (the Campbell theme):

| Role | Palette index (normal/bright) | Means |
| --- | --- | --- |
| Ink | 7 / 15 | a primary value |
| Muted | 8 | labels, units, sample counts |
| You | 6 / 14 | belongs to the player |
| Them | 1 | an opponent's identity - not a danger signal alone |
| Danger | 1 -> 9 | lethal risk: the enemy's hue, promoted |
| Load | 5 / 13 | encumbrance, self-inflicted stat penalties |
| Caution | 3 / 11 | degraded, not lethal |
| Good | 2 / 10 | beating your own historical baseline |

## Alert tiers

Three STATE tiers. There are no event (one-off flash) tiers.

| Tier | Colour move | Motion | Duration | Meaning |
| --- | --- | --- | --- | --- |
| T1 | normal hue | none | while true | worth noticing on your own time |
| T2 | bright hue | none | while true | worth noticing soon |
| T3 | bright hue | glow pulse, `Blink.PulsePeriodMilliseconds` (1200 ms) | while true | act now |

Rules:

- At most one T3 element at a time (`CombatTierResolver.ResolvePulseTier`). If two conditions
  qualify, the most urgent (lowest time-to-die) wins the pulse; the other renders T2. Tie-break:
  stamina always wins, because it is the only signal that directly ends the encounter in death.
- Escalating transitions fade in over 250 ms; calming transitions stop instantly.
- No motion at all outside combat.
- Motion is always a glow/opacity layer behind text via WinUI Composition (D8), never the text's
  own colour.

### What triggers what

| Signal | Tier | Condition |
| --- | --- | --- |
| Stamina / hits-left | T3 | hits-left <= 2, or projected time-to-die < 15 s and shorter than time-to-kill |
| Stamina | T2 | hits-left <= 4, or stamina < 25% of max |
| Stamina | T1 | stamina < 50% of max, in combat |
| Strength delta chip | T2 | effective strength < 50% of max |
| Strength delta chip | T1 | effective strength < 75% of max |
| Dexterity delta chip | T1 | any nonzero penalty, in combat |
| Unarmed | T2 | whenever current weapon is null and a fight is live |

### Hard floor

At or below `CriticalStaminaThreshold` (6.5 stamina) the urgency tier renders at no less than T2,
regardless of what the table above computes from hits-left or projected time-to-die. The table may
still promote it to T3; nothing may render it below T2 while stamina sits at or under the free-flee
threshold. `CombatTierResolver.CriticalStaminaFloorTier` implements the floor.
`SurvivalStaminaThreshold` (20) is the other risk-tier boundary used by the flee pill.

## Keybindings

| Binding | Sends |
| --- | --- |
| `Ctrl+F` | `flee` |
| `Ctrl+Shift+F` | `flee <typed direction>`, or bare `flee` if the input box is empty |
| `Ctrl+W` | wield the alternate weapon |

Each binding refocuses the input box after sending.

## Performance contract

An 11-NPC pack fight once rebuilt 200+ native WinUI spans per combat event and delayed combat text
by 2-3 seconds. A throttle bounds render FREQUENCY, not render COST, and does nothing about a
history lookup whose cost grows across a session; hence D7, D8, D9 and the rules below.

### Render surface

A single `SKCanvasView` inside the Combat Rail (D7). All text, bars and gauges are Skia draw calls
against a FIXED layout: a capped number of rows (urgency headline; stamina gauge; roster rows up to
`ParticipantRoster.MaxRows` plus a hidden-count footer; the flee pill, one row; pursuit block)
whose draw-call count depends only on that cap, never on total participant count or total
historical fight count. On WinUI, `SKXamlCanvas` paints ON the UI thread - acceptable because the
draw-call count is bounded and the canvas is invalidated only on genuine state change.

Pulse/glow (D8): a WinUI element positioned BEHIND the canvas in the same grid cell (canvas
background transparent), driven by `ElementCompositionPreview.GetElementVisual` plus a
`ScalarKeyFrameAnimation` on `Opacity`, started/stopped only on tier transitions, and torn down in
the host's `OnHandlerChanged` when `Handler` is null. A live Composition animation on a destroyed
visual crashes the process with `RO_E_CLOSED` (`0x80000013`), so teardown is not optional. This is
the ONLY mechanism producing continuous motion anywhere on the panel; the canvas is never asked to
animate. `Rendering/PulseLayer.cs` is the implementation.

The recent-swing ring buffer bound is `FightAccumulator.RecentSwingCapacity` (6): two fixed
`SwingOutcome[]` arrays per fight, O(1) to write and capped regardless of fight length. The strip
never draws more than 6 columns per side.

### Work budget

| When | What runs | Cost |
| --- | --- | --- |
| Per combat event (fires on the Feed thread; a pack fight can emit several lines per tick) | Update the relevant `FightAccumulator` counters and the swing ring buffer; set a dirty flag. Nothing else. | O(1), independent of history size |
| Per UI tick (at or below `ClogRenderGate`'s ~4-5 Hz cap) | If dirty: rebuild the small immutable `CombatLiveView`; compare with the last-rendered one by value; if changed, `InvalidateSurface()` once. | Bounded by the fixed layout |
| Per encounter (fight close) | Persist the `FightRecord` off-thread; update the in-memory incremental `HistoryIndex` for that npc_group/instance/weapon bucket. | Off the UI thread; only the finished summary is marshalled back |

### History summaries stay incremental

`HistoryIndex` is built once, off-thread, at startup, holding per-npc_group, per-instance and
per-(weapon, npc_group) buckets of the values needed for their medians. On fight close, that
fight's values are inserted into its buckets (binary-search insertion). On encounter start, the
`CombatHistoryContext` for the primary target is looked up once and cached for the encounter;
never re-queried per event or per tick. Because the index is only updated when a fight fully closes
and flushes, the in-progress encounter's own rows are never in it, so self-comparison is
structurally impossible.

### Never on the UI thread

- File or database I/O of any kind.
- A rescan of the full fight corpus for any median or summary.
- Native view creation/teardown proportional to participant count.
- `Task.Wait()` / `.Result` or any synchronous blocking call.
- Repeating timers driving opacity or colour; all continuous motion is Composition, started and
  stopped on transitions only.

### Never on the Feed thread

- Anything that blocks on the UI thread.
- File or database I/O; persistence is queued onto a background writer.
- History lookups beyond the O(1)/O(log n) incremental update. The Feed thread's job for combat is:
  classify the line, update counters, set dirty. Only the UI tick reads the cached history context.
