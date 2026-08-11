# Extended combat info - seed brief

**Status: not designed. Deliberately deferred to its own session.** This file exists so the
requirement and its motivating evidence survive the context boundary, not to prescribe a design.

Scope boundary: the **Combat Rail** (`COMBAT-RAIL-SPEC.md`) is the at-a-glance instrument and is
in progress. Everything below is the *other* surface - the one you read when you are not being
hit. Do not merge them; the rail's whole discipline is that it assists a glance and gets out of
the way.

## What the owner asked for

Two distinct things, and they are not the same:

1. **A detailed combat-analysis page.** A real study surface, with time to be dense and time to
   be beautiful. Post-fight review, weapon-vs-creature history, the corpus-wide picture.
2. **Indicators and hoverables that let you probe** from wherever you already are - "this fight
   vs previous fights with this NPC, or with similar NPCs". Progressive disclosure on the
   existing surfaces rather than a separate destination.

## The motivating problem: telling a mini-boss from a bad sample

The owner's framing: something that can *"potentially draw out which one of the rats or dwarfs
is a mini-boss/boss type npc rather than just misunderstanding it as a one-off out-of-family
fight"*.

This is a real and already-evidenced phenomenon, not a hypothetical:

- `bestiary.tsv` publishes **rat0 with 100 stamina against every other rat's 25** - identical
  STR and DEX, four times the pool. Dwarves vary hugely across instances too (dwarf27 has 100
  stamina, dwarf19 has 10).
- The client ALREADY splits per-instance from per-group history for exactly this reason
  (`FightHistory.SummarizeInstance` vs `SummarizeByWeapon`, and
  `CombatHistoryContext.InstanceSampleFloor = 2`). That split was made from observed difficulty
  before anyone had seen the published table. It was right.
- So the design problem is not "detect an outlier" - it is **surfacing the distinction the data
  model already makes**, in a way that reads as "this one is different" rather than as noise.

Statistical care required: a single hard fight against `rat0` and a single unlucky fight against
`rat7` look identical at n=1. The honest surface has to distinguish "this instance is genuinely
harder" from "this sample is thin", which is the same medians-with-n discipline
`FightHistorySummary` already keeps. Do not let it assert a boss on one bad fight.

## Constraints that carry over

- **Invariant #0 and #1 still apply.** Nothing focusable on a live surface; no UI-thread work on
  the typing path. An out-of-combat analysis window MAY float (`DESIGN_FINAL.md`); no live combat
  surface may.
- **Verified before displayed.** Per `MUD2-PUBLISHED-MECHANICS.md`, the published FAQ figures are
  hypotheses. Analysis surfaces may reason about them openly and label them as unverified; the
  rail may not show them at all until `verify_mechanics.py` settles them.
- **Permanently out of scope, on this surface too:** flee cost, flee statistics, points at risk.
  See `COMBAT-RAIL-SPEC.md` section 10. The reason is not that the number is unknown - it is now
  fully documented - but that showing it is cognitive burden at the worst moment.
- The **post-combat / chase surface** was pushed out of the rail explicitly to land here.

## Inputs that will exist by then

- `verify_mechanics.py` + `MECHANICS-VERIFICATION.md` - which published claims our own data
  actually supports.
- `bestiary.tsv` - 143 creatures with STR/DEX/STA/pacificity/points.
- `MUD2-PUBLISHED-MECHANICS.md` - the formulas, as hypotheses.
- `SESSION-NOTES-20260810.md` - two reduced sessions with the owner's own `//NOTE` annotations.
- The fight corpus itself, growing per session.

## Open questions for that session

1. Is the analysis surface a separate window, a mode of the rail, or a page in the main window?
   (Prior art: the rail is a docked right-edge panel toggled by `$clog on`.)
2. How does a hoverable stay compatible with Invariant #0 - hover has no focus implications, but
   anything clickable does.
3. Does the study surface read the live DB or a snapshot? Live read/write was already decided for
   the rail (plan decision D2).
4. What earns the label "this one is different", in numbers, and at what sample size does the
   panel dare to say it.
