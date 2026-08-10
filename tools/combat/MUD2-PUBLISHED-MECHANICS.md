# MUD2 published mechanics

Transcribed from TheMudWiz's MUD2 Strategy Guide (v0.42, updated 2024-03-27) on GameFAQs -
the `Combat and Stats` and `Mobiles` sections. Player-written, but it states formulas and
per-creature stat tables as fact rather than estimate, and every figure in it that our own
capture corpus can check, checks out (see "Corroboration" below).

Companion data: `bestiary.tsv` - all 143 rows of the guide's mobile table, tab-separated.

**Access note:** GameFAQs returns 403 to non-browser fetches, and web.archive.org is
unreachable from the agent tooling. The pages were supplied as saved HTML by the owner. If
these numbers ever need re-checking, expect to save the pages from a browser again.

---

## 1. What this document corrected

**A rat has 25 stamina.** An earlier note in `COMBAT-RAIL-SPEC.md` and in
`NpcHealthRungs`' own remarks claimed a rat0 stayed `critically injured` "from 407 to 560
points of damage". That was wrong, and the owner spotted it as implausible on sight.

The error: the analysis query accumulated damage per `(capture, npc-name)` and never reset on
a fight boundary. MUD2 reuses instance names (`rat7` is the same name every reset), so a
session with a dozen separate fights against `rat7` summed into one figure. Re-run with the
reducer's own fight segmentation (`combat_fights`), the **largest damage total in any single
fight in the entire corpus is 108**.

The lesson is procedural, not arithmetic: per-instance analysis over a capture MUST be scoped
to a fight, because the instance name is not unique over time.

## 2. Effective strength

Determines damage dealt, and whether you can shift certain obstacles.

```
base    = S + BS                       S = base strength, BS = bonus (vials/pillar)
step 1  -= W                           W = total inventory weight in kg, rounded DOWN
step 2  -= (W^2 + 25) / 50
step 3  -= sum(Wn / 2)                 Wn = each carried object's weight in kg, rounded down
                                       objects INSIDE a container are ignored; their weight
                                       counts toward the container's own weight
step 4  -= (30 - SD) / 2               only if SD < 30, where SD = stamina + (drunkenness / 8)
```

All fractions round down. **Floor: effective strength cannot drop below 50% of base.**

Note the perverse consequence of step 3: putting objects *into* a container costs MORE
effective strength than carrying them loose, because rounding each object's weight down
individually gives more opportunities to round down. (Containers help dexterity, not strength.)

## 3. Effective dexterity

Determines chance to hit, and navigation of hazardous terrain.

```
base    = D + BD
step 1  -= sum(1 + (On * Mn))          per carried item. On = objects inside container n,
                                       Mn = container modifier:
                                         closed container 25%, open container 50%, boat 100%
                                       a non-container contributes exactly 1
step 2  -= D / 10                      if unable to perceive surroundings (blind, unlit room)
step 3  -= D / 2                       if unable to perceive your TARGET
step 4  -= DR / 10                     DR = drunkenness
step 5  -= (40 - S) / 3                only if current stamina S < 40
```

All fractions round down. **Floor: 25% of base.**

Step 3 is per-opponent, not a property of you: `score` shows your general effective dexterity
and cannot show the halving that applies only against an opponent you cannot see. Two
mechanics defeat it - `Magic-Seer` mobiles see through everything and cannot be blinded,
`Keen-Smeller` mobiles do not lose the half against unseen opponents. Both are flagged per
creature in `bestiary.tsv`.

**Two different stamina knees, and neither is 20:** strength starts degrading below **30**
stamina, dexterity below **40**. This is the mechanic behind the owner's repeated deaths to
rats - at low stamina your dexterity is falling, which raises *their* hit chance, which is
why three rats can all land on one tick instead of the usual ~0.7 of them.

## 4. Combat

- **Rounds.** Each combatant gets one chance to hit per round (usually).
- **Chance to hit** = `Dy / (Dy + Do)` - your effective dexterity over the sum of both.
- **Damage on a hit** = a random value in `1 .. (CS / 6) + 1`, where `CS` = effective strength
  + the weapon's own strength. So 100 effective strength with a 30-strength weapon is 130 CS,
  giving 1-22 damage. **This is what the client's `(20-29)` brackets are bucketing** - the
  observed bracket ceiling of 29 is consistent with a high-CS blow.
- **A sleeping opponent** is hit with 100% certainty and takes **+50% damage**.
- **Magical resistance** = your level (a Mage has 10). No magic at all grants **+3**. It
  decays as stamina is lost: `((Smax - Sc) * 10) / (S * 3)` - roughly 3 points of resistance
  lost at low health.
- **Round period equals the regeneration tick period.** Expected damage per round is
  `P(hit) * damage - opponent regeneration`, so a creature with regeneration can outheal a
  weak attacker outright.

## 5. Mobile aggression

A mobile decides to attack by walking a fixed sequence: `DISLIKES` membership, then your level
vs its `CLEVEL`, then a `RATE` check modified by `PACIFICITY`; failing that, vendetta/provoked
(`VPACIFICITY`), then you-are-asleep, then you-are-already-fighting (`FPACIFICITY`).

`RATE` is the mobile estimating how many rounds it would survive against you versus how many
you would survive against it, expressed as a ratio. `PACIFICITY` scales its estimate of YOUR
side: **100 is an honest appraisal, below 100 is optimistic, above 100 is pessimistic.**

- `PACIF` **<= ~20** - will attack a healthy, full-stat player.
- `PACIF` of several hundred - docile.
- `PACIF` of 1 - suicidal; will engage regardless.

Worked example from the guide, Zombie1 (80/40/40, 1 regen) vs a 100/100/100 player:
player hits 71% for a mean 9, zombie hits 29% for a mean 7.5; player deals 5.4/round after
regen, zombie 2.2/round; zombie lasts 7 rounds, player 45; **rating 15%**.

**Design consequence:** `RATE` is computable from data we already hold plus `bestiary.tsv`.
The opportunistic-join warning in the plan ("being visibly wounded attracts new attackers")
is not a heuristic - it is this function crossing a published threshold.

## 6. Fleeing

Fleeing removes a portion of the player's total points, scaled by remaining stamina:

| Remaining stamina | Point-loss modifier |
|---|---|
| 100% | 400% |
| >75% | 200% |
| 26% - 75% | 100% |
| 11% - 25% | 50% |
| 0% - 10% | 0% |

The guide's last row literally reads `0% - %5`, a typo; the band above it starts at 11%, so
the intended range is 0-10%. Corroborated by the owner's own prior observation that fleeing is
free below ~6.5 stamina.

**Fleeing at full health costs four times the base rate** - which is exactly the accident that
cost the owner 1300 of 13000 points and a level, fleeing a zombie at 90/100.

This stays out of the UI. `COMBAT-RAIL-SPEC.md` section 10 puts flee cost out of scope
permanently: the player knows fleeing is expensive, and a price tag at the decision moment is
cognitive burden at the worst possible instant. This table is documented so nobody
re-introduces a "fleeing is cheap here" affordance, which is the same reason
`FleeCostLadder` is retained and drives nothing.

## 7. Per-creature stats

`bestiary.tsv`: `Mobile, Area, STR, DEX, STA, Dislikes, Level, PACIF, VPACIF, FPACIF, Speed,
React, Interv., MS, KS, Points`.

`Speed` is ticks between actions; `React` is the countdown a mobile drops to when it notices
someone (low React = reacts fast); `Interv.` is the chance it blocks your movement through its
room; `Points` is the score awarded for killing it.

Spread of stamina pools, for scale against a player's ~100:

| creature | STR | DEX | STA | points |
|---|---|---|---|---|
| Dragon | 200 | 200 | **800** | 1,950 |
| Giant0 | 310 | 40 | 280 | 678 |
| Wolf | 140 | 70 | 150 | 298 |
| Thief | 90 | **125** | 80 | 202 |
| Ram0 | 80 | 60 | 100 | 106 |
| Water-snakes | 60 | 70 | 90 | 86 |
| **Rat0** | 30 | 75 | **100** | 42 |
| Zombie0-7 | 80 | 40 | 40 | 38 |
| **Rat1-21** | 30 | 75 | **25** | 22 |
| Dragonfly | 5 | 130 | **4** | 10 |
| Firefly0-4 | 8 | 120 | **1** | 1 |

Two things worth reading twice:

- **Rat0 has four times the stamina of every other rat** (100 vs 25) on identical STR/DEX.
  The client's per-instance history already treats `rat0` as its own thing rather than
  averaging it into `rats` - that decision was made from observed difficulty, and this is why
  it was right.
- **The dangerous creatures are dangerous through DEX, not STA.** A thief has 125 dexterity
  against a player's 100: it hits ~56% of the time while the player hits ~44%. Stamina pools
  tell you how long a kill takes; dexterity tells you who wins.

## 8. Corroboration against our own corpus

Per-fight cumulative bracket-midpoint damage in fights that ended in a kill, from
`combat_fights` (reducer-segmented), against the published pool:

| group | published STA | corpus median damage to kill | n |
|---|---|---|---|
| snakes | 90 | 100.5 | 6 |
| rams | 100 | 98.5 | 1 |
| banshees | 80 | 82.0 | 1 |
| zombies | 40-50 | 49.0 | 8 |
| dwarves (dwarf21) | 40 | 39.0 | 1 |
| rats (mixed with rat0) | 25 / 100 | 29.2, max 106.5 | 22 |
| mice | 10 | 14.0 | 1 |

Close enough to conclude that **summed bracket midpoints approximate damage in stamina points,
and the published `STA` is the real pool.** Small creatures over-read (a dragonfly with 4
stamina takes a 17-point blow) because a single hit overshoots, and creatures finished off
after a chase under-read because most of their damage was dealt in an earlier fight.

## 9. What the guide does NOT contain

**The wound descriptions are absent from both pages.** No mention of `superficially injured`,
`covered in wounds`, `close to death`, or any ordering or percentage mapping between them.
Searched both pages for every one of those phrases: nothing.

So the seven-rung ladder in `NpcHealthRungs` remains derived from our own corpus - transitions
observed within reducer-segmented fights - and is not corroborated by any published source. It
should be treated as the best available reading of the game's behaviour, not as documented
fact. Its ordering evidence is 62 worsening transitions against 4 improving ones (all of the
latter exactly one rung), with zero transitions contradicting the rung order.

## 10. Open opportunities this creates

Not built, deliberately - each is a scope decision for the owner:

1. **Replace `EstimatedStaminaPool` with the published `STA`.** The client currently estimates
   an NPC's pool as the median damage of fights that ended in a kill, because "MUD2 never
   reports NPC stamina" - which is true of the protocol but not of the world. A lookup would
   be exact, available on the first ever encounter, and would make "how close is this thing to
   dropping" a real number instead of a thin-sample guess. The comments in
   `FightHistory.EstimatedStaminaPool` and `CombatContracts.CombatLiveView` assert the
   estimate is "the only route"; that is now false and they say so.
2. **Anchor the health ladder to real health.** With a known pool and exact damage dealt, the
   pips could show measured remaining stamina rather than an ordinal descriptor rung - or
   better, show both and let disagreement between them be the interesting signal.
3. **Compute `RATE` live** for the opportunistic-join warning, instead of inferring
   "wounded players attract attackers" behaviourally.
4. **Show the real hit-chance split** (`Dy / (Dy + Do)`) once the opponent's published DEX is
   known - the exchange bars currently compare observed rates with no idea of the true one.
5. **Weapon strengths** are on the guide's `weapons` page (not yet transcribed) and would
   complete the `CS` calculation, making outgoing damage predictable rather than historical.
