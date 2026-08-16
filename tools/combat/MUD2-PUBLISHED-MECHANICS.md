# MUD2 published mechanics

Transcribed from TheMudWiz's MUD2 Strategy Guide (v0.42, updated 2024-03-27) on GameFAQs -
the `Combat and Stats` and `Mobiles` sections.

> **These are HYPOTHESES, not ground truth.** The guide is player-derived and years old. It
> states formulas and stat tables as fact, and the figures we have been able to check do agree
> with our captures (see "Corroboration"), but agreement on a handful of creatures is not proof
> of a formula, and MUD2 could have changed under it. **Nothing here may be treated as settled
> until our own data settles it.**
>
> The tool for that is `verify_mechanics.py`, and its current findings are in
> `MECHANICS-VERIFICATION.md`. Anything this client SHOWS the player must rest on what we have
> verified; anything from this document alone belongs in analysis and design discussion, not on
> the combat rail. That ordering is not pedantry - this is a permadeath game, and a confident
> readout built on an unchecked player FAQ is exactly the kind of thing that gets a character
> killed while its owner trusts the panel.

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

**Two stat knees: strength below 30 stamina, dexterity below 40.** Both verified against our own
captures (`MECHANICS-VERIFICATION.md`). This is the mechanic behind the owner's repeated deaths to
rats - at low stamina your dexterity is falling, which raises *their* hit chance, which is why
three rats can all land on one tick instead of the usual ~0.7 of them.

**There is a third knee at 20, and it is not a stat formula.** An early draft of this document said
"neither is 20", which was wrong in the way that matters: it confused *what the engine computes*
with *what the player must decide*. Three thresholds, three different kinds of thing:

| stamina | what it is | evidence |
|---|---|---|
| **40** | dexterity begins degrading, `(40-S)/3` | formula, verified |
| **30** | strength begins degrading, `(30-S)/2` | formula, verified |
| **20** | **the survival threshold** | consequences, below |

Why 20 is real, per the owner:

- **It is where flee cost starts to fall**, because total-death risk has become significant. Above
  it you are paying the full price to leave; fleeing at or above 20 can cost 2-3 hours of play.
- **The game itself says so** - MUD2 prints its own "you might want to consider fleeing" at
  around this point. The client is not inventing a threshold the game disagrees with.
- **Many NPCs have a maximum hit in the 15-20 range**, so for most opponents this is the point at
  which the next single blow can kill outright.
- **It flips NPC aggression.** Several creatures go from peaceful to hostile against a player this
  wounded - which is `RATE`/`PACIFICITY` (section 5) crossing its threshold, computed against your
  degraded stats. So the stat knees at 40 and 30 are what *drive* the danger at 20.
- **A newly-arrived NPC gets a surprise blow.** Even if the current opponent provably cannot hit
  for more than 10, a hostile-capable creature walking in will attack and will likely land 5-15.
  The owner's read is that surprise blows skew higher when an NPC strikes a player than the
  reverse - unverified, and worth testing once we can identify surprise blows in a capture.

The owner's own tally: **outside rats, of 5 occasions at exactly 20 stamina, 3 cost the
character.** That is the number that matters. It is a small sample and it is also lived
experience in a permadeath game, and it outranks any formula here for the purpose of deciding
what the panel shouts about.

**Consequence for the rail:** 40 and 30 explain *why* you are losing; 20 is *when to act*. They
are not competing thresholds and the panel should not collapse them into one scale.

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
the intended range is 0-10%.

### The owner's model, which is more precise and does not agree in shape

Stated from play experience, in **absolute stamina** rather than percentage of maximum:

- **Maximum loss is 10% of current score.**
- That maximum is **flat all the way down to 20 stamina**.
- **Below 20 it drops quickly.**
- At about **7 stamina** the loss is small - the owner estimates 500-600 points on a ~46,000 score,
  so roughly 1.2%.
- **Below about 6 stamina, fleeing is free.**

Measured against the one flee in the corpus: score **46,416 -> 44,337, exactly -2,079, at 19/105
stamina**. That is **4.48% of score**, where the flat maximum would have been ~4,642.

**So the cliff sits exactly at the survival threshold, and it is brutal: fleeing at 20 costs more
than twice what fleeing at 19 costs.** That is a genuinely perverse incentive structure and it is
worth understanding rather than displaying - at 20 stamina you are simultaneously one blow from
death and paying the maximum price to leave, and the game rewards holding for one more tick at
precisely the moment holding is most likely to kill you. (See `COMBAT-RAIL-SPEC.md` 6a; this is a
further reason 20 is the threshold that matters.)

### Absolute stamina or fraction of maximum? - the open test

The guide's bands are **percentages of maximum stamina**; the owner's thresholds are **absolute**.
On this character they nearly coincide - max stamina 105, so the guide's 11-25% band spans 12-26
and its free band ends at 10, against the owner's 20 and 6 - which is presumably why a FAQ author
with a ~100-stamina character wrote it in percentages at all.

**They diverge sharply for anyone else.** A low-level character with 30 maximum stamina would, on
the guide's reading, flee free below 3 and pay half up to 7; on the owner's reading they flee free
below 6 and pay half up to 20 - two thirds of their whole bar. One reading is very wrong for new
characters, and nothing in this corpus can say which.

Settling it needs a flee on a character with a materially different maximum stamina. Until then
`verify_mechanics.py` reports flee cost as INSUFFICIENT, which is honest, and the owner's absolute
figures are the ones to trust for this character because they come from play rather than from a
transcription.

**Does the threshold scale above 100 maximum stamina?** The owner's own follow-on hypothesis, and it
is a good one: maximum stamina is permanently boostable to 120, they have never tested whether going
above 100 changes anything, and a threshold that is "20" on a 100-stamina character is exactly 20%.
This character sits at 105, so it cannot distinguish the two readings either. Untested, and worth
testing before anyone hard-codes 20.

### The two models are not reconcilable at the top end

Worth stating plainly, because it makes the guide's table the weaker source rather than the
tie-breaker:

Our one data point is **4.48% of score lost at 19/105 stamina**. Under the guide's bands that is the
`11%-25%` row at a 50% modifier, so the `100%` base rate would have to be **8.96%** - which makes a
full-health flee, at the published `400%`, cost **~36% of total score**. The owner's lived figure is
that the maximum possible loss is **10%**.

Run it the other way: if the maximum really is 10% and the guide's `400%` is right, the base rate is
2.5%, and the `11%-25%` row predicts **1.25%**, or ~580 points. We measured 2,079.

**Neither direction works.** No single base rate satisfies both the published modifier ladder and the
observed loss. Either the guide's top-end modifiers are wrong, or its bands do not describe the same
quantity the owner is describing. Our measurement fits the owner's model - flat 10% to 20, steep drop
below - and does not fit the guide's structure at all.

### Designing the experiment - and its price

Four questions, in order of value:

1. **Is the boundary at 20 absolute, or 25% of maximum?** On this character those are 20 and 26.25.
   A single flee at **22-26 stamina** separates them: the absolute model charges the full 10%, the
   fractional model charges about half.
2. **Is the maximum loss 10%, or 4x a base?** One flee at full health answers it, and the two
   predictions differ by a factor of three and a half.
3. **Does anything change above 100 maximum stamina?** Needs a boosted character.
4. **Absolute or fractional in general?** Needs a character with a very different maximum.

**The cost is proportional to current score, which is the whole trick.** These experiments are
ruinous on a 46,000-point necromancer - question 2 alone costs ~4,600 points - and nearly free on a
fresh character, where 10% of a few hundred points is pocket change. A low-score character can map
the entire curve for less than one bad fight costs at level 9.

So: **do not run these on the main persona.** The right instrument is a throwaway character, and the
data it produces is worth more than the same data bought at high score, because the low-score
character also has a low maximum stamina - which answers questions 1 and 4 at the same time.

**One earlier disagreement was mine, not the data's.** I reported the measured rate as conflicting
with a remembered "1300 of 13,000". Those are two points on a steep curve at different stamina, not
two estimates of one quantity, so there was never anything to reconcile.

### Where the points go

**They are not destroyed - they are paid to the attackers you fled from.** Per the owner's own
in-session annotation, fleeing the ram gave the ram points and potentially levelled it, and killing
it afterwards in revenge returned only a fraction: 313 recovered against 2,079 lost.

This resolves an anomaly `verify_mechanics.py` flagged and could not explain. The ram kill awarded
**313 points against a published `Points` value of 106**, while every other species matched its
table entry exactly. The ram had been *promoted by the owner's own flee* moments earlier. So the
bestiary's `Points` is the value of a creature at its base rank, and a creature that has eaten a
player's flight is worth more than the table says.

**Creature value and rank are directly queryable** with the `value` command, per NPC. That is the
route to grounding this - and to explaining the +4 seen on each of the water-snake's failed escapes,
which the same annotation suggests is the same mechanism running the other way.

**The PvP shape of this is severe.** Points bleed from the fleeing player to the attacker, so
provoking a high-level player into an early flight is a strategy: a level-9 necromancer who runs
loses 3-4,000 points and hands the attacker several hundred. A player who instead stands and loses
gives up a couple of thousand - 60-90 minutes of ordinary play. Any future PvP-facing surface has to
be designed knowing that *making the other player flee* is itself the win condition, which is the
opposite of the PvE instinct that fleeing is merely a personal cost.

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

Close enough to suggest that **summed bracket midpoints approximate damage in stamina points,
and the published `STA` is close to the real pool.** Small creatures over-read (a dragonfly with
4 stamina takes a 17-point blow) because a single hit overshoots, and creatures finished off
after a chase under-read because most of their damage was dealt in an earlier fight.

Note what this is and is not. Seven creature groups agreeing, most at n=1, is consistent with
the guide being right; it does not establish that it is right, and it says nothing at all about
the 136 rows of `bestiary.tsv` nobody in this corpus has fought. Treat a published `STA` as a
prior to be checked, not a number to display.

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
