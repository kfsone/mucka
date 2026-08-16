# Server-side text wrapping vs. Mucka's line matching — review and proposal

**Status:** review only. No source was changed by this document.
**Date:** 2026-08-11
**Question asked:** were the recent pattern matchings written with awareness that MUD2 wraps text
server-side; should wrapping be turned off; or should there be a helper/abstraction so nobody has to
keep remembering that "a newline can occur where a space should be"?

---

## Verdict up front

The concern is **real, currently live, and mostly invisible**. Wrapped sentences are **not** rejoined
anywhere — each server wrap produces its own `StyledLine`, so an `^…$`-anchored pattern sees two
fragments and matches neither. On the desktop at a wide window nothing wraps and everything looks
fine; on a narrow pane or on Android **most combat patterns are past the wrap column and fail
silently**. One pattern (`WeaponSwitch`, 100 chars worst case) can break even at the 100-column
desktop width the captures were taken at.

Recommendation: **Option C — a small wrap-aware matching helper that owns a one/two-line join window,
generalising the join-then-match trick `WatchwordStore` already uses.** Not A (turning wrapping off),
not B (rejoining in the parser). Reasoning in §5.

---

## 1. The facts

### 1.1 MUD2 wraps, and the wrap is byte-identical to a real line end

From `RESEARCH/mud2-multi-combat.jsonl` (the same capture `CombatCaptureReplayTests` replays), raw rx
bytes around a wrap point in the tea-room description:

```
… beams and soft,   \r \x00 \r \n   velvet-covered furniture provide it …
        62 65 61 6D 73 20 61 6E 64 20 73 6F 66 74 2C 0D 00 0D 0A 76 65 6C …
```

and around a genuine end of sentence:

```
… zombie9 misses you. FF FF   \r \x00 \r \n   <next frame's C1 codes> …
```

**The wrap emits exactly the same `\r \0 \r \n` as a real line ending.** There is no marker, no
soft-wrap escape, no continuation code. Additional properties measured over the whole 4.2 MB capture
(36 497 lines):

- **Word wrap, and the break consumes the space.** `"…and soft,"` + `"velvet-covered…"` — a naive
  concatenation yields `soft,velvet-covered`. Rejoining therefore has to **insert** a space. This is
  precisely the "newlines occur where spaces should" the owner described.
- **Hard break when a single token exceeds the width.** `NarrowTerminalHeartbeatTests` was
  reconstructed from a live Pixel 5 capture and documents the FES data line hard-wrapped mid-line at
  a narrow width. So "always insert a space when rejoining" is right for prose and wrong for
  unspaced payloads — the escaped FE probe responses.
- **The wrap column is `/T` − 2.** The capture sends `ESC-[ ESC^F ESC-T ESC-N /T100 ESC-]`, the
  server confirms `[New terminal width is 100]`, and the longest observed plain game line is exactly
  **98** characters (`rstrip`ped): 772 lines at 98, 905 at 97, 887 at 96 — a clean cliff at 98, and
  the "would this break have been forced?" test fits W = 98 far better than 99 or 100.

### 1.2 The client does not rejoin them — each wrap becomes its own StyledLine

`MudStreamParser.EmitChar` (`mudsharp/Protocol/MudStreamParser.cs:547`) finalises a `StyledLine` on
**every** `'\n'`. Nothing distinguishes a wrap `'\n'` from a sentence `'\n'`, because nothing can.
The line then goes to `LineAnalyzer.Analyze`, the sound triggers, and — via
`MudSession.WireParserEvents` (`mudsharp/Session/MudSession.cs:365`) — to `CombatTracker.Observe`.

The project comments that suggested otherwise say something different on close reading:

- *"the C01/C02 protocol manages partial lines in game mode / `EmitPartial` is only pre-game"* —
  this is about **prompts**, i.e. text with no `'\n'` at all yet. `EmitPartialLine` exists so a
  login prompt (`"Account ID: "`) is displayable before its newline arrives. It has nothing to do
  with wrapping, which by definition *has* a newline.
- `StyledLine.ContinuesChat` and `C1Scope.Chat` are **proof of the opposite**: they exist only
  because "the server soft-wrapped one speaker message across several `'\n'` lines". The codebase
  already knows wrapping splits lines — it just solved it for chat colouring only, by carrying the
  colour scope across the wrap, not by rejoining text.
- `MudStreamParser.cs:566`: *"on narrow terminals the server line-wraps even these escaped
  responses"* — direct in-repo acknowledgement.
- Countervailing false belief, worth fixing: `Core/Watchword.cs:56` claims collapsing whitespace runs
  means "server-side line wrapping … cannot break trigger matches". Whitespace normalisation inside
  one line does nothing about a wrap, because the newline never reaches the matcher — the text
  arrives as two unrelated strings. Watchwords are in fact saved by something else (§5.4).

### 1.3 The wrap column follows the pane width, so "it works on my machine" is a trap

`GameViewModel.NotifyWindowSize` (line 1656) computes `_effCols` from the measured pane width and
font metrics, clamps it to **20…160**, then calls `SetWindowSize` **and** `SendTerminalWidth()`,
which re-issues `/T{cols}` — the comment at `Core/MuckaConnection.cs:338` correctly notes the server
wraps on `/T`, not on NAWS. Consequences:

| Situation | `/T` | wrap column ≈ |
|---|---|---|
| Wide desktop window (default font) | 160 (clamped) | ~158 |
| The research captures | 100 | **98** |
| Pixel 5 portrait (per `NarrowTerminalHeartbeatTests`) | ~43 | **~41** |
| Narrowest the client permits | 20 | **~18** |

So the same pattern set is correct on the author's desktop and broken on his phone, with no error,
no log line, and no visible difference except a panel that quietly never updates.

### 1.4 Can wrapping be turned off?

Evidence gathered from `RESEARCH/MUD2_FrontEndCodes.txt` (§2) and `MUD-ClientProto.md`:

- `ESC-N` — *"normal mode (user defines wrap etc.)"*. **Already sent** by
  `MuckaConnection.SendClientModeEntry`. It does not disable wrapping; it means the width comes from
  `/T` instead of the fixed mode widths (`ESC-T` = 78 wide, `ESC-G` = 57 wide).
- `/T{n}` — the MUD shell width command. The only wrap control we have evidence for. Server
  acknowledges with `[New terminal width is N]` (and/or `ESC-<n>W`), which
  `MudStreamParser.TryEmitTerminalWidthLine` / `AnsiSgrState` already surface as
  `TerminalWidthConfirmed` — i.e. **any width we ask for is verifiable at runtime**.
- Telnet NAWS (`TelnetNegotiator`, `OPT_NAWS = 31`) is negotiated and updated, but per the comment at
  `MuckaConnection.cs:338` the server does **not** wrap on it. It is not a lever.
- **No "no wrap" / "wrap off" command is confirmed to exist.** There may be one behind the game's
  own `help` — unknown. To find out: in a live session try `help terminal`, `help set`, `help width`,
  and probe `/T0` / `/T255` while watching the `[New terminal width is N]` confirmation. Do not
  write code against a guessed command name.
- Practical approximation of "off": set `/T` to a large constant (e.g. 160, the client's own cap) and
  decouple it from the pane width. Confirmed working only at 100; 160 is untested.

**Cost of doing that:** the terminal pane renders what the server sends, so a larger `/T` moves
wrapping to the client. `Mucka.Terminal/LineWrapper.cs` already re-wraps every logical line at paint
time (`Rendering/TerminalView.cs:502`, `BuildVisualRows`), so the plumbing exists — but its own
docstring says it is a **hard break with no word awareness**, explicitly relying on the server having
wrapped already ("a no-op when the server has already wrapped"). Raising `/T` above the pane width
turns every description line into mid-word breaks until `LineWrapper` learns word wrapping. Server-
formatted column output (the `score:` line, `who`/`exits` listings, the login ASCII art) is also laid
out to `/T` and would no longer fit the pane.

---

## 2. Risk table

Wrap column W. "Longest observed" is from the 4.2 MB capture; "worst case" uses the longest bestiary
name (`thickset dwarf guard` + instance digit = 21) and a 21-char weapon.

Every pattern below is `^…$` anchored (`CombatTracker`, `NpcHealthRungs`) unless stated, so a wrap
anywhere in the sentence means **no match at all** — a silent miss, not a partial one.

| Pattern | Anchors | Observed max | Worst case | W=158 | W=98 | W=41 |
|---|---|---|---|---|---|---|
| `WeaponSwitch` | `^…$` | 73 | **100** | ok | **BREAKS** | BREAKS |
| `PlayerAttackStart` | `^…$` | 58 | 82 | ok | at risk (82) | BREAKS |
| `NpcWeaponEquip` | `^…$` | 52 | 80 | ok | at risk (80) | BREAKS |
| `NpcHealthRungs` run-on (`…, and is holding the following:`) | `^…$`/`,\s.*$` | 53 | 80 | ok | at risk (80) | BREAKS |
| `WithdrawOffer` | `^…$` | 55 | 69 | ok | ok | BREAKS |
| `MutualWithdraw` | `^…$` | — | 67 | ok | ok | BREAKS |
| `NpcAggroStart` | `^…$` | 47 | 66 | ok | ok | BREAKS |
| `NpcStaminaRead` | `^…$` | — | 66 | ok | ok | BREAKS |
| `NpcFleeFailed` | `^…$` | — | 61 | ok | ok | BREAKS |
| `NpcFled` | `^…$` | 42 | 54 | ok | ok | BREAKS |
| `WeaponUnusable` | `^…$` | 40 | 54 | ok | ok | BREAKS |
| `NpcHealthRungs` plain | `^…$` | 44 | 54 | ok | ok | BREAKS |
| `WeaponEquip` | `^…$` | 40 | 53 | ok | ok | BREAKS |
| `NpcKilledYouNarrative` | `^…$` | — | 50 | ok | ok | BREAKS |
| `GuardConfusion` | `^…$` | 47 | 47 | ok | ok | BREAKS |
| `NpcHitsYou` | `^…$` | 33 | 45 | ok | ok | BREAKS |
| `YouHit` | `^…$` | 31 | 44 | ok | ok | BREAKS |
| `YouKilled` | `^…$` | 31 | 42 | ok | ok | at risk |
| `NpcKilledYou` | `^…$` | — | 41 | ok | ok | at risk |
| `WeaponBroke` | `^…$` | 27 | 41 | ok | ok | at risk |
| `PlayerAttackStartUnarmed` | `^…$` | — | 37 | ok | ok | ok |
| `NpcMissesYou` | `^…$` | 26 | 37 | ok | ok | ok |
| `YouMiss` | `^…$` | 24 | 35 | ok | ok | ok |
| `YouFled` | `^…$` | 33 | 33 | ok | ok | ok |
| `ItemDropped` | `^…$` | 33 | ~45 (item names vary) | ok | ok | at risk |
| `FightEndOther` | `^…$` | 27 | 27 | ok | ok | ok |

`GameLineAnalyzer` fares much better — almost every pattern there is `^`-anchored with **no `$`**, so
a wrap after the captured number is harmless:

| Pattern | Verdict |
|---|---|
| `StaminaMaxRegex`, `WeightCarriedRegex`, `ObjectsCarriedRegex` | Low. Prefix-anchored, both fields near the line start. Would only break below W≈35. |
| `LevelRegex`, `GamesPlayedRegex`, `ScoreRegex`, `YourStaminaRegex`, `CompactStaminaRegex` | Safe. Single capture within the first ~20 columns. |
| `StrengthRegex` / `DexterityRegex` | **Silent degradation.** The optional `effective strength: M` clause sits ~25–40 columns in; a wrap before it drops it and the snapshot silently falls back to *raw* strength. Effective strength is the stat the weapon-handling gate depends on. |
| `CombatStaminaRegex` (`\(N/M\)` anywhere) | Unanchored — survives, unless the wrap lands inside the bracket. |
| `PersonaSavedScoreRegex` | Unanchored, but needs `(Persona saved on …)` and the closing `).` on one line; long forms can break below W≈45. |
| `DreamwordLineRegex` | Pre-game only, where the terminal is wide. Low. |
| `CheckSoundTrigger` prefixes (63–66 chars) | Prefix matches; break only if W < 66, i.e. every mobile session. Three ambience sounds go missing on the phone. |

**Adjacent exposure, same root cause (not part of the ask, but worth logging):** the FEX/FEI/FEW
capture buffers in `MudStreamParser` flush one item **per `'\n'`** (`_feiLine`, `_fexLine`,
`FinalizeFewName`). A wrapped item/name line therefore becomes **two bogus entries** rather than a
missed one. `NarrowTerminalHeartbeatTests` proves those escaped responses do wrap.

**Direction of failure:** overwhelmingly **misses**, not false positives. A wrapped head fragment can
never end with the sentence's final `.`/`!`, so it cannot satisfy an `$`-anchored combat pattern. The
one shape with genuine false-positive potential is `ItemDropped` (`^<word> dropped\.$`), which a tail
fragment ending in "… dropped." could satisfy.

**Likely bearing on the concurrent weapon-detection investigation:** `WeaponEquip` (53),
`NpcWeaponEquip` (80) and `WeaponSwitch` (100) are three of the four longest patterns in the set. If
that bug reproduces on a narrow pane or on Android and not on a wide desktop window, this is almost
certainly the same root cause.

---

## 3. Options

### A. Turn wrapping off at the source
Set `/T` to a large constant (160, or whatever the server accepts), decoupled from the pane, and let
the client wrap for display.
*For:* one change, fixes every pattern at once, no per-call-site discipline.
*Against:* no confirmed "wrap off" command exists — this is "wrap so wide it never bites", and the
maximum accepted `/T` is unverified; `LineWrapper` must gain word awareness or all long prose breaks
mid-word; server-formatted columns (`score:`, `who`, `exits`, ASCII art) stop fitting the pane;
failure mode is silent and global (one dropped `/T` after a resize and every matcher regresses with
no signal); and it does nothing for the 4.2 MB of existing captures the tests replay.

### B. Rejoin wrapped lines in the parser
In `MudStreamParser.EmitChar`, hold a finished line back and merge it with the next when it looks
like a wrap.
*For:* everything downstream sees whole sentences; zero call-site discipline forever.
*Against:* two fatal problems. (1) **It cannot know.** The wrap is byte-identical to a line end
(§1.1); the only available signal is "was this break forced at the wrap column", and over the capture
~1 600 of ~6 300 long-line pairs were breaks that *would have fit* — i.e. genuine line ends that the
length test cannot separate from wraps without also using the wrap width, which is itself only known
approximately (`/T`−2, and only after `TerminalWidthConfirmed` arrives). A wrong merge glues two
unrelated lines, which the brief rightly calls worse than a miss. (2) **It breaks rendering.** The
terminal is fed the same `StyledLine`s; merging them hands `LineWrapper`'s hard break responsibility
for prose it cannot word-wrap, so option B silently drags option A's renderer work in with it. It
would also change `LongDescLineReady`, `RoomShortReady`, prompt gating and the asterisk-preamble
suppression, all of which are per-line by design.

### C. A wrap-insensitive matching abstraction (**recommended**)
Leave the parser and the render path exactly as they are. Put a tiny helper between `LineReady` and
the **matching** consumers that remembers the previous unmatched line and, when the current line
matches nothing, retries the pattern against `previous + " " + current`. Authoring a new pattern needs
no wrap awareness at all: write the logical sentence, anchored, as today.

### D. Do nothing
Refuted by §1 and §2: `WeaponSwitch` can already break at the desktop width the captures used, and
21 of 26 combat patterns break on the owner's own phone.

---

## 4. Judgement against the four criteria

| | A (wide `/T`) | B (parser rejoin) | C (matching helper) | D |
|---|---|---|---|---|
| Never merge unrelated lines | n/a (nothing merges) | **fails** — heuristic merge, visible damage | strong: a bad join is discarded unless it fully satisfies an anchored pattern, and the terminal never sees it | n/a |
| Never miss a real one | good while `/T` holds; silent global regression if it doesn't | good | good, incl. multi-fragment breaks at narrow widths | **fails** |
| Invariant #1 | free | free | free — this all runs on the Feed thread (`MudStreamParser` threading contract; `CombatTracker`: *"called from the parser Feed thread only"*), never the UI thread. One string concat per unmatched line. | free |
| Testability | needs a live server to verify `/T` | hard: needs width-aware fixtures | easy: feed pre-split fragments, no wire bytes needed | n/a |
| "Stop having to be careful" | yes, but by trusting an invisible remote setting | yes | yes, if matching goes through the helper — enforceable by making it the only convenient path | no |

### 4.1 Why C's false-merge risk is small, concretely
A join is only *offered* when the current line matched nothing. It only *takes effect* when the joined
string satisfies a full `^…$` pattern. For a false event, the previous line would have to be an exact
prefix of a combat sentence and the current line its exact remainder — and if the previous line had
been a complete, legitimate line starting `The <npc>`, it would have matched on its own and been
consumed. Two cheap width-free guards close the rest:

1. Do not join when the previous line ends in `.`, `!`, `?`, `"` or `:` — a wrapped **head** fragment
   never ends with its sentence's terminator, while nearly every complete game line does.
2. Clear the memory whenever a line **is** consumed by a match, on a blank line, on a partial/prompt
   line, and on game-mode exit — so a head fragment can never be reused for a second match.

Bias is deliberate: guard 1 can cost a rare miss but cannot create a merge.

### 4.2 There is already a precedent in this repo
`GameViewModel.ScanHistory` (line 1164) joins the last 80 lines with a single space and matches
unanchored triggers on the result; `CrNulLineEndingTests.SouthTombReplay_TriggerTextSurvivesWrapJoin`
exists to prove a watchword trigger survives the server's wrap point. Watchwords work **because they
join and then match**, not because of the whitespace normalisation their comment credits.
Recommendation C is that same trick, narrowed to a 2–3 line window so it is safe for short anchored
patterns.

---

## 5. Migration sketch for C

### 5.1 New type (proposed): `mudsharp/Protocol/WrapAwareLine.cs`

```csharp
/// One logical game line, plus the tail of the previous unmatched line(s), so a sentence the
/// server word-wrapped across several '\n' lines still matches an ^…$ pattern.
/// Feed-thread only; not thread-safe (same contract as CombatTracker.Observe).
public sealed class WrapAwareLine
{
    private const int MaxJoinFragments = 3;   // covers W≈20 worst case for a 100-char sentence
    private const int MaxJoinLength    = 320;

    private readonly List<string> _pending = new(MaxJoinFragments);
    private string _text = string.Empty;
    private string? _joined;                  // built at most once per line, lazily

    /// The line exactly as the parser produced it.
    public string Text => _text;

    /// Begin a new line. Call once per LineReady, before any TryMatch.
    public void Advance(StyledLine line)
    {
        if (line.IsPartial) { Reset(); return; }         // prompts are not sentences
        _text   = line.PlainText;
        _joined = null;
        if (_text.Length == 0) Reset();                  // a wrap never yields a blank row
    }

    /// Match `re` against this line, and — if that fails and the preceding line(s) look like an
    /// unfinished sentence — against them joined with a single space. On success the pending
    /// fragments are consumed so they can never satisfy a second pattern.
    public bool TryMatch(Regex re, out Match m)
    {
        m = re.Match(_text);
        if (m.Success) { Consume(); return true; }
        if (!CanJoin) return false;
        m = re.Match(Joined);
        if (m.Success) { Consume(); return true; }
        return false;
    }

    /// Call once after the whole match chain, matched or not: an unmatched line becomes a
    /// candidate head fragment for the next line.
    public void Commit()
    {
        if (_text.Length == 0 || EndsSentence(_text)) { Reset(); return; }
        if (_pending.Count == MaxJoinFragments) _pending.RemoveAt(0);
        _pending.Add(_text);
    }

    public void Reset() { _pending.Clear(); _joined = null; }

    private bool CanJoin => _pending.Count > 0 && _text.Length > 0
                            && JoinedLength <= MaxJoinLength;
    private string Joined => _joined ??= string.Join(' ', _pending) + ' ' + _text;
    private void Consume() { _pending.Clear(); _joined = null; }

    private static bool EndsSentence(string s)
        => s.Length > 0 && s[^1] is '.' or '!' or '?' or '"' or ':';
}
```

Notes on the design:
- **No width knowledge required.** Deliberately: `/T`−2 is only known after
  `TerminalWidthConfirmed`, is stale for one frame after every resize, and getting it wrong turns
  misses back on. Correctness rests on the anchored pattern plus the `EndsSentence` guard, both
  width-free. (A `wrapWidth` hint could later be added purely to skip pointless joins, never to
  authorise one.)
- **Insert exactly one space.** Correct for prose wraps, which consume the space (§1.1). It is
  *wrong* for the unspaced FE payloads that hard-break mid-token — which is why this helper is for
  prose matchers only and the FEX/FEI/FEW buffers need their own fix (§2, adjacent exposure).

### 5.2 Call sites

`MudSession.WireParserEvents` (`mudsharp/Session/MudSession.cs:365`) owns one instance and drives it:

```csharp
_wrapAware.Advance(line);
_combat.Observe(_wrapAware, CombatClock());   // new overload; StyledLine overload kept for tests
_wrapAware.Commit();
```
plus `_wrapAware.Reset()` alongside the existing `_combat.ForceEnd(...)` calls on game-mode exit,
auto-reset and logout.

`CombatTracker.Observe` changes shape only at the pattern calls — the chain, its ordering comments and
all the hard-won `Begin`/`End` semantics stay byte-for-byte:

```diff
-var text = line.PlainText;
-if (string.IsNullOrEmpty(text)) return;
-Match m;
-if ((m = PlayerAttackStart.Match(text)).Success)
+if (line.Text.Length == 0) return;
+Match m;
+if (line.TryMatch(PlayerAttackStart, out m))
 {
     Begin(m.Groups["npc"].Value);
-    Emit(timestampUtc, CombatEventKind.FightStart, …, text);
+    Emit(timestampUtc, CombatEventKind.FightStart, …, m.Value);
 }
-else if (YouFled.IsMatch(text))
+else if (line.TryMatch(YouFled, out _))
```

`raw` on the emitted `CombatEvent` should become `m.Value` (the sentence actually matched) rather than
the fragment, so clogs record the logical line. `NpcHealthRungs.TryParse(string, …)` gains a
`TryParse(WrapAwareLine, …)` overload delegating to `TryMatch`; the phrase tables are untouched.

The authoring rule afterwards is one line long, and it is the natural thing to do anyway: *matchers
call `line.TryMatch(pattern)`; write the pattern against the logical sentence.* No `\s+`-instead-of-
space discipline, no length budget to remember.

### 5.3 Sequencing (each step independently shippable)

1. `WrapAwareLine` + its unit tests. No production wiring. Zero risk.
2. `CombatTracker` / `NpcHealthRungs` moved onto it; `MudSession` wiring. Re-run
   `CombatCaptureReplayTests` — the ground-truth counts (58 encounters, 42 kills, 30 flees, …) must
   be **unchanged**, since nothing in that capture wraps a combat line at `/T100`.
3. `GameLineAnalyzer`: only `StrengthRegex`/`DexterityRegex` (recover `effective`) and
   `CheckSoundTrigger`. Leave the prefix-anchored stat patterns alone — they are already wrap-safe and
   changing them buys nothing.
4. Separately, and *not* part of this change: fix the FEX/FEI/FEW per-`'\n'` flush so a wrapped item
   is not published as two.

Not recommended as part of this: raising `/T`. If it is wanted later for its own sake (fewer joins,
nicer scrollback re-wrapping on resize), it needs word-aware `LineWrapper` first, and `WrapAwareLine`
should stay regardless as the thing that makes correctness not depend on a remote setting.

### 5.4 What to test

Unit (`WrapAwareLine`):
- two-fragment join at a space, one space inserted, `^…$` pattern matches;
- three-fragment join at W≈20 (`WeaponSwitch` split three ways);
- fragment cap and length cap both refuse a fourth/over-long join;
- a matched line clears the pending fragments (no reuse for a second pattern);
- blank line, `IsPartial` prompt line, and explicit `Reset()` each clear the window;
- previous line ending `.`/`!`/`?`/`"`/`:` is never used as a head;
- **negative:** `"The large rat0 is here."` followed by `"misses you."` must produce nothing;
- **negative:** two complete adjacent combat lines must produce exactly two events, never three.

Combat regression:
- every pattern in the §2 table, hand-split at W = 98, 41 and 20, asserting one event with the right
  NPC/weapon captures and the full sentence as `raw`;
- `ItemDropped` false-positive probe: a prose tail fragment ending `"… dropped."` must not emit while
  the preceding line ends in a terminator;
- `NpcHealthRungs` run-on form (`…, and is holding the following:`) split at the comma;
- `CombatCaptureReplayTests` unchanged counts (the guard that the refactor changed nothing at width
  100).

Integration / live:
- an Android session at ~41 columns: kill something with a weapon and confirm `WeaponEquip`,
  `NpcWeaponEquip`, health rungs and `NpcFled` all now fire — the current failure the weapon-detection
  investigation is likely chasing;
- resize the desktop pane narrow mid-fight (`NotifyWindowSize` → `/T`) and confirm events keep firing
  across the width change, including the frame where `/T` and the server disagree.

---

## 6. One-line answers to the questions asked

- **Were the patterns written wrap-aware?** No. They are `^…$`-anchored logical sentences, correct
  only while the pane is wide.
- **Should wrapping be turned off?** No confirmed way to turn it off exists, and the closest
  approximation (a large `/T`) shifts word wrapping onto a renderer that only hard-breaks. Not worth
  it as the fix.
- **Should there be a helper so nobody has to keep remembering?** Yes — that is the right answer, it
  costs one small class plus a mechanical edit at the call sites, it runs off the UI thread, and this
  repo already relies on the same join-then-match trick for watchwords.
