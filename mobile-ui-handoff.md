# Handoff — deferred work after the narrow-terminal regression fix + full review (2026-06-12)

Context being reset. The previous session fixed the FES/FEW narrow-terminal regressions, ran a
4-agent review (input path, protocol/session, UI/rendering, simplification), and applied the
top findings. This file is the queue of everything reviewed-but-deferred, in priority order,
with enough detail to act without re-deriving. Line numbers are approximate — trust symbol names.

## Environment / build / test (unchanged)

- Windows build: `dotnet build Mucka.csproj -f net10.0-windows10.0.19041.0`
- Android build+deploy: `dotnet build Mucka.csproj -f net10.0-android -p:LocalAndroid=true -t:Run`
  (drop `-t:Run` to build only). Device: Pixel 5 (`redfin`), USB-authorized.
- adb (NOT on PATH): `C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe`
- Screenshot: `adb exec-out screencap -p > out.png` then view. Launch app:
  `adb shell monkey -p org.kfs.mucka -c android.intent.category.LAUNCHER 1`
- Tests: `dotnet test mudsharp.Tests` — **239 passing, 0 skipped** (replay tests find their
  private recordings on this box).
- Input-latency measurement: build with `-p:InputDiag=true`, log at `%TEMP%\mucka-input.txt`,
  synthetic typist at `tools/type-test.ps1`. **Measure before fixing feel — see the
  project-input-perf memory; speculative input fixes have burned multiple sessions.**
- Live-connect testing: use the **MUD2.COM** profile (test account). The MUDII UK profile is
  the user's own play account — never auto-connect it.

## State of the working tree (all uncommitted on `main`)

Two clusters of changes are interleaved in the diff:
1. **Mapping work in progress** (`Core/Mapping/`, `Pages/MappingPage.cs`, `tools/mapping/`,
   `MUD-Cartography.md`) — pre-existing, not from the fix session.
2. **The 2026-06-12 fix batch** (see below). Worth committing separately from the mapping work
   if the user wants history; the protocol fixes + tests are self-contained.

### What just landed (for context, all tested)
- **Root cause of the blank-Players-list / blank-lines-per-FEW regressions**: on narrow
  terminals the server hard-wraps even escaped probe responses AND ends the FES line with a
  bare CR (`\r\0`, no `\n`). `OnFesData` terminated only at `\n`, so it swallowed the pop,
  prompt container, FEW opener, and first WHO name — FEW context never opened. Fixed via
  parse-at-`\r`-or-`\n`-once-15-fields + new `FesLineTail` absorb state + separator insertion
  at wrap points (`Mud2C1Decoder.OnFesData/OnFesLineTail`). FEW name echo is conditional again
  (visible outside probe contexts). Suppressed-context newlines no longer tick `PromptAllowed`
  (was the stray-`*` noise) and FEI/FEX text no longer clears `_atLineStart` (was suppressing
  RoomEntered after heartbeats). Regression tests: `mudsharp.Tests/Fixtures/NarrowTerminalHeartbeatTests.cs`
  (reconstructed byte-for-byte from a Pixel 5 logcat capture).
- Review fixes applied: GameLineAnalyzer TryParse (remote-crash via `say "(huge/9)"`),
  option-menu exit matcher arms only at column 0 (griefable mid-line match), `StyledLine.PlainText`
  cached, `OnInputHandlerChanged`/`OnTerminalHandlerChanged` invoked directly after subscribe
  (HandlerChanged race = the likely recurring-lag trigger), Windows ▶ Send button reads the
  native TextBox (was sending blank lines), FEI side-panel rebuild skipped when SequenceEqual,
  MappingPage guidance pulse aborted on disappear, read-loop double copy removed,
  `ExitGameMode` clears `_pendingRoomShort`.

### ⚠ Deploy note
The Pixel 5 has the APK from BEFORE the review-fix batch (it has the regression fixes only —
deployed mid-session, then the user was live-testing so no redeploy). Both targets build clean.
**First action when the device is free: redeploy to Android.**

---

## Deferred task queue (priority order)

### P1 — Stall-proof the send path (input-smoothness, the #1 app priority)
`Core/MuckaConnection.cs` `SendBytesSync` does a synchronous `_stream.Write` under `_writeLock`
on the calling thread — which is the **UI thread** for every Enter/fkey/dreamword send and the
50 ms `AntiIdleTick` (fires with no user action). Hazards: (a) `NetworkStream.Write` blocks when
the TCP send buffer fills (degraded network = exactly when the player is frantically typing);
(b) the UI thread can queue behind a heartbeat-probe write from a ThreadPool timer
(`MudSession.SendFesSubscription`/`OnStaleDeadline` share the lock); (c) while capture is on,
`SessionCapture` has `AutoFlush=true` → synchronous disk flush inside the lock per send.
**Fix:** a `Channel<byte[]>` drained by one writer task owned by `MuckaConnection` (preserves
ordering, takes the lock off the UI path); move capture writes onto the same writer task.
While there: set `_client.NoDelay = true` in `ConnectAsync` (Nagle adds 40–200 ms to small
command writes — perceived command latency, one line).
Related hygiene in the same file: `ConnectAsync` re-subscribes `_session.OutgoingBytes += SendBytesSync`
per connect with no guard, and replaces the previous `TcpClient`/CTS undisposed.

### P2 — Gate the per-keystroke `UpdateLayout()` (caret-follow workaround)
`Pages/GamePage.xaml.cs` `OnInputSelectionChanged`: fires on every typed char and, with the
caret at end-of-text (i.e. always while typing), calls `tb.UpdateLayout()` — a synchronous
measure/arrange of a guaranteed-dirty TextBox subtree. It exists only to compensate for WinUI's
caret-follow dying **after another app window ($con / map) has been activated**.
**Fix:** set a flag the first time `OnOpenRawConsoleRequested`/`OnMapPanelRequested` runs and
skip the whole handler until then (or skip when `_inputScroller.ScrollableWidth == 0`).

### P3 — Heartbeat-synchronized UI work (periodic jank candidates)
- `ViewModels/GameViewModel.cs` `OnStatsUpdated`: raises the full ~50-name PropertyChanged batch
  every heartbeat even when the snapshot is unchanged (idle character = common case), feeding
  both the wide AND the always-inflated compact stats grids. **Fix:** keep the previous
  `GameStatsSnapshot`, early-return on equality; optionally latest-wins coalescing of the
  `BeginInvokeOnMainThread` dispatch. INPUT_DIAG's `STATS update` marker exists to correlate
  against `UI STALL` lines — measure first.
- `Rendering/TerminalView.cs` `BuildVisualRows`: re-wraps the entire ≤200-line buffer via
  `LineWrapper.WrapAll` on EVERY paint (live append, selection-drag, scroll) — ~600–1000 small
  allocations/frame, ~20 Hz ceiling during bursts. Not a current stall (paint is sub-ms), but
  the dominant per-frame cost and Gen0-churn source. **Fix:** cache wrapped rows keyed on
  (buffer revision, columns); wrap incrementally on append; wrap-once for the frozen history
  snapshot.

### P4 — Protocol follow-ups (from the protocol/session review; all have repro notes)
- **C00 semantics (needs Clio verification first):** `Mud2C1Decoder` case 0x9B does
  `Apply(WHITE)` — a PUSH — but C00 is documented as init_stack/reset. Stack grows ~1 entry per
  frame (1001 deep after 1000 frames in repro) and, worse, a context that loses its closing pop
  currently suppresses all output until game exit, whereas a true reset would self-heal at the
  next frame. **Fix if Clio agrees:** clear `_colorStack`, set/push WHITE, then
  `CheckContextClosures()`.
- **FEW name continuation not cancelled at context close** (`BeginFewNameContinuation` /
  `ExitFewContext`): a rainbow name whose `\n` is lost keeps appending and fires a bogus
  `FewPlayerReady` AFTER `FewListComplete`. Finalize-or-cancel in `CheckContextClosures`.
- **Unbounded buffers:** `OnFesData` checks its 1024 cap only at CR/LF; `OnC95Data` (waits for
  5 newlines) and telnet `IacSbData` (waits for IAC SE) have no cap. Cheap per-byte caps.
- **`OnC95LogoutLine`/C95 Rule-A parsing** trims only `\r` — `TrimEnd('\r','\0')` is free
  insurance if the block ever uses CR-NUL endings.
- **`EscapeDashAnnotation`** swallows blindly to `\n` — exit early on a C1/IAC lead too.
- **FEI/FEX partial item lost on context close** — flush non-empty `_feiLine`/`_fexLine` in
  `ExitFeiContext`/`ExitFexContext`.
- **`TcpMudConnection`** (mudsharp/Transport) duplicates MuckaConnection's read loop minus the
  write lock + connect timeout; only `mudsharp.Demo` uses it. Consolidate or delete.
- Perf nits: intern the 16×16 fg/bg `TextStyle` table in `Apply` (alloc per color code on
  color-dense output); digit/paren pre-filter before the 8 regexes in `GameLineAnalyzer`;
  `Encoding.ASCII.GetString(CollectionsMarshal.AsSpan(buf))` to kill the `ToArray()` copies;
  `TryEmitTerminalWidthLine` allocates `prefix[..n]` per pre-game line.
- Dead logic: `Mud2C1Decoder.Reset(ref ParserState)`'s C95 correction is overwritten by the
  caller; `ParserState.Iac` is never a resting state.

### P5 — Android input parity (deliberate decision needed)
`Pages/GamePage.xaml:~512` keeps `Text="{Binding InputText, Mode=TwoWay}"` — on Android every
keystroke round-trips native→VM→PropertyChanged (the exact mechanism that was the Windows lag
bug; Windows neutralizes it at runtime). Either replicate the Windows pattern (read
`Entry.Text` on send; VM pushes only deliberate changes) or document that Android accepts the
round-trip so the next regression hunt doesn't burn time here. Verify with INPUT_DIAG on device.

### P6 — Simplification batch (from the simplification review; all low-risk, grep-verified)
- `ViewModels/GameViewModel.cs`: stat property setters are dead (`OnStatsUpdated` writes backing
  fields directly) → make get-only; delete never-bound `Rank`, `StaText/MagText/StrText/DexText/ScoreText`,
  `IsVeryCompact`, `DreamwordCompactDisplay`, empty `OnRoomEntered`; ~60 lines.
  **Do NOT** swap the batch for `OnPropertyChanged("")` — it would rewrite the Android input box
  mid-typing via the TwoWay binding.
- `GameViewModel.DisposeAsync` unsubscribe list has drifted from the ctor (missing `RoomEntered`×2,
  `RoomShortReady`, `FeiListStarting/ItemReady/ListComplete`) — same leak class as the old
  FES-on-relog bug. Extract a single (event,handler) table used by both.
- `Core/Mapping`: the `"edge: …"` annotation parser exists twice (`MapGraph.ParseEdgeLine` vs
  `MappingStore.ParseEdgeAnnotation`) and capture files are scanned 4× (session ctor 2×, page
  Reload 2×). One `ReadEdgeAnnotations(dir)` iterator consumed by all; session should own the
  graph the page queries. ~70 lines + halved IO. Fresh untested code — review hardest.
- `Pages/GamePage.xaml`: wide + compact stats bars duplicate every binding (8 copy-pasted
  FormattedString stat labels) → a `StatChip` ContentView; ~20 repeats of the Cascadia Mono
  font attributes → one Style. Medium risk (visual check both widths).
- `GameViewModel.SendFkey`/`SendFkeyAbsolute`: identical bodies → extract `SendMacro`.
- Dead: `Profile.FewRefreshInterval` (+ `fewrefresh` ini lines), `MuckaConnection.ClientModeReceived`,
  `SidePanelViewModel.CharacterName/HasCharacterName`, `/!sleep` stub.
- Small: 13-exit triple repetition in `SidePanelViewModel` → table; `OpenToolWindow` helper for
  the two copy-pasted window-reuse bodies; `LogCrash` duplicated in GamePage/ConnectPage **with
  an accidental append-vs-truncate difference** — unify in Core; `Mucka.csproj` 15-line exclude
  block → one `DefaultItemExcludes` property; MainActivity F1–F12 switch → range arithmetic;
  `MappingSession.EnabledExits` returns the live mutable set → return a copy.
- Repo-root scratch: `fecodes.txt` (protocol doc — worth keeping, move to `docs/` or `tools/`),
  `screen.png`/`android-screen.png` (gitignore).

### P7 — Mobile UI leftovers (from the 2026-06-11 session, still open)
1. **Dreamword chip → floating semi-transparent chip** over the top-right of the terminal,
   hidden when unset (`DreamwordIsPlaceholder`), "always float" as a saved option
   (ClientSettings/Profile + settings UI). Currently inline: normal-mode Border + compact-mode
   label in the compact grid.
2. **White oblong on compact row 2** (between score and dream chip) — may already be gone after
   the TTR column restructure; verify with a connected compact-mode screenshot; if present,
   `adb shell uiautomator dump` to identify the element.

### P8 — MappingPage polish (new uncommitted code)
- Guidance pulse is still a UI-thread dispatcher animation while the map window is open —
  port to a compositor animation (pattern: `Behaviors/StaleDimBehavior.cs`) or use a static highlight.
- `Reload()` runs megabyte-scale `Split`/`Distinct` summaries on the UI thread after its await,
  re-reads + re-parses every `.jsonl` per completed op, and is `async void` with the post-await
  code outside the try. Move counts into the `Task.Run`, cache per-file scan results keyed on
  (path, LastWriteTime).

## Review coverage notes (so the next session doesn't re-audit)
The 2026-06-12 review explicitly verified CLEAN: the Windows keystroke path (zero managed code
per plain key; hotkeys are native accelerators), focus pinning, network→UI batching (one
ConcurrentQueue drain, no per-line marshal, 3 dispatches per heartbeat), terminal invalidation
policy (1 per ≤50 ms batch), compositor-only fades, FesLineTail/FesHasAllFields, context-depth
bookkeeping, prompt capture/PromptAllowed gating, telnet negotiation, Reset() completeness,
threading contract, probe debounce policy, the profiles.json migration shim, and INPUT_DIAG
zero-cost gating. Full agent reports are in the 2026-06-12 session transcript if detail is needed.
