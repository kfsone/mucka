# Replays

A replay is a stretch of a real session - what the server sent, and when - played back by the client
with no server and no socket. It is for watching, not playing: reaching a screen state on any
platform to look at it, check how something renders (a combat rail, an effect, a buff) or take a
screenshot of it. Whatever the client sends during a replay is dropped.

## The file

`.muckrep`, read and written by `Muckrep.Read` and `Muckrep.Write`. One record per line,
`{offset_ms} {rx|an} {payload}`, the same line shape as the test fixtures' `.c1` captures: the
payload is the record's own bytes read as Latin-1, with only a backslash, a CR and an LF escaped.
Offsets count from the first record. Lines opening with `#` are header notes. Like a `.c1`, a
`.muckrep` is raw server bytes, a third of them above 0x7F, so `.gitattributes` marks it `binary`
and `.editorconfig` leaves it alone.

A file never holds anything the client sent: Tx records are dropped whole. Annotation records - a
`$MARK`, a reset note - are kept, and are not played.

## Masking the login

A replay holds nothing the client sent: Tx records are dropped whole, so what was typed - the
password included - never reaches a file. What the server sent back is kept, and the account ID in
it is masked where the server printed it (`Muckrep.MaskLogin`), found by its context and never by
its value.

DECISION (operator) By context, not by value. Replacing a known account ID or password wherever it
occurs marks every place it occurs: a password that is also a word said in play shows itself by
being replaced, and a saved password says nothing about a session played before it was changed.
Value-matching against the login, the profile's own credentials and the saved passwords was built
and rejected for this.

The contexts, as the server prints them:

| Where | What |
|---|---|
| the account prompt | everything echoed after `Account ID:` (any case, anywhere on a line) to the end of its line, whatever was typed there |
| FE code 95 block, or 95 00 | the account, its fourth line (`mud2_FE4.txt`: licence, min, max, account, privs); a line may end CR NUL LF, as `Mud2C1Decoder` allows |
| FE code 95 02, 95 03 | the account ID they bracket: an account change, an account logout |
| the login banner | `<id> logged in on pts/N.` |
| logout | `<id> logged out.` |
| a second login | `Account <id> is already logged in.` |
| the account page (`E`) | `Account: <id>` |
| EXAMINE's last games | `Last games for <id>:` |

In prose the ID is held to its shape, a letter then digits, because the banner follows the
screen-clear `ESC [ 2 J` with no space between.

The password is guarded on its own line only: anything the server sent after `Password:` on that
line is masked with asterisks. A line in play that holds `Password:` is masked the same way; that
masks the line rather than pointing at any value.

EVIDENCE On a copy of the operator's database, 2026-11-13 (520 runs naming an account, 4 distinct
account IDs): every place the server sent one of those IDs falls in one of the contexts above; the
line after `Password:` was empty in all 504 prompts, so the server was never seen to echo a
password. With
masking applied to all 853 Sessions, no case-blind occurrence of any of the 4 IDs remained, no
record changed length, and nothing was masked after a password prompt.

FACT An account ID the player types into play - said, told, shouted - is not masked: it is in none
of the contexts. That is the cost of masking by context, and it is accepted.

The account ID is replaced rather than blanked, so a replay still reads like a login:

- `Muckrep.PlaceholderAccountId` (`M00010101`) stands for a valid account;
- `Muckrep.PlaceholderPassword` (`passWord`) stands for a password the server accepts;
- `Muckrep.PlaceholderWrongPassword` (`PASSwORD`) is the go-to wrong password, for anything that
  wants a login to fail. It is not the only one allowed, just the one to reach for first.

These three are the project's stand-in credentials everywhere a fixture, a test or a replay needs
one. A real account ID or password never goes into the repo.

Persona names, other players' among them, and what was said stay in a replay; a replay is a whole
stretch of play, and cutting it down to what names nobody is not what one is for. A line that should
not ship is edited with a substitution when the replay is made (`--sub` below): that is by value,
because the person making the replay chose the value.

On a mudii.co.uk telnet login the account ID comes back one keystroke at a time, split across
records, and again in lower case in the code 95 block that follows. `Muckrep.MaskLogin` therefore
searches what the server sent as one stream, over the whole recording before any range of it is
cut, and lays the placeholder over the ID byte for byte, so a split echo stays split where it was
and, where the ID is as long as the placeholder, every record keeps its length; a lower-case ID gets
a lower-case placeholder.

## Making one

- `$MAKEREPLAY [name]` in the client writes the session so far to the player's replays folder
  (`MuckaPaths.GetReplayDirectory`, `~/.mucka/replays`). The name defaults to the time, and an
  existing file is never overwritten. Desktop only: there is no command on a phone or tablet.
- The Logs page's Save as Replay writes any Session as a replay, on every platform
  (`docs/logs-design.md`).
- `scripts/make-muckrep.cs` makes one from any run in any `mucka.db`, optionally only a range of its
  records:

```
dotnet run --file scripts/make-muckrep.cs -- ~/.mucka/mucka.db
dotnet run --file scripts/make-muckrep.cs -- <mucka.db> <run id> <out.muckrep> [first] [last] [--sub OLD=NEW]...
```

`first` and `last` are the run's `seq` values, inclusive. The login is masked over the whole run
before the range is cut, so a range whose edge falls inside a masked context is still masked. Each
`--sub` replaces every case-blind `OLD` with `NEW`, laid over byte for byte as the mask is.

## Playing one

A profile or a direct connect whose host is `replay:<name>` plays the replay instead of connecting;
the port and SSH settings are ignored. `ReplayFiles` looks for `<name>.muckrep` (the extension is
optional) in this order: an absolute path as given; the player's replays folder; on Android, the
app's external files folder; and the replays packaged with the app. The Logs page's Replays tab lists the
same places and plays one with a tap (`docs/logs-design.md`).

`ReplayTransport` stands in for the socket and `ReplayPlayer` schedules the records. The replay bar
covers the input row the way the SCROLLBACK bar does, and the whole replay counts as the keyboard
being owned elsewhere (Invariant #0's exceptions), so a phone's keyboard stays down. Its close glyph
hides it for the rest of the replay (`GameViewModel.HideReplayBar`), so a screenshot shows the input
row a live session has; the replay plays on at the speed it had. Its slider sets
`ReplayPlayer.Step`:

- far left (`ReplayPlayer.PauseStep`) - paused;
- `ReplayPlayer.OriginalStep`, where it starts - 1.0x, the original session's own gaps;
- the steps between - fixed multiples of that, labelled by `ReplayPlayer.Label`;
- far right (`ReplayPlayer.MaxStep`) - one record every `ReplayPlayer.MaxIntervalMs`, whatever the
  original gaps were.

A change takes effect within `ReplayPlayer.SliceMs`. When the last record has played the link stays
open, holding the final screen until the player quits.

## A replay records nothing

A replay connection (`MuckaConnection.IsReplay`) opens its store on a throwaway file in the system
temp folder and deletes it on dispose (`MuckaDb.Delete`): no wire log, no fights, no scores and no
persona sessions reach `mucka.db`. A store left behind - the process killed, or a connect that failed
before anything disposed it - is deleted by the next replay once it is older than
`MuckaConnection.StaleReplayStoreAge`. Everything that writes through the store works as it does live,
which is why the side panel's fight history shows the replay's own fights. The transcript does not
arm on a replay, and the client's annotations are not copied to the crash log.

DECISION A throwaway store rather than a switch on each writer. A switch per writer is a list that
has to be kept complete, and the writer added next would be the one missing from it.

## Where replays come from

`Resources/Replays/` holds the replays that ship. The Windows and Mac builds package them; the
Android and iOS builds do not, so on a phone or tablet a replay is copied in by hand. All of these
work on Release builds as well as Debug ones:

- iOS simulator - the app's data folder is on the Mac's own disk:

```
DATA=$(xcrun simctl get_app_container booted org.kfs.mucka data)
mkdir -p "$DATA/Library/replays" && cp login-and-spells.muckrep "$DATA/Library/replays/"
```

- Android emulator - the app's external files folder takes an `adb push` on any build:

```
adb shell mkdir -p /sdcard/Android/data/org.kfs.mucka/files/replays
adb push login-and-spells.muckrep /sdcard/Android/data/org.kfs.mucka/files/replays/
```

`ShippedReplayTests` plays every replay in `Resources/Replays/` through `MudSession`, the same
parser a replay feeds, and fails if one does not reach its last line.

`login-and-spells.muckrep` is records 0-115 of an iPhone-simulator session on mudii.co.uk: the login,
character select, a few minutes standing in a room, then two spells. 76 seconds at 1.0x. Made with
`--sub sexy=sweet`.

`screenshots.muckrep` is `login-and-spells.muckrep` up to the walk north, edited for store and README
screenshots: Crispybob the necromancer and Folly the warlock are added to every player list, and a
tell from Drizzle and a shout from Folly, both invented, follow Awlie's say. 71 seconds at 1.0x.
