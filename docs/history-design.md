# History - design

History is a modal page from the connect page's header (`HistoryPage`), on every platform. It lists
the player's Sessions and the replays they can play.

## What a Session is

FACT The store has three things that could be called a session, and the player sees one:

| Store | What it is | Player-facing name |
|---|---|---|
| process lifetime | one run of Mucka; can make many connections | none |
| `mucka_runs` | one connection to a server; holds 0..n logins | none |
| `persona_sessions` | one login of one persona | **Session** |

DECISION (operator) A History row is a `persona_sessions` row and is called a Session. Players are
non-technical gamers; runs and connections never appear in the UI, and offering a choice between
the three units is rejected. A connection that never reached the game has no Session and is not
listed.

## The Sessions tab

One row per Session, newest first, read by `SessionHistoryReader` from `persona_sessions` and the
score readings, never from `wire`:

| Shown | Source |
|---|---|
| date and time | `persona_sessions.started_ms`, local time |
| character | `persona_sessions.persona`; a Session whose character was never named says so |
| points | `SessionPoints.Of`, below; blank when the Session has no score reading |
| server | the Session's host, else its run's |
| duration | `ended_ms - started_ms`; left out when `ended_ms` is null |
| how it ended | `ended_note` as stored; left out when null. The schema calls it advisory, so a missing note is never shown as a crash or as unknown |
| name, tags | `session_names`, `session_tags` |

**Points match the score graph by construction.** They are computed from the readings
`ScoreHistoryReader` builds for the graph, including the step to zero at a permadeath, as the last
reading inside the Session less the reading before it (the Session's own first reading when its
character has none earlier). Summing `score_events.delta` is rejected: it is nullable, and the graph
never uses it. EVIDENCE: on the operator's database, 2026-11-13, the sum of `delta` disagreed with
the difference of `total`s in 115 of the 474 Sessions that had readings. EVIDENCE: on a copy of the
same database, 2026-11-13 (853 Sessions, 15 with no recorded end), each character's Session points
summed to its graph's last reading less its first for all 13 characters. A consequence: points made
while nothing was recording (between two Sessions) count toward the Session whose first reading
follows them, exactly as the graph draws that step.

Each row's "..." button opens its menu: **Rename** and **Tags**. A blank name removes it; tags are typed as one line
separated by `SessionLabels.Separator`, and `SessionLabels.Parse` trims them and drops blanks and
case-only repeats. Each edit is one short transaction (`SessionLabels`), off the UI thread.

DECISION (operator) A row's actions are on its "..." button, as on the profile cards. Opening the
menu on a tap of the row was rejected: it would take the first click of a double-click, and tap and
double-click are kept for opening a Session. Until that exists, a tap on the row does nothing.

DECISION Name and tags key on `persona_session_id`, as every fact table does, and `RunTransfer`
rewrites that key on import with the rest of a login's rows. Keying them on a new
`persona_sessions.uid` was rejected: it would add a column to an existing table (a non-additive
migration, so an upgrade backup on every install) and need minting in every writer, including older
releases still writing the same file, for a guarantee the id rewrite already gives. Import skips a
run the destination already holds, whole, so a name or tag given to its Sessions after the first
import does not travel with a second.

Tags are shown in `SessionLabels.Ordered` order, by the reader and by a row redrawn after an edit.

A database not yet upgraded to the tables (the launch skips the upgrade while "nettimes" is on)
still lists its Sessions, with no names or tags.

## The Replays tab

Every `.muckrep` that `ReplayFiles` can play, listed by `ReplayLibrary` in the order `ReplayFiles`
searches, so a name held in two places is listed once, as the copy that plays: the player's replay
folder, Android's external files folder, then the replays packaged with the desktop builds. Each
row's "..." button opens its menu:

- **Play** closes History and connects `replay:<name>` the way a guest preset connects
  (`ConnectViewModel.PlayReplayAsync`): nothing is saved, and the form returns to what it showed.
  While a new profile is being typed it refuses, over History, because the form would be lost.
- **Rename** renames the file in its own folder (`ReplayLibrary.NameFromTyped` drops a typed
  `.muckrep`), refusing an empty name, a character a file name cannot hold, or a name any listed
  replay already has, in any folder: only one of two same-named replays lists and plays.
- **Share** hands the file to the platform share sheet (`ShareFileRequest`).
- **Delete** asks first, and removes the file.

A packaged replay can be played and shared only. `FileSystem.OpenAppPackageFileAsync` opens a
packaged file by name but cannot list them, so `ReplayLibrary` reads the folder they are installed
to. On Windows that is `replays` beside the executable, observed in the Debug output. On the Mac it
is read from the bundle's resources folder, which has not been observed; if they are installed
elsewhere the tab lists none of them, and they still play by name.
