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

One row per Session, newest first unless sorted otherwise, read by `SessionHistoryReader` from `persona_sessions` and the
score readings, never from `wire`:

| Shown | Source |
|---|---|
| date and time | `persona_sessions.started_ms`, local time |
| character | `persona_sessions.persona`; a Session whose character was never named says so |
| points | `SessionPoints.Of`, below; blank when the Session has no score reading |
| server, or title | the title the player gave it (`SessionSummary.ShownTitle`); without one, the Session's host, else its run's |
| duration | `ended_ms - started_ms`, in seconds (at least 1) under a minute; left out when `ended_ms` is null |
| how it ended | `ended_note` as stored, except `PersonaSessionEnd.Permadeath`, which reads "deaddead", the game's own word; left out when null. The schema calls it advisory, so a missing note is never shown as a crash or as unknown |
| title, tags | `session_names`, `session_tags` |

The row's first line is the date, the character and the points; the second the title or the server,
with the duration and how it ended right-aligned under the points; the third the tags. The date
column has a fixed width that scales with the UI font step, so the characters line up. Rows
alternate between the page colour and one 10 lighter on each channel. All the row's text follows
the UI font step.

A filter box (character, server, title, tag or outcome, ignoring case and a leading `#`), a character
picker and a sort (newest, oldest, most or fewest points, longest, shortest; a Session with no points
or no recorded end sorts last) narrow the list in memory, without reading the store again.

FACT (operator) The game's longest play session is 3 hours, so a duration never needs more than
hours and minutes. EVIDENCE On the copy of the operator's database, 2026-11-13: the longest recorded
Session was 171.8 minutes; none of the 838 with an end exceeded 3 hours.

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

Each row's "..." button opens its menu: **Open**, **Set title**, **Tags**, **Save as...** (Replay,
Text log, Simple Color Log, Fancy Color Log, Web page), **Trim**, and **Delete** last. Set title,
Tags, Save as Replay, Text log, Simple Color Log and Fancy Color Log work; every other entry is a
placeholder that says it is not built, so the menu already has its final shape. On a phone or
tablet, Text log, Simple Color Log and Fancy Color Log ask the player to send Ollie a mudmail.

DECISION (operator) Replays are saved on every platform, text and color logs on desktop only.

Every Session has a title: until the player sets one it is `SessionSummary.DefaultTitle`, the
character and the local start time, which is also the name a file saved from it is offered. The
row shows a title in place of the server only once it differs from the default. Set title opens
on the current title; blank, or the default typed back, removes it (`SessionSummary.TitleToStore`).
The date and character on the row's first line are never retitled. Tags are typed as one line
separated by `SessionLabels.Separator`, and `SessionLabels.Parse` trims them and drops blanks and
case-only repeats. Each edit is one short transaction (`SessionLabels`), off the UI thread.

DECISION (operator) "Set title", not "Rename": the date and character are what the Session is, and
a title is shown beside them, not over them.

DECISION One **Save as...** entry leading to the formats, not an entry per format: on Windows a
cascading entry of the drop-down, on a phone a second sheet (`ActionMenu`). "Export" was rejected
because it implies a location the player chooses, and a replay goes to the replays folder.

DECISION (operator) A row's actions are on its "..." button, as on the profile cards. Opening the
menu on a tap of the row was rejected: it would take the first click of a double-click, and tap and
double-click are kept for opening a Session. A double-click on a desktop, or a tap on a phone or
tablet, does what Open does. A single click on a desktop selects the row, and opening it or its
"..." menu selects it too: the selected row is highlighted, one at a time, and stays selected
through the filter and the sort while it is listed.

DECISION (operator) On Windows the menu is a drop-down under the button (`ActionMenu`), not a
centred dialog. Where a dialog is used - the sheet on a phone, and the prompts every action opens -
it repeats everything the row shows, so a mis-click shows which Session it is about.

DECISION Title and tags key on `persona_session_id`, as every fact table does, and `RunTransfer`
rewrites that key on import with the rest of a login's rows. Keying them on a new
`persona_sessions.uid` was rejected: it would add a column to an existing table (a non-additive
migration, so an upgrade backup on every install) and need minting in every writer, including older
releases still writing the same file, for a guarantee the id rewrite already gives. Import skips a
run the destination already holds, whole, so a title or tag given to its Sessions after the first
import does not travel with a second.

Tags are shown in `SessionLabels.Ordered` order, by the reader and by a row redrawn after an edit.

A database not yet upgraded to the tables (the launch skips the upgrade while "nettimes" is on)
still lists its Sessions, with no titles or tags.

## A Session's recording

FACT `wire` is keyed by run, not by login, and a run holds 0..n Sessions, so a Session's recording
is a slice of its run (`SessionSlice`).

DECISION A Session's slice is its run's rows after the previous Session's `ended_ms` up to and
including its own (`SessionSlice.Bounds`). The first Session's slice starts at the run's first row,
so the login exchange and the character menu leading into it are its; the last Session's runs to
the run's last row, so the logout tail is its. A Session with no `ended_ms` ends where the next one
starts. A boundary cuts the run at its first row stamped later than it, so the slices partition the
run - no row in two, none in none - even where a stamp steps backwards (`SessionSlice.Range`).

EVIDENCE On a copy of the operator's database, 2026-11-13, read on his Windows desktop by a Debug
build: 853 Sessions in 441 runs; no slice empty; every run's slices summed to the run; the slowest
Session read and masked in 213 ms, over a run of 14,472 rows. The largest run held 33,713 rows and
2,769,194 bytes of payload; a slice is read with its whole run, so that is the most one replay holds
in memory before masking copies it.

## Save as Replay

A Session's slice written as a replay (`SessionSlice.ToReplay`, `Muckrep.WriteNewFile`), with the
same header, the same never-overwrite rule and the same login masking as `$MAKEREPLAY`
(`docs/replay.md`, "Masking the login"): Tx dropped, and the account ID masked where the server
printed it, found by context and never by value. The masking runs over the WHOLE run before the
slice is cut, because a masked context can cross the slice's edge; that, not the login, is why the
whole run is read.

EVIDENCE On the same copy: replays of all 853 Sessions held no case-blind occurrence of any of the
4 account IDs `mucka_runs` records, and every one read back from the file as written.

DECISION Save as Replay writes to the player's replay folder (`ReplayLibrary.NewPath`), where the
Replays tab lists it and `replay:<name>` plays it; on a phone or tablet the share sheet then offers
the file. The name offered is the Session's title (`SessionSummary.DefaultTitle` until one is set). A save-location picker on desktop was rejected: a replay saved elsewhere does not play by
name and is not listed, and the Replays tab already shares one.

## Save as Text log, Color Log

The Session's screen as plain text (`SessionTextLog`) - or, as a Simple Color Log or a Fancy Color Log
(`.log`), with each span in the colour and face the pane draws it in, as ANSI SGR (`SgrText`) - written
by the live transcript's own
`SessionRecorder`, so a line means what it means in a transcript armed live
(`docs/session-rec-design.md`). Desktop only, saved where the player picks in the system's save
dialog, which opens on Mucka's own folder (`MuckaPaths.GetDataDirectory`, beside the live
transcripts) and offers the Session's title as its name. On Windows the dialog comes first and the
log is written where it says (`WindowsSaveDialog`: WinRT's picker cannot open on a folder of our
choosing); a Mac's dialog exports a file that already exists, so there the log is written to a
scratch folder first, copied where the player picks, and the scratch copy deleted.

DECISION (operator) The save dialog opens on Mucka's folder, `~/.mucka` on a desktop.

DECISION (operator) A log is the persona session: from the persona entering the game to it leaving,
never the login, the character menu or the option menu around it. Those are in the Session's slice,
because the slice partitions the recording, but they are not the Session's play. The bounds are the
parser's own game entry and exit (`MudSession.GameModeEntered`, `MudSession.GameModeExited`): the
first entry inside the slice - or the slice's start, when the persona is already in the game there,
because a Session with no recorded end is cut at the next one's start and can take the record
holding that one's entry - to the exit after it, or the slice's end when the recording stops
first; the log's opening and closing lines carry those two records' times. Logging a whole login
session, every persona in it, is a save option to come, not this.

The run is fed from its first record through `MudSession.ForRecording`, so the parser's state at the
game entry is what it was live. As live,
the line in progress is emitted after each record read outside game mode; a record can hold more
than one socket read, so a line before game mode may break where the live screen did not.

DECISION What a live session hid on its own sends is hidden where the recording holds those sends:
each tx record goes to `MudSession.NoteRecordedSend` in its place, and opens the swallow window a
live send of the same bytes opened - the post-character-select setup batch, a creature value probe,
a sniff. The timers that opened them live do not run; a creature probe's timeout runs on the
records' own times (`MudSession.NoteRecordedTime`). Opening the setup window on game entry instead
was rejected: only the batch's score reply closes it, so on a recording with no batch the player's
own first score would be swallowed, and 15 of the 853 Sessions on the operator's database have no
batch. Probe replies (FES, FEW, FEI, FEX) need nothing: the parser never makes them lines, whatever
was sent, so a trimmed Session hides them as well as an untrimmed one.

DECISION The account ID is masked as a replay's is (`Muckrep.MaskLogin`), unlike a live
transcript's: a saved log is a file made to be passed on.

DECISION (operator) A Color Log, Simple or Fancy, leaves the terminal's own default foreground and
background alone: a span in the pane's default text colour gets no colour code, and a background is
written only for a span on a slot other than the pane's page colour. Game text arrives with explicit
colours - white (slot 7), on black (slot 0) - which on screen are that default colour on that page,
so they are the terminal's own look too (`SgrText`). Writing every span in the exact 24-bit pane
colours, on the pane's background, was rejected: it forced a background, so the log looked wrong in
a terminal. Every other colour is written as `SgrColours` says, below. A span with
no chat colour keeps the pane's "bold = bright" promotion of slots 0-7
(`CampbellPalette.ForegroundRgb`), so a bold red span is bright red and bold, and a bold span with
the default foreground is bold alone. Bold and italic are the pane's faces
(`ChatColorizer.Palette.FaceOf`), plus underline; reverse and blink are not written, as the pane
draws neither. An escape is written only where the look changes from the span before it: spans of
one look share one, plain default text has none, and a line ends in a reset only if it left an
attribute set. When an attribute has to be switched off the escape is a reset followed by
everything the new look sets; otherwise it carries only what changed. Stripping the escapes from a
Color Log gives the text log exactly.

DECISION (operator) The two Color Logs differ in colour depth only, by `SgrColours`. A Simple Color
Log writes nothing but the 16 standard colour codes, which the terminal draws in its own theme:
30-37 and 90-97 for a foreground, 40-47 and 100-107 for a background; a chat colour, Dim applied,
is the nearest of the 16 slots in `CampbellPalette.Rgb` by squared RGB distance, the lowest slot of
two equally near. A Fancy Color Log writes every colour it writes - all but the terminal's own,
above - in 24-bit, exactly as the pane draws it, whatever the terminal's theme: a palette slot as its `CampbellPalette.Rgb` colour, a chat colour as
the player set it, resolved as the pane resolves it (`ChatColorizer.ColourPartOf`,
`ChatColorizer.Dim` for a Dim face). Writing a palette slot as its standard code in Fancy too was
rejected (operator): the two logs then looked the same, differing only in chat colours. A chat colour is never promoted to a bright slot for bold, in
either, because the pane never promotes one. Chat spans are drawn in the player's chat colours and faces as they are when the log is
saved, marked as the persona's own or another's (`LogColours`, `ChatColorizer.ApplyAll`) over the
whole Session at once.

FACT The client's own system lines and F-key notes are not in the recording, so they are not in
the log.

EVIDENCE On a copy of the operator's database, 2026-11-13, logs for all 853 Sessions on his Windows
desktop by a Debug build: none failed, every one entered the game, and none held an account prompt,
a character menu or an option menu; 918,522 lines in 125 s, the slowest Session 788 ms. What
the server sent held 604 auto-fex confirmations and 835 setup-batch echoes; the logs held no
confirmation, and the only setup words shown were help listings and commands the player typed in
play. In the 44 Sessions recorded since the client began sending the creature probe as a command
interrupt (`ESC-[value ... ESC-]`, first seen in run 506), every `value` line shown was one the
player typed.

FACT Earlier recordings hold the creature probe as an ordinary command (`value <name>`, 4,281 sends
on that database), which cannot be told apart from the same command typed by the player, so its
echo and replies show in those logs: 3,454 lines shaped like a `value` echo across all 853 logs,
the player's own among them.

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
