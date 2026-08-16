# Combat UX proposal

Design proposal for Mucka's combat surfaces. Windows only. Written 2026-08-05.

Everything in this document is ASCII, including every glyph proposed for use in code, per
`INTERNAL.md` ("Models which use non-ascii characters in code will be rejected").

Scope note: this is a design document. No application code was changed.

Contents:

1. [Honest critique of the current clog window](#1-honest-critique-of-the-current-clog-window)
2. [Information architecture](#2-information-architecture)
3. [Layouts and wireframes](#3-layouts-and-wireframes)
4. [Visual language spec](#4-visual-language-spec)
5. [Interaction model](#5-interaction-model)
6. [Staged implementation plan](#6-staged-implementation-plan)
7. [MAUI-on-Windows feasibility](#7-maui-on-windows-feasibility)

---

## 1. Honest critique of the current clog window

Reference: `Pages/ClogPage.cs` (217 lines), `ViewModels/CombatHistoryFormatter.cs` (618 lines),
`ViewModels/ClogDisplay.cs`, opened from `GamePage.xaml.cs:OnOpenClogWindowRequested` as a
330x520 `Window`.

First, credit where it is due. The *content* decisions in `CombatHistoryFormatter` are mostly
right and hard-won: median not mean, sample size shown first, "usual" instead of "med",
instance-vs-group two-tier keying, refusing to project early, refusing to print `-- / --`,
splitting the strikethrough span off the padding span. Whoever wrote those comments understood
the problem. The critique below is almost entirely about *presentation*, not about what was
chosen to show.

That said, the owner is right that it is a mess. Specifically:

### 1.1 It is one Label. That is the root cause of most of the rest.

The entire window is a single `Label` with a `FormattedString`, inside a `ScrollView`, inside a
`Border`. Six logically distinct blocks (banner, headline, participants, exchange, load, outlook,
history, weapons, session) render as one undifferentiated text blob at one font size with one
line height.

Consequences that all trace back to this:

- **No visual hierarchy.** The thing that can kill you and the thing that tells you your session
  kill tally are rendered identically: 11px, Cascadia Mono, one of seven colours. There is
  literally no typographic way for the panel to say "look here first".
- **Alignment is space-padding inside a text run.** This already failed once, badly, in a way
  worth remembering: the font family string was a CSS-style fallback list that MAUI does not
  parse, so every column silently rendered in a proportional font. The fix is documented in the
  file. The fragility remains: any future font change, DPI edge case, or non-ASCII glyph
  reintroduces it.
- **No graphics are possible.** Not one bar, meter, sparkline, or distribution strip exists,
  because a `FormattedString` cannot contain one. Every quantity in the window is a decimal
  number. For a user who has explicitly said he is not a stats or maths person, this is close to
  the worst possible encoding.
- **Layout jumps.** Lines are conditionally emitted (`AppendDeficits` returns early when
  deficits are zero; `AppendPair` skips when both sides are null; `AppendFleeRisk` only fires at
  >= 50%; `AppendOutlook` is silent until `MinimumElapsedSeconds`). So during a live fight rows
  appear and disappear and everything below shifts. Glanceable peripheral-vision reading depends
  on *stable geography* - the eye learns where a number lives and saccades straight to it. This
  layout relearns itself every few seconds.

### 1.2 The most important number in a fight is not in the window

`CombatStatDeficits` carries `StaminaCurrent` and `StaminaMax`. They are consumed only as inputs
to `CombatOutlook.Project`. Stamina is never *displayed*.

So during a fight the player must read the clog window for the fight and the main window's top
strip for their stamina. Two windows, two saccades, for the one question that matters most. The
owner's own example of a live-combat need was "your stamina is too low to survive this fight" -
the current design cannot express that at all.

### 1.3 The alert vocabulary is a single 2px border colour

`RefreshFrame()` sets the `Border.Stroke` to one of three static colours. That is the entire
urgency channel. The class comment explains why - Invariant #1 forbids UI-thread animation
timers - and that reasoning is correct as far as it goes, but the conclusion drawn ("therefore no
motion at all") is wrong: WinUI composition animations run off the UI thread entirely. See
section 7.4. There is a sanctioned way to pulse and it was left on the table.

The only other emphasis mechanism in the whole window is that `LOSING` is spelled in capitals.

### 1.4 Colour semantics are overloaded to the point of non-communication

Seven tones, mapped in `ClogPage.ToneColor`:

| Tone | Hex | What it currently means |
| --- | --- | --- |
| `Warn` | `#d29922` | stat penalty, NPC fled, you fled, flee risk, "too close", weapon underperforming |
| `Dim` | `#6e7681` | every label, every unit, sample counts, outcome tallies, the whole outlook prefix, "withdrew", separators, roughly 60% of all rendered characters |
| `Hostile` | `#f85149` | the opponent's name, the opponent's numbers, "killed by", AND the estimated damage-to-kill |
| `Friendly` | `#58a6ff` | the player's numbers, AND the weapon name in the headline |
| `Heading` | `#a371f7` | section headings |
| `Good` | `#3fb950` | kills, and "this weapon is beating the record" |
| `Value` | `#cccccc` | everything left over |

Two specific failures:

- **Amber means six unrelated things.** "You are carrying too much", "it ran away", and "your
  weapon is doing less damage than usual" are not the same kind of information and should not
  share a colour.
- **`Hostile` conflates "belongs to the enemy" with "is dangerous to you".** The opponent's name
  and your imminent death are the same red. There is therefore no colour left to escalate to.

### 1.5 Non-ASCII glyph literals, against an explicit project rule

`CombatHistoryFormatter` and `ClogPage` between them ship these characters in string literals
that are rendered to screen: `U+2714`, `U+2718`, `U+2192`, `U+2190`, `U+2015`, `U+00B7`,
`U+00BB`, `U+00D7`, `U+2014`.

Two problems, one of which is worse than the rule violation:

1. It violates `INTERNAL.md`'s hard constraint directly.
2. **Several of those glyphs are not fixed-advance in Cascadia Mono.** The heavy check and
   cross in particular are commonly East-Asian-width or fall back to a different face. In a
   layout whose entire column alignment is space-padded character counting, a single
   wrong-width glyph shifts a row. This is the same class of bug as the font-fallback bug, still
   live, just less obvious.

The glyphs also buy nothing. `U+2714 killed` is not clearer than `KILLED`.

### 1.6 Live and post-hoc are interleaved in one 330px column

In a single scroll, top to bottom: result banner, headline, per-participant rows, a 2-column
exchange table, load line, outlook, history heading, flee risk, a 2-column now/usual table, a
3-column weapon table, session totals. Five tables. In 330 pixels. At 11px.

These serve completely different tasks on completely different clocks. Mid-swing you need three
things in under 300ms. Between fights you want to read carefully. Sharing one scroll means the
live layer is diluted by ten rows of history you cannot use right now, and the history layer is
cramped into a column too narrow to compare anything.

### 1.7 Smaller but real

- **`ScrollView` during live combat.** Content grows as NPCs join. If it exceeds the window the
  player has to scroll *during a fight*, which also means clicking, which means activating
  another window (see 5.4).
- **The eval hint is permanent chrome.** Two lines of onboarding text plus a divider are pinned
  to the bottom of the window forever, on every frame of every fight. It belongs in `$clog help`.
- **The clear affordance is an unlabelled floating glyph** overlaying the top-right of the
  readout, in the same cell as the result banner. Its function (dismiss the finished summary) is
  not discoverable, and it is the only clickable thing in the window.
- **Recording and display are welded together.** `$clog on` starts writing files *and* opens the
  window; the native close button turns recording off. You cannot watch without recording, or
  record without a window on screen. The file comment frames this as a feature ("exactly one
  place to look"). It is a reasonable simplification that has outlived its usefulness now that
  `fights.jsonl` records unconditionally anyway (`FightHistoryStore` records always, unlike
  clogging) - so the coupling is already only half true, which is worse than either state.
- **No window geometry persistence.** Opens at 330x520 at the OS default position, every time.
- **Nothing is actionable.** The owner's single most-quoted pain point, verbatim in
  `MECHANICS_NOTES.md`, is chasing a fleeing NPC: "very annoying having to try and find the flee
  message and type: se,kill that thing with this weapon *before* that thing wanders off." The
  clog window watches this happen and offers nothing.
- **Goal (c) is entirely unserved.** There is no historical or aggregate view in the client at
  all. Every cross-fight insight currently requires leaving the game, running
  `analyze_mechanics.py`, and reading markdown. The `IsCombatExpanded` / `CombatFoldGlyph` /
  `ToggleCombatCommand` triple in `SidePanelViewModel` is dead code - a docked combat section
  that no XAML binds - which suggests this drifted rather than being decided.

---

## 2. Information architecture

The single biggest structural error is that one surface is trying to be three products. They have
different clocks, different attention budgets, different interaction rights, and different
tolerance for density.

| | Clock | Attention budget | Interactive? | Density |
| --- | --- | --- | --- | --- |
| **Live** | 1-2s | peripheral, < 300ms glance | **no** | very low, huge |
| **Review** | after a fight | foveal, 5-30s | yes | medium |
| **Ledger** | between sessions | foveal, minutes | fully | high |

Proposed surfaces, in order of value:

### Surface 0 - Vitals (in the main window, top strip)

**The most valuable change in this whole document, and the cheapest.**

The player is already looking at the terminal. The top strip already shows `Sta / Mag / Str /
Dex`. That is exactly where the eye already goes, and it is exactly where the encumbrance and
survivability signals belong. The owner's two live-combat examples - stamina too low, purple-pulse
the effective dex - are both about numbers *that are already on screen there*.

Augment in place: a meter under each stat, a delta chip when effective differs from raw, and the
pulse vocabulary. No new real estate, no new window, no focus question, works even with every
combat window closed.

### Surface 1 - Threat band (in the main window, left rail)

The left rail already hosts `Online` / `Carrying` / `Here` / compass. Revive the dead
`IsCombatExpanded` section as a `COMBAT` fold. This is the live layer proper: who is on you, the
race, and the **action buttons** (pursue a fleeing NPC). Being in the main window is what makes
those buttons legal under Invariant #0 - the existing `RequestFocus` pattern already works there.

### Surface 2 - Combat HUD windlet (floating, optional, non-interactive)

For players who want more than the rail: a small always-available floating readout. Deliberately
**zero interactive controls** - see 5.4 for why that constraint falls straight out of Invariant
#0 rather than being an arbitrary restriction.

Replaces today's clog window in the live role. Much shorter than today, because the history
content moves out.

### Surface 3 - Combat Ledger (large window, fully interactive)

Where review and analysis live. Four tabs:

- **Fights** - the session/history list, click through to a per-fight replay. Goal (b).
- **Creatures** - per npc_group and per instance rollups. What is dangerous, what pays.
- **Weapons** - the weapon x creature matrix. The core hypothesis surface.
- **Findings** - plain-language candidate discoveries and the trials that would confirm them.
  Goal (c), and the answer to "the owner is not a stats person".

Opening it is a deliberate context switch, like Settings - it may take focus, and returns it on
close. That is the sanctioned exception in `CLAUDE.md`.

### What lives where

| Information | Vitals | Threat band | HUD | Ledger |
| --- | :---: | :---: | :---: | :---: |
| Stamina absolute + trend | **yes** | | yes | |
| "N hits left" survivability | **yes** | yes | yes | |
| Encumbrance str/dex delta | **yes** | | yes | |
| Carried weight / object count | | | yes | yes |
| Active targets, who is dead | | **yes** | yes | |
| Exchange (hits/misses/damage) | | small | **yes** | yes |
| Outlook / race bar | | **yes** | yes | |
| Pursue-fleeing action | | **yes** | | |
| Current weapon vs. its record | | | yes | yes |
| History medians for this target | | | small | **yes** |
| Per-swing replay | | | | **yes** |
| Weapon x creature matrix | | | | **yes** |
| Findings, trials, hidden modifiers | | | | **yes** |
| Session totals | | | yes | yes |

---

## 3. Layouts and wireframes

All wireframes are monospace at the stated column count. Character-cell dimensions assume
Cascadia Mono, whose advance is 0.6em: at 12px that is 7.2px/char, at 11px 6.6px/char.

### 3.1 Vitals - top strip augmentation (main window, full width)

Idle today (approximately, from Screenshots/mucka3.png):

```
 <  o  i | Sta: 105/105 | Mag: 105/105 | Str: 100/100 | Dex: 100/100 | Score: 26375 (+0) |  rec   Rain  95m
```

Proposed, out of combat and unencumbered - visually identical to today plus a hairline meter:

```
+---------------------------------------------------------------------------------------------------------+
| <  o  i | Sta 105/105 | Mag 105/105 | Str 100/100 | Dex 100/100 | Score 26375 +0 |   rec    Rain     95m  |
|         | ########### |             | ########### | ########### |                |                       |
+---------------------------------------------------------------------------------------------------------+
```

Proposed, in combat, hurt, and encumbered:

```
+---------------------------------------------------------------------------------------------------------+
| <  o  i | Sta  38/105 | Mag 105/105 | Str  89/100 -11 | Dex  71/100 -29 | Score 26375 |  rec   Rain  95m  |
|         | ####....... |             | ########..      | #######...      |             |                  |
+---------------------------------------------------------------------------------------------------------+
             ^^^^^^^^^^^                        ^^^^^^^^            ^^^^^^^^
             DANGER, throbbing                  LOAD chip           LOAD chip, breathing
```

Rules:

- The meter row is **always present** (hairline, 2px) so nothing ever reflows. Out of combat it
  is drawn at 25% opacity.
- Meters are filled proportionally to `current/max`, drawn as Skia rectangles, not characters.
  Str and Dex fill against `MaxStrength` / `MaxDexterity` (100 for both; note STA caps at 120
  when permanently buffed, so the Sta meter must read `MaxStamina` and not a hardcoded 100).
- The `-11` / `-29` **delta chips** appear only when `Strength != RawStrength` or
  `Dexterity != RawDexterity`. To avoid reflow, the space for them is always reserved (4 chars,
  right-aligned) and rendered empty when the delta is zero.
- The delta chip is `LOAD` purple. Its magnitude drives the pulse level (4.3).
- Stamina's colour and pulse are driven by *survivability*, not by a fixed percentage - see 4.4.

This one change delivers most of goal (a) with no new window, no focus risk, and no new data.

### 3.2 Threat band - left rail (main window, ~230px = 30 columns at 12px)

Idle (occupies the same height as combat state, so nothing above or below moves):

```
+------------------------------+
| COMBAT                   [v] |
|                              |
|   no fight in progress       |
|                              |
|   last: rat0  KILLED  0:24   |
|   session 12f 9k 1d          |
|                              |
|                              |
|                              |
|                              |
+------------------------------+
```

Live, two targets, one already dead, one fled:

```
+------------------------------+
| COMBAT   0:24            [v] |
|                              |
|  rat0                        |
|  [##########........]   ~65  |
|  zombie4      DEAD  0:11     |
|                              |
|  you  [#########...]    64%  |
|  them [#####.......]    27%  |
|                              |
|  LOSING                      |
|  kill 0:31    die 0:14       |
|                              |
|  +------------------------+  |
|  |  chase zombie4  se     |  |
|  +------------------------+  |
+------------------------------+
```

- `rat0`'s bar is *estimated remaining stamina* - `(pool_estimate - damage_done) / pool_estimate`
  from `FightHistorySummary.EstimatedStaminaPool`. It is an estimate, so it is drawn with a
  dotted rather than solid outline and captioned `~65`, never a bare percentage. When there is no
  pool estimate the bar renders as an empty dotted track with `?` - honest, and still holds its
  place.
- `you` / `them` bars are observed hit rates, side by side. This is the only *observable* proxy
  for the relative-dexterity mechanic the domain notes describe, and it must be labelled as hit
  rate, never as dexterity.
- The chase button is the whole point of this surface. One per fleeing NPC, per
  `MECHANICS_NOTES.md`: injects `se,k zombie4 wi <current weapon>`, explicit activation only,
  suppressed while any other fight in the encounter is unresolved, and calls `RequestFocus` on
  activation like every other rail control.

### 3.3 Combat HUD windlet - 380 x 300 (44 columns at 12px)

Live state. Note the fixed geography: every band is always present at the same height.

```
+--------------------------------------------+
|  IN COMBAT                     0:24        |   <- band, DANGER tint while live
+--------------------------------------------+
|                                            |
|   STA        38 / 105                      |   <- 18px hero number
|   [#########.........................]     |
|   3 HITS LEFT            -14 in last 10s   |   <- DANGER, throbbing
|                                            |
+--------------------------------------------+
|   LOAD    str -11    dex -29               |   <- LOAD purple, breathing
|           3.4kg, 7 items   drop to fix     |
+--------------------------------------------+
|   dagger0  vs                              |
|     rat0          0:24   [#########---]~65 |
|     zombie4       0:11   DEAD              |
+--------------------------------------------+
|              you        them               |
|   hit/miss   9 / 5      4 / 11             |
|   hit rate     64%        27%              |
|   damage      28.5       11.0              |
|                                            |
|   OUTLOOK   LOSING                         |
|   [=====kill 0:31=====|==die 0:14==]       |   <- race bar
+--------------------------------------------+
|  rats: usually ~35 dmg, 0:22, you win 22/24|
+--------------------------------------------+
```

Idle state - identical geometry, dimmed, no motion:

```
+--------------------------------------------+
|  NO COMBAT                                 |
+--------------------------------------------+
|                                            |
|   STA       105 / 105                      |
|   [######################################] |
|   healthy                                  |
|                                            |
+--------------------------------------------+
|   LOAD    unencumbered                     |
|           1.1kg, 3 items                   |
+--------------------------------------------+
|   dagger0  vs  --                          |
|                                            |
|                                            |
+--------------------------------------------+
|   last fight                               |
|   rat0        KILLED     0:24     28.5     |
|                                            |
|   session                                  |
|   12 fights  9 killed  1 died  2 fled      |
|   431.0 dealt   188.5 taken    4:12        |
+--------------------------------------------+
|  press F9 or type $ledger for full history |
+--------------------------------------------+
```

The swamp special case, because it can cost a real inventory:

```
|   LOAD    str -11    dex -29               |
|           IN THE SWAMP - DO NOT DROP       |   <- CAUTION, overrides "drop to fix"
```

### 3.4 Ledger - Fights tab, 1000 x 680 (fight list + replay pane)

```
+---------------------------------------------------------------------------------------------------+
| COMBAT LEDGER      [ Fights ]  Creatures   Weapons   Findings                                     |
+---------------------------------------------------------------------------------------------------+
| filter: [ all creatures  v ] [ all weapons v ] [ this session v ]     87 fights, 62 with detail    |
+--------------------------------+------------------------------------------------------------------+
| when      target    wpn   out  |  rat0   with dagger0                    2026-08-05  19:41:22      |
+--------------------------------+------------------------------------------------------------------+
| 19:41:22  rat0      dgr   KILL |  Elizabethan tearoom, Rain          str 89/100 (-11)  dex 71 (-29)|
| 19:40:08  zombie4   dgr   KILL |  carrying 3.4kg / 7 items           sta 52 at start             |
| 19:38:51  rat13     dgr   FLED |                                                                   |
| 19:35:02  wolf      fal   YOU  |     you  ############--------  9 hit / 5 miss    64%             |
| 19:31:44  thief     fal   KILL |    them  #####-----------      4 hit / 11 miss   27%             |
| 19:28:10  rat3      fal   KILL |                                                                   |
| 19:22:59  starfish  bsw   KILL |  damage    28.5 dealt      11.0 taken      3.2 per landed blow   |
| 19:19:31  mouse     bsw   KILL |  duration  0:24            vs usual 0:22 for rats                |
| 19:15:07  parrot    bsw   KILL |                                                                   |
| 19:02:18  rat13     fal   KILL |  TIMELINE                                                        |
| 18:58:40  rat3      fal   KILL |  sta 52 +--------------------------------------------+           |
|                                |         |*..                                         |           |
|                                |         |  `*-.__                                    |           |
|                                |         |        `--*.__                             |           |
|                                |      38 +------------------`-*--------------------+  |           |
|                                |         0s        8s       16s       24s             |           |
|                                |                                                                   |
|                                |  SWINGS   h = you hit   m = you miss                             |
|                                |           H = they hit  M = they miss   . = silent tick          |
|                                |                                                                   |
|                                |  0:00  h.M  hM.  .mH  h.M  mM.  hH.  h.M  mM.  hH.  h..  KILL    |
|                                |        ^                          ^                              |
|                                |        opened with dagger0        their 4th landed blow          |
+--------------------------------+------------------------------------------------------------------+
```

The swing strip is the "per-round breakdown" the review goal asks for, and the silent-tick marker
`.` makes MUD2's invisible pass mechanic *visible* - which is itself a research finding the owner
would otherwise never see. It needs data we do not currently persist (see 6.6).

### 3.5 Ledger - Weapons tab

The hypothesis surface. Rows are weapons, columns are creature groups, cells are median damage per
landed blow, shaded by how it compares to that creature's best-known weapon.

```
+---------------------------------------------------------------------------------------------------+
| COMBAT LEDGER        Fights   Creatures   [ Weapons ]   Findings                                  |
+---------------------------------------------------------------------------------------------------+
|  show: [ damage per blow  v ]   only creatures with 5+ fights                                     |
+---------------------------------------------------------------------------------------------------+
|                  rats    zombies  dwarves  thieves  ravens   wolves   goats                       |
|                  (24)    (9)      (1)      (4)      (4)      (1)      (1)                         |
|                                                                                                    |
|  dagger0         3.2     4.1      3.9      2.8      --       --       17.0                        |
|                  ####    #####    ###?     ###      ....     ....     #####?                      |
|                  22 fts  4 fts    1 ft     1 ft                       1 ft                        |
|                                                                                                    |
|  axe0            2.4     3.6      --       2.1      2.9      --       --                          |
|                  ###     ####     ....     ##       ###      ....     ....                        |
|                  2 fts   3 fts             2 fts    4 fts                                         |
|                                                                                                    |
|  falchion        2.9     3.9      --       3.4      --       3.1      --                          |
|                  ###     ####     ....     ####     ....     ###      ....                        |
|                  3 fts   2 fts             1 ft              1 ft                                 |
|                                                                                                    |
|  broadsword      --      --       --       --       --       --       --                          |
|                  ....    ....     ....     ....     ....     ....     ....                        |
|                                                                                                    |
|  ? = too few fights to trust.  .... = never tried. Click a cell for the fights behind it.         |
+---------------------------------------------------------------------------------------------------+
```

Every cell carries its sample size and marks thin evidence with `?`. Nothing in this table is
allowed to look confident when it is not. Clicking a cell filters the Fights tab.

### 3.6 Ledger - Findings tab

This is where the "not a stats person" constraint is answered. Findings are **sentences**, ranked
by strength of evidence, each backed by one picture and one instruction.

```
+---------------------------------------------------------------------------------------------------+
| COMBAT LEDGER        Fights   Creatures   Weapons   [ Findings ]                                  |
+---------------------------------------------------------------------------------------------------+
|                                                                                                    |
|  +---------------------------------------------------------------------------------------------+  |
|  |  LOOKS REAL                                                                                 |  |
|  |                                                                                             |  |
|  |  The dagger0 hits rats harder than the axe0 does.                                            |  |
|  |                                                                                             |  |
|  |     dagger0    . .::.:####:::. .              typical 3.2 per blow    22 fights             |  |
|  |     axe0        .:.##:. .                     typical 2.4 per blow     6 fights             |  |
|  |                +------+------+------+------+                                                |  |
|  |                1      2      3      4      5   damage per landed blow                       |  |
|  |                                                                                             |  |
|  |  The two groups barely overlap, and you have enough fights with each for that to mean        |  |
|  |  something. Your strength was in the same range for both, so it is probably the weapon.      |  |
|  +---------------------------------------------------------------------------------------------+  |
|                                                                                                    |
|  +---------------------------------------------------------------------------------------------+  |
|  |  WORTH A LOOK                                                                               |  |
|  |                                                                                             |  |
|  |  Carrying a lot seems to make rats hit you more often.                                       |  |
|  |                                                                                             |  |
|  |     light  (under 2kg)   .::.#:.                they hit you 24% of the time     9 fights   |  |
|  |     heavy  (over 3kg)      .:.##:.:.            they hit you 38% of the time    11 fights   |  |
|  |                                                                                             |  |
|  |  TO FIND OUT: fight 8 more rats while carrying under 2kg. Same weapon, same room if you can. |  |
|  |                                            [ start this trial ]                             |  |
|  +---------------------------------------------------------------------------------------------+  |
|                                                                                                    |
|  +---------------------------------------------------------------------------------------------+  |
|  |  TOO EARLY                                                                        (2 more)  |  |
|  |  The falchion may be better than the dagger0 against thieves.   1 fight vs 1 fight           |  |
|  +---------------------------------------------------------------------------------------------+  |
|                                                                                                    |
+---------------------------------------------------------------------------------------------------+
```

Three design commitments here:

1. **Never a p-value, never a confidence interval, never the word "significant".** The evidence
   strength is a four-rung word ladder (4.6) and the picture is a dot strip - one dot per fight -
   so "these two clouds barely overlap" is read directly off the image.
2. **Every card ends in an instruction, not a conclusion.** "Fight 8 more rats while carrying
   under 2kg" converts a sample-size problem into a quest. That is the correct register for this
   user and it is also just honest: the answer genuinely is "go get more data".
3. **The trial helper** implements the controlled-A/B methodology already written down in
   `MECHANICS_NOTES.md` section "Hidden weapon modifier methodology". Starting a trial pins a
   target condition; the threat band then shows `TRIAL 3/8` and warns when the current fight will
   not count (wrong weapon, wrong creature, load out of band).

### 3.7 A specific findings surface worth calling out: the hidden stamina/strength wield threshold

The domain brief describes a hidden strength variant affected by stamina, governing whether you
can keep wielding e.g. `axe01234`. That is not measurable directly, but it *is* bisectable from
play, and nothing else in the design surfaces it:

```
|  WEAPON THRESHOLDS                                                                                |
|                                                                                                    |
|  axe01234    works down to  sta 62      failed at  sta 41                                          |
|              [--------------|##########?#########|--------------]                                  |
|              0             41                   62             105                                 |
|              Somewhere between 41 and 62 you stop being able to hold it.                            |
|              TO NARROW IT: try wielding it around sta 50.                                          |
```

This requires capturing a wield-refusal event which does not exist today (see 6.6).

---

## 4. Visual language spec

The north star is Clio in Windows Terminal: dark, terminal-flavoured, dense, no chrome. The
existing hexes are already close to right (they are GitHub-dark-ish, which sits well next to the
Campbell palette in `Rendering/TerminalTheme.cs`). The problem is assignment, not choice.

### 4.1 Surfaces

| Token | Hex | Use |
| --- | --- | --- |
| `Bg` | `#0C0C0C` | page background - identical to `TerminalTheme.Background`, keep it |
| `BgRaised` | `#101618` | band and card backgrounds - matches the existing windlets |
| `Rule` | `#21262d` | 1px separators inside a panel |
| `Stroke` | `#2d333b` | panel borders at rest - already in use |

### 4.2 Semantic colours

Eight roles. Each has exactly one meaning. Where a role already exists at a hex, keep the hex.

| Role | Hex | Means | Must never mean |
| --- | --- | --- | --- |
| `Ink` | `#cccccc` | a primary value | a label |
| `Muted` | `#6e7681` | labels, units, scaffolding, sample counts | any value the eye needs to find |
| `You` | `#58a6ff` | belongs to the player | good, or safe |
| `Them` | `#c9524a` | belongs to an opponent | danger to you |
| `Danger` | `#ff5c57` | **lethal risk to the player, and nothing else** | an opponent's identity |
| `Caution` | `#d29922` | degraded, not lethal; contextual warnings | an outcome |
| `Good` | `#3fb950` | better than your own baseline | an outcome |
| `Load` | `#a371f7` | encumbrance and self-inflicted stat penalties | a heading |

Four changes from today, all deliberate:

- **`Them` desaturates from `#f85149` to `#c9524a`,** freeing hot red exclusively for `Danger`.
  Today the enemy's name and your imminent death are the same colour, which leaves nothing to
  escalate to.
- **Purple stops being "heading" and becomes "load".** The owner explicitly asked for a purple
  encumbrance signal; purple cannot be both that and generic section chrome. Headings become
  `Muted` uppercase - see 4.7.
- **Outcomes stop using `Good` / `Caution`.** `KILLED`, `FLED`, `DIED`, `WITHDREW` are facts, not
  judgements, and rendering "it ran away" in the same amber as "you are overloaded" is what makes
  amber meaningless. Outcomes render `Ink` with a `Muted` prefix; only `DIED` gets `Danger`.
- **`Good` narrows to exactly one job:** "this is better than your own historical baseline". That
  makes green genuinely informative in the weapon table.

### 4.3 The alert vocabulary

Four levels. **At most one L3 element may exist on screen at any moment** - enforce this in code,
because the instant two things scream, nothing does.

| Level | Name | Motion | Period | What it says |
| --- | --- | --- | --- | --- |
| L0 | Steady | none | - | normal |
| L1 | Breathe | opacity 1.00 -> 0.68 -> 1.00, ease-in-out | 2.4s | worth noticing when you get a moment |
| L2 | Pulse | opacity 1.00 -> 0.45 -> 1.00, ease-in-out | 1.2s | deal with this soon |
| L3 | Throb | opacity 1.00 -> 0.30 -> 1.00 + a `Danger` glow behind the text | 0.55s | act now |

Rules:

- Motion is **only ever applied to a background/glow layer behind text**, never to the text
  itself. Reason in 7.4: only that is animatable off the UI thread, and moving text is also just
  harder to read.
- **Every pulse starts on a state transition and stops on a state transition.** Nothing polls,
  nothing restarts per tick.
- **Transitions in the calming direction are instant** (drop your load, the pulse stops that
  frame). Transitions in the alarming direction get a 250ms fade-in so a single noisy frame does
  not flash the panel.
- Motion is **never** used out of combat. The idle HUD is completely still.

### 4.4 What is allowed to alert, and when

| Signal | Level | Condition |
| --- | --- | --- |
| Stamina / hits-left | L3 Throb, `Danger` | estimated hits-left <= 2, **or** projected time-to-die < time-to-kill and < 15s |
| Stamina | L2 Pulse, `Danger` | hits-left <= 4, or stamina < 25% of max |
| Stamina | L1 Breathe, `Caution` | stamina < 50% of max while in combat |
| Dex delta chip | L2 Pulse, `Load` | dex penalty >= 20, **or** the penalty just got worse mid-fight |
| Dex / Str delta chip | L1 Breathe, `Load` | any nonzero penalty while in combat |
| Outlook `LOSING` | L1 Breathe, `Caution` | verdict is Losing (the stamina L2/L3 carries the real urgency) |
| Chase button available | L1 Breathe, `Ink` | a fleeing NPC is pursuable and no other fight is open |
| Swamp warning | L0, `Caution` | in the swamp with a load penalty - static, but always present |
| Everything else | L0 | - |

**"Hits left" is the key invention.** `floor(stamina / their_observed_damage_per_landed_blow)`,
falling back to the historical median for the group when this fight has too few landed blows.
It answers "am I going to survive this" in a unit a human parses instantly, where `38/105` does
not. It is also honest about its own uncertainty: below 3 observed incoming blows it renders as
`about 3 hits left` in `Muted` rather than a hard number, and it never renders at all until
something has actually hit you.

### 4.5 Iconography - ASCII only

No non-ASCII character literals anywhere. The full sanctioned vocabulary:

| Purpose | ASCII | Notes |
| --- | --- | --- |
| Bar fill / empty | `#` / `.` | text fallback only; prefer real Skia rectangles |
| Estimate marker | `~` | prefix, e.g. `~65` |
| Unknown / thin evidence | `?` | |
| No data | `--` | already the convention, keep it |
| Trend | `^` `v` `=` | |
| Current selection marker | `>` | replaces `U+00BB` |
| Fold open / closed | `[v]` / `[>]` | replaces the triangle glyphs; also fixes their width |
| Outcomes | `KILLED` `DIED` `FLED` `YOU FLED` `WITHDREW` `LIVE` | words, not glyphs - replaces `U+2714`/`U+2718`/arrows |
| Silent combat tick | `.` | in the swing strip |
| Dismiss / close | a `Button` reading `close`, or an SVG asset | replaces the bare `U+00D7` |

Anything richer than the above - a weapon silhouette, a skull, a shield - **must be an image
asset**, not a character. The project already has the pipeline for this: `Resources/Images`
carries `combat.png`, and `tools/rasterize-status-icons.cs` exists to rasterize status icons.
Route any new iconography through there.

Assets needed if the design is taken beyond ASCII (all optional, none blocking):

- `hud-danger-glow.svg` - a soft radial for the L3 throb layer. Could also be a Skia gradient,
  which is preferable.
- Per-outcome 12px marks for the Ledger fight list, if the word labels prove too wide.

### 4.6 Evidence-strength ladder (Findings tab)

Four rungs, words only, never a number:

| Rung | Rendered | Rough rule |
| --- | --- | --- |
| `TOO EARLY` | `Muted`, collapsed by default | fewer than 5 fights in either arm |
| `WORTH A LOOK` | `Caution` | >= 5 each, medians separated but distributions overlapping |
| `LOOKS REAL` | `Ink` | >= 12 each, interquartile ranges barely overlap |
| `CONFIRMED` | `Good` | >= 30 each, clean separation, holds across at least two conditions |

The thresholds are deliberately blunt. Underneath, use a bootstrap over the median difference and
an IQR-overlap check - but **surface none of it**. The user sees the word, the dot strip, and the
instruction.

Card copy must obey three rules: no jargon, always name the sample size in fights, and always end
with either a plain-English caveat or a next action.

### 4.7 Typography

Family: `"Cascadia Mono"` - exactly that string. `MauiProgram.cs` registers exactly one face,
`CascadiaMono.ttf`. Do not write the CSS-style fallback list; `MappingPage.cs` and
`RawConsolePage.cs` still contain the broken form and are almost certainly rendering ragged today.

Only three sizes, with clear jobs:

| Size | Job |
| --- | --- |
| 18px | hero numbers only: stamina, hits-left, the outlook verdict. Two per surface, maximum. |
| 12px | body, all tables, all values. Up from today's 11px - 11px Cascadia at 100% DPI is below comfortable for a glance surface. |
| 10px | labels, units, sample counts, footnotes. Always `Muted`. |

**No bold.** Only the Regular face is registered, so `FontAttributes.Bold` gets a synthesised
smear that in a monospace face can also perturb advance width - the same failure mode as the
glyph-width problem in 1.5. Emphasis comes from size, colour, and `UPPERCASE`. If bold is really
wanted, register `CascadiaMono-SemiBold.ttf` first; in Skia surfaces, use an explicit
`SKTypeface` as `Rendering/TerminalFont.cs` already does.

Section labels: `UPPERCASE`, 10px, `Muted`, followed by a `Rule` hairline. No coloured headings.

### 4.8 Density and rhythm

- 4px vertical base unit. Line height 1.35 (16px at 12px text).
- Band padding 8px. Gap between bands 8px, separated by a 1px `Rule`.
- Bars: 6px tall, 2px corner radius, `Rule`-coloured track, semantic-coloured fill. Estimated
  values use a 1px dotted track outline to say "this is inferred".
- **Every band holds its height whether or not it has content.** Empty bands render a `Muted`
  placeholder. Stable geography is worth more than compactness on a glance surface.
- Numeric columns right-align; label columns left-align; a table never has more than three
  columns at HUD width.

---

## 5. Interaction model

### 5.1 Opening and closing

Decouple recording from display. `$clog` currently does both; keep it working, add the rest.

| Command | Does |
| --- | --- |
| `$clog on` / `off` | **unchanged** - starts/stops recording *and* shows/hides the HUD, exactly as today |
| `$clog status` | unchanged |
| `$clog eval <itemid>` | unchanged (and its help text moves out of the HUD chrome into here) |
| `$hud` | toggles the HUD windlet without touching recording |
| `$ledger` | opens the Combat Ledger |
| `$combat` | toggles the left-rail threat band |

The Vitals augmentation (3.1) has no command. It is always on, because it costs no space.

Optional hotkeys go through the existing Hotkeys settings tab (visible in
`Screenshots/mucka5.png`) so the user opts in. **Do not hardcode any key** - every unclaimed
keystroke belongs to the command box.

### 5.2 Docking vs floating

- **Vitals** - docked, permanent, non-optional, part of the top strip.
- **Threat band** - docked in the left rail, foldable via `[v]`/`[>]`, pinnable to a floating
  windlet using the *existing* pin infrastructure (`IsMapPinned` / `IsFloatingMapVisible` pattern
  in `SidePanelViewModel`). Note that the two existing windlets are hand-copied duplicates; a
  third is the point at which extracting a shared `FloatingPanel` control pays for itself.
- **HUD windlet** - a separate `Window`, like today. Adds: persisted position/size (in
  `mucka.ini`, alongside the other settings), and an optional always-on-top toggle.
- **Ledger** - a separate `Window`, normal chrome, resizable, geometry persisted.

### 5.3 Resizing

- HUD: minimum 320x260, maximum 520x520. The layout is banded, not scrolled - **the live HUD
  never scrolls.** If a pack fight has more targets than fit, the target list caps at 4 rows plus
  `+3 more` rather than growing. Scrolling during a fight is a design failure.
- Ledger: minimum 900x600. The Fights list/detail split is draggable.
- Both persist size and position.

### 5.4 Focus - Invariant #0 across multiple windows

This deserves stating plainly because it is subtler than the single-window case.

`GamePage.FocusInput()` returns focus to the input *control*. But clicking a **different top-level
window** changes which window the OS considers active, and keystrokes follow window activation,
not control focus. So a click anywhere in a floating HUD sends the next keystroke into the void,
and no amount of `RequestFocus` fixes it - the fix would be re-activating the main window on every
click, which produces visible z-order flicker.

Therefore:

> **The HUD windlet contains no interactive controls at all.** No buttons, no tabs, no scrollbar,
> no clear glyph, no tap targets. It is a pure readout. There is nothing to click, so Invariant #0
> cannot be violated.

Everything actionable lives in the main window, where the existing `RequestFocus` machinery
already works: the chase button, the fold toggle, the pin toggle, dismissing a finished summary
(which the threat band does automatically on the next encounter anyway, making the clear button
unnecessary).

The Ledger is the sanctioned exception, in the same class as Settings and the F-key editor: it is
a deliberate context switch, it owns focus while open, and it returns focus to the command box on
close.

Additional consequences:

- The HUD should **not** be a `ContentPage` full of MAUI controls that can take tab focus. Draw it
  as a single `SKCanvasView` with `EnableTouchEvents = false` and `IsTabStop = false` on the
  platform view.
- If always-on-top is enabled, the HUD must still not be click-through-transparent or
  non-activating - both require P/Invoke (7.3). Not being interactive sidesteps the whole problem.

### 5.5 Keyboard

- No new global keys. Nothing in this design is reachable only by mouse *and* nothing requires a
  key.
- Inside the Ledger, normal desktop expectations: arrow keys move the fight selection, `Esc`
  closes and returns focus to the command box, `Ctrl+F` focuses the filter.
- The Ledger's `Esc` handler must call the same `RequestFocus` path used by the settings dialogs.

### 5.6 No combat active

- **Vitals**: meters stay, at 25% opacity for the hairline; delta chips remain visible (being
  encumbered out of combat is still worth knowing, at L0 - no motion); no pulses.
- **Threat band**: same height, shows last fight and session totals.
- **HUD**: the idle layout in 3.3. Same geometry, no motion, dimmed banner.
- **Grace period** (post-kill, `IsCombatGracePeriod`): keep today's good behaviour - it is a
  genuinely well-observed distinction. Banner reads `WINDING DOWN`, colours drop to 60%
  saturation, all motion stops immediately. The player is out of danger; stop shouting.

---

## 6. Staged implementation plan

Ordered by value per unit of work. Each stage is independently shippable.

### Stage 0 - Corrections (half a day, no new UI)

Fix what is wrong regardless of anything else in this document:

- Strip the nine non-ASCII glyph literals from `CombatHistoryFormatter.cs` and `ClogPage.cs`,
  replacing them with the 4.5 vocabulary. Fixes a rule violation and a latent alignment bug.
- Fix `MonoFont` in `MappingPage.cs` and `RawConsolePage.cs` (still the broken CSS list form).
- Split `Danger` from `Them` in `ToneColor`; retire `Heading`-as-purple.
- Add stamina to the readout. It is the most important number in a fight and it is absent.
- Move the `$clog eval` hint out of the window into `$clog help`.

### Stage 1 - Vitals (2-3 days) **- do this first**

Meters, delta chips, and the pulse mechanism on the existing top strip. Highest value in the
document: it is in the window the player is already looking at, it needs no new data, no new
window, and no focus reasoning.

Includes building the reusable composition-animation helper (7.4) that everything else uses.

Delivers: most of goal (a), including both examples the owner gave.

### Stage 2 - Threat band (3-4 days)

Revive `IsCombatExpanded` in the left rail. Targets, race, outlook, chase buttons.

The chase feature needs a small parser change: extend the flee event with the parsed direction,
add a distinct event kind for the "tried to go" non-flee, and a direction-word-to-abbreviation map
(`southeast` -> `se`, not first-letter truncation). All of this is already specified in
`MECHANICS_NOTES.md` under "Fleeing NPCs and pursuit"; that section is effectively the ticket.

Delivers: the rest of goal (a), plus the owner's loudest single complaint.

### Stage 3 - HUD windlet rebuild (4-5 days)

Rewrite `ClogPage` as a single Skia-drawn banded readout. Fixed geography, no interactivity, no
scroll, persisted geometry, idle state.

Keep `CombatHistoryFormatter`'s *decisions* - port them, do not rewrite them - but the output type
changes from `List<ClogLine>` to a structured view model that a Skia painter consumes, so bars and
meters become possible. The existing formatter tests are valuable; keep a text projection of the
model alive so they keep passing.

Delivers: a live surface that is actually readable, and retires the current window.

### Stage 4 - Ledger: Fights + Creatures + Weapons (1-1.5 weeks)

The window, the tab bar, the fight list, the per-fight detail pane (without the swing strip and
stamina trace, which need Stage 6 data), and the two rollup tabs.

All of this can be built on data that **already exists** in `~/.mucka/clogs/fights.jsonl` via
`FightHistory.Summarize` / `SummarizeInstance` / `SummarizeByWeapon`. No new capture needed.

Delivers: goal (b) at encounter granularity, and the bulk of goal (c).

### Stage 5 - Findings (1 week)

The card model, the four-rung ladder, dot strips, the copy generator, and the trial helper.

The statistics are deliberately simple (medians, IQR overlap, a bootstrap on the median
difference). The hard part is the writing: each finding template needs a hand-written plain-English
sentence and a hand-written "to find out" instruction. Budget for copy, not for maths.

Delivers: the rest of goal (c), in the register the owner asked for.

### Stage 6 - Deeper capture, then swing-level review (ongoing)

Data work that unlocks the remaining review detail. Best done incrementally and in parallel with
5, because every day of un-captured play is data lost.

### 6.6 Data not currently captured that this design needs

Flagged explicitly, roughly in order of value:

1. **Per-swing event stream in `fights.jsonl`.** Today only aggregate counters are persisted per
   fight; the detailed event stream exists only in the per-encounter clog, and only when `$clog on`.
   The swing strip (3.4) needs it. Recommended: a compact array per fight, e.g.
   `"swings":["h5-9","m","H3","M","."]` at roughly 4 bytes per tick. Cheap.
2. **Per-swing timestamps**, or at least a tick index. Needed for the timeline and, more
   importantly, to make the **silent pass tick** visible. This is the mechanic the owner is most
   curious about and it is currently completely invisible.
3. **A stamina series over the fight.** Today stamina is only sampled when an NPC hit line reports
   it. The FES heartbeat has more. Needed for the stamina trace, and it would also let the
   already-documented "regen fog" residual be quantified rather than assumed.
4. **Stats snapshot at every weapon change and every joiner start,** not only at encounter start.
   Already flagged as a gap in `MECHANICS_NOTES.md`. Without it, a fight where you switched
   weapons attributes all damage to whichever weapon you started with.
5. **Held vs stowed inventory split.** The owner's own observation is that the same weight in a bag
   costs the same strength but much less dexterity. Until inventory container parsing exists, the
   dexterity hypothesis is not testable and the Findings card for it cannot be written.
6. **A wield-refusal / too-weak event.** Nothing today captures "you cannot hold this any more".
   Without it, the hidden stamina-linked strength threshold (3.7) - which the owner named as a
   specific known-hidden mechanic - is unobservable.
7. **Invisibility state.** `IsBlind`, `IsDeaf`, `IsCrippled`, `IsDumb` are captured;
   invisible is not, and the domain brief says it affects the dexterity calculation.
8. **Sleep state.** Already flagged in `MECHANICS_NOTES.md`: sleep silences *all* combat text, so a
   naive stamina diff across a sleep window reads as an impossible instantaneous drop. Any
   swing-level analysis will produce garbage on those fights unless they are flagged.
9. **Weapon weight joined to the fight record.** `$clog eval` measures it per item into
   `items.jsonl`, but `FightRecord` has no `weapon_weight_grams`, so weapon weight cannot be
   controlled for in any comparison.
10. **NPC value/points per group,** cached once, fetched outside combat - per the existing
    recommendation in `MECHANICS_NOTES.md`. Cheap, and makes a "what is worth fighting" column
    possible in the Creatures tab.

### Deferrable

- The projection refinement in `STATS_DESIGN.md` section 7. `CombatOutlook` as it stands is
  conservative and honest; leave it until there is enough data to sanity-check pool estimates.
- Ledger export / share.
- Any per-instance override list for npc grouping. The automatic threshold already covers `rat0`
  and `dwarf48`.
- Sound cues for combat state. The owner explicitly deferred the level-crossing ding; assume the
  same appetite here.

---

## 7. MAUI-on-Windows feasibility

Concerns are flagged as **OK** (proven in this codebase), **CARE** (works, has a trap), or
**AVOID**.

### 7.1 Secondary windows - OK

`new Window(new SomePage(vm))` + `Application.Current.OpenWindow(...)` is already used three times
(`_clogWindow`, `_rawConsoleWindow`, `_mapWindow`). No risk.

### 7.2 Window geometry persistence - CARE

MAUI's `Window.X` / `Y` / `Width` / `Height` setters have been unreliable on Windows across
versions. Go to the platform: `window.Handler.PlatformView` -> `Microsoft.UI.Xaml.Window` ->
`AppWindow` -> `Move(PointInt32)` / `Resize(SizeInt32)`. Restore after the handler is created, not
in the constructor. Guard with `#if WINDOWS`, as `ClogPage` already is in its entirety.

### 7.3 Always-on-top, and non-activating windows

- **Always-on-top - CARE.** `AppWindow.Presenter as OverlappedPresenter` then
  `IsAlwaysOnTop = true`. Windows-only, straightforward.
- **Non-activating (`WS_EX_NOACTIVATE`) - AVOID.** Not exposed by `AppWindow`; requires
  `SetWindowLong` P/Invoke and brings its own hit-testing problems. **The design avoids needing it
  by making the HUD non-interactive** (5.4). Do not reintroduce a clickable HUD without revisiting
  this.

### 7.4 Animation - the important one

- **`ViewExtensions.FadeTo` / `Animation` / `Application.Current.Dispatcher.StartTimer` - AVOID.**
  MAUI's animation ticker runs on the UI thread. Every window in the process shares one UI thread
  and one dispatcher, so an animation in the HUD window competes directly with typing in the game
  window. This is exactly Invariant #1, and it is why `ClogPage`'s author correctly refused to add
  a pulse. It is also why the current design has no motion at all.

- **WinUI Composition - OK, and this is the sanctioned mechanism.** Reach the platform view, then:

  ```
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

  This runs entirely in the compositor, off the UI thread and in fact largely out of process.
  **Zero UI-thread cost while running**, no timer, no per-frame managed code. It satisfies both
  invariants.

  Constraints that follow:

  - Only `Opacity`, `Offset`, `Scale`, `RotationAngle`, and brush properties on a `SpriteVisual`
    are animatable this way. **A MAUI `Label.TextColor` is not** - animating it would be a
    dependent, UI-thread animation. Hence 4.3's rule: *pulse a layer behind the text, never the
    text.*
  - Start and stop on state transitions only; call `visual.StopAnimation("Opacity")` and reset
    opacity to 1.0 when the condition clears.
  - Tear down in `OnHandlerChanged` when `Handler is null`, exactly as `ClogPage` already does for
    its event subscriptions. A leaked forever-animation on a destroyed window is a real leak.
  - Wrap all of it in one small `#if WINDOWS` helper (`PulseLayer.Attach(view, level)`) so no
    caller ever touches WinUI types directly. Build this in Stage 1; every later stage reuses it.

### 7.5 SkiaSharp - OK, with a known ceiling

`SKCanvasView` is proven three times (`TerminalView`, `RadarCompassView`, `SwampSeamView`), all
event-driven with `InvalidateSurface()` and no render loop. `.UseSkiaSharp()` is already registered
in `MauiProgram.cs`.

- On WinUI, `SKCanvasView` is backed by `SKXamlCanvas`, which **paints on the UI thread**. A 380x300
  HUD with roughly 60 draw operations is well under 1ms, and it repaints at most 1Hz (the existing
  `OnAntiIdleTick` cadence) plus on genuine change - fine. Keep the existing diff-before-invalidate
  discipline (`ClogLine.SequenceEquals` today; the equivalent for the new view model).
- **Do not drive Skia at 30-60fps for pulses.** That is what 7.4 is for.
- Escape hatch if per-frame Skia is ever genuinely required: `SKSwapChainPanel` renders on a
  background thread. Not needed by anything in this proposal - noted so nobody reaches for a UI
  timer instead.
- DPI: scale via `e.Info.Width / (float)view.CanvasSize.Width`, as `RadarCompassView` already does.
  Author geometry in a fixed logical space and scale, rather than mixing device pixels.

### 7.6 Fonts - CARE

Only `CascadiaMono.ttf` Regular is registered. `FontAttributes.Bold` will synthesise. In Skia,
control it explicitly via `SKTypeface` the way `Rendering/TerminalFont.cs` does. In MAUI-control
surfaces, avoid bold entirely or register a second face first. See 4.7.

### 7.7 Tabs - CARE

MAUI has no desktop-appropriate tab control worth using here (`TabbedPage` is mobile-shaped;
`Shell` is not in play for these windows). Build the mode bar from `Border` + `TapGestureRecognizer`
- the settings window already does exactly this (Settings / Hotkeys / Sounds / Friends in
`Screenshots/mucka5.png`), so follow that existing pattern rather than inventing one.

### 7.8 Long lists in the Ledger - CARE

MAUI `CollectionView` on WinUI has a poor track record for virtualization and measure correctness
at scale. With thousands of fights, expect trouble.

Two acceptable routes: (a) draw the fight list in Skia as a virtual list, consistent with
`TerminalView`; or (b) use `CollectionView` but hard-cap the visible set through filtering and
paging. Prefer (a) for visual consistency; (b) is a fine Stage 4 shortcut.

### 7.9 Off-thread aggregation - CARE, and non-negotiable

Both windows share one UI thread. Loading and aggregating `fights.jsonl` for the Ledger **will**
stall typing in the game window if done inline. All parsing, grouping, median computation, and
bootstrapping goes on a background thread with only the finished view model marshalled back -
the same rule `FightHistoryStore.LoadAsync` already follows.

The Findings bootstrap in particular is the first genuinely CPU-heavy thing this client would do.
Run it on demand, off-thread, with a visible "working" state, and cache the result keyed on the
record count.

### 7.10 Multi-window focus - see 5.4

The single most important platform behaviour in this design, and the reason the HUD is
non-interactive. Restated here so it is not missed: control focus and window activation are
different things, and `RequestFocus` only addresses the first.

### 7.11 Horizontal overflow - CARE

Wide monospace tables inside a MAUI `ScrollView` scroll badly on WinUI. Design every table to fit
its container at the minimum window width (the wireframes above are sized for this), and truncate
with `Truncate`-style helpers rather than relying on horizontal scroll.

---

## Appendix: the three goals, and where each is served

**(a) Live combat awareness** - Vitals (3.1) carries it, because it is already where the eye is.
The threat band (3.2) adds targets, the race, and the one action that matters. The HUD (3.3) is
optional depth. The alert vocabulary (4.3, 4.4) is what makes any of it peripheral rather than
something to read. Both of the owner's stated examples are directly implemented: stamina becomes
`N HITS LEFT` throbbing in `Danger` red, and the effective dex delta chip breathes and then pulses
in `Load` purple.

**(b) Post-combat review** - the Ledger's Fights tab (3.4): per-fight detail, the stamina trace,
and a per-tick swing strip that also makes MUD2's invisible pass mechanic visible for the first
time. The swing strip needs data items 1 and 2 from 6.6.

**(c) Historical and aggregate analysis** - Creatures and Weapons tabs give the rollups from data
that already exists. Findings (3.6) is the answer to "not a stats person": sentences ranked by a
four-word evidence ladder, one picture each, and every card ending in an instruction rather than a
conclusion. The trial helper turns the A/B methodology already written in `MECHANICS_NOTES.md`
into something the game tells you to go and do.
