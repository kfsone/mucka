# Working on Mucka

Mucka is a .NET MAUI client for MUD2, for Windows first and Android second. Read this before
`INTERNAL.md`. That file says what the project is. This one says what kind of knowledge this
project's knowledge is, which is the part that keeps going wrong, and then the two invariants that
outrank everything else in the code.

## Two ends, and why the combination is dangerous

Mucka is a modern client for an ancient game. The near end is bleeding edge -- C#, .NET 10, MAUI,
GitHub Actions -- and the far end is a text game from 1980 running on the other side of a telnet
socket. Almost every serious error in this project comes from applying one end's assumptions to
the other end.

Both ends fail an LLM in opposite directions. MAUI is new enough and thin enough in training data
that plausible-looking APIs, WPF habits, and version-skewed advice all come out fluent and wrong.
MUD2 is old enough that nothing in modern practice describes it, so the temptation is to reason
from contemporary design instead of from evidence. Confidence is high in both cases and it is not
correlated with being right.

## Museum status

MUD2 is a frozen artifact. It is not maintained, it will not be extended, and nobody is going to
fix it. This has a consequence that is easy to state and easy to forget: **the absence of a
feature is a permanent property, not a gap to compensate for.** There is no TLS because OpenSSL
did not exist when it was written, and there never will be, because nobody is developing MUD2 at
this remove. That is not an oversight to work around or a deficiency to note. It is the shape of
the thing.

Nor is it bad programming. Bartle did not become the grandfather of a genre by accident, and the
design is deliberate and willful far more often than it is naive. Where MUD2 does something modern
games do not, the useful question is what it cost him to learn that, not how it would be done today.
Assume intent before assuming error.

The specific hazard for inference: MUD2's "protocol" is closer to a markup grammar than to
anything a modern protocol document describes -- nested escape codes, parenthetical context, a
prompt that is a bracketed sequence rather than a character. Do not assume the prompt is `*`. Both
the game and the model are doing something tokenizer-shaped here, and they are not the same shape.

## What counts as evidence, strongest first

For **protocol** questions -- what a code means, what frames exist, what a command returns:

1. Bartle's own documentation. `G:\Source\mud-fe\mud2_FE4.txt` and `MUD-FECodes.txt`. Definitive.
   The gitignored `fecodes.txt` and `Bartle.MUD2-C1-Codes.txt` in this worktree are copies and a
   lossy reduction of these -- prefer the originals, and do not conclude something does not exist
   because the reduction omits it.
2. Bartle's own words on a specific question. `docs/MUD2-flee-cost.md` is one: the flee formula,
   from his email, verbatim.
3. Captured wire traffic. The operator's session recordings and the wire database; see
   `docs/Lab-spec.md` for the tool that is meant to query them.
4. `G:\Source\clio-1.8a` -- a working client written by a human who understood the protocol. A
   strong second opinion, and the North Star for client design, but not authority over Bartle.

For **mechanics** questions -- how fast stamina recovers, what an object does, what a puzzle wants:
Bartle's FE docs are silent, so observation is all there is and Clio moves up to first opinion. The
published GameFAQs guide is hypothesis, not truth. Getting these two rankings backwards is how "the
spec does not mention it" turns into "it is not real."

## The AI-written corpus is not evidence

Every line of this project was written by an AI. There is no colleague whose intent needs
respecting, no other developer's investigation to preserve, and no reviewer who checked any of it.
Design documents and code comments were produced by models, and their claims cannot be assumed to
trace to observation.

The mechanism, because it recurs: across a compaction a model meets its own earlier output as an
existing corpus, treats it as another developer's work, and builds on it deferentially. Modern
game-design reasoning then fills the gaps where evidence is missing, fluently. Audits of this
corpus have found the pattern to be that **what was observed is recorded accurately and why it
happened is invented.** Trust the observation layer; verify every mechanism claim.

When you find odd-looking code with a comment explaining the incident that earned it, the incident
is usually real and the explanation of the mechanism usually is not.

### What a comment may say

A comment survives if it records **what was observed** (a wire fact, a captured line, a measured
value with its conditions) or **a rule the operator set**. Nothing else: no dates, no attributions,
no changelog of how the code got here, no theories about why the server behaves as it does, and no
prose forbidding future work. Git history holds all of that.

The failure this prevents: an agent that could not compute the flee cost left a note saying it
should not be displayed until the formula was known. A later agent read that note as a directive
from the operator and refused his request to implement the formula Bartle had supplied. A comment
that forbids is a fossilised opinion that will be mistaken for an instruction.

### Where prose lives

- `docs/` -- tracked. Specs the code answers to, and evidence (Bartle's words, captured frames).
  `docs/archive/` holds documents for parked work.
- `tools/` -- gitignored scratch. An agent may write a script there to do some processing, and
  deletes it when the work it served is committed. Nothing governing may live there, and nothing
  tracked may depend on a path under it.
- `~/.mucka/` -- the operator's runtime data (databases, recordings) and `~/.mucka/notes/`, where
  material that does not belong in the repo is kept. Do not turn notes into repo docs.

Prefer findings that are a stored query plus its result over findings that are prose.

## One operator, five installs, all his

There is one human here and he is the producer and lead, not a coder on this project. He is also
essentially the entire userbase. So: no staged rollout, no unknown client states, no backward
compatibility, and no keeping code "in case it is needed again." A migration written for a
mistake made last week is not compatibility, it is sediment. Delete it, and delete the old data
rather than reading it.

The acceptance gate is whether it plays right in his hands. Anything that never reaches his hands
-- tooling, analysis output, documentation -- has no gate at all unless you give it one, which is
precisely how the corpus above rotted.

## Invariant #0 -- the command box owns the keyboard

**The single most important UX rule in this codebase: the user must always have immediate
typing access to the command input box, with no extra clicks or focus dance.** Every feature,
control, gesture, and animation is subordinate to this.

Concretely:

- Any tap, click, drag, toggle, or widget interaction (compass, floating panels, chips, fold
  toggles, resize buttons, the terminal, stray clicks) **must leave keyboard focus on the input
  box** when it finishes. If an interaction takes focus, it must hand it straight back.
- The **only** exception is when a *different real text input* is legitimately focused -- e.g. the
  `$con` console entry, the settings/config editor, the F-key editor, or a modal dialog with its
  own field. Those own focus while open; on close, focus returns to the command box.
- Reviewing scrollback (terminal history mode) is exempt while active -- the input is hidden then.

**When you add an interactive element, you wire up nothing.** That is the design.
`Behaviors/FocusGuard.cs` enforces this invariant for every element under the game page by tree
position, not by name, so a control that did not exist when it was written is covered the moment
it appears. One owner, one file. Read its header comment before changing anything about focus.

The one thing you must NOT do: **start a new list of "elements that must not steal focus".**
Enforcement used to be exactly that -- a dozen `x:Name`s against ~50 interactive elements -- and
the hamburger rows, the floating map panel, the Chat button and the data-templated fkey buttons
were all missing from it. A list that has to be remembered is a list that rots. It rotted. It is
gone; do not rebuild it.

Supporting paths (keep intact):
- `GamePage.FocusInput()` -- the single refocus path. Deferred one dispatcher tick; skips re-focus
  when the box already holds it, asking WinUI's `FocusManager` rather than MAUI's cached
  `Entry.IsFocused`, which is stale mid-click.
- `GameViewModel.RequestFocus` / `SidePanelViewModel.RequestFocus` events, wired to `FocusInput`.
  Still the right thing to fire after a command that moves focus deliberately; no longer the thing
  the invariant depends on.
- `Behaviors/NoFocusStealBehavior` -- for an element that is interactive in the first instants of
  its life, before the guard's next sweep reaches it. Rarely needed now.

When you add any interactive element, still ask: "after the user touches this, can they
immediately type a command?" If the answer is no, the guard has a hole -- fix the guard, in the
guard, rather than patching the one control that revealed it.

## Invariant #1 -- thou shalt not block the input widgets

**Never do work on the UI thread that can stall text entry.** The operator types at 120-130 wpm;
a "little" 50 ms hitch in the input box is downright offensive and counts as a bug. Keystrokes
and cursor movement must stay perfectly fluid at all times.

Concretely:

- No synchronous I/O, parsing, layout thrash, allocation storms, or `Task.Wait()`/`.Result` on
  the UI thread anywhere near the typing path. Push work off-thread; marshal only the final
  result back.
- No repeating UI-thread timers driving animation/opacity/fades -- they compete with typing.
  Visual fading belongs on the compositor/render thread.
- Rebuilding lists/`FormattedText`/native views re-templates on the UI thread -- diff first and
  skip the rebuild when nothing changed.
- The input box is sandboxed in `Mucka.Input`; read `Mucka.Input/README.md` before touching
  anything input-related. Measure with `INPUT_DIAG` / `scripts/type-test.ps1` before and after
  any change that could touch the typing path.

When adding anything that runs often or on input, ask: "could this cause even a 50 ms hitch while
typing?" If maybe, get it off the UI thread.

## Layout

- `mudsharp/` -- protocol, transport, session and combat model. No MAUI. Tested by `mudsharp.Tests/`.
- `Mucka.Terminal/` -- output buffer, wrapping, selection. No MAUI. Tested by `Mucka.Terminal.Tests/`.
- `Mucka.Input/` -- the command box's logic. No MAUI. Tested from `mudsharp.Tests/`.
- `Core/`, `ViewModels/`, `Pages/`, `Rendering/`, `Behaviors/`, `Audio/` -- the MAUI app.
- `docs/` -- governing prose and evidence. `scripts/` -- tracked helper scripts.
- No non-ASCII characters in source code.

## Build

- Windows: `dotnet build Mucka.csproj -f net10.0-windows10.0.19041.0`
- Android (local): `dotnet build Mucka.csproj -f net10.0-android -p:LocalAndroid=true`
- Tests: `dotnet test Mucka.slnx`
- **Never kill a running Mucka.** It is the operator's live game session. He runs the Release
  build; a Debug build does not lock it, so build Debug.

## Branches and worktrees

**Do not create a branch unless you were asked to, and never without being told which worktree it
belongs in.** No branches separate of worktrees. If a branch is wanted and no worktree is named,
ask which one -- do not pick, and do not create one.

Work on whatever branch the worktree you are in already has checked out. That is nearly always the
right answer: one operator, no rollout, nothing to isolate from.

The reason this is a rule and not a preference: the repo is bare at `G:\Source\mucka` with its
worktrees beneath it, so a worktree's directory name is the only visible clue to what is checked
out in it. A feature branch checked out inside the worktree named `main` reads as `main` in every
path, every prompt and every glance at the tree, and stays wrong until someone runs
`git worktree list`. Directory name and branch name are load-bearing here; keep them the same.

Committing is likewise on request only, and pushing is a separate request again -- "commit" is not
"push."
