# Combat corpus queries, September 2026

Every script here was run against the live corpora and its output is stored beside it. That is the
whole point of the directory: the figures in this project's combat comments could not be checked
because the queries behind them were never saved, and three of them turned out on re-query to be
wrong. Add a script here before you put a number in a comment.

## Corpora, and which one a script means

Three exist and they are not interchangeable. Naming the wrong one is how two different-looking
figures turn out to be the same measurement, and how a claim becomes uncheckable.

| Corpus | What | Span |
|---|---|---|
| `~/.mucka/combat/mucka.db` | `swings` (per-swing FES state), `fights` (score before/after) | `swings` from 2026-08-14 |
| `~/.mucka/clogs/` | per-encounter event logs | `NpcHealth` only from ~2026-08-10 |
| `%LOCALAPPDATA%\Temp\mucka` | raw session recordings — the only place with verbatim wire text | — |
| `~/.mucka/combat/combat.db` | old offline reduction of `RESEARCH/mud2-multi-combat.jsonl` | one day, 25 fights |

`load_clogs.py` builds `clogs.db` from the clog tree; `fights.py` and `common.py` are the shared
segmentation helpers. The two `.db` files those produce are derived and deliberately not committed —
re-run the loader.

**A trap worth stating.** "Session recordings + clogs" is a real corpus definition and is not the
same as either alone: the `The value of` line count is 458 over both, 431 over recordings only, 463
once `RESEARCH/` joins. A figure without its corpus is not reproducible.

## What was measured

Tick lattice is **2000 ms**, fitted jointly over 26 sessions of ≥15 min, median 1999.98 ms with mean
absolute residual 10–40 ms. An engaged NPC produces a swing line on **~49.6%** of the ticks it is
engaged (8,275/16,669), and that is flat 46–51% across all 12 species with n>100 — the spread across
species is not significant (p=0.194). So the 3.7× range in damage-per-tick between species comes
entirely from hit rate and damage size, not from swing frequency.

Damage taken per hit is exact — the wire prints post-hit stamina — and per-species means over 2,452
blows give damage per engaged tick of roughly: rat 0.55, zombie 0.85, ram 0.92, snake 1.07, dwarf
1.26, banshee 1.45, goblin 2.05, thief 2.07. CV of per-tick damage runs 2.2–3.5, so a *live* dpt
readout would need 93–323 engaged ticks (3–11 minutes of continuous engagement) to reach ±20%.

**Flee cost keys on stamina as a fraction of maximum, not on absolute stamina.** 40 flee events with
score before and after, sorted by fraction, zero misordering: free below ~6% of max, ~2.0–2.4% of
score from 6.7–15.2%, ~4.1–5.0% from 16%. The `6.5` constant in the client is that ~6% boundary as
it happens to land on a 105-max persona — a fraction laundered into an absolute.

**Individual NPC instances do not vary.** ICC ≈ 0 for rats, snakes, zombies, banshees and dwarves,
and the power check says a 20% instance-to-instance spread would have been detected. So "this one is
hitting harder than normal" is not a detectable event for any animal. Consequently the species prior
beats the running current-fight mean at *every* sample size and is never overtaken (predicting the
rest of the fight: 0.19 vs 0.48 of the species mean at one blow, 0.21 vs 0.23 at eight). The prior is
worth 8–30 blows of live evidence and only 39 fights in the corpus ever reach 8. Thieves are the sole
exception (ICC 0.478, current wins 73%) — and they are the species that arms itself mid-fight.

**The player's weapon does not separate outgoing damage.** Between-weapon distances sit inside the
same-weapon split-half noise floor at every sample size available; you need ~120 hits per cell and
exactly two cells out of 6,429 outgoing swings reach it. What does move the histogram is STR — the
20-29 bucket runs 1% below STR 80 to 12% at STR 100+ (n=3,554, p=0.0002).

**`npc_weapon` is humanoid-only and announce-only.** Populated on 8.9% of incoming rows; every animal
is exactly 0.0%; it is set by an equip announcement and never cleared, so NULL means "not announced",
never "unarmed". No line in 36,291 clog events announces a disarm.

**Rung descent is monotonic enough to model as monotonic** — 0.75% of clean steps go up. Zombies are
the real exception at 7.4% of single-instance fights (p=0.0002, with instance-renumbering,
adjacent-kill and mixed-vocabulary confounds each tested and eliminated). That is an observation
about rungs; it is not a measurement of regeneration. But **39% of fights emit no rung line at all**
(ceiling ~74% however long they run), and the two reads a rate needs do not arrive until ~tick 6, by
which point 37% of fights are over. A fixed per-species constant beats the fight's own measured
descent rate (41% vs 53% median error), so measuring it live is worse than not measuring it.

**There is no rung-to-stamina mapping anywhere.** 7 rungs over 3,599 observations establish the
*order* only; `npc_stamina_reads` has 0 rows and the only 4 numeric diagnose brackets are pre-combat
and unpaired. Any equal-sevenths assumption is an assumption.

## Two cautions

`NpcGroups.Normalize` folds `large rat` (mean 3.34) into `rat` (2.62). That fold alone manufactures a
significant "instances vary" result for rats — F=2.64, p=0.000, collapsing to F=1.00, p=0.460 once
the kinds are separated. Anything keyed on normalised groups can produce a real-looking signal out of
nothing. Check for it.

`verify_mechanics.py` defaults to `combat.db`, which is 25 fights from one day and predates the
`fights.score_at_start/score_at_end` columns. That is why it still reports flee cost as n=1 while
`mucka.db` holds 40 events, and it is the structural reason the comment figures went unchecked for so
long. Point it at `mucka.db`.
