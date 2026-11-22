# Logs - design

Logs is a modal page from the connect page's header (`LogsPage`), on every platform. It lists
the player's Sessions, shows one as a log (the Viewer), lists the replays they can play, and holds
the passes over every Session's recording (the Maintenance tab).

DECISION (operator) The page is called Logs, the old MUD word for a saved session. "History" was
rejected: in a game this old it reads as the game's history.

## What a Session is

FACT The store has three things that could be called a session, and the player sees one:

| Store | What it is | Player-facing name |
|---|---|---|
| process lifetime | one run of Mucka; can make many connections | none |
| `mucka_runs` | one connection to a server; holds 0..n logins | none |
| `persona_sessions` | one login of one persona | **Session** |

DECISION (operator) A Logs row is a `persona_sessions` row and is called a Session. Players are
non-technical gamers; runs and connections never appear in the UI, and offering a choice between
the three units is rejected. A connection that never reached the game has no Session and is not
listed.

## The Sessions tab

One row per Session that still has a recording (Delete, below), newest first unless sorted otherwise,
read by `SessionHistoryReader` from `persona_sessions` and the score readings, and from `wire` only
its ids and stamps:

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
and a sort (newest, oldest, most or fewest points, longest, shortest; a Session with no points or no
recorded end sorts last) narrow the list in memory, without reading the store again. Character and
sort are icons beside the filter box, each opening a drop-down with the current choice ticked; the
person icon is lit while one character is chosen, and the box's prompt names them, and the sort icon
while the sort is not newest first.

DECISION (operator) Icons, not labelled pickers: a picker's title sat above its box and broke the
toolbar's line. A date range waits for the Search page.

FACT (operator) The game's longest play session is 3 hours, so a duration never needs more than
hours and minutes. EVIDENCE On the copy of the operator's database, 2026-11-13: the longest recorded
Session was 171.8 minutes; none of the 838 with an end exceeded 3 hours.

**Points match the score graph by construction.** They are computed from the readings
`ScoreHistoryReader` builds for the graph, including the step to zero at a permadeath, as the last
reading inside the Session less its opening. The readings are the `(Persona saved on ...)` lines
(`score_events`) and the post-login score sheet (`score_sheets`, `MudSession.ScoreSheetRead`). The
opening is a reading the graph places at the Session's start: its first reading's total less that
reading's own delta, so "(Persona saved on +200 = 200)" opens on 0 and a sheet opens on what it
printed. Summing `score_events.delta` is rejected: it is nullable, and the graph never uses it.
EVIDENCE: on the operator's database, 2026-11-13, the sum of `delta` disagreed with the difference of
`total`s in 115 of the 474 Sessions that had readings. The opening takes one line's two figures, never
a sum, and the 428 of 452 below is its check. The graph's card for a Session reads the same
`SessionPoints.Of`, so the card and the row agree.

EVIDENCE The sheet's `score:` is the same quantity as a save line's total. Ollie, 12 Sep 2026: the
sheet after the 12:48 login printed "score:  36,791 points", and the next Session's first save line
was "(Persona saved on +38 = 36,829)." - 36,829 - 38 = 36,791.

DECISION (operator) Points made where nothing recorded them (another client, another machine) count
toward no Session: they are the graph's step up to the next Session's opening. The rejected rule
counted them in the next Session with a reading. It put 9,495 points made elsewhere (11 Sep 16:14 to
12 Sep 12:48) into Ollie's 12 Sep 12:54 Session, which showed +11,087, and it put a new character's
first gain outside every Session. EVIDENCE: on a copy of the operator's database, 2026-11-14, the
opening from the first reading equalled the reading before the Session in 428 of the 452 Sessions
that had both; the other 24 are points made elsewhere. Rows recorded before `score_sheets` have no
sheet: a Session with no save line has no opening and its points are blank.

Each row's "..." button opens its menu: **Open**, **Set title**, **Tags**, **Save as...** (on a desktop
a choice of Replay, Text log, Simple Color Log, Fancy Color Log, Web page), **Trim**, and **Delete**
last (below). On a phone or tablet Save as... has no choice: it tells the player to send "Ollie" a
mudmail.

DECISION (operator) Save as... on a phone or tablet answers at once. Asking for a format first, when
every format but Replay ends in that answer, was rejected as a tease. A Replay is saved there only by
Delete's "Save a Replay first".

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
cascading entry of the drop-down, on a Mac a second sheet (`ActionMenu`). "Export" was rejected
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

## Delete

Delete, last on a row's menu, deletes the Session's recording (`SessionRecording.Delete`): its
slice's `wire` rows, the `trimmed` rows stamped inside its bounds, and its title and tags, in one
transaction, after `DatabaseBackup.TakeOnce`, because nothing puts them back. Its `persona_sessions`
row and every fact table stay, so its points stay on the score graph and its fights in Combat
Insights. A Session whose slice is empty is not listed (`SessionRecording.Recorded`, read by
`SessionHistoryReader`): that is derived, not a flag, and it cuts a run where `SessionSlice` does, by
row id, so the list and the slice cannot disagree.

DECISION (operator) Delete removes the recording only. Removing the Session's facts with it was
rejected: the score graph and Combat Insights would change under the player.

Delete first offers a drop-down: on a desktop Save a Replay first, Save a Text log first, Save a Simple
Color Log first, Save a Fancy Color Log first, Save a Web page first, and Delete without saving; on a
phone or tablet Save a
Replay first and Delete without saving. A confirmation then repeats the row and says what deleting
does, because the Windows drop-down has no title. A backup is the same save as Save as..., and a save
that fails or is cancelled deletes nothing. The overlay covers the page while it runs.

A Session whose run has no end is refused: another Mucka may still be writing it. The run gets one
when the same account next logs in on the same server, and a run with no account never does
(persistence-design.md, "Runs that never ended"), so its Sessions cannot be deleted or trimmed.

A trimmed row has left `wire`, so it has no id among the slice's: Delete takes the trimmed rows
stamped inside the Session's bounds. Where a stamp steps backwards across a boundary, a trimmed row
can go with the Session beside the one its neighbours went with.

EVIDENCE On a copy of the operator's database, 2026-11-14 (279,220,224 bytes, 837 Sessions), by a
Debug build on his Windows desktop: `SessionRecording.Recorded` agreed with `SessionSlice.Read` on
every Session and took 550 ms, the whole list 572 ms. Deleting a middle Session of a three-Session
run removed its 6,892 rows (819,434 bytes) in 9,136 ms, the first backup of the copy included; both
neighbours' slices were byte for byte the same after, and `persona_sessions`, `score_events`,
`fights`, `swings` and `encounters` kept their counts.

## Trim

Trim, on a row's menu, takes the background status checks Mucka repeats out of the Session's
recording (`WireTrim.TrimSession`): the run's trim plan, only inside the Session's slice
(persistence-design.md, "Trim, started by the player"). What the Session shows, its logs and its
replay stay the same, and Restore, on the Maintenance tab, undoes it. A run that has not ended, or
that the heal has not been over, is refused.

EVIDENCE On the same copy: a Session's Trim moved 3,687 rows (286,707 bytes) in 223 ms, and its text
log's 170 lines were the same before and after.

FACT What a trim takes (persistence-design.md, "Trim, started by the player"): every status-check
send (FES, FEW, FEI, FEX), and every reply that repeats the one before it. A reply is kept when it is
the first after a login, when anything in it changed, and when it is the last of its kind before the
next login. A FEX reply is never compared, because the game sends the exits unasked with every room.
What is taken stays in the trim backup until Flush; no age deletes it by itself.

## The Maintenance tab

The third tab has two sections; each button has an (i) whose tooltip, and whose tap, says what it
does:

| Section | Row | Does |
|---|---|---|
| Trim old Sessions | an age in days (offered at `WireTrim.DefaultDays`), and Trim under it | `WireTrim.Trim` |
| | with a trim backup: its size, then Restore and Flush, each on a line of its own, in the column Trim is in and at its width | `WireTrim.Revert`; `WireTrim.Expire` at an age of 0, after a confirmation |
| | without one: "(No current backup)", and neither button | |
| Save space | mucka.db's size, then Shrink mucka.db, and Delete backups with the backup's size | `DatabaseVacuum.Shrink`; `DatabaseBackup.Delete`, after a confirmation |

Shrink mucka.db and Delete backups share a line where they fit and take a line each where they do not,
each with its (i) and the backup's size: a button never wraps away from its own (i).

DECISION (operator) The Maintenance tab is on the Logs page, not the settings page's Data tab: the
settings page also opens during a game, and these passes do not run beside one. `$trimdb` is retired
(persistence-design.md, "Shrink mucka.db").

DECISION (operator) The layout and the words: one Trim section whose backup holds Restore and
Flush, shown only while there is a backup, and a Save space section. The rejected first draft had a section per action, with Put back,
Delete trimmed for good (with its own age), and Join broken lines.

DECISION "Shrink mucka.db", not "Compact" or "Optimize": Compact is the operator's word for a later,
separate feature - cutting a Session down to what a log needs, after which it no longer makes a
replay - and this changes no recording.

DECISION (operator) Joining the lines the server wrapped is not on the tab: `WireHeal` does it once
per run at launch (line-wrapping.md, "Old recordings"). The rejected option is the Join broken lines
button.

While an action runs the page's overlay covers it (`MaintenanceOverlay`), and Escape, the back button
and the close button wait for it.

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

## Save as Text log, Color Log, Web page

The Session's screen as plain text (`SessionTextLog`) - or, as a Simple Color Log or a Fancy Color Log
(`.log`), with each span in the colour and face the pane draws it in, as ANSI SGR (`SgrText`), or as
a web page (`.html`, `HtmlText`) - written by the live transcript's own
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

A Web page is the same lines as a Color Log, marked the same way, as HTML: one page whose `pre`
holds the log, on the pane's page colour in its default text colour, in a monospace face, long lines
wrapped at the window's edge as the pane wraps them (`HtmlText.Head`). Every span is in the colour
the pane draws it in, exactly (`CampbellPalette.ForegroundRgb`, so a Fancy Color Log's colours), a
span in the default text colour or on the page colour taking nothing; bold, italic and underline as
in a Color Log, and neighbouring spans of one look share one element. The page title is the
Session's title. `HtmlPageWriter` writes the page's head before the recorder's first line and its
foot when the recorder closes it. Markup characters and everything outside ASCII are written as
character references, so the page is ASCII whatever the server sent, and control characters other
than a tab are dropped. Taking the tags out of the `pre` and decoding the references gives the text
log exactly.

DECISION The page is drawn on the pane's own colours, unlike a Color Log, which leaves the
terminal's: a browser has no terminal theme to fall back on, and the pane's game text is light on
near-black.

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

## The Viewer

Opening a Session - Open on its menu, a double-click on a desktop, a tap on a phone or tablet -
shows it as a log in a viewer (`LogViewerPage`). On a desktop the viewer is a window of its own, so
several Sessions can be open beside each other. Each Session has at most one, and opening it again
brings its window forward. Logs stays as it was, and closing Logs, or the main window, closes its
viewers. On a phone or tablet the viewer is a page over Logs, with `LogViewerPage.PhoneCardMargin` of
black round a border of the accent colour and `LogViewerPage.PhoneCardTop` above it to clear the
status bar. The log is drawn on the game pane's background with `LogViewerPage.LogBackgroundLift`
added to each of red, green and blue (`TerminalView.ClearColor`), so a log is never mistaken for
the game; a desktop window takes the smaller `LogViewerPage.WindowLogBackgroundLift`. Its X, Escape, or the system back button close the viewer and nothing else, and Logs is as
it was left. Escape and back close the innermost thing open: the Find or Filters panel, then the
viewer.

A window is titled with the Session's title - the one the player gave, else the date, the character
and the points (`SessionItem.Ident`) - which is the header's top line. On Windows its title bar
draws no text, so the title is not written twice, one above the other. A double-click opens, or
raises, a viewer once its button has come up (`LogsPage.AfterClick`): on Windows the main window
took the front back at the release, from behind a window opened or raised before it.

DECISION (operator) A window of its own on a desktop, one for each Session, in place of a card over
Logs on a dimmed backdrop. The card left a margin of backdrop round it, was capped in width, and
only one Session could be open. A tab of Logs was tried before the card and rejected: opening a
Session reconfigured the page in place, and Escape from the list it had replaced closed Logs.

The viewer's header is the Session's row, built by the same code as the list's (`LogsPage`'s
`SessionLines`), so the two read alike: the date, the character and the points; the title or the
server, and how long it lasted and how it ended; its tags. Over a log the title the player gave
comes first, in the place of the date and the character, which go under it where the server was;
with no title the date and the character are the title, and the server is under them
(`SessionLines` with `titleFirst`). Beside it are two icons, a magnifier for
Find and a funnel for Filters, then the row's "..." menu without Open, and the X. The menu is
Logs's own (`LogsPage.RowMenuAsync`), its dialogs opening over the viewer it was called from; a
Delete from a viewer closes it, and a new title or tags show in it at once.

DECISION (operator) Find and Filters are two modes, each an icon that opens its own small panel
floating over the log under the icons, one at a time; neither pushes the log down. An always-shown
toolbar beside the row - a Find box, its buttons and a Filters button - was built and rejected. An
icon is drawn in the accent colour, with an outline, while its panel is open, and the funnel stays
in the accent colour while any filter is set, so a filtered log is never mistaken for the whole
Session. The icons are drawn as paths: no icon font is on every platform.

The Session is shown as its text log: from the same feed (`SessionTextLog.Lines`), so the persona
session only, masked as the log is, and with no filters the same lines (`TerminalBuffer.ScreenOf`).
They are drawn by the game's own pane (`TerminalView.ShowDocument`), in the player's chat colours
(read as a Color Log reads them) and game text size, a screen clear drawn as the pane's rule in
scrollback. It is read only: no command box, no side panel, no stats, which a replay has. On Windows
and the Mac, scrolling, selecting and copying are the game pane's scrollback's, with the same mouse
(`TerminalPointer`), the scrollbar's thumb dragged and its track clicked a page at a time; on a
phone or tablet a finger scrolls it. The lines are filtered off the UI thread.

EVIDENCE On a copy of the operator's database, 2026-11-13, a Debug build on his Windows desktop: all
854 Sessions read, and with no filters each one's lines were its text log's exactly. The longest was
13,028 lines, read in 327 ms; the slowest read took 957 ms, for 3,071 lines.

### Find

DECISION Search is on request, not as the player types: Enter in the Find box, or the down button, finds the next
match, and Shift+Enter, or the up button, the one before (`LogSearch`). A match is within one line,
ignoring case. Each search moves on from the current match; with none - a new search, or new text -
it starts at the first line the view shows, or for one backward the last. Past either end it carries
on from the other, as a browser's find does, and the Find panel says which match it is of how many. The
match is drawn over the text and scrolled to the middle of the view. It searches what the filters
leave.

On Windows, F3 finds as Enter does, Ctrl+F opens Find, Page Up and Page Down scroll a
page, Ctrl+Home and Ctrl+End go to the start and the end, and Ctrl+C copies the selection when the
Find box has none of its own. The Mac and phones find forward with Enter and both ways with the
buttons.

### Filters

The funnel's panel is a table of rules (`FilterRule`). A rule is up to three parts (operator): what
it picks out, which of it where the kind has a choice, and a pattern the line must hold where the
kind takes one; an eye on its left says whether what it matches is shown or hidden, its count says
how many of the Session's lines it matches, and an x removes it, the x that closes elsewhere. On a desktop the
table has a header (SHOW, WHAT, WHICH, MATCHING, LINES) and a row per rule; on a phone a rule takes
two lines. "+ Add a rule" offers the kinds, each with how many of the Session's lines it holds, and
a new rule shows what it matches. While any rule is set, a strip under the viewer's header says
"Showing N of M lines" with the rules in words, and Show all removes them all. The rules belong to
the viewer they are set in, and a change shows the log from its start. Typing in a pattern
refilters once typing pauses.

DECISION (operator) The panel has no close button: its icon, the other icon, or Escape shuts it. Two
earlier panels were rejected: rows of a category and Show / Hide / Only buttons with a close-like x
("Show/Hide/Only is weird and confusing"; a row "looks like a ribbon in a window"), and a checklist
of every kind, ticked, with an "only" link per row or presets.

DECISION The rules together, in any order (`LogFilter`): with no rule's eye open every line is shown;
with any open, a line is shown when an open-eyed rule matches it. A closed-eyed rule then hides what
it matches, whatever an open one said. So "only chat" is one rule, Chat with its eye open, and
"everything but room text" is one rule, Room text with its eye closed.

DECISION A rule judges the lines as the parser made them, before the pane's buffer merges a prompt
onto the line that finishes it, which is where the game's chat filter judges them (`PaneHistory`);
what is kept then becomes lines of screen. So a merged line never loses its kind or its codes, a
Chat rule shows what the game's chat filter shows, prompts matched by no rule, and a prompt before a
hidden line goes on to the next line shown.

DECISION A rule's kind and its "which" are the server's own codes, never the prose; a pattern only
narrows what they picked out (`RuleKind`):

| Kind | Which | Pattern |
|---|---|---|
| Chat: what the game's chat filter shows (`PaneHistory.IsChat`) | any channel, say, shout, tell (`SpeechPart`, from the 09 code's inner frame; a wrapped row by the channel `ChatColorizer.ApplyAll` marked) | from: in the speaker's part of the message, a wrapped row by its message's first row |
| Room text: the 02 family | Short (02 00, 02 01 names), Short + Long (and 02 02 descriptions), Long, All (and "Looking westward, you see ..." lines, 12 00 round a 02 01 name) | containing |
| Combat: 07 (isolated hits) and 08 (the fight) | any, you attack (08 01, 08 02), attacking you (08 03, 08 04, 07) | with: a name |
| Kills | any, you killed (08 08), killed you (08 09) | with: a name |
| Flees: 08 11, a flee tried whether or not it got away | - | with: a name |
| Weather: the 14 family | - | - |
| Any text | - | containing |

A name matches as a word, a creature's number after it allowed: "rat" matches "rat21", not
"rattlesnake". Every pattern ignores case.

DECISION (operator) Chat's choices are the channels the codes give. The server marks say, ask and
whisper alike (09 02), and shout, yodel and scream alike (09 01), so a choice between those verbs
would rest on the wording and is not offered.

EVIDENCE On a copy of the operator's database, 2026-11-14, a Debug build: 1,243,221 lines over all
854 Sessions; chat 22,517 lines, tells 4,033, shouts 838, room text 303,494, combat 72,492, kills
3,754, flees 1,187. Eight lines sampled from each were all of their kind: tells included the
wrapped rows of a long tell, shouts held yodels and hollers, flees held "has fled by trying to go"
as well as "has fled by going", and room text held "Looking westward, you see a place known as ..."
(12 00 holding a 02 01 room name).

FACT A line's codes come from the same colour frames that colour its text: a frame carries both, and
a pop takes both away (`Mud2C1Decoder`). So a frame whose pop never came would put its code on
following lines only as it put its colour there, on screen.

FACT Points has no category: mud2_FE4.txt lists no code for a gain or a loss of points. Its score
codes (12 02 "SCORE information." and its kin) are the replies to asking for the score.

## The Replays tab

Every `.muckrep` that `ReplayFiles` can play, listed by `ReplayLibrary` in the order `ReplayFiles`
searches, so a name held in two places is listed once, as the copy that plays: the player's replay
folder, Android's external files folder, then the replays packaged with the desktop builds. Each
row's "..." button opens its menu:

- **Play** closes Logs and connects `replay:<name>` the way a guest preset connects
  (`ConnectViewModel.PlayReplayAsync`): nothing is saved, and the form returns to what it showed.
  While a new profile is being typed it refuses, over Logs, because the form would be lost.
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
