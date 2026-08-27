# The ends of a fight

Owner-stated model, 2026-08-19, with a verbatim capture for every case, plus an eighth end found on
2026-08-26. This supersedes every earlier description of fight-end detection in this directory -- in particular the conclusion in
`SESSION-NOTES-20260810.md` ("Lines the parser does not recognise", item 1) that a failed enemy flee
should be treated as *not* ending the fight. That conclusion was wrong, and this document exists
because the wrong version was written down persuasively enough to survive in the code for months.

A "creature" is any mobile, and the player is itself a creature -- hence the `U`/`C` pairing in the
outcome names (`U` = you, `C` = creature).

## The rule

**Every end prints inside a single frame -- one prompt to the next -- always.**

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
| 8 | You lose the creature | `The X drops dead, poisoned...` (one member of an open family) | that creature | `NoMore` |

Cases 4, 5 and 7 change the *player's own* combat state, so MUD2 returns the fight count to 0 and
every open fight ends at once. The other five are per-creature and leave the rest of a pack swinging.

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

## Case 8: you lose the creature (`NoMore`)

**The name is the owner's, and the reasoning with it.** This is not losing a *fight* -- "Lost" alone
reads that way, and it is the opposite of what happened. You lost the *creature*: it stopped being
available to fight, and to kill, and (on the evidence so far) credited you nothing. `Died` was too
narrow for the family and collides with the player's own death. Do not confuse it with `EndOther`,
which comes from the same "no longer" sentence but only means MUD2 stopped a fight without saying
why -- there the creature may be standing right in front of you.

**One member observed, more expected.** Poison is the observed one, below. The owner's next candidate
is poisoning the ogre with alcohol -- "a lot of juggling and luck" to reproduce -- and its wording is
likely to differ. So the outcome is named for what the player lost rather than for how it happened,
the regex behind it matches `The X drops dead, <cause>...` for any cause rather than the word
"poisoned", and a genuinely new wording is a new `CombatEventKind` at most: it maps to this same
outcome. That split is the point of having an event kind (an observed line) and an outcome (what the
fight was worth) rather than one thing.

### The observed member: poison

Found 2026-08-26, by the owner noticing the client still claimed he was fighting a wyvern that was
lying dead in front of him.

**Two occurrences, and they are not the same fight.** Keeping them apart matters, because they were
merged into one "verbatim" transcript in the first draft of this section and an adversarial review
caught it -- the fabricated version had the weapon, the stamina and the score of one and the byte
provenance of the other.

**A -- reported by hand.** The owner pasted this from his screen. Persona at 5,201 points, so it is
the earlier of the two; not in any capture we hold, and nothing but his paste attests it:

```
*The wyvern hits you (41/99).
You hit the wyvern (10-14).
The pitchfork breaks to bits.
You cannot use the pitchfork to fight now!
The wyvern looks covered in wounds.
*The wyvern drops dead, poisoned...
The wyvern has just passed on.
You can fight the wyvern no longer.
(Persona saved on +26 = 5,201).
```

**B -- captured.** `session-rec.mud2.co.uk.20260826-134435.jsonl`, records 2905-3034, extracted as
`mudsharp.Tests/Fixtures/Data/wyvern-poison-death.jsonl` and replayed through the production session
by `WyvernPoisonDeathReplayTests`. A different fight: `dagger0` in hand throughout, no weapon break,
stamina 57/99 at the death, score a stable 6,209, and **no `(Persona saved ...)` line in the death
frame at all.** Decoded, with the tags the decoder shows -- the elision is marked because roughly
thirty lines of FES/FEI probe traffic and the `value wyvern` exchange sit in the gap, and this is not
one contiguous frame:

```
{c08.03}The wyvern hits you (57/99).{/c08.03}
{c08.01}You hit the wyvern (10-14).{/c08.01}
{c12}The wyvern looks close to death.
[PROMPT]
   ... ~30 lines: FEI probe, `value wyvern` and its reply, FES probe ...
[PROMPT]
The wyvern drops dead, poisoned...
The wyvern has just passed on.
{c08.12}You can fight the wyvern no longer.{/c08.12}
```

What both frames share is the part that matters: the three closing lines, and the absence of
**any `You have killed the X.`** The player's blow did not finish the wyvern; poison did, and MUD2
says so in its own sentence -- `The X drops dead, <cause>...` -- which is not the kill line and does
not name whoever applied the poison. Three separate lines announce the end and the client matched
**none** of them, so the encounter had no terminator at all and the panel went on reporting combat.

Two independent occurrences of the same wording is a better evidence position than one. It is also
the whole reason to be careful with the labels: A is a recollection, B is bytes.

`NoMore`, not `Kill`, and the distinction is not pedantry. The damage that ended these fights never
crossed the wire, so `FightHistory.EstimatedStaminaPool` -- which infers a creature's stamina pool
from the damage dealt across fights that ended in a kill -- would read B as a wyvern killed by 10-14
points of dagger.

Capture B also undercuts the obvious assumption that the player was credited with the kill. He had
poisoned the wyvern himself (`up,feed herb to wyvern,d`) and asked its value eight seconds before it
died -- **239 points** -- and the FES `points` field reads **6,209 both immediately before and
immediately after the death frame**, with no `(Persona saved ...)` line in it at all. Frame A's
`+26 = 5,201` is not 239 either. Two observations, conditions in `MECHANICS_NOTES.md`; not a
mechanism. But it is a second reason not to call this a kill.

### The other two lines in that frame

Both are now parsed, both per-creature, and both are backstops rather than the primary evidence:

| Line | Role |
|------|------|
| `The X has just passed on.` | Printed for every death however caused, so it trails an ordinary kill too. Acted on **only** when that creature is still believed engaged -- which after any matched terminator it is not. Reaching it with the fight open means we missed the real end. |
| `You can fight the X no longer.` | The object slot of case-3's trailing line is **not always a pronoun.** Counted across every capture: `it` 14, `him` 4, `her` 1, `the wyvern` 1. What they trail is mixed -- 11 a real flee, 8 a *failed* one where the creature never moved, 1 a death -- so the sentence is a generic acknowledgment and its object slot says nothing about what happened. Named, it can close that one fight; the pronoun forms close a fight only with the code behind them (below). |

Whether "left the room" is what selects the pronoun is a guess and is written down here as one. What
is established is that the named form exists.

### What the capture settled about the codes

The decoded frame, tags and all:

```
[PROMPT]
The wyvern drops dead, poisoned...
The wyvern has just passed on.
{c08.12}You can fight the wyvern no longer.{/c08.12}
```

Two facts, both the opposite of what you would guess, and both now pinned by a test:

1. **The death lines carry no C1 code at all** -- and not merely "none observed". Bartle's own code
   list (`Bartle.MUD2-C1-Codes.txt`) is exhaustive on the 08 family: `08 08` you killed them, `08 09`
   they killed you, and three fight-end *reasons*. There is no death or corpse code anywhere in the
   document. On the wire, `The X has just passed on.` arrives untagged in **87 of 87** occurrences
   across every capture on disk. MUD2 is frozen, so that absence is permanent: prose matching is not
   a workaround for these lines, it is the only thing there will ever be -- and `reduce_combat.py`,
   which keys every event off its code, is structurally blind to them until it grows a plain-text
   pass.
2. **The trailing line is `08.12`** -- `Fight ends - other` -- and so is the pronoun form
   (`£§` = 08 12 precedes `You can fight him no longer.` in the older captures). That is the
   one statement in the whole frame that a fight ended, made in the protocol rather than in English.

Hence the split now in `CombatTracker`: **authority from the code, identity from the text.** The C1
tag reaches it as `LineKind.FightEnd` (C08.10/11/12), and:

- a line matching a known wording closes the fight it names, as before;
- a fight-end-coded line that names nobody closes the fight when exactly ONE creature is engaged --
  there is no other fight it could mean;
- a fight-end-coded line whose wording nothing matches does the same, and lands in the clog verbatim,
  which is how the *next* unknown wording gets found;
- with two or more creatures engaged, an unnamed end still closes nothing. Filing a pack fight's
  ending under the wrong creature would be worse than leaving it open: a wrong row is evidence, an
  open fight is only a bug;
- and a line naming a creature **we are not fighting** is ignored (owner: "we ignore it if we're not
  fighting the npc -- simple"). MUD2 stacks several end messages in a frame and one can land after
  another has already closed the fight -- the wyvern frame does exactly that, the poison death closing
  it two lines before the `08.12` arrives.

That last rule is load-bearing, not tidiness. A name on this event reaches two consumers that
get-or-**create** a fight bucket from it, and `FightHistoryRecorder` had no in-combat guard, so a
trailing name resurrected the fight after its own flush and the next flush wrote it out again as a
zero-swing row -- a second fight against a creature already recorded properly. Fixed in both places,
each pinned by its own test because they are independent:
`CombatTrackerTests.FightEndOther_NamingACreatureWeAreNotFighting_ReportsNoName` for the tracker
dropping the unverified name, and
`FightHistoryRecorderTests.TrailingFightEndAfterTheEncounterClosed_WritesNoSecondRow` for the recorder
refusing to *create* a fight outside an open encounter -- that one feeds the recorder directly, so it
holds the persisting layer honest whatever the tracker does.

This is the part worth carrying forward. Every wording bug this file records -- a creature's failed
flee, the player's failed flee, a named `no longer` line -- was **correctly coded on the wire and
missed anyway**, because the tracker read only the prose. The code was right all three times.

But the division of labour is sharper than "code good, prose bad", and the TODO item that asked for
this work (commit 039cb00: "worth checking against captures whether a FAILED flee carries 08 12 while
a real one carries 08 11") has an answer, measured across all 31 captures:

| sentence | code | n |
|---|---|---|
| `The X has fled by going <dir>.` | `08.11` | 11/11 |
| `The X has fled by trying to go <dir>.` | `08.11` | 8/8 |
| `You have fled by going <dir>.` | `08.11` | 6/6 |
| `You have fled by trying to go <dir>.` | `08.11` | 1/1 |
| `You can fight X no longer.` | `08.12` | 20/20 |
| `The X withdraws from your fight...` | `08.10` | 1/1 |
| `You have killed the X.` | `08.08` | 126/126 |
| `The X drops dead, <cause>...` | none | 1/1 |
| `The X has just passed on.` | none | 87/87 |

**No: a failed flee carries the same `08.11` as a real one, 19 to 0.** Bartle's wording explains why
-- `08 11` is "Fight ends - *flee*", a reason the fight ended, not a report of whether anyone got
away. So the fragile one-word text distinction cannot be replaced by the code, and the rule is not
"prefer the code" but: **the code carries the reason, the prose carries the particulars.** Ask the
code whether a fight ended and why; ask the text which creature and what became of it.

### The backstop: you cannot walk out of a fight

Owner's rule, and the reason `CombatTracker.NoteRoomChanged` exists: **movement is refused while
fighting** -- leaving costs a flee, which prints its own line. So a room change is proof the fight is
over, independent of having matched any sentence at all. `MudSession` calls it whenever the room short
description changes (changes, not merely arrives: `look` reprints the same room and looking around
mid-fight is free).

It force-ends with its own reason string, `(forced end: room changed)`, so a clog distinguishes an
encounter closed by evidence from one closed by the backstop. **Every occurrence of that string is an
unmatched fight-end line waiting to be found** -- grep for it before assuming the parser is complete.
It cannot rescue a fight the player is still standing in; it only bounds how long a phantom one
survives.

### Read the list as open

Three of the eight ends were found by a player noticing the readout was wrong -- cases 3 and 5 on
2026-08-19, case 8 a week later -- each in a frame that a careful reading of the then-current list said
could not happen. "Exactly seven" was true of what had been observed by then, and was written down as
though it were closed.

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
`mucka.flee_failed.wav` (see `Resources/Raw/sounds/README.md`) exists to announce: the text differs from the
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
