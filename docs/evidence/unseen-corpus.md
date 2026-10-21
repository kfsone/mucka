# Unseen opponents: corpus measurements

The figures docs/unseen-presentation.md cites as EVIDENCE, with the replay that produced them. Tags
follow CLAUDE.md.

## The corpus and the replay

EVIDENCE Corpus: a read-only copy of the operator's `~/.mucka/mucka.db`, every `mucka_runs` row in
it: 401 runs, which the replay below reads as 256,975 frames and 2,735 encounters. Every figure on
this page is over this one copy.

The replay, per run: the run's `wire` rows in `id` order. A server-to-client row (`direction` 0) is
handed to a fresh `MudSession` exactly as stored, followed by `MudSession.EmitPartial` while the
session is not in game mode; a client-to-server row is not fed. The session's timers are inert, its
FES heartbeat interval is a day (so it sends no probes), and its combat clock is the row's
`ts_ms`. Its own `CombatTracker` produces every event counted here. A frame is one
`MudSession.FrameClosed`; an encounter is one `MudSession.InCombatChanged` true-to-false span. A
swing event is one of `CombatEventKind.Hit`, `CombatEventKind.Miss`, `CombatEventKind.HitByNpc`,
`CombatEventKind.MissByNpc`; the first two are the player's.

## Anonymous swings

EVIDENCE Fight frames - frames holding at least one swing event: 18,538. Of those, 201 (1.1%) hold a
swing event naming `AnonymousOpponent.Group`.

## Swings per frame

EVIDENCE (frame, named opponent) pairs with at least one swing at the player: 19,193. In 94 of them
the opponent swung at the player more than once inside the frame.

EVIDENCE Pairs with swings both ways inside the frame: 19,187. The first swing of the pair was the
player's in 11,425 (59.5%).

## Missed starts

EVIDENCE Named opponents - an encounter and a name, the name on at least one swing event: 3,878. Of
those, 10 had their first swing with no earlier `CombatEventKind.FightStart` naming them in the
encounter. 6 of the 10 were in an encounter with "You have just been woken up!" or "has woken up!"
among its lines (from three lines before it opened to its close). The other 4 were a player persona
(three encounters) and an old dwarf (one).

## Creature flights

EVIDENCE Delivered lines containing "has fled by going": 419. Coded as a fight end
(`LineKind.FightEnd`): 417. Plain: 2.

## Species classes

EVIDENCE Every named own-fight ending that concluded a slot while that slot was the only unseen
participant - the last `CombatEventKind.UnseenChanged` before the ending carried
`UnseenSnapshot.Count` 1 and nothing parked, and the ending carried `CombatEvent.ConcludedName` -
grouped by `NpcGroups.Normalize` of the name, with the word every anonymous start of the encounter
up to then shared:

| species | word | count |
|---|---|---|
| basilisks | something | 1 |
| rats | something | 2 |

No species showed both words.
