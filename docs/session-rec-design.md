# Session recording (`rec`), as plain text

What `rec` produces is **a transcript of the screen**: what the player saw, in the order they saw
it, with MUD2's markup resolved away. One file per session,
`~/.mucka/session-rec.{host}.{yyyyMMdd-HHmmss}.txt`.

It is deliberately the lossy, human half of the pair. The byte-exact record is the wire log, which
is always on and needs no decision here (`docs/persistence-design.md`). Nothing in this document
may be read as a reason to weaken that; a transcript that disagrees with `wire` is the transcript
that is wrong.

## Clio does this, and its shape is the one to copy

`G:\Source\clio-1.8a` is the North Star for client design, and it logs sessions. What it does,
from `src/logging.c` and `src/main.c`:

- **It logs the display, not the socket.** `ldisplay()` is called from `wdisplay()`, on the same text
  that window is being asked to show.
- **But one step upstream of the codepage.** `wdisplay` maps its string through `o_codepage` into
  `buf`, hands `buf` to curses, and then calls `ldisplay(w, s)` - `s`, the unmapped original. The
  file gets the client's own text; the codepage translation is for the glyphs on the terminal and
  does not reach the log.
- **It logs one window.** Windows carry a `logged` flag set at `wopen()`. `main.c` opens the status
  line, the main scrolling window and the input window (twice, for two screen sizes), and only
  `mwin`, the main scrolling window, passes `logged=1`. The map panel opens its own in
  `map_panel.c` and passes 0.
- **It is a character stream, not a record format.** No timestamps, no direction markers, no
  framing. `\b` seeks back one byte so an in-place correction reads correctly in the file.
- **It is bracketed by one line at each end**: `Logging by Clio MUD2 client from {host} starting on
  {date}.` and a matching `finished on`.
- The file is `.txt`, named from the clock.

Note what Clio does NOT do: it never logs the input window. Mucka's target includes input, and the
next section is why that costs nothing.

## Input is already in the output

MUD2 echoes. Verified on the wire, session 14:

| Record | Dir | Bytes |
|---|---|---|
| 119965 | Tx | `wave\r\r\n` |
| 119966 | Rx | `wave\r\r\n` |

The server sends the command back, and `TerminalBuffer.Append` merges it onto the live prompt's row,
so the command is on screen without the client echoing anything itself. A transcript of the screen
carries the player's commands because the screen does - post-alias and post-F-key expansion, which
is the form worth keeping.

The three things that are typed but never echoed, and what happens to each:

- **Client-local commands** (`$`-prefixed, `^1`-`^3`). Never sent, so never echoed - but each one
  answers with `GameViewModel.AddSystemLine`, which is on screen and therefore in the transcript.
- **F-key macros.** `GameViewModel.AnnotateFkey` shows `// {macro}` on screen through
  `AnnotationReady`, a path that bypasses `OnLineReady` entirely. The recorder needs its own tap
  there - see below.
- **The login exchange.** The account ID **is** echoed (record 119941, `Z00012305`); the password
  is **not** (119942 Tx, and 119943 comes back `OK` with no echo). So a transcript armed before
  login carries the account ID in clear and never the password, which is Clio's behaviour too. See
  the ruling below.

## Where the tap goes

`GameViewModel.OnLineReady`. It is the single funnel: server lines arrive there via
`_conn.LineReady` and every client-side system line via `AddSystemLine`.

It is NOT a TCP-thread-only path - `AddSystemLine` reaches it from UI-thread command handlers,
`ToggleRecording` among them. Invariant #1 holds because `Append` does no I/O and no allocation
storm, not because of where it runs.

It must **not** be `TerminalView`. The chat filter in `GamePage.DoFlushWork` decides what reaches
the view, and toggling it replays `ChatSnapshot()` through `AppendLines` - a recorder downstream of
that would record a filtered session and duplicate it on every toggle. `OnLineReady` is upstream of
the filter and sees the whole stream once.

Second tap: `AnnotationReady`, for the F-key notes above, recorded the way `InjectAbovePartial`
displays them - standalone, not merged into the prompt.

One consequence, measured rather than designed: lines are recorded as they arrive off the socket but
reach the pane on the next UI drain, while an annotation reaches both synchronously. An annotation
raised inside that one-tick gap lands in the file on the other side of the line it followed on
screen.

## What a line is

Not every `StyledLine` is a line on screen. The prompt is `IsPartial` and is *replaced* until it
completes; writing each one out would duplicate every prompt in the file.

`TerminalBuffer.Append` already owns these semantics exactly - partial replaces partial, blank
complete promotes the partial, non-empty complete merges into it, form-feed clears - and the
recorder must not grow a second copy of them. The recorder holds its own `TerminalBuffer` and
writes a line at the moment that buffer commits one. That needs one new thing in `TerminalBuffer`:
an event raised from `Commit`. The cap is small; the recorder keeps no scrollback.

Text comes from `StyledLine.PlainText` after `TerminalText.Sanitize` and `TerminalText.ExpandTabs`,
which is what `TerminalView.AppendLines` applies before buffering - same input, same output.

## The file

- UTF-8. `PlainText` is a .NET string; the repo's ASCII rule is about the repo, not runtime output.
- Opened on arm, on the calling thread, so a failure has a human in front of it - the reason the
  old sink opened in its constructor.
- Recording a line hands a string to an unbounded channel; one background task does the writing,
  `AutoFlush` on. Surviving a crash mid-session is most of why the file exists. Callers may be on
  the UI thread and nothing on that path may touch the file (Invariant #1). The opening line is the
  exception - the constructor writes it, before that task starts.
- A write that fails after arming ends the recording and reports itself to the terminal. A file that
  silently stopped growing under a lit chip is the failure worth engineering against here.
- The client's own "Recording started/stopped/failed" notes go to the screen and **not** into the
  transcript. A transcript records the session, not its own existence, and the header and footer
  lines already mark where it begins and ends.

  They are kept out by a `transcribe: false` flag on the line, **not** by attaching the recorder
  after the note or detaching it before. Sequencing around the field looks equivalent and is not:
  the read loop delivers lines throughout, so every instant the field is null while the file is open
  is a line the transcript silently loses. The recorder is attached the instant it exists.
- `-record` arms on the read-loop thread, before `OnGameModeEntered` marshals the rest of its work.
  A dispatched block waits its turn while the read loop is still delivering the entry banner and the
  first room, which is precisely what the flag was asked to capture. It also puts the one synchronous
  file open off the UI thread, which is the right side of Invariant #1.
- Header and footer, after Clio: host and local time. A blank line under the header and another
  above the footer, so the transcript proper is everything between them.
- Path resolves through `MuckaPaths.GetDataDirectory()`, so Android lands in app data and nothing
  hardcodes a Windows path.

## Where the code lives

`Mucka.Terminal`. It already references `mudsharp`, already owns the commit semantics and the
sanitiser, is MAUI-free, and is tested by `Mucka.Terminal.Tests`. Putting it in `Mucka.WireLog`
instead would mean a new reference edge from the byte-level library to the screen library, which is
backwards; a sixth `Mucka.Util` library for one file is not worth the entry in `CLAUDE.md`.

The wart is file I/O in a library named for a buffer. If that is the wrong trade, the alternative is
to split it - commit semantics stay here, the file half moves to `Mucka.WireLog` as a plain
string sink - at the cost of the glue landing in `Core/`, where the MAUI-free suites cannot test it.

## Tests

The fixture pattern `WireLogTests` uses: captured bytes through the production parser into the
recorder, assert the text. Required cases:

1. **Prompt is not duplicated.** A prompt partial, replaced several times, then completed by an
   echo, writes one line.
2. Prompt and echo land on one line, as the screen shows them.
3. A form-feed clears nothing in the file: the screen clears, the transcript keeps scrolling. The
   file is a record of what was shown, and a clear is not an unshowing.
4. An F-key annotation is written standalone, above the prompt it was raised against.
5. A write failure reports itself, ends the recording, and leaves `Completion` completed rather than
   faulted - teardown awaits it after closing the store, and a faulted task there would take the
   store's close down with it.

The system-line half of case 4 has no home here: `AddSystemLine` lives in `GameViewModel`, which
`Mucka.Terminal.Tests` cannot reach. It shares `OnLineReady` with every server line, so what it
exercises is already covered.

## Settled by the operator

1. **Hand-armed, as before.** Not always on, which is the one place `rec` differs from the wire log.
   Off until pressed, from any of:
   - the `rec` chip in the wide status bar, as it was before;
   - an `(R)` in the compact bar's elastic spacer column - grey idle, red recording. The compact bar
     has no room for a labelled chip, and riding the `*` column means it widens nothing and moves
     nothing when it appears;
   - "Log Recording" in the hamburger, a checkable live toggle like Side Panel and the rest, so the
     control has a name on it at every width and on a phone;
   - `-record` on the command line.

   The three on-screen ones are the same toggle, and all of them are available at all times - at the
   shell, before login, mid-fight. The controls were once gated on game mode; that was left over from
   when recording was a debug-build feature, and it is gone. Nothing may reintroduce a gate: he
   prunes transcripts by starting and stopping them, and a control that disappears is one he cannot
   use.

   **`-record` ARMS; it does not toggle.** It fires at the first game-mode entry, which is after the
   shell - so by the time it runs he may already have started recording by hand, and a toggle would
   switch off the recording he had just switched on. `StartRecording` is the arm-only path and is a
   no-op when something is already recording; `ToggleRecording` is what the three controls call.
2. **The account ID is not masked.** A transcript armed before login carries it in clear - which is
   reachable, since arming is. Clio does the same, and the file is on his own machine.

## Left out

**Retention.** Nothing here deletes old transcripts. The wire log's retention work is a separate
item and should decide for both.
