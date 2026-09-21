# Working on Mucka

FACT Mucka is a .NET MAUI client for MUD2, Windows first and Android second. `INTERNAL.md` says
what the project is; this file says what kind of knowledge this project's knowledge is, and states
the two invariants that outrank everything else in the code.

RULE Read this file before `INTERNAL.md`.
enforced_by: none: judgement

Every governing statement below carries a tag saying what KIND of statement it is:

- **FACT** - how the world is. Never an instruction.
- **RULE** - do or do not, with its scope. Each one ends with an `enforced_by:` line naming the
  gate that fails the build, or the literal `none: judgement` when nothing but a reader catches it.
- **DECISION** - we chose this and rejected that. The rejected option is named, because an option
  nobody can see rejected comes back as an original idea.
- **EVIDENCE** - observed: a wire fact, a captured line, a measured value with its conditions, a
  quotation. Verbatim, never paraphrased, never compressed.

## Two ends: a modern client, a 1980 server

FACT The near end is bleeding edge - C#, .NET 10, MAUI, GitHub Actions - and the far end is a text
game from 1980 on the other side of a telnet socket. Almost every serious error in this project
comes from applying one end's assumptions to the other end.

FACT Both ends fail an LLM, in opposite directions. MAUI is new enough and thin enough in training
data that plausible-looking APIs, WPF habits and version-skewed advice all come out fluent and
wrong. MUD2 is old enough that nothing in modern practice describes it, so the pull is to reason
from contemporary design instead of from evidence. Confidence is high in both cases and is not
correlated with being right.

## Museum status

FACT MUD2 is a frozen artifact. It is not maintained, it will not be extended, and nobody is going
to fix it. The absence of a feature is a permanent property, not a gap to compensate for: there is
no TLS because OpenSSL did not exist when it was written, and there never will be.

FACT The design is deliberate and willful far more often than it is naive. Bartle did not become
the grandfather of a genre by accident. Where MUD2 does something modern games do not, the useful
question is what it cost him to learn that, not how it would be done today.

RULE Assume intent before assuming error, and treat a missing feature as the shape of the thing
rather than an oversight to work around or a deficiency to note.
enforced_by: none: judgement

FACT MUD2's "protocol" is closer to a markup grammar than to anything a modern protocol document
describes: nested escape codes, parenthetical context, and a prompt that is a bracketed sequence
rather than a character. Both the game and the model are doing something tokenizer-shaped here,
and they are not the same shape.

RULE Never assume the prompt is `*`.
enforced_by: none: judgement

## What counts as evidence, strongest first

DECISION For protocol questions - what a code means, what frames exist, what a command returns -
the ranking is by source. Two candidates are ranked and rejected inside it: the local reduction of
the FE documents, below the originals it was made from, and Clio, below Bartle.

1. Bartle's own documentation. `G:\Source\mud-fe\mud2_FE4.txt` and `MUD-FECodes.txt`. Definitive.
2. Bartle's own words on a specific question. `docs/MUD2-flee-cost.md` is one: the flee formula,
   from his email, verbatim.
3. Captured wire traffic. The operator's session recordings and the `wire` table in
   `~/.mucka/mucka.db`; `docs/persistence-design.md` says how to read them.
4. `G:\Source\clio-1.8a` - a working client written by a human who understood the protocol. A
   strong second opinion, and the North Star for client design, but not authority over Bartle.

EVIDENCE The gitignored `fecodes.txt` and `Bartle.MUD2-C1-Codes.txt` in this worktree are copies
and a lossy reduction of the two FE documents.

RULE Prefer the originals, and never conclude something does not exist because the reduction
omits it.
enforced_by: none: judgement

DECISION For mechanics questions - how fast stamina recovers, what an object does, what a puzzle
wants - Bartle's FE docs are silent, so observation is all there is and Clio moves up to first
opinion. The published GameFAQs guide is hypothesis, not truth; treating it as documentation is
rejected, because getting these two rankings backwards is how "the spec does not mention it" turns
into "it is not real."

## The AI-written corpus is not evidence

FACT Every line of this project was written by an AI. There is no colleague whose intent needs
respecting, no other developer's investigation to preserve, and no reviewer who checked any of it.
Design documents and code comments were produced by models, and their claims cannot be assumed to
trace to observation.

FACT The mechanism recurs: across a compaction a model meets its own earlier output as an existing
corpus, treats it as another developer's work, and builds on it deferentially. Modern game-design
reasoning then fills the gaps where evidence is missing, fluently.

EVIDENCE Audits of this corpus have found the pattern to be that what was observed is recorded
accurately and why it happened is invented. Where odd-looking code carries a comment explaining the
incident that earned it, the incident is usually real and the explanation of the mechanism usually
is not.

RULE Trust the observation layer; verify every mechanism claim before building on it.
enforced_by: none: judgement

### What a comment may say

RULE A comment survives if it records **what was observed** (a wire fact, a captured line, a
measured value with its conditions) or **a rule the operator set**. Nothing else: no dates, no
attributions, no changelog of how the code got here, no theories about why the server behaves as it
does, and no prose forbidding future work. Git history holds all of that.
enforced_by: none: judgement

RULE An agent that cannot do something records what it observed and leaves the work open. A comment
that forbids is a fossilised opinion that the next reader will take for an instruction from the
operator.
enforced_by: none: judgement

### Where prose lives

FACT Prose in this repo has three homes:

- `docs/` - tracked. Specs the code answers to, and evidence (Bartle's words, captured frames).
  `docs/archive/` holds documents for parked work.
- `tools/` - gitignored scratch.
- `~/.mucka/` - the operator's runtime data (databases, recordings) and `~/.mucka/notes/`, where
  material that does not belong in the repo is kept.

RULE An agent may write a script in `tools/` to do some processing, and deletes it when the work it
served is committed. Nothing governing may live there, and nothing tracked may depend on a path
under it.
enforced_by: none: judgement

RULE Never turn the operator's notes into repo docs.
enforced_by: none: judgement

RULE Prefer a finding that is a stored query plus its result over a finding that is prose.
enforced_by: none: judgement

### What a doc may say

RULE The same rule as a comment, one level up. A doc in `docs/` carries **rules and decisions**,
**what was observed** (a wire fact, a captured frame, a measured value with its conditions), and the
**reasoning behind a choice we made**. Reasoning about the SERVER is a mechanism claim and needs
evidence like any other.
enforced_by: none: judgement

RULE A number travels with the name that owns it: `CombatRailView.SlotHeight` rather than a bare
"46dp", and `SidePanelWidthDp = 228dp` is fine because a reader who doubts it knows where to look.
A naked value is the thing that rots. Numbers that are the point in themselves - a measurement, a
corpus size, a figure from Bartle - carry their conditions instead, and that is evidence rather than
a restatement. The test a restated value fails: could this sentence become false because somebody
edited a number somewhere else? If yes, name the owner.
enforced_by: none: judgement

RULE Name a symbol that exists. A dangling name is worse than a stale number, because a wrong
number looks wrong and a wrong name reads exactly like a right one.
enforced_by: DocsCitedSymbolsExistTests

RULE Never cite code by line number - not `Foo.cs:123` in `docs/`, not in a source comment. Name
the symbol. These rot faster than anything else in the tree.
enforced_by: DocsCiteSymbolsNotLinesTests

RULE A doc states the SHAPE of a thing, and when the shape changes the doc is part of the change.
Redrawing something means rewriting the prose that describes it, in the same commit. This is the
failure the value rules do not catch: a structure that has stopped existing carries no number, no
constant and no line reference to go stale.
enforced_by: none: judgement

RULE A design doc is future-tense until its code lands, then present-tense forever. Writing the doc
first is how work is done here, so "what this will do" is correct while it is a plan. The commit
that lands the work rewrites it to what the system now does and deletes the work order - the
migration steps, the "still to delete" list, the "not in this stage" notes. A doc still written in
the future tense after its stage shipped is the defect. Git history is the changelog; a decision the
work order recorded is not history and stays.
enforced_by: none: judgement

RULE Evidence is immutable and outranks the code. Bartle's words, captured frames, recorded
measurements: if the implementation disagrees with one of these, the implementation is what is
wrong. Never edit evidence to agree with code.
enforced_by: none: judgement

FACT The published GameFAQs guide and the bestiary transcribed from it are NOT on that list. They
are hypothesis, exactly as ranked above, and a confident readout built on one is how a character
dies while its owner trusts the panel.

## Who writes, who accepts, and where releases land

FACT There is one human here; his role is direction and acceptance testing, not coding. All
engineering is done by LLMs, every line, and no human reads it. Releases go out on GitHub and
strangers run them.

RULE For code that changes nothing: no staged rollout, and no keeping code "in case it is needed
again." A migration written for last week's mistake is sediment - delete it, and delete the old data
rather than reading it.
enforced_by: none: judgement

FACT The database is the exception. There is no central database and no data is ever shipped. Each
install creates and owns its own `~/.mucka/mucka.db` on its own machine; the only thing that travels
is code, which then runs against a file we cannot see, shaped by whichever release that machine last
installed. On the operator's file a destructive step costs a table he can recreate; on a stranger's
it destroys records nobody can recover - no backup, and no way to reach them.

RULE A schema change must be forward-compatible and must not assume the file it opens is the
operator's own. "The operator clears the table by hand" is not a migration path for a machine he
will never sit at.
enforced_by: SchemaDdlLivesInMigrationsOnlyTests, MigrationScriptsAreFrozenTests

DECISION The guard on the schema is structural - a test that fails the build - and a policy
sentence in this file was tried and rejected. A model reads a policy and talks itself past it, and
no human reads the schema change after it.

RULE The acceptance gate is whether it plays right in the operator's hands. Anything that never
reaches his hands - tooling, analysis output, documentation, and every install that is not his - has
no gate unless you give it one, which is precisely how the corpus above rotted.
enforced_by: none: judgement

## Invariant #0 -- the command box owns the keyboard

RULE The single most important UX rule in this codebase: the user must always have immediate typing
access to the command input box, with no extra clicks or focus dance. Every feature, control,
gesture and animation is subordinate to this. Concretely:

- Any tap, click, drag, toggle, or widget interaction (compass, floating panels, chips, fold
  toggles, resize buttons, the terminal, stray clicks) **must leave keyboard focus on the input
  box** when it finishes. If an interaction takes focus, it must hand it straight back.
- The **only** exception is when a *different real text input* is legitimately focused - the `$con`
  console entry, the settings/config editor, the F-key editor, or a modal dialog with its own field.
  Those own focus while open; on close, focus returns to the command box.
- Reviewing scrollback (terminal history mode) is exempt while active - the input is hidden then.
enforced_by: none: judgement

RULE Never start a list of "elements that must not steal focus." A list that has to be remembered
is a list that rots.
enforced_by: none: judgement

FACT `Behaviors/FocusGuard.cs` enforces this invariant for every element under the game page by tree
position, not by name, so a control that did not exist when it was written is covered the moment it
appears. One owner, one file. When you add an interactive element, you wire up nothing: that is the
design.

RULE Read `Behaviors/FocusGuard.cs`'s header comment before changing anything about focus. When you
add any interactive element, ask: "after the user touches this, can they immediately type a
command?" If the answer is no, the guard has a hole - fix the guard, in the guard, rather than
patching the one control that revealed it.
enforced_by: none: judgement

RULE Keep these supporting paths intact:

- `GamePage.FocusInput()` - the single refocus path. Deferred one dispatcher tick; skips re-focus
  when the box already holds it, asking WinUI's `FocusManager` rather than MAUI's cached
  `Entry.IsFocused`, which is stale mid-click.
- `GameViewModel.RequestFocus` / `SidePanelViewModel.RequestFocus` events, wired to `FocusInput`.
  Still the right thing to fire after a command that moves focus deliberately; no longer the thing
  the invariant depends on.
- `Behaviors/NoFocusStealBehavior` - for an element that is interactive in the first instants of its
  life, before the guard's next sweep reaches it. Rarely needed now.
enforced_by: none: judgement

## Invariant #1 -- the typing path is never blocked

EVIDENCE The operator types at 120-130 wpm.

RULE Never do work on the UI thread that can stall text entry. A "little" 50 ms hitch in the input
box is downright offensive and counts as a bug; keystrokes and cursor movement must stay perfectly
fluid at all times. Concretely:

- No synchronous I/O, parsing, layout thrash, allocation storms, or `Task.Wait()`/`.Result` on the
  UI thread anywhere near the typing path. Push work off-thread; marshal only the final result back.
- No repeating UI-thread timers driving animation/opacity/fades - they compete with typing. Visual
  fading belongs on the compositor/render thread.
- Rebuilding lists/`FormattedText`/native views re-templates on the UI thread - diff first and skip
  the rebuild when nothing changed.
- When adding anything that runs often or on input, ask: "could this cause even a 50 ms hitch while
  typing?" If maybe, get it off the UI thread.
enforced_by: UiThreadTimersAreAllowlistedTests, Mucka.Input/BannedSymbols.txt (RS0030)

RULE The input box is sandboxed in `Mucka.Input`; read `Mucka.Input/README.md` before touching
anything input-related. Measure with `INPUT_DIAG` / `scripts/type-test.ps1` before and after any
change that could touch the typing path.
enforced_by: none: judgement

## Layout

FACT Where code lives:

- `mudsharp/` - protocol, transport, session and combat model. No MAUI. Tested by `mudsharp.Tests/`.
- `Mucka.Terminal/` - output buffer, wrapping, selection, and the plain-text session transcript
  (`docs/session-rec-design.md`), which is there because it answers to the buffer's idea of what one
  line is. No MAUI. Tested by `Mucka.Terminal.Tests/`.
- `Mucka.Input/` - the command box's logic. No MAUI. Tested from `mudsharp.Tests/`.
- `Mucka.Util/` - five small libraries, no MAUI, tested by `Mucka.Util/Mucka.Util.Tests/`:
  `Mucka.Store` (the one database, `~/.mucka/mucka.db`: schema, the single background writer, the row
  seam - `docs/persistence-design.md` governs it), `Mucka.WireLog` (framing, the always-on wire
  writer, the reader; readable by a CLI on its own), `Mucka.Combat` (ledgers, fight history,
  encounter-log writer, tick timing, the panel's aggregator, geometry, floats and window arithmetic),
  `Mucka.Commands` (aliases, shell text, session-drop classification), `Mucka.Sounds` (sound
  catalogue, WAV probe).
- `Core/`, `ViewModels/`, `Pages/`, `Rendering/`, `Behaviors/`, `Audio/` - the MAUI app.
- `docs/` - governing prose and evidence. `scripts/` - tracked helper scripts.

RULE Anything MAUI-free the app grows belongs in one of the `Mucka.Util` libraries, not linked
file-by-file into a test project.
enforced_by: none: judgement

RULE Everything in the repo is ASCII: code, comments, XAML, documentation. A glyph the UI needs is a
named constant in `Core/Glyph.cs`, defined by its escape (`"\uXXXX"` with XXXX the code point), or
an XML entity (`&#xXXXX;`) in XAML, never the literal character. Punctuation is plain ASCII (`-`,
`...`, `+/-`). Any other byte fails `dotnet test`, tracked or not.
enforced_by: RepoIsAsciiTests

## Build

FACT The three commands:

- Windows: `dotnet build Mucka.csproj -f net10.0-windows10.0.19041.0`
- Android (local): `dotnet build Mucka.csproj -f net10.0-android -p:LocalAndroid=true`
- Tests: `dotnet test Mucka.slnx`

FACT A running Mucka is the operator's live game session. He runs the Release build, and a Debug
build does not lock it.

RULE Never kill a running Mucka. Build Debug.
enforced_by: none: judgement

## Proving a test can fail

RULE Break the thing on purpose, watch the test fail, then restore it. A test written alongside a
fix passes for two different reasons - because the fix works, or because the test never touched it -
and a green run cannot tell them apart. The only way to know which you have is to take the fix out.
enforced_by: none: judgement

RULE Confirm the break actually applied. A `sed` that silently matches nothing, or a script that
aborts before it writes, leaves the code untouched - and then "it still passes" is not evidence of a
weak test, it is evidence of nothing at all. Re-read the line, or watch the compiler reject it,
before believing a passing run.
enforced_by: none: judgement

EVIDENCE Where a fix is load-bearing the break says so loudly: removing the transcript's
partial-line hold failed five tests at once. An unconfirmed break has produced a false result twice.

RULE Say plainly what cannot be tested from here rather than implying the gate covers it: nothing
reaches the MAUI assembly, and a XAML binding path is checked by neither the compiler nor any suite.
The acceptance gate for those is the operator's hands.
enforced_by: none: judgement

## Branches and worktrees

RULE Do not create a branch unless you were asked to, and never without being told which worktree it
belongs in. No branches separate of worktrees. If a branch is wanted and no worktree is named, ask
which one - do not pick, and do not create one.
enforced_by: none: judgement

RULE Work on whatever branch the worktree you are in already has checked out.
enforced_by: none: judgement

RULE Committing is on request only, and pushing is a separate request again - "commit" is not
"push."
enforced_by: none: judgement

DECISION Directory name and branch name are kept the same here, and a feature branch checked out in
a conveniently available worktree is rejected. The repo is bare at `G:\Source\mucka` with its
worktrees beneath it, so a worktree's directory name is the only visible clue to what is checked out
in it: a feature branch inside the worktree named `main` reads as `main` in every path, every prompt
and every glance at the tree, and stays wrong until someone runs `git worktree list`.
