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

**The health phrase** (`looks critically injured`) is drawn **overlaid on the pip row**,
centred, in the terminal's monospace with a shadow for legibility - the pips are shorter
than the text and read behind and around it. Echoing the game's own wording anchors the
panel to what the player just read in the scroll.

Staleness: the ladder only updates when you land a hit (player hit rate 0.57). A one-tick
gap is normal (68% of gaps); fade to **stale at 3 ticks**, to **unknown at 5**.

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

**Weapon (centre).**
- Sword icon + weapon name, or open-hand icon + `UNARMED`. **Never the word "armed"** - the
  weapon name already says it.
- Below it, the **alternate weapon**: hotkey chip left, name right-aligned. **`Ctrl+W`**
  (consistent with `Ctrl+F` flee, `Ctrl+E` chase, `Ctrl+G` follow). The handler must mark the
  key event handled so it never reaches a default close-window action.
- Hovering the alt-weapon shows a terse comparison. How the alternate is chosen is out of
  scope for now; assume a sensible comparison exists.

**Unarmed timing.** `wield` is **per-engagement, not sticky** - every encounter starts with
nothing in hand until stated. So an unarmed opening is *normal*: stay calm for the first
ticks, go amber only **after damage has landed**, and never straight to red.

## 6. Tick meter and encounter gauge

- Ember's tick, at the very bottom, **pale and dim** - grey/white, low opacity. It is a
  timer, not a judgement, so **no colour coding and no label**. A small drawn metronome mark
  is permitted; text is not.
- Two exceptions only: **red at stamina <= 30**, **glow at stamina <= 20**.
- The **encounter gauge is drawn over the tick**, sharing its pixels - not above, below or
  beside it.
- Count format: **up to 5 crossed-swords marks, plus `+N` for the remainder.** Nine opponents
  renders 5 swords and `+4`; fourteen renders 5 swords and `+9`. **Not zero-padded** - `+4`,
  never `+04`. Two digits is the practical ceiling.
- The swords must read as weapons, not as a letter x - crossguards and pommels.

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

Being **in combat at all** is signalled by the **application window border** turning red and
pulsing slowly, and optionally a red outline on the input box - never by a large coloured
block inside the rail. A giant glow just because a fight started is a distraction.

Pulse is a very dark red, slow. (Owner's note read as roughly RGB 16-24; one designer
reported that range is near-invisible on a dark UI, so this needs a look on real hardware.)

## 9. Amendments after v9

1. **Slot count is dynamic** (section 3) - v9 hard-coded four.
2. **Encounter gauge format** (section 6) - v9 rendered `09`; correct is 5 swords + `+4`.

## 10. Explicitly out of scope / never build

- Flee cost, flee statistics, points at risk, or a "free to flee" band **in any form**.
  The player knows fleeing hurts; a price tag at the decision moment is cognitive burden.
  `FleeCostLadder` is retained as documented domain knowledge and drives no UI.
- The fled-NPC / chase surface. Nothing can be done about it mid-fight, so showing it then is
  cognitively antagonistic. It belongs to a post-combat view.
- Anything labelled "Exits" - the word means something else in MUD.
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
