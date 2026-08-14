# Adversarial audit: `feat/combat-redux` rebase onto `main`

Audited at `HEAD = fa1fde6` ("Fix doubled event dispatch from the rebase"), which landed
*during* this audit. Baselines: `main`, `backup/feat-combat-redux-prerebase`, and their
merge-base `3120cdf` (the old main tip, referred to below as OLDMAIN).

Build: clean (`dotnet build Mucka.csproj -f net10.0-windows10.0.19041.0`, exit 0).
Tests: 777 passed, 0 failed.

## Bottom line

**Nothing dangerous enough to warrant emergency action.** No data loss, no crash, no lost
combat work, no reverted commit from `main`.

The assumption that the blind union produced *more* nonsense than the known doubled-dispatch
bug turned out to be **false**. The union's other two files (`MudSession.cs`,
`mudsharp.Tests.csproj`) were resolved correctly — by luck in one case, because those hunks
genuinely were additions-only. The two real findings below both come from the *hand* work
(resolutions 2 and 3), not from the union.

Two findings, in severity order:

1. **A behaviour regression against the pre-rebase branch** — the branch's quit-suppression
   mitigation was dropped on a factually incorrect premise. Real, user-visible, narrow window.
2. **A test silently weakened** — a NUL byte lost in hand-transcription.

---

## FINDING 1 — DEFINITELY WRONG: the quit-suppression mitigation was dropped on a false premise

**File:** `ViewModels/GameViewModel.cs` (resolution 3), `Core/GuidedLogin/SessionDropContext.cs`

### The claim that justified the resolution

`f7dfba5`'s message says:

> Main's version wins outright - **quit is one of its classified reasons**, so it already
> covers what the branch's heuristic was for, and better.

**This is not true.** `SessionDropReason` has exactly three members
(`Core/GuidedLogin/SessionDropContext.cs:6-17`):

```csharp
public enum SessionDropReason { Unknown, Reset, Permadeath }
```

There is no `Quit`. Main's own doc comment on `Unknown` says so outright:

> No classifying signal — **a deliberate QUIT**, an idle boot, a server-side kick, or
> anything else we cannot name.

And main's doc comment on `ClassifyDrop` (`GameViewModel.cs:889-891`) admits the exact gap:

> It still cannot, on its own, tell a reset-driven drop from a player who typed QUIT during
> the finish-up period -- that needs the verb/separator/speech-aware input parser GitHub #143
> tracks.

### The behaviour that was lost

`ClassifyDrop` → `IsResetDrop()` (`GameViewModel.cs:906-918`) is **pure timing**. A deliberate
`qq` that lands inside the reset window is classified `Reset`, so:

```csharp
var autoRelogAfterReset = drop.Reason == SessionDropReason.Reset
    && !string.IsNullOrWhiteSpace(exitedPersona);
```

is `true`, and the player is auto-relogged straight back into the persona they just
deliberately quit — with `ForcePersonaChoice: false`, so they never see the picker.

The window is up to **95 seconds** when the reset was announced
(`AnchoredResetLeadWindow` 5s + `ResetRelogLagWindow` 90s) and **120 seconds** otherwise
(`ResetRelogLeadWindow` 30s + 90s).

The pre-rebase branch prevented precisely this, and its comment said so in as many words:

```csharp
var autoRelog = autoRelogAfterReset && !deliberateQuit;
```
> Choosing to leave a character is not a request to be put straight back into it, and silently
> relogging the same persona undid the one thing the player had just asked for. [...] That also
> makes it irrelevant whether the quit happened to land inside ShouldAutoRelogAfterReset's
> window around a reset: an explicit quit outranks the inferred reason every time.

`_deliberateQuit` was set by a `"Cheerio!"` sniff on the read thread, which arrives *before*
`GameModeExited` fires.

### Corroborating evidence

`ShellText.IsQuitFarewellLine` (`Core/GuidedLogin/ShellText.cs:127`) now has **zero production
callers** — only its two tests reference it. `f7dfba5` noticed this and kept the predicate for
tidiness ("a small tested predicate, and deleting it would mean deleting its tests for no
gain"). That reads the orphan as harmless leftovers; it is actually the *symptom* of the lost
mitigation.

### Nuance the owner should weigh

Against `main`, HEAD is **no worse** — main has the same gap and tracks it as #143. So this is
"the branch lost a mitigation it had", not "the rebase broke main". Main's contribution was to
*narrow* the false-positive window (`f0fd8d3`, "mitigation for #143"); the branch's was to
*detect the quit*. The two were complementary, and the rebase kept only the narrowing.

### What the correct state should be

Re-add quit detection on top of main's structure rather than reverting to the branch's shape:

- add `Quit` to `SessionDropReason`;
- restore the `"Cheerio!"` read-thread sniff (cheap-word-first, as it was) setting a
  `_deliberateQuit` flag;
- test it in `ClassifyDrop` **before** `IsResetDrop()`, so an explicit quit outranks the
  timing inference;
- `autoRelogAfterReset` then falls out `false` for free, and the overlay gains an accurate
  headline for quits as a bonus.

This also removes the `IsQuitFarewellLine` orphan.

---

## FINDING 2 — DEFINITELY WRONG: hand-transcribed test lost its NUL byte

**File:** `mudsharp.Tests/Fixtures/ShellTextTests.cs`, test `DetectsQuitFarewellLine`
(resolution 2)

Git treats this file as binary because it contains real NUL bytes, so main's version was taken
and the branch's two tests re-typed by hand. The transcription was lossy.

Pre-rebase (`backup/feat-combat-redux-prerebase`, line 78) — with a **literal NUL**:

```csharp
Assert.True(ShellText.IsQuitFarewellLine(ShellText.NormalizeWhitespace("Cheerio!\r<NUL>\r\n")));
```

At HEAD — the NUL is **gone**:

```csharp
Assert.True(ShellText.IsQuitFarewellLine(ShellText.NormalizeWhitespace("Cheerio!\r\r\n")));
```

NUL-byte census: OLDMAIN 0, main 4, prerebase 1, HEAD 4. HEAD's four are all main's (both on
`ShellTextTests.cs:229-230`, the persona-list fixture). The branch's one NUL is **not** in
HEAD.

The test's own comment states its purpose:

> Wrap padding exactly as GameModeExitTests' captured qq byte sequence carries it.

It no longer does. `\r\r\n` is a sequence MUD2 never sends; the whole point was that the real
wire format is `\r\0\r\n`, and that `NormalizeWhitespace` copes with the embedded NUL. It
passes either way, which is why nobody noticed.

**Correct state:** restore the literal NUL in that string. (Low impact while Finding 1 stands,
since the predicate is production-dead — but if Finding 1 is fixed, this test goes back to
guarding live behaviour and needs to be correct.)

---

## Verified clean — what was actually checked

### Structural: no commit lost, none dropped, none emptied
- 41 pre-rebase commits → 41 replayed, matched by subject line. Zero dropped, zero added
  beyond the two repair commits (`f7dfba5`, `fa1fde6`).
- No commit in `main..HEAD` is empty (all 43 carry a diff).
- No conflict markers anywhere in tracked files.

### The decisive test: branch-contribution vs branch-contribution
Comparing `git diff OLDMAIN prerebase` against `git diff main HEAD` **per file, as patch
text** (not as stats — stats are blind to union duplicates, because main's duplicated line
survives as *context*; this is exactly how the doubled-dispatch bug hid):

- Both contributions touch the **same 108 files**. No file gained or lost.
- **100 of 108 files are byte-identical patches.** The 8 that differ are exactly the 8 files
  that both `main` and the branch touched. There are no casualties outside the overlap set.

This proves the whole late combat body of work carried over untouched: `SwingLedger`,
`CombatMetronome`, `TickSweep`, `NpcHealthRungs`, `NpcFleeFailed`, `NpcStaminaRead`,
`CombatRailView`, `SidePanelViewModel`, `ScoreSheet` parsing, `FightHistory*`, `ClogWriter`,
`ItemEvalSession`, all of `tools/combat/`.

### Authoritative 3-way merge reconstruction of the 8 overlap files
Ran `git merge-file --diff3` with OLDMAIN as base, then diffed the result against HEAD:

| File | merge-file conflicts | HEAD vs ideal merge |
|---|---|---|
| `Core/GuidedLogin/ShellText.cs` | 0 | **identical** |
| `Pages/GamePage.xaml` | 0 | **identical** |
| `Pages/GamePage.xaml.cs` | 0 | **identical** |
| `mudsharp.Tests/mudsharp.Tests.csproj` | 1 | correct (additions-only union) |
| `mudsharp/Session/MudSession.cs` | 1 | correct |
| `Core/MuckaConnection.cs` | 1 | correct (after `fa1fde6`) |
| `ViewModels/GameViewModel.cs` | 4 | main's side taken + documented deletions |
| `mudsharp.Tests/Fixtures/ShellTextTests.cs` | binary | see Finding 2 |

Per-file notes:

- **`mudsharp.Tests.csproj`** — the union was legitimate here: base was empty at that hunk,
  branch added 8 `Compile Include`s, main added 1. All 9 present. `grep | sort | uniq -d` over
  every `Include="..."` in the file returns **nothing** — no duplicate registrations.
- **`MudSession.cs`** — the single conflict was the `AutoResetInitiated` subscription. HEAD
  keeps the branch's `NoteAutoResetInitiated` + its long justification comment, plus main's
  `AutoResetInitiated?.Invoke()`, as **one** subscription. The branch's `_combat.ForceEnd` was
  already gone (removed deliberately in `aab2fd8`). Correct.
- **`MuckaConnection.cs`** — now exactly the ideal resolution: the branch's 6 combat-aware
  forwards plus main's 2 genuinely-new lines (`PersonaWiped`, `AutoResetInitiated`). Note that
  `RoomShortReady` and `CharacterIdentified` had the *same* union hazard but escaped it,
  because base and main were identical there so git auto-merged the branch's replacement. The
  three that broke were only the ones adjacent to main's new lines.
- **`GamePage.xaml` / `GamePage.xaml.cs`** — auto-merged, and I checked main's substance
  survives rather than trusting the auto-merge: `x:Name="RecChip"`, `_fkeyEditorPage`,
  `DisableFocusOnInteraction(StatusBar, RecChip, ...)`, the `SessionDropContext drop` parameter
  threading, `editor.CloseAsync()`, `leftAtOptionMenu`, `NoteLeftAtOptionMenu`,
  `ManualAtOptionMenu`, the `_vm.IsConnected` guards — **all present**.

### Main's 21 commits: substance intact
Of the 26 files main touched, **18 are byte-identical to `main` at HEAD** — including
`GuidedLoginController.cs`, `SessionDropContext.cs`, `SettingsStore.cs`, `Mucka.csproj`,
`FkeyEditorPage.xaml.cs`, `GuidedLoginPage.xaml{,.cs}`, `ConnectViewModel.cs`,
`GuidedLoginViewModel.cs`, `Mud2C1Decoder.cs`, `MudStreamParser.cs`, `MudSessionOptions.cs`,
`version.json`, and 5 test fixtures. Nothing from main's guided-login, persona-wipe,
resite-FEX, ctrl-macro, or sound-settings work can have been reverted in those files. The
remaining 8 are the overlap set, verified above.

### Duplicated subscriptions / registrations — repo-wide sweep
Scanned every `.cs` file for the same left-hand side subscribed more than once. Four hits, all
benign on inspection:

- `ViewModels/GameViewModel.cs:1913-1915` — `GameModeExited` twice, but **two different
  handlers** (`OnGameModeExited` and `SidePanel.OnGameModeExited`), each paired with a `-=` at
  1946-1948.
- `Core/ItemEvalSession.cs:195,262` — two separately-scoped local handlers, each with its own
  `-=`.
- `Pages/MappingPage.cs:526,546` — two different button factory methods.
- Test files — separate test methods.

Wire/unwire symmetry checked on `GameViewModel`, `MuckaConnection`, `SidePanelViewModel`,
`GamePage.xaml.cs`. The unmatched `+=`s are all lifetime-scoped (e.g. `MuckaConnection` owns
`_session` for its whole life) and follow main's own pattern. **No double-subscription
anywhere.**

### Dead / orphaned code from the resolutions
- `ShouldAutoRelogAfterReset` — **fully gone**, zero references.
- `_deliberateQuit` — **fully gone**, zero references (field and both `= false` resets).
- `_personaInvalidated` — correctly re-sourced from main's `OnPersonaWiped()` C1 event
  (`GameViewModel.cs:811`) now that the `"Not updating persona."` text sniff is gone.
- `ResetRelogRetryWindow` — still read at `GameViewModel.cs:874`.
- `IsQuitFarewellLine` — production-dead. **This is Finding 1's symptom, not tidy leftovers.**
- Checked every line the branch deletes from `main` (66 deletions total, enumerated). All are
  legitimate branch rewrites — the score-sheet full parse replacing per-line regexes, the
  side-panel/status-icon redesign, font consolidation. Specifically confirmed **not** orphaned:
  the setup-swallow trio (`_setupWindowActive` / `_setupSwallowingFrame` /
  `_setupCloseAfterFrame`, all still live in `MudSession.cs`), `StaminaColor` (still threaded
  through `Mud2C1Decoder` → `MudSession:549` → `GameViewModel:1017`), and
  `StaleStats.Score/Strength/Dexterity` (all still set at `MudSession.cs:503-506`, with
  *widened* conditions).

### Carried-weight removal held
No weight field survives in `GameStatsSnapshot`. Every remaining `weight` reference is either a
comment explaining the removal or score-sheet *input* fixture text. Nothing re-parses,
re-stores or re-displays it, and no test asserts that it does.

---

## Minor — pre-existing on the branch, NOT rebase casualties

Both of these are byte-identical between pre-rebase and HEAD, so the rebase did not cause
them. Noting them only because the audit surfaced them.

1. **`tools/combat/--db`** — a 217 KB SQLite database committed under the literal filename
   `--db`, evidently from a mistyped CLI argument (`95bf6a8`). Junk in the tree.
2. **`mudsharp.Tests/Fixtures/ScoreSheetTests.cs:8`** — doc comment still calls the score sheet
   "the ONLY source for carried weight", which `4768a3c` made obsolete. Comment only; no
   assertion depends on it.
