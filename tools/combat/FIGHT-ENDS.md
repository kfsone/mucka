# The seven ends of a fight

Owner-stated model, 2026-08-19, with a verbatim capture for every case. This supersedes every earlier
description of fight-end detection in this directory -- in particular the conclusion in
`SESSION-NOTES-20260810.md` ("Lines the parser does not recognise", item 1) that a failed enemy flee
should be treated as *not* ending the fight. That conclusion was wrong, and this document exists
because the wrong version was written down persuasively enough to survive in the code for months.

A "creature" is any mobile, and the player is itself a creature -- hence the `U`/`C` pairing in the
outcome names (`U` = you, `C` = creature).

## The rule

MUD2 ends a fight in exactly seven ways. **Every one of them prints inside a single frame -- one
prompt to the next -- always.**

That guarantee is the whole design. It is why `CombatTracker` needs no timer, no idle window and no
"lull" state to decide a fight is over: the terminator is never separated from its fight by a frame
boundary, so what the frame says is the complete answer. A fight not ended by a line in the frame
really is still running.

| # | Case | Line | Scope | `FightOutcome` |
|---|------|------|-------|----------------|
| 1 | Creature passes on | `You have killed the X.` | that creature | `Kill` |
| 2 | Creature flees | `The X has fled by going <dir>.` | that creature | `CFled` |
| 3 | Creature's flee fails | `The X has fled by trying to go <dir>.` | that creature | `CFledFail` |
| 4 | Player flees | `You have fled by going <dir>.` | **all fights** | `UFled` |
| 5 | Player's flee fails | `You have fled by trying to go <dir>.` | **all fights** | `UFledFail` |
| 6 | Mutual withdraw | `The X withdraws from your fight, and so do you.` | that creature | `Withdraw` |
| 7 | Player dies | `The X has killed you.` / `You have been killed by ...` | **all fights** | `Died` |

Cases 4, 5 and 7 change the *player's own* combat state, so MUD2 returns the fight count to 0 and
every open fight ends at once. The other four are per-creature and leave the rest of a pack swinging.

Case 6 reads like a player-side terminator -- the player agreed to it -- but it is an agreement with
**one** creature. It does not zero the count.

### A new encounter can begin in the same frame

Nothing waits for the frame to end. MUD2 will kill the last creature of one encounter and have the
next thing turn on the player in the same output:

```
*You hit the rat for (10-15).
The rat 21 has passed on.
(Updating persona +24)
...
The rat 20 is snarling at you aggressively.   <- new fight; if rat21 was the last of the previous
                                                 encounter, this is a NEW encounter
*
```

## Why case 3 was wrong for so long

`The X has fled by trying to go <dir>.` differs from case 2 by one word and means the opposite: the
creature is still standing in the room. From that, an earlier analysis pass concluded the *fight* was
also still running, and pointed at a real capture in support -- a water-snake that attempted it seven
times in thirteen seconds, which under an immediate close became eight recorded encounters instead of
one continuous fight.

The reasoning was inverted. Eight re-engagements **are** eight encounters: each is its own frame, its
own attack command, and its own weapon selection. And the price of pretending otherwise was worse than
the fragmentation it avoided -- since nothing else in the frame can close a fight, a player who simply
walked away instead of re-attacking left the client "in combat" with no line left that could ever end
it, until reset or logout forced it. That is precisely the frame the owner reported:

```
The water-snake3 looks close to death.
The water-snake3 hits you (82/115).
The water-snake3 has fled by trying to go up.
You can fight it no longer.
```

`You can fight it no longer.` is a **trailing acknowledgment**, never a terminator in its own right: it
names no creature, so promoting it would close other still-live fights in a pack. Case 3 closing its own
fight is what makes that line redundant rather than load-bearing -- which is the right place for a line
that cannot say who it means.

Corollary from `MECHANICS-VERIFICATION.md` section 3, unaffected and still interesting: each failed
enemy escape paid **+4** points, and appeared to come out of the same budget as the kill award (79
instead of 86 on the eventual kill).

## A failed flee is not free

Case 5, verbatim from `session-rec.mud2.co.uk.20260819-000137`, all one frame:

```
flee n
You cannot go north from here.
You have changed experience level from protector to novice.
(Persona saved on -102 = 98).
You have fled by trying to go north.
*
```

102 points **and** a whole experience level, for no escape at all. A successful flee in the same
session cost 83 (`(Persona saved on -83 = 15).`). Whatever the cost function is, failure is not a
discount -- worth remembering before any UI ever describes fleeing as cheap.

**It costs the weapon too**, and there are at least two distinct reasons a flee fails. A second frame
(owner, 2026-08-19):

```
The giant1 looks covered in wounds.
*flee o
Your way is blocked by the giant1.
Two-handed sword dropped.
You have fled by trying to go out.
*qq
```

So the full price of a failed flee is: points, possibly an experience level, the weapon out of your
hands, every fight you were in ended -- and you are still standing in front of whatever you were
running from, now unarmed. The owner quit one heartbeat ahead of dying to exactly this. That is what
`flee-failed.wav` (see `Resources/Raw/sounds/README.md`) exists to announce: the text differs from the
success line by two words and arrives while the player is reading fast and about to act on the belief
that they got away.

Two failure reasons are on record, and they are worth telling apart if anything ever acts on them:

| Line | Meaning |
|------|---------|
| `You cannot go <dir> from here.` | No such exit -- the player asked for a direction that does not exist |
| `Your way is blocked by the X.` | The exit exists but a creature is standing in it |

The weapon drop already reaches the client as an ordinary `ItemDropped` event, so the "you are now
unarmed" half is handled. **Neither reason line is parsed**, and nothing yet distinguishes them.

## Outcome vocabulary

The live client (`mucka.db.fights.outcome`) and the offline reducer
(`combat.db.combat_fights.outcome`) now use **identical** spellings, being the `FightOutcome` member
names. They previously disagreed in case and separator (`Killed` vs `killed`) despite `schema.sql`
claiming they were directly comparable. Existing rows were migrated in place by
`tools/combat/migrate_outcomes.py` (1106 rows; `.pre-outcome-rename.bak` copies alongside each
database).

`CFledFail` and `UFledFail` were **not** back-filled and cannot be. No rollup row records which line
ended the fight, and the rows that should have been `CFledFail` were written as `Unresolved` or never
written at all. Re-reducing the original captures with the current `reduce_combat.py` is the only
honest way to recover them.

## Related: identify and fightbrief

Fight-end lines are terse in every mode, but almost nothing else is. With `fightbrief` off, every
swing arrives as narrative prose that no parser here matches (`Your limp, frontal attack is
indifferently killed by the rat.`, `Damage range: 5-9.`); with `identify` off, four rats in a pack all
call themselves `the rat` in prose while the FEW panel lists `rat21 rat19 rat16 rat17`, collapsing
four fights onto one participant. Both are now sent in the post-character-select setup batch
(`MudSession.SetupCommands`); before 2026-08-19 neither was, which is why the two sessions that
prompted this document recorded almost no swings.

Both are **sets, not toggles** (owner, 2026-08-19), so the batch is safe to fire unconditionally on
every game-mode entry -- no "is it already on?" probe needed, and no risk of turning off a setting a
persona already had.

Their confirmation frames are swallowed like the rest of the batch. Each has **two** wordings, since
MUD2 answers differently when the setting was already on -- and because these are sets that the batch
re-sends every entry, the "already" pair is the ordinary case from the second login onward:

```
You'll now get object identification numbers where applicable.
You're already getting object identification numbers where applicable.
You'll now get brief descriptions of fights.
You're already getting brief descriptions of fights.
```
