# Why the combat panel says UNARMED — 2026-08-11 investigation

Report: *"I've seen a couple of fights where it showed me as unarmed despite my initiating combat
with a weapon."*

Method: both 2026-08-10 captures were replayed through a Python port of `CombatTracker` (regexes
verbatim) plus the weapon half of `CombatStatsAggregator`, driven off the real per-line text as
`MudStreamParser` would emit it (telnet stripped, C1 codes stripped, prompt containers removed,
split at every `\n`, residual buffer carried across records). Script:
`<scratchpad>/replay.py` + `dump.py`. All timestamps below are capture-relative.

- Capture **A** = `session-rec.mud2.co.uk.20260810-152631.jsonl` (9 encounters, 14 per-NPC fights)
- Capture **B** = `session-rec.mud2.co.uk.20260810-161441.jsonl` (11 encounters, 11 fights)

---

## 0. First, a game rule that reframes everything (proven)

**MUD2's wielded weapon is per-fight, not persistent.** It is dropped when the fight ends, and
`wield`/`use` is refused outside a fight:

```
B +00:15:21.710 tx| use axe
B +00:15:21.900 rx| You are now using the falchion to fight!     <- during the rat fight
B +00:15:48.477 rx| You have killed the rat20.                   <- fight over
B +00:15:54.874 tx| wield best weap
B +00:15:55.188 rx| What for? You're not in a fight..!           <- REFUSED, no weapon armed
```

```
A +00:16:35.951 rx| You are now using the axe0 to fight!   (rat fight)
   ... all six rats dead ...
A +00:18:25.130 rx| You are now using the axe0 to fight!   (ram fight — printed AGAIN)
```

So `CombatStatsAggregator.BeginEncounter`'s `_currentWeapon = null` is **correct modelling**, and an
NPC-initiated fight legitimately reads UNARMED until the player wields. Do not "fix" that by
carrying the weapon across encounters — the offline reducer does carry it
(`reduce_combat.py` keeps `self.current_weapon` across sessions) and that is why `combat_fights`
shows `start_weapon` populated for every fight while the live client would not. **The offline
`start_weapon`/`weapon_used` columns are not ground truth for "was I armed at the bell".**

---

## 1. ROOT CAUSE — the flee/break wipe destroys the fight's own weapon record

**Explains: the ram fight in capture A, plus 2 fights in `RESEARCH/mud2-multi-combat.jsonl`.
Ranked first: it is the only mechanism found that reports UNARMED for a fight the player
demonstrably fought armed.**

MUD2 prints the auto-drop **before** the flee line, in the same tick:

```
A +00:19:48.293 tx| flee w
A +00:19:48.622 rx| (Persona saved on -2,079 = 44,337).
A +00:19:48.622 rx| Axe0 dropped.                       <- CombatEventKind.ItemDropped
A +00:19:48.622 rx| You have fled by going west.        <- CombatEventKind.YouFled
```

`ItemDropped` arrives while the ram fight is still `Unresolved`, so
`CombatStatsAggregator.cs:209-223` runs `_currentWeapon = null` **and**
`fight.NoteWeaponBroke()` on it. `NoteWeaponBroke()` is `WeaponUsed = null`
(`FightAccumulator.cs:159`). The 83-second ram fight — opened at +00:18:24.939, armed with axe0 at
+00:18:25.130, 7 hits / 13 misses with that axe — ends recorded as having no weapon.

The player then reads the **post-fight summary**, which is the surface most likely to be
remembered, and it says UNARMED: `SidePanelViewModel.cs:538-539` computes
`weaponText`/`IsUnarmed` from `snapshot.CurrentWeapon`, and line 566 reuses those same two values
for the `InCombat: false` post-combat view. The summary lingers until dismissed.

Same shape from a break, confirmed in the older research capture (session 51, the 16-rat pack):

```
The dagger0 breaks to bits.            <- 1785611404142
Your guard drops momentarily in your confusion.
... 7 more swings ...
You have fled by going out.            <- 1785611411223
```

`rat3` and `rat13` were open at the break. Offline keeps `weapon_used = dagger0` for both; the live
pipeline sets `WeaponUsed = null`, so `FightHistoryRecorder` persists two fights fought almost
entirely with a dagger as unarmed — poisoning `FightHistory.SummarizeByWeapon`.

**Minimal fix** — separate "what is in my hands now" from "what this fight was fought with":

1. `FightAccumulator`: keep `WeaponUsed` as the historical fact (first non-null weapon seen while
   this fight was open, never cleared). Add a distinct `WeaponLostUtc`/`IsDisarmed` for the live
   state, set by `NoteWeaponBroke()`. Nothing else calls `NoteWeaponBroke` for any other purpose.
2. `SidePanelViewModel.RefreshCombatSignals`: in the `!snapshot.InCombat` branch (line 558-581),
   derive `weaponText`/`IsUnarmed` from the primary fight's `WeaponUsed`, not
   `snapshot.CurrentWeapon`. Live (line 617) must keep using `CurrentWeapon` — being disarmed
   mid-fight is exactly what the rail exists to shout about.
3. Secondary inconsistency to close at the same time: `FightHistoryRecorder.OnCombatEvent` has
   **no `ItemDropped` case at all** (`Core/FightHistoryRecorder.cs:132-209`), so today the display
   loses the weapon on a flee-drop and the persisted row keeps it. The comment at
   `FightHistoryRecorder.cs:154-155` ("The two aggregators consume the same event stream and must
   not disagree") is already violated.

---

## 2. CONFIRMED, BUT THE READOUT IS RIGHT — the raven fight

**Explains: 1 fight (capture B, 23 s). The panel said UNARMED for its whole duration and MUD2
agrees.** This is the only fight in either capture that reads UNARMED start to finish.

```
B +00:15:54.874 tx| wield best weap
B +00:15:55.188 rx| What for? You're not in a fight..!
B +00:15:57.232 tx| k bird                       <- no "with <weapon>" clause
B +00:15:57.578 rx| You attack the raven.        <- unarmed form
B +00:15:58.475 rx| You hit the raven (10-14).
B +00:16:20.476 rx| The raven has fled by going northwest.
```

The falchion was **carried** throughout (every FEI probe in that window lists it below the
`========` separator, e.g. `+00:15:56.752`), which is what the owner remembers. It was not
*wielded*: `k bird` with no `with` clause attacks bare-handed, and the `wield` that would have
fixed it was refused because MUD2 will not arm you outside a fight. Proof it was genuinely
un-wielded rather than a client mis-read: 26 s later the identical `k bird` → `You attack the
raven.` was followed 0.198 s later by a full `You are now using the falchion to fight!`
(+00:16:24.199) — MUD2's already-wielding reply is `You're using the X anyway...`
(`reduce_combat.py:79`), and it did not appear.

**Fix is informational, not state**: two lines carrying real weapon truth are unparsed by
`CombatTracker`, and either would have let the panel explain itself instead of just saying UNARMED.

- `What for? You're not in a fight..!` — the wield-refused-out-of-combat line. Nothing in the C#
  tree matches it. Worth a `CombatEventKind.WieldRefused` so the rail can say *"arm attempt
  ignored — not in a fight yet"* instead of a silent open hand.
- `You're using the <weapon> anyway...` — a free positive confirmation of the wielded weapon,
  already known offline, unparsed live. It is the only line that can re-sync a weapon the client
  missed. **Not observed in either capture** — provenance is `reduce_combat.py` only, so treat the
  exact wording as unverified until a capture shows it.
- `weapon in use:  falchion` from `score`/`sc` (`A +00:00:42.088`, one occurrence) is a third
  authoritative channel, also unparsed live. One sample, so it is not established whether `sc`
  reports it out of combat.

---

## 3. CONFIRMED, ALSO RIGHT — the transient UNARMED head of an NPC-initiated encounter

**Explains: 5 encounters.** `NpcAggroStart` and `PlayerAttackStartUnarmed` name no weapon, and per
§0 the player genuinely is bare-handed until they wield, so the panel flashes UNARMED for the gap
between the aggro line and the wield:

| capture | encounter | opened | armed | UNARMED for |
|---|---|---|---|---|
| A | rats ×6 | +00:16:32.977 | +00:16:35.951 axe0 | **2.97 s** |
| A | ram | +00:18:24.939 | +00:18:25.130 axe0 | 0.19 s |
| B | water-snake5 | +00:08:16.481 | +00:08:17.055 falchion | 0.57 s |
| B | rats ×4 | +00:15:20.068 | +00:15:21.900 falchion | 1.83 s |
| B | raven (2nd) | +00:16:24.001 | +00:16:24.199 falchion | 0.20 s |

Correct, and arguably the most useful thing the rail can show ("you are being hit and you have not
armed"). No fix needed; listed so it is not mistaken for cause 1. If the flicker is unwanted, the
honest treatment is a distinct "not yet armed" state, not a guessed weapon.

---

## 4. THE WRAPPED-LINE HYPOTHESIS — not a cause here, but a real latent bug

**Not a cause of a single fight in these captures. It is also not dead in general — and it is not
dead for the reason the brief guessed.**

`MudStreamParser` does **not** rejoin server-wrapped rows. `EmitLine` fires a `StyledLine` at every
`\n` (`MudStreamParser.cs:600-683`); there is no rejoin anywhere, and wrapping is visibly present
in the captures:

```
A +00:16:32.440 rx| evil, black rat (rat20) bares its razor-sharp incisors at you. An evil, black rat (rat19) bares
A +00:16:32.440 rx| its razor-sharp incisors at you. An evil, black rat (rat17) bares its razor-sharp incisors at you.
```

So every anchored `^...$` regex in `CombatTracker` *would* break on a wrapped line. It simply never
happened here, because both sessions ran at the server's 99-column wrap while the fight-start
sentence is short:

- max rx line length: 99 (B), 99 (A, plus one 157-char unwrapped line)
- longest `PlayerAttackStart` observed: **61** — `You attack the zombie2, using the croquet mallet as a weapon.`
- zero orphan/split candidates across ~7k lines (searched for lines ending `using the` / `to fight`,
  starting `as a weapon`, etc.)

The width is not fixed: `MuckaConnection` sends `/T{_windowCols}` from the real window size, clamped
to **[20,160]** (`MuckaConnection.cs:145`, `GameViewModel.cs:1660`, re-sent on every resize). Below
~62 columns — a narrow desktop window, an explicit `MaxColumns`, or Android portrait — the
fight-start sentence wraps, `PlayerAttackStart` never matches, the encounter opens later from
`YouHit`'s defensive `Begin()`, and **the fight reads UNARMED for its entire duration**. That is
precisely the reported symptom, and none of the captures can rule it in or out.

**What would prove it:** capture a fight with `/T40` set. Predicted result: no `fight-start` event,
encounter opened by `you-hit`, weapon null throughout. Reported here only; the wrapped-line
abstraction is another agent's design.

---

## 5. Candidates checked and cleared

- **`kill <npc> with <weapon>` where the weapon is not held.** MUD2 substitutes silently and the
  tracker records the substitute **correctly**, because it reads the game's line, not the command:
  `B +00:00:46.233 tx| kill ws with axe` → `B +00:00:46.569 rx| You attack the water-snake4, using
  the falchion as a weapon.` (no axe carried anywhere in capture B until +00:20:16). Same for
  `use axe` → `You are now using the falchion to fight!` (`B +00:15:21.900`). Not a cause of UNARMED.
- **`use` vs `wield`.** Both produce the same `You are now using the X to fight!` line and both are
  matched. The owner's "`use` was a mistake" is about inventory, not parsing.
- **Weapon dropped/broken earlier and never re-armed (readout right, memory wrong).** Real, and it
  is the *live* half of cause 1 — during `A +00:19:48.622 → flee`, and after the dagger0 break,
  UNARMED was the truth. The bug is only that the finished fight's *record* inherits it.
- **`WeaponEquip` ordering (the documented past bug).** The comment in `CombatTracker.cs:341-350` is
  accurate for what it fixed, but a hole remains: `CombatStatsAggregator.Observe` returns early for
  any non-`FightStart` event while `!InCombat` (lines 156-161), so a `WeaponEquip` arriving before
  the client has opened an encounter is **discarded, permanently**. Reachable: `k <npc> with <weapon>`
  against an already-engaged NPC prints *only* the equip line, no attack line —
  `A +00:18:24.906 tx| k ram wi axe` → `A +00:18:25.130 rx| You are now using the axe0 to fight!`
  with no `You attack the ram`. Here the aggro line beat it by 0.19 s so it landed; had the aggro
  phrasing been unmatched, the axe would have been lost for the whole 83-second fight.
  **Unproven — not observed firing in either capture.** What would prove it: a capture where an
  aggro line is unmatched (or arrives after the player's `k X wi Y`) and the fight then opens on
  `You hit ...`. Cheap defensive fix meanwhile: in the `!InCombat` early return, latch the weapon
  from a `WeaponEquip` into a pending field and seed `BeginEncounter` from it, rather than
  dropping the event. Note the aggro line reliably *does* arrive in these captures — every
  `bares its razor-sharp incisors at you` join is paired with a matching
  `The ratN is <verb>ing at you <adv>.` in the same tick (A +00:16:32.440/977, B +00:15:20.068) —
  so this is a hardening, not an observed defect.
- **`NpcAggroStart` trailing-dots form.** `The ram is glaring at you menacingly...` (three dots)
  matches — `\.*$` covers it. No gap.
- **Dispatcher ordering between `OnInCombatChanged` and `OnCombatEvent`.** Both post through
  `MainThread.BeginInvokeOnMainThread`, FIFO at one priority, and `CombatTracker.Begin()` raises
  `InCombatChanged` before `Emit()`, so `BeginEncounter`'s `_currentWeapon = null` always precedes
  the `FightStart` that repopulates it. No race found.
