# Line wrapping

Who wraps MUD2's text, by what rule, and where the width comes from. The code answers to this page.

Read `CLAUDE.md` first. The rule below is a measurement from the wire log, not something Bartle
documents, so it ranks as captured wire traffic and not as his word.

## Who wraps

RULE The client wraps. At the Option menu it sends one command interrupt, `ClientModeEntry.Bytes`,
which turns on the shell's local formatting, and `LineWrapper` breaks each logical line to the width
of the pane at paint time, so resizing the window wraps everything already on screen again.
enforced_by: `LocalFormattingTests.EntryBytes_AreOneCommandInterrupt_ThatTurnsLocalFormattingOn`

RULE The client sends the shell no terminal width: no `/T` at login, on a resize or ever, and no
column setting in a profile or in the settings dialog. The pane wraps at
`TerminalColumns.Effective` of what the terminal pane measures. The transport still advertises that size (NAWS,
or a pty window change over SSH), as it always did, and nothing in the recordings shows the server
wrapping on it.
enforced_by: `LocalFormattingTests.EntryBytes_NeverCarryATerminalWidth`, `LocalFormattingTests.NoSourceFileSendsATerminalWidth`

DECISION The server wrapped until this change, at the width the client sent with `/T` and sent again
whenever the window resized. Two things were rejected. Keeping `/T` and the server's wrap was
rejected because the server wraps whatever it sends, including the replies to the client's own probes,
which is what made those replies hard to parse. Re-sending `/T` on each resize was rejected because a
resize then re-wrapped only text sent afterwards, and never what was on screen.

FACT `TerminalBuffer` stores logical lines and never wraps; the renderer wraps every line on every
paint, and `LineWrapper.Wrap` records which rows continue the row above so that copying a wrapped
line yields one line (`TerminalSelection.Extract`).

FACT The pane's column count has no upper bound, so NAWS can now carry more than the 160 the
client used to cap it at.

## The server's side of it

EVIDENCE The Option menu's own help, from a guest login (`/C`, then `/F+`, then the reply):

```
/E - echo user input                    /F - toggle local formatting of lines
Option (H for help): /F+
[Local formatting is now on]
```

The reply is preceded by the ANSI colour code `ESC[0;37;40m`. It was captured in plain ANSI mode;
how it arrives after the client-mode entry has not been observed. The parser swallows the line
"[Local formatting is now on]" before game mode (`LocalFormattingConfirmed`) and the connection
writes a wire-log annotation for it, so a session that was not answered shows no annotation and shows
the line on screen.

FACT The entry is sent at the first Option prompt of each transport connection, and again after a
transport reset. Whether the setting survives the shell's drop back to the Option menu after a game
reset has not been observed.

FACT A server that wraps (a connection that never sent `/F+`, or a recording made before the client
did) ends each row, wrapped or not, with `\r\0\r\n` in the recordings of one server version and
`\r\r\n` in those of another. `ServerText.Collapse`
and the parser's wrapped-line handling remain for those.

## The rule

EVIDENCE Measured over the `wire` table of the operator's database at `/T80`, `/T90` and `/T100`,
replaying each run's rx bytes through `MudStreamParser`: a wrapped row holds at most W-2 characters,
and a row that ends a paragraph holds at most W-2 as well, plus the space a game line sometimes ends
in. Over 8,862 distinct paragraph-and-width pairs -
consecutive game lines, none but the last ending in a space, each row followed by a word that would
not have fitted - the rule below reproduced 8,845. The other 17 were each two server lines that the
detector had joined (one was "You feel a strange longing..." followed by a "(104/105)." line).

RULE `LineWrapper` breaks where the server did, with P the columns of the terminal pane itself - the
`TerminalView`, not the window and not the page, so a side panel that narrows the pane narrows the
wrap with it (`GamePage.RecalculateColumns`):

- A row shows at most P characters.
- The space that closes a wrapped row hangs one cell past the last column, and is not drawn there.
- A line of P characters or fewer is not wrapped.
- A longer line breaks after the last space at or before its (P+1)th character.
- The space stays at the end of the row it closes, so the rows of one logical line concatenate to
  its exact text.
- Spans are split at the break and keep their style and `ClickInsertText`.

enforced_by: `LineWrapperTests.RecordedParagraphs_RewrapToTheRowsTheServerSent`

DECISION The server's W is two cells wider than the pane that reproduces its breaks: a row holds W-2
characters, so P = W-2, and `AssertBreaksWhereTheServerDid` maps one to the other. Rejected:
reserving the two cells, as the server did, which left the text two cells short of the pane's right
edge while the left edge has `CombatRailResize.TerminalGutterDp`.

FACT The pane counts its columns from its width less `CombatRailResize.TerminalGutterDp` on the left
and `CombatRailResize.TerminalScrollbarDp` on the right, where `TerminalView` draws its scrollbar over
the last cell. The right gap is therefore never less than the left, and is larger by what is left over
when the width is not a whole number of cells. `TerminalView` also caps `Columns` at what it fits.

DECISION Three cases the recordings do not show are chosen, not measured: a space with only indent
before it is not a break, a word longer than a row is cut at P, and a run of spaces is left alone.
The tests that pin them say so.

`Mucka.Terminal.Tests/Data/server-wrapped-paragraphs.txt` holds recorded paragraphs at the three
widths, with rows where the next word would have made the row exactly W-1 characters, which is
the case that tells a break at W-2 from one at W-1. Several are the same text recorded at two
widths, and `ATextRecordedAtTwoWidths_ReproducesBothWrappings` reproduces both.

## Old recordings

FACT A recording made while the server wrapped holds the server's rows as separate lines, which the
pane then wraps again at its own width. `$trimdb unwrap` (`WireUnwrap`) joins them, for runs that
ended some days ago, using the width the client sent with `/T` before each row.

DECISION It infers, and is a command rather than something the client does itself. A server wrap and
a line the server ended on purpose are the same bytes. Two consecutive game-mode lines are one wrapped
line when the first holds at most W-2 characters, does not end in a space, the next word would not
have fitted, the second does not begin with a space, and the second line's first character follows
the first line's terminator directly: a code, a pop, a prompt or a probe block between them means the
first line closed a paragraph or a turn. Only a width the client sent as the width command itself
counts, never a `/T` inside something the player typed. The splash and the menus, lines with a C1
kind, and a run that sent no width are left alone. A run is refused when the rewritten stream does not
parse to exactly the old lines, kinds included, with the joins made. Every row it changes is copied
first and can be put back with `$trimdb revert`, until `$trimdb expire` removes the copy.
enforced_by: `WireUnwrapBoundaryTests`, `WireUnwrapTests`, `WireTrimTests.AnUnwrap_JoinsTheLinesTheServerWrapped_AndRevertPutsTheOriginalBytesBack`

DECISION Unwrap is an exception to "evidence is immutable" (CLAUDE.md), taken because the wire log is
the only record and a replay of a server-wrapped run wraps twice. The rejected option is changing
`wire` with no copy. Run it before `$trimdb trim`: trim removes the probe sends and the prompts that
unwrap uses as boundaries.

EVIDENCE At each width the server did not wrap what players typed: echoed chat and tells run past W
in the recording.
