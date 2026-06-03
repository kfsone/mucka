# Mucka — Copilot Instructions

Mucka is a .NET MAUI MUD2 telnet client targeting **Android** and **Windows** (iOS/macOS later).
It is inspired by Clio, a C/flex/bison MUD2 client whose source is our authoritative reference for
the MUD2 wire protocol.


## Build commands

Build for development (Windows only, fast):
```
dotnet build -f net10.0-windows10.0.19041.0 -c Debug
```

Build for Android (requires `maui-android` workload, run on Linux/Mac or with `EnableWindowsTargeting`):
```
dotnet build -f net10.0-android -c Release -p:EnableWindowsTargeting=true
```

Run tests (mudsharp unit suite, 165+ tests):
```
dotnet test Mucka.slnx -c Debug
```

Run a single test class:
```
dotnet test mudsharp.Tests\mudsharp.Tests.csproj --filter "FullyQualifiedName~ClassName"
```

## Architecture

For practical reasons, *only* MUD2 features and capabilities are implemented as they will be
untested, unchecked, and will be tech-debt-on-arrival.


### Data flow

```
TCP bytes
  → TcpMudConnection.ReadLoopAsync       (background Task)
  → MudSession.Feed()                    (→ MudStreamParser, synchronous byte-stream state machine)
  → MudSession / MudStreamParser events:
      LineReady(StyledLine)              → GameViewModel enqueues to ConcurrentQueue
      StatsUpdated(GameStatsSnapshot)    → GameViewModel updates properties via MainThread
      GameModeEntered                    → MudSession starts FES+FEW+FEI heartbeat timer
  → GamePage 50 ms IDispatcherTimer
  → GameViewModel.FlushPendingLines()
  → TerminalView.AppendLines()           (SkiaSharp terminal — renders directly, no WebView/JS)
```

### Layers

- **`mudsharp/`** — `net10.0` class library (`MudSharp` namespace); no MAUI dependencies; tested by `mudsharp.Tests/`
  - `Protocol/MudStreamParser` — byte-stream parser (telnet + ANSI SGR + MUD2 C1 protocol); fires events synchronously on the `Feed()` caller thread
  - `Protocol/Mud2C1Decoder` — handles MUD2 proprietary C1 binary sequences
  - `Protocol/TelnetNegotiator` — telnet option negotiation (including NAWS)
  - `Session/MudSession` — policy wrapper around `MudStreamParser`; owns FES heartbeat, stats merging, dreamword tracking; **primary API for consumers**
  - `Session/MudSessionOptions` — configures heartbeat interval (default 10 s)
  - `Models/GameStatsSnapshot` — immutable `record` snapshot of FES game stats; `HasFesStats=true` means boolean flags are authoritative (replace); `false` means OR-merge
  - `Models/StyledLine` / `StyledSpan` — styled text model
  - `Transport/TcpMudConnection` — TCP socket + read loop; owns a `MudSession`

- **`Mucka.Terminal/`** — `net10.0` display library (`Mucka.Terminal` namespace); depends only on `MudSharp.Models`, **no MAUI**; tested by `Mucka.Terminal.Tests/`
  - `TerminalBuffer` — committed-line ring (cap 120) + one live partial line
  - `LineWrapper` — naive fixed-column wrapping (style-preserving)
  - `TerminalText` — strips control-char "tofu"; expands tabs to 8-column stops
  - `TerminalSelection` — plain-text extraction for clipboard copy

- **`Core/`** — MAUI app glue (no protocol logic)
  - `MuckaConnection` — wires `TcpMudConnection` to the ViewModel
  - `MudLoginHandler` — pre-game login state machine
  - `Profile` / `ProfileStore` — connection profiles persisted as JSON in `FileSystem.AppDataDirectory`
  - `SessionCapture` — optional JSONL session transcript logging

- **`ViewModels/`** — standard MVVM; no DI container (objects manually wired)
  - `BaseViewModel` — `INotifyPropertyChanged` with `Set<T>` helper
  - `GameViewModel` — drives the 50 ms flush timer, command history, and `_historyBuffer`
  - `ConnectViewModel` — profile CRUD and connection setup

- **`Pages/`** — MAUI `ContentPage` subclasses
  - `GamePage` — hosts the SkiaSharp terminal (`Rendering/TerminalView`) and the input box
  - `ConnectPage` — profile list; creates `GameViewModel` on UI thread after connect

- **`Rendering/`** — SkiaSharp terminal rendering (MAUI app, `Mucka.Rendering` namespace)
  - `TerminalView` — `SKCanvasView`: live render + frozen-snapshot scrollback + mouse selection
  - `TerminalFont` — loads the embedded Cascadia Mono typeface; fixed cell metrics
  - `TerminalTheme` — the Campbell colour palette

## Key conventions

### MUD2 prompt detection
The MUD2 game prompt is **not** the `*` character. The prompt is signalled by the slot1 escape markup in the binary protocol. Do not infer prompt state from text content.

### Code conventions
- No non-ASCII characters anywhere in source code.
- `MudSession` is the correct consumer-facing API; do not reach into `MudStreamParser` directly from MAUI code.
- `GameStatsSnapshot` is an immutable `record`. `HasFesStats=true` means boolean condition fields (IsBlind, IsDeaf, IsCrippled, IsDumb, PersonaSaved) are authoritative server state and **replace** current values. `false` means OR-merge.

### Session capture (JSONL)
`SessionCapture` logs raw transcripts in JSONL format. Each entry is one of:
- `["...elided..."]` — elision marker to reduce context burden
- `[timestamp_ms, mode, json-escaped-data]` — where `mode` is `"tx"` (sent), `"rx"` (received), or `"an"` (annotation)

### Threading rules
- `MudSession.Feed()` (and all `MudStreamParser` events) fire on the **TCP background thread**.  
- Never touch UI, MAUI properties, or `IDispatcherTimer` from a TCP callback.  
  Always marshal with `MainThread.BeginInvokeOnMainThread(...)`.
- `IDispatcherTimer` must be **created** on the UI thread — the reason `OnGameModeEntered` wraps timer creation in `BeginInvokeOnMainThread`.

### ViewModel properties
All bindable properties use `BaseViewModel.Set<T>`:
```csharp
public int Stamina { get => _stamina; set => Set(ref _stamina, value); }
```
When a derived display property must also update, chain `OnPropertyChanged`:
```csharp
public int Stamina { get => _stamina; set { Set(ref _stamina, value); OnPropertyChanged(nameof(StaText)); } }
```

### Commands
- `AsyncCommand` for async `ICommand` (disables itself while executing).
- `Command` / `Command<T>` for synchronous operations.

### Terminal rendering (SkiaSharp)
The scrollback is a SkiaSharp `SKCanvasView` (`Rendering/TerminalView`), **not** a WebView — that
was removed in v0.5.0 because Chromium on the UI thread fought keyboard input. The 50 ms flush
hands styled lines to `TerminalView.AppendLines()`, which sanitises them (control-char strip +
tab expansion) into a `TerminalBuffer`; the view paints directly. Mouse input (drag-selection,
click-to-scrollback, wheel) is driven by **native WinUI pointer events** on Windows; SkiaSharp
touch events handle finger-pan on Android.

### Partial lines
`StyledLine.IsPartial = true` means the line has no `\n` yet (e.g. a login prompt).
- `TerminalBuffer` keeps at most one live partial line; a completing line merges into it
  (prompt + echo on one line), and a blank line promotes it to a committed line.
- The buffer caps at **120 committed lines** (oldest trimmed). This logic is unit-tested in
  `Mucka.Terminal.Tests` (it was a faithful port of the old JS injection rules).

### MUD2 C1 protocol
The MUD2 server mixes a proprietary binary protocol into the telnet stream.
Key facts for `MudStreamParser` / `Mud2C1Decoder`:
- C1 bytes are in the range **0x9B–0xFE**. Every recognised sequence ends with **C255 = `0xFF 0xFF`**.
- Unrecognised C1 bytes fall through to the `C1GenSeq`/`C1GenFF1` catch-all states and are consumed silently.
- FES subscription: the **periodic heartbeat** (`MudSession`) sends `\x1B-[FES,FEW,FEI\x1B-]` (stats + who-list + inventory) every 10 seconds. **Reactive** C1-triggered sends (inside `Mud2C1Decoder`) send FES-only to avoid clearing the who-list during combat/spell events.
- FES is sent on game-mode entry and then on every heartbeat tick. Never on TCP connect.
- The reference implementation for every C1 sequence is `MUD-ClientProto.md` and verifiable thru `G:/Source/clio-1.8a/src/telnet.l`.

### Versioning
`version.json` holds `baseVersion` (e.g. `"0.3.00"`) which must match the latest git release tag.
Pushing a `v*.*.*` tag triggers the release workflow (Windows zip + Android APK → GitHub Release).

### Multi-TFM build quirk
Building both TFMs at once will report the same warning **twice** (once per target framework).
Build with `-f net10.0-windows10.0.19041.0` during development to keep output unambiguous.
