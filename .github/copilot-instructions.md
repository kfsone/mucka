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

There are no test projects yet. The CI workflow skips the test step if no `*.Tests.csproj` exists.

## Architecture

For practical reasons, *only* MUD2 features and capabilities are implemented as they will be
untested, unchecked, and will be tech-debt-on-arrival.


### Data flow

```
TCP bytes
  → MudConnection.ReadLoopAsync          (background Task)
  → MudStream.Feed()                     (synchronous, byte-by-byte state machine)
  → MudStream events:
      LineReady(StyledLine)              → GameViewModel enqueues to ConcurrentQueue
      StatsUpdated(GameStats)            → GameViewModel updates properties via MainThread
      GameModeEntered                    → GameViewModel starts FES heartbeat timer
  → GamePage 50 ms IDispatcherTimer
  → GameViewModel.FlushPendingLines()
  → InjectLinesAsync → ExecuteScriptAsync (JavaScript into WebView)
```

### Layers

- **`Core/`** — protocol and data; no MAUI dependencies
  - `MudStream` — byte-stream parser (telnet + ANSI SGR + MUD2 C1 protocol)
  - `MudConnection` — TCP socket + read loop; owns a `MudStream`
  - `GameStats` — mutable FES stats snapshot; owned by `MudStream`, passed by reference to subscribers
  - `StyledText` — `StyledSpan` (text + color) and `StyledLine` (list of spans)
  - `Profile` / `ProfileStore` — connection profiles; persisted as JSON in `FileSystem.AppDataDirectory`

- **`ViewModels/`** — standard MVVM; no DI container (objects manually wired)
  - `BaseViewModel` — `INotifyPropertyChanged` with `Set<T>` helper
  - `GameViewModel` — owns the FES heartbeat, command history, and `_historyBuffer`
  - `ConnectViewModel` — profile CRUD and connection setup

- **`Pages/`** — MAUI `ContentPage` subclasses
  - `GamePage` — hosts the WebView scrollback and drives the 50 ms flush timer
  - `ConnectPage` — profile list; creates `GameViewModel` on UI thread after connect

- **`Helpers/HtmlScrollback`** — converts `StyledLine` → HTML; holds the static terminal page (Campbell color scheme, Cascadia Mono)

## Key conventions

### Threading rules
- `MudStream.Feed()` and all its events fire on the **TCP background thread**.  
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

### JavaScript injection (WebView)
On WinUI, `EvaluateJavaScriptAsync` silently fails for `HtmlWebViewSource` pages.
**Always use `ExecuteScriptAsync` (the local wrapper)**, which routes to `CoreWebView2.ExecuteScriptAsync`
on Windows and `EvaluateJavaScriptAsync` on Android:
```csharp
await ExecuteScriptAsync(script);   // never call EvaluateJavaScriptAsync directly
```

### Partial lines
`StyledLine.IsPartial = true` means the line has no `\n` yet (e.g. a login prompt).
- Rendered with CSS class `lnp` (not `ln`).
- The JS injection replaces an existing `.lnp` in-place rather than appending a new element.
- The live-line buffer caps at **120 permanent `.ln` lines** (trimmed from the top in JS).

### MUD2 C1 protocol
The MUD2 server mixes a proprietary binary protocol into the telnet stream.
Key facts for `MudStream`:
- C1 bytes are in the range **0x9B–0xFE**. Every recognised sequence ends with **C255 = `0xFF 0xFF`**.
- Unrecognised C1 bytes fall through to the `C1GenSeq`/`C1GenFF1` catch-all states and are consumed silently.
- FES subscription (`\x1B-[FES\x1B-]`) is sent only on the **game-mode entry signal** (`0x9D 0x9C 0xFF 0xFF`) and then repeated every **10 seconds** by the heartbeat timer. Never on TCP connect.
- The reference implementation for every C1 sequence is `MUD-ClientProto.md` and verifiable thru `G:/Source/clio-1.8a/src/telnet.l`.

### Versioning
MinVer reads git tags with prefix `v` (e.g. `v0.2.0`). Untagged local builds get `0.0.0-dev.N`.
Pushing a `v*.*.*` tag triggers the release workflow (Windows zip + Android APK → GitHub Release).

### Multi-TFM build quirk
Building both TFMs at once will report the same warning **twice** (once per target framework).
Build with `-f net10.0-windows10.0.19041.0` during development to keep output unambiguous.
