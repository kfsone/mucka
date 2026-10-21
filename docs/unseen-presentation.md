# Unseen opponents: what the rail and the history show

Tags follow CLAUDE.md: FACT, RULE, DECISION, EVIDENCE. Every figure below that is a measurement
carries its conditions. The operator's worked examples are quoted in fenced blocks and are the
authority where a sentence here seems to disagree with one.

## Principle

DECISION What happens to an unseen opponent stays unseen. A name receives a blow, a score or an
ending only from a line that carries that name. An anonymous line ("Someone hits you.", "You miss
something.", "You have killed someone.") never credits a name. Rejected, all of them attempts to
recover identity by elimination:

- crediting an anonymous blow to the sole candidate of its class,
- learning a species' class from who could have thrown a blow, and persisting it,
- excluding a creature named earlier in the same frame from the frame's anonymous swings,
- retro-crediting an unseen's blows to its name once the name appears, and the trailing mark that
  display carried,
- a pool-and-slot ledger that tracked which blows were provable.

Each of these was built or prototyped, each produced a wrong credit in a randomised ground-truth
test, and each serves a rare case: see the first EVIDENCE entry. The cost of being wrong in the
rejected direction is a wrong name on a row the operator trusts; the cost of the chosen direction is
a blow shown under "unseen".

EVIDENCE Anonymous swings were in 201 of 18,538 fight frames (1.1%) of the operator's wire corpus
(docs/evidence/unseen-corpus.md, "Anonymous swings", which gives the corpus and the replay).

EVIDENCE A named opponent swung at the player more than once inside one frame in 94 of 19,193
(frame, named opponent) pairs, so a per-frame count of exchanges does not bound the number of
opponents. Within a pair with swings both ways the player's came first in 11,425 of 19,187 (59.5%)
(docs/evidence/unseen-corpus.md, "Swings per frame").

FACT Every opponent opens its own fight with a coded start line, whether or not the player can see
it, and every fight ends with a signalled ending: a kill, a flee, a withdrawal or a death. A
creature's flight is never unseen. The measured exceptions to the start line are few - 10 of 3,878
named opponents had a swing with no earlier start in their encounter, 6 of them beside "You have
just been woken up!" or "The man has woken up!" (docs/evidence/unseen-corpus.md, "Missed
starts").

FACT Every creature has a unique id and two creatures never share a name at once. A creature a
wizard brings back after a kill can be fought a second time, never at the same time as its first
fight.

FACT A flight cancels all combat with that creature, whether or not the move succeeds; the failed
move is only the failure of the flight. Fighting it again is a new start.

FACT NPCs fight and kill each other, so a creature's death, flight or withdrawal in the room can
be about a creature that is not fighting the player. The ending lines of the player's own fights
carry the C08 fight-end code; the plain-text lines ("has just passed on", a creature's flight in
the room) do not say whose fight they are. Of 419 delivered lines containing "has fled by going",
417 were coded as a fight end and 2 were plain (docs/evidence/unseen-corpus.md, "Creature
flights").

RULE An ending line coded as a fight end is an ending of the player's own fight, and so is any
"You have killed ..." line. A plain line that names a Creature with an open row concludes that row,
whoever caused the ending, because the line carries its name (the poison death below). A plain line
naming a Creature with no row - a bystander - concludes nothing and spends no slot, and a plain
anonymous line is never counted as a participant's ending.
enforced_by: none: judgement

## Participants

A participant is an opponent the encounter has opened and not concluded. It is either

- a **named row**: a creature with a named start or a named swing line, shown under its name with
  its own stats. A row with a named start and nothing else is counted and drawn on the rail, and
  enters the history once it has a swing, an ending or a score. A start for a name whose row is
  closed opens a fresh row, so a creature fought twice is two fights; or
- an **anonymous slot**: an anonymous start ("Someone is about to attack you.", "You attack
  something.") not yet concluded, carrying the word its own start line used.

RULE An anonymous swing opens a slot only when nothing open could have thrown it: no open slot of
its word and no open row whose class could produce the word. That is a missed start. When an open
row could have thrown it, the swing parks that row and opens nothing.
enforced_by: none: judgement

A named creature can be **parked**: shown inside the unseen group instead of as its own row, with
its row's stats kept. The unseen count the rail shows is slots plus parked rows.

RULE Vision signals and anonymous lines may park a row; only a line carrying the creature's name
promotes it. A parked row resumes the stats it held, and blows thrown while it was parked are in the
unseen group and stay there. A vision signal that is missed or wrong therefore costs a row shown
parked for a moment, never a wrong credit.
enforced_by: none: judgement

RULE Parking triggers are: a witnessed fade of an engaged creature; the player losing sight (dark or
blind); an anonymous swing, start or ending whose word the creature could have produced (a creature
of known class that cannot produce it is not parked; a creature of unknown class can). The light
coming back, a creature becoming visible and a visibility line of any kind, including a named one
such as "has regained his visibleness!", promote nothing; only a swing line carrying the name does.
enforced_by: none: judgement

RULE A named swing or ending for a name whose row exists, open or closed, never spends an
anonymous slot: a creature with a start of its own is not an anonymous slot. A closed row that
swings again without a start of its own is a missed start; the row is reopened and nothing else
changes.
enforced_by: none: judgement

RULE After "You have just been woken up!" or "has woken up!" counting is off for the rest of the
encounter, because starts go missing there (docs/evidence/unseen-corpus.md, "Missed starts"). Slot
spending stays on: it changes only the count and never moves a blow, and with it off a slot whose
Creature is then named stays open for good ("You attack someone.", "The man has woken up!", "You
hit the man (30-39).", then his flight, verbatim from the corpus:
`UnseenGroupPipelineTests.AfterAWakeUp_TheNamedManSpendsHisSlot_AndHisFlightCloses`).
enforced_by: none: judgement

RULE A name with no row at all that first appears on a swing, or on any own-fight ending (a kill,
a flight, a withdrawal, a death, a coded "no longer"), spends one available anonymous slot. The
count changes; no blow moves.
enforced_by: none: judgement

RULE A slot is available while it is open and not already matched by a pending anonymous ending of
its word (`UnseenBook.AvailableSlotsOf`). A pending ending concluded one participant, so a slot it
may be about is never spent by a name, concluded by a named ending, or taken as the producer of an
anonymous swing. One definition, used for all three.
enforced_by: none: judgement

RULE A start for a Creature whose row is open while anonymous endings are pending turns counting
off for the rest of the encounter: the start says the Creature is still there, so an anonymous
ending already counted against it was someone else's (`UnseenBook.Start`).
enforced_by: none: judgement

## The unseen group

RULE One unseen group per encounter holds every anonymous blow, in either direction, and every
parked row's blows from the time it was parked. Named lines never enter it. Its figures are the
ordinary fight figures: hits, misses, damage ranges, damage taken.
enforced_by: none: judgement

RULE The group carries score only from endings that were handed to a named row by counting (below),
because those scores cannot be placed on a row. Every other ending's score stays on that ending's own
badge.
enforced_by: none: judgement

## Endings

RULE An anonymous ending is one conclusion. A named ending is the named row's, including a creature
that was never named on a swing: an ogre whose start named it, dying of poison while attacking. A
named ending for a name the book has no row for concludes an available slot or nothing
(`UnseenBook.End`); with nothing to conclude, a line that is the player's own ("You have killed the
ogre.") still reaches the event stream under its name, and the book is unchanged.
enforced_by: none: judgement

RULE A named own-fight ending of any outcome for a name with no row, while a slot is available, is a
conclusion of that slot, shown under the name (see "What the history shows") with no stats, and
not a row that takes any of the group's blows. A
named line never becomes an anonymous ending and never parks: with no slot available, a named
ending for a name with no row is a bystander's and moves nothing.
enforced_by: none: judgement

RULE The player's death by an unseen killer ("Someone has killed you.") is one of the group's
conclusions: the badge reads "killed you" and the session counts the death.
enforced_by: none: judgement

RULE A coded fight end that names nobody ("You can fight it no longer." with nothing else ended in
its frame) concludes one participant: the sole named Creature if it is the only one open and not
parked, otherwise an anonymous ending with the outcome "ended", which counting treats as an outcome
of its own. An ogre dying of poison unseen in the dark is closed this way.
enforced_by: none: judgement

RULE **Counting.** Each participant concludes exactly once, so when the number of anonymous endings
of one outcome equals the number of participants and every ending has that outcome, every
participant had that outcome. The rows take it without a score or an order. Which ending belongs to
a slot is read from its word: an ending of word W goes to a slot of word W first, and the leftovers
can only be about parked rows. Anonymous endings that do not add up this way (fewer than the
participants, or mixed outcomes) are assigned to nobody: the parked rows show "fate unseen" when the
encounter closes and every anonymous ending is a conclusion of its own.
enforced_by: none: judgement

FACT Counting is only as good as the starts it is given. With a start missed and no wake-up line to
say so, an invisible newcomer's anonymous swing parks a visible Creature of unknown class, and an
anonymous kill of the newcomer is counted onto that Creature: "The ogre0 is moving towards you",
"The ogre0 hits you", "Someone hits you." (start missed), "You have killed someone." gives the ogre0
a kill by counting. The wake-up guard covers the missing-start cases seen in the corpus.

Worked example, from the operator. Three named rats start, the torch is dropped and a someone starts;
all swings after that are anonymous:

```
* The rat0 is advancing at you menacingly.
  The rat1 is charging at you threateningly.
  The rat2 is doing it too.
*You drop the torch.
It is too dark to see now.
* Someone is about to attack you.
* You hit something.   Something has died. +5      (x3)
* Someone hits you.  * You hit someone.  Someone has died. +10
```

```
[ unseen +15 ... fight stats ]   the three +5 endings, handed to rows by counting
[ unseen +10 killed ]            the someone slot's own ending
[ rat1  killed ]                 rows take the outcome, no score
[ rat0  killed ]
[ rat2  killed ]
```

## What the history shows

RULE The fight is unseen, so its accounting is its own badge: the group is the first badge of the
encounter's unseen entries, with the blows and no points of its own beyond the endings counting
handed to rows. Each conclusion that was not handed to a named row is one further badge, points
and outcome only, no stats, in the order the conclusions happened and mixed in with the named rows:
"[unseen +N outcome]" for an anonymous ending, and the Creature's plain name, "[rat18 +22 killed]",
for a slot a named line concluded - a name learned at the end attaches a name, an outcome and points
to that conclusion, and moves no blow. A row handed its
outcome by counting is shown with the outcome and no score. A group holding no anonymous blow, no
conclusion of its own and no ending MUD2 scores (a kill, a creature's flight) has no badge: a row
parked for a moment with nothing anonymous after it, or a parked player counted "ended"
(`UnseenGroupPipelineTests.ARowCountedAnUnscoredEnding_WithAnEmptyGroup_LeavesNoUnseenBadge`).
When the player's flight ends the encounter, the group badge carries that ending's words ("you
fled", "flee failed") as a named row does; when a death or a force end cuts it short, "cut short".
enforced_by: none: judgement

RULE KILLED YOU is shown only under the one the MUD says killed the player: the named Creature
("The rat11 has killed you."), or the unseen group for "Someone has killed you." - as its own
conclusion, or the fold when it was the only unseen participant. Every other fight open at that
moment reads "cut short", as at a force end, and is recorded Unresolved; the session counts one
death.
enforced_by: none: judgement

RULE When the only participant ever unseen is one anonymous slot, its conclusion folds into the
group badge and there is one badge: "[unseen +86 killed ... fight stats]" for a creature met in a
dark room and killed. When a named line concluded that slot the badge is titled by the name in
parentheses, as MUD2 writes an invisible persona ("(Ollie the necromancer)"): "[(basilisk) +80
killed ... fight stats]" - the group's stats, the conclusion's outcome and score. A row handed its
outcome by counting never folds: it keeps its own badge, its name and its stats, with the outcome
and no score, and the score is the group's.
enforced_by: none: judgement

RULE A kill is a kill: every own-fight anonymous kill, and every row counted killed, is a kill in
the session's totals (`SessionCombatTotals.Accumulate`), and every unseen participant is a fight:
each conclusion, and each slot still open when the encounter closes (a flight, a force end).
enforced_by: none: judgement

RULE If the unseen entries of an encounter would exceed five badges, counting the group, the
unseen conclusions and the rows whose outcome came from counting, they collapse into one badge
carrying the participant count, the flight count and the summed score:
"[unseen x3 ()x5 +258 ... fight stats]", the "()" being the flights concluded against the unseen:
a flight is always signalled, but its name may not be attributable to a row. Named rows with their
own named endings are never collapsed. This keeps a long PvP exchange, in which a player goes in and
out of sight many times while the fight never ends, to one group and a handful of badges.
enforced_by: none: judgement

Operator examples of the badge layout:

```
two unseens, the second never named:
[unseen (all damage by some-one/-thing, no points) ... stats]
[unseen +16 (no stats)]
[rat0   +32 ... stats]
[unseen +22 (no stats)]

the second unseen became the badger and was named before it died:
[unseen (25-39 dmg) ... stats]
[unseen +16]
[rat0   +32 ... stats]
[badger +22 (10-15 dmg) ... stats]

one unseen, named only by the line that killed it (a shape seen in the corpus):
[(basilisk) +80 killed ... stats]

three unseens, each named only by the line that killed it (a shape seen in the corpus):
[unseen ... stats]
[rat18 +22 killed]
[rat19 +22 killed]
[rat21 +22 killed]
```

FACT The dead strip draws these badges for an encounter once it has closed, because folding and
the lump are settled only then; while the encounter is open the group is on the rail
(`UnseenEndings`). A badge's score is the sum of the awards paired to the endings it stands for
(`UnseenEntry.Awards`), paired by `KillAwardLedger` like every other ending's and read afresh on
every rebuild, so an award announced after the close still lands; null when nothing was paired. A
row handed its outcome by counting is never queued for an award.

## The combat rail

The rail follows the same split. A parked row is drawn inside the unseen group with its count; a
promoted row is drawn as a row again with the stats it held. The ten-step walk-through from the
operator is the behavioural check:

```
1. Rat attacks you:           [ Rat ... ]                                   [ You ... ]
2. Invisible old man attacks: [ <unknown> ... ] [ Rat ... ]                 [ You ... ]
3. You invis the rat:         [ <unknown> x 2 ... ]                         [ You ... ]
4. The rat loses invisibility and swings by name: [ <unknown> ... ] [ Rat ... stats from above ] [ You ... ]
   (the visibility line alone changes nothing; the swing brings the row back)
5. It goes dark:              [ <unknown> x 2 ... ]                         [ You ... ]
6. The old man becomes visible, unseen by the player: [ <unknown> x 2 ... ]
7. Light returns, no swings yet:                      [ <unknown> x 2 ... ]
8. A swing at the old man by name:  [ <unknown> ... ] [ Old Man ... new stats, first attributed ]
9. A swing from the rat, a hit from Someone: [ <unknown> ... ] [ Rat ... stats from above ]
10. The old man hits again by name: [ Old Man ... resuming stats ] [ Rat ... ]
```

Step 8 does not conclude that the remaining unknown is the rat, though the counts say so: the rat
may be invisible too.

FACT The class lookup (`SomeKindKnowledge.Known`) is read-only and seeded: man, thief, goblin,
dwarf, giant and every player are "someone"; rat, water-snake, fox and eagle are "something",
observed in the operator's wire corpus as "Something hits you" while the player was blind or in the
dark, and the basilisk, whose one unseen slot opened with "Something is about to attack you." was
concluded by "You have killed the basilisk.". A Creature of no seeded class is parked by an
anonymous line of either word.

EVIDENCE Every named own-fight ending that concluded a slot while that slot was the only unseen
participant (count 1, nothing parked), grouped by species, with the word all anonymous starts of the
encounter shared: basilisks "something" 1; rats "something" 2. No species showed both words
(docs/evidence/unseen-corpus.md, "Species classes").

## Where it lives

FACT `CombatTracker` keeps one `UnseenBook` per encounter, opened with the encounter and closed with
it, and feeds it every start, swing and ending line with its name or its anonymous word, the fades,
the player losing and regaining sight (`CombatTracker.NoteCannotSee`), and the wake-up lines.
Every event of an anonymous line, and of a named ending that concluded a slot, carries
`AnonymousOpponent.Group` as its participant name. After each change the tracker emits
`CombatEventKind.UnseenChanged` with the book's `UnseenSnapshot`, and one
`CombatEventKind.UnseenRowEnded` per named row the book concluded without a line naming it. The
encounter closes when no participant is open (`UnseenBook.OpenParticipants`): the book's open rows,
plus its open slots, less the anonymous endings counting has not placed. The snapshot is rebuilt only
when the book's `UnseenBook.Version` has moved, so a blow to an open row that has swung before builds
nothing.

FACT The aggregator holds the group as one fight that no ending naming the group resolves, so every
anonymous blow of the encounter is in it; the rail draws it as the unseen badge
(`ParticipantRoster.Build`). It is a live opponent - to the primary fight, the incoming tempo and
the largest live blow - only while the unseen count is above zero (`FightSnapshot.IsResolved`),
and it is the primary fight only when no named Creature is live (`ParticipantRoster.PrimaryIndex`,
which the roster's bold row and `CombatComposition.PrimaryFight` share).
The group's participant name, `AnonymousOpponent.Group`, is `<unseen>`, which no persona can be;
the history draws it as "unseen". The fight records hold the group the same way, plus one swing-less row
per unseen conclusion (`FightHistoryRecorder`); docs/persistence-design.md has the columns.

RULE The player's own flight and death are not the book's: they end every fight, the group's
included, and the book is closed with the encounter.
enforced_by: none: judgement

DECISION The group's figures on the rail and in the history are the aggregator's fight for the
group, so the group's tile is built like every other tile. The book keeps no figures of its own
beyond whether the group has a blow (`UnseenBook.GroupBlows`), which decides whether the group has a
badge; a second set of tallies beside the aggregator's was rejected.

## Not separated

The group and its badges do not separate "someone" from "something"; the words are used only to
match an ending to a slot and to leave a Creature of known class unparked. The five-badge limit
(`UnseenBook.MaxUnseenBadges`) is the operator's figure from the worked example, to be tuned by
looking at real fights.
