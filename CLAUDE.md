# Mucka — project instructions

Mucka is a .NET MAUI client for the MUD2 game. Windows is the primary/first-class target;
Android is a secondary port.

## Invariant #0 — the command box owns the keyboard

**The single most important UX rule in this codebase: the user must always have immediate
typing access to the command input box, with no extra clicks or focus dance.** Every feature,
control, gesture, and animation is subordinate to this.

Concretely:

- Any tap, click, drag, toggle, or widget interaction (compass, floating panels, chips, fold
  toggles, resize buttons, the terminal, stray clicks) **must leave keyboard focus on the input
  box** when it finishes. If an interaction takes focus, it must hand it straight back.
- The **only** exception is when a *different real text input* is legitimately focused — e.g. the
  `$con` console entry, the settings/config editor, the F-key editor, or a modal dialog with its
  own field. Those own focus while open; on close, focus returns to the command box.
- Reviewing scrollback (terminal history mode) is exempt while active — the input is hidden then.

How this is enforced today (keep these paths intact when adding UI):
- `GamePage.FocusInput()` — deferred one dispatcher tick so it wins WinUI's post-click focus
  settle; skips re-focus if the input already holds it (avoids cursor-reset glitches).
- `GameViewModel.RequestFocus` / `SidePanelViewModel.RequestFocus` events → wired to `FocusInput`.
  Fire one of these after any command/interaction that could move focus.
- Root `PointerReleased` handler (`handledEventsToo: true`) as a safety net for stray clicks.
- New SkiaSharp canvases (e.g. `RadarCompassView`) grab focus in ways the root net can miss, so
  they refocus **explicitly**: every click routes through a command that calls `RequestFocus`,
  even on an empty/miss hit.

When you add any interactive element, ask: "after the user touches this, can they immediately
type a command?" If not, wire it to `RequestFocus`.

## Invariant #1 — thou shalt not block the input widgets

**Never do work on the UI thread that can stall text entry.** The author types at 120–130 wpm;
a "little" 50 ms hitch in the input box is downright offensive and counts as a bug. Keystrokes
and cursor movement must stay perfectly fluid at all times.

Concretely:

- No synchronous I/O, parsing, layout thrash, allocation storms, or `Task.Wait()`/`.Result` on
  the UI thread anywhere near the typing path. Push work off-thread; marshal only the final
  result back.
- No repeating UI-thread timers driving animation/opacity/fades — they compete with typing.
  Visual fading belongs on the compositor/render thread (see the stale-dim behaviors and the
  history behind removing the old 10 Hz fade timer).
- Rebuilding lists/`FormattedText`/native views re-templates on the UI thread — diff first and
  skip the rebuild when nothing changed (see `OnFeiListComplete`).
- Bindings on the input box are hand-tuned; don't casually add converters, triggers, or
  `PropertyChanged` fan-out on the typing hot path. Measure with `INPUT_DIAG` / `tools/type-test.ps1`
  before and after any change that could touch it.

When adding anything that runs often or on input, ask: "could this cause even a 50 ms hitch while
typing?" If maybe, get it off the UI thread.

## Build

- Windows: `dotnet build Mucka.csproj -f net10.0-windows10.0.19041.0`
- Android (local): add `-p:LocalAndroid=true`
- Close any running Mucka instance first — it locks the output exe.
