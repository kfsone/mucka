# UNARMED, round 2 — reproduced. The discarded `WeaponEquip` is real.

Answering "short fight, no flee, no break, the game named the weapon, panel said UNARMED".
Round 1 (`UNARMED-BUG-20260811.md`) is not repeated; its §1 fix is already in the tree
(`FightAccumulator.NoteDisarmed` no longer nulls `WeaponUsed`), which is consistent with the owner
saying that was not what they saw.

**New evidence source, not used in round 1:** `~/.mucka/clogs/` — **364 encounter clogs** plus
`fights.jsonl` (**487 live-pipeline fight rows**). `ClogWriter.OnLineReady` returns early when
`IsRecording`, and `MudSession.cs:365-366` runs `_combat.Observe` **before** `LineReady`, so the
opening line never reaches the pre-roll. That makes the pre-roll an exact oracle:

> **any line present in a clog's `preroll` was observed while the client's encounter was CLOSED.**

## 1. VERDICT — reproduced, and it is the hypothesis in the brief

`CombatStatsAggregator.Observe` (`ViewModels/CombatStatsAggregator.cs:156-161`) drops every
non-`FightStart` event while `!InCombat`. A `WeaponEquip` landing in that state is gone forever,
because the encounter that follows is reopened by a **swing** line (`YouHit`/`YouMiss`/
`NpcHitsYou`/`NpcMissesYou` → `CombatTracker.Begin`), and no swing line carries a weapon. There is
no later line that can re-supply it. The fight then reads UNARMED start to finish.

Corpus counts: **53** `WeaponEquip` events captured inside an open encounter; **4** distinct equip
lines stranded in a pre-roll, i.e. discarded. Of 364 clog encounters, **17** were opened by a swing
line rather than a `FightStart`, and **14** of those carry no weapon on any event.

### The instance that is still live today — `clog.20260807-011019.jsonl`

```
pre| Auto-reset initiated, you have 120 seconds to finish up. No further warnings will be issued!
pre| *
pre| use bs
pre| You are now using the broadsword to fight!      <- DISCARDED (encounter closed)
pre| *
ev  MissByNpc  w=None  The eagle misses you.         <- reopens the encounter, weapon null
... 41 events, 4 participants, 24 s, every event w=None ...
ev  YouFled    w=None  You have fled by going out.
```

`fights.jsonl` duly recorded eagle 23.7 s, dwarf17 17.7 s, dwarf18 9.6 s, dwarf16 7.7 s — all
`"weapon_used": null`, all fought with the broadsword.

**Why the encounter was closed: the auto-reset *warning*.**
`Mud2C1Decoder.cs:846-847` fires `EmitAutoResetInitiated()` on C06 C04, which is the
**120-second notice**, not the reset. `MudSession.cs:377-381` reacts by calling
`_combat.ForceEnd(...)`. The game has not ended anybody's fight; the client just declared combat
over and then spends up to two minutes throwing away every combat event that is not a `FightStart`.
Any wield in that window is lost, and the fight that resumes on the next tick is unarmed forever.
The preceding clog `20260807-011013.jsonl` ends on exactly `(forced end: reset/disconnect)`.
`ForceEnd` on the *real* reset is already handled separately at `MudSession.cs:569`
(`OnGameModeExited`), so the line at 380 is both premature and redundant.

Two more clogs show this same force-close landing mid-fight with the fight demonstrably continuing:
`clog.20260810-215945.jsonl` (water-snake, 4 events then forced end) and
`clog.20260811-134440.jsonl` (giant1, 25 events then forced end).

### The same bug, historical, from a cause already fixed

`clog.20260802-034424` (magpie / croquet mallet) and `clog.20260806-233111` (thief / staff0) show
`You attack the <npc>.` **in the pre-roll** — i.e. unmatched — followed by the equip line, also in
the pre-roll, also discarded. That is the `PlayerAttackStartUnarmed` gap described in
`CombatTracker.cs:41-46`. Every swing-opened, weaponless encounter in the corpus predates
2026-08-07 **except** the auto-reset one above. The regex fix closed that door; it did not close the
discard itself.

## 2. Exact line ordering MUD2 produces (this is what makes the bug reachable)

Established from both session captures and the clog corpus:

| situation | what MUD2 prints for `k <npc> wi <weapon>` |
|---|---|
| fresh NPC, weapon held | `You attack the <npc>, using the <weapon> as a weapon.` — one line, carries the weapon |
| **NPC already engaging you** | **`You are now using the <weapon> to fight!` — and nothing else.** No attack line at all |
| weapon named not held, but something else is | silent substitute: `kill ws with axe` → `You attack the water-snake4, using the **falchion** as a weapon.` |
| weapon noun unknown to the parser | `I don't know to what "staff" you're referring.` — **the whole command is rejected, no attack happens** (47 occurrences in the corpus; also seen for NPC nouns: `I don't know to what "zombie" you're referring.`) |

The already-engaged form is the crux: it is the **only** line carrying the weapon, so if it is
discarded there is no second chance. Two captured instances, both saved only by luck of ordering —
`A +00:18:24.906 k ram wi axe` (aggro line beat the equip by 0.19 s) and
`B +00:08:16.722 kill ws with axe` (aggro beat it by 0.57 s).

Answering the brief's sub-questions:
- **`k` vs `kill`, `wi` vs `with` are indistinguishable to the client.** The client only ever sees
  the game's reply; all four forms produce byte-identical output. Not a factor.
- **`You're using the X anyway...`** — **zero** occurrences across 364 clogs, both session
  captures, and all pre-rolls. Still unverified; provenance remains `reduce_combat.py` only.
- **`You drop your guard as you switch from using the A to the B.`** — also zero occurrences.
  Note `CombatStatsAggregator` has no `DroppedGuard` case, so if that line *is* how MUD2 reports a
  mid-fight weapon change, the panel would keep showing the **old** weapon. Latent, unobserved.
- **Same-packet ordering (candidate 3).** Not needed. In the reproduced case the gap between the
  discarded equip and the reopening swing was a full ~2 s combat tick, not a packet race.
- **`CombatComposition.PrimaryFight` (candidate 2).** Cleared. The in-combat readout
  (`SidePanelViewModel.cs:573-577`) uses encounter-level `snapshot.CurrentWeapon`, not the primary
  fight, so fight selection cannot affect it live; and `WeaponEquip` broadcasts to every unresolved
  fight (`CombatStatsAggregator.cs:181-185`) while `FightFor` seeds new ones from `_currentWeapon`.
  No corpus fight has a weapon on one participant and null on a concurrent one.
- **Window width.** Not re-derived. Worth noting only that `/T` is clamped to [20,160] and is sent
  from the real window size, so a narrow window remains a separate latent path.

## 3. Minimal fix

**(a) Stop force-ending combat on the auto-reset warning.** `mudsharp/Session/MudSession.cs:377-381`
— drop the `_combat.ForceEnd(CombatClock());` line, keep `_resetClock.NoteAutoResetInitiated(...)`.
The notice is a countdown, not an end of combat; the actual reset already reaches `ForceEnd` via
`OnGameModeExited` (`MudSession.cs:569`). This alone removes the only currently-reachable trigger
found in the corpus, and it also stops the reset warning truncating fight-history rows.

**(b) Stop the aggregator discarding a weapon it was handed.** In
`CombatStatsAggregator.Observe`'s `!InCombat` early return (`CombatStatsAggregator.cs:156-161`),
latch a `WeaponEquip` instead of dropping it:

- add `private string? _pendingWeapon; private DateTime _pendingWeaponUtc;`
- in the early return, if `Kind == WeaponEquip`, record weapon + timestamp, then return;
- in `BeginEncounter`, seed `_currentWeapon` from the latch **only if**
  `startedUtc - _pendingWeaponUtc` is inside a short window, then clear the latch.

The window matters and must be short (a tick or two, ~5 s — the same order as
`CombatTracker.KillGrace`). Round 1 §0 proved MUD2's wielded weapon is **per-fight** and is dropped
when the fight ends, so an unbounded latch would resurrect the raven fight's UNARMED-but-correct
reading as a false "falchion". Also clear the latch on `ItemDropped`/`WeaponBroke`/`WeaponUnusable`
and on `EndEncounter`.

(a) fixes the observed case; (b) is the general guard, and covers the other closure paths that can
strand an equip line — `NpcFled`/`MutualWithdraw` closing while a silent second attacker is still
engaged, post-kill grace expiry in a pack, and any aggro phrasing `NpcAggroStart` does not match
(e.g. `An evil, black rat (rat6) bares its razor-sharp incisors at you.`, seen sitting at the end
of `clog.20260804-121532.jsonl`'s pre-roll).

## 4. What is still not proven

The reproduction is an eagle/dwarf pile-on, not the owner's zombie. Every *recent* null-weapon
zombie row in `fights.jsonl` that has a clog (`20260811-000610`, `-001326`, `-134142`,
`20260810-024612`) was a bare `k z` with no `with` clause — genuinely unarmed, readout correct.
Rows from 08-09 onward without clogs (`08-11 14:23 zombie9`, `14:25 zombie2`, `14:36 banshee`)
cannot be adjudicated.

**Capture that would settle it:** clogging ON, then either
(i) wait for `Auto-reset initiated, you have 120 seconds...` and start a fight after it — predicted
result: no `FightStart` event, encounter opened by a swing, weapon null for the whole fight; or
(ii) let a zombie engage you first and only then type `k z wi axe` — the game will print only
`You are now using the axe0 to fight!`; if the panel says UNARMED at that moment, the client's
encounter was closed and the equip was discarded. The clog's `preroll` field answers it directly:
if the equip line is in the pre-roll rather than in the events, that is the bug.
