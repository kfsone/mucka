# mucka

A lightweight MUD2 client for Windows, written in Go.

Renders ANSI-coloured fixed-width text to an append-only scrolling view, with
automatic login, live character stats, and a F-key macro system.  Informed by
the Clio client.

## Requirements

- Windows 10/11
- Go 1.22+

## Build

```
go build ./cmd/mucka
```

## Configuration

mucka reads `%USERPROFILE%\mucka.ini` on startup and creates sensible defaults
if the file is absent.

```ini
[general]
font-name = Go Mono   ; any TrueType/OpenType font installed on the system
font-size = 14
width     = 80
height    = 40
history   = 2000

[fkeys.none]          ; unmodified F1–F12 bindings
f1 = inventory
f2 = score

[fkeys.shift]         ; Shift+F1–F12
[fkeys.ctrl]          ; Ctrl+F1–F12

[mud2]                ; one section per server profile (name used with .connect)
host     = mud2.example.com
port     = 4242
login    = mylogin
account  = myaccount
password = s3cr3t
```

Passwords survive `#`-style inline comments because the parser is configured
to ignore them.

## Connecting

```
.connect mud2
```

Replaces the profile name with whichever `[section]` you defined in the INI.
Auto-login sends your login, account, and password in sequence.

## Commands

| Prefix | Example | Description |
|--------|---------|-------------|
| *(none)* | `go north` | Sent to the server (or echoed locally if not connected) |
| `.` | `.connect mud2` | Local client command |
| `$` | `$stream greet.txt` | Utility command |

### Dot commands

| Command | Description |
|---------|-------------|
| `.connect <profile>` | Connect to a server profile |
| `.fkeys` | Open the F-key binding editor |
| `.help` | List dot-commands |
| `.quit` | Exit |

### Dollar commands

| Command | Description |
|---------|-------------|
| `$stream <file>` | Display a file in the text panel, one line per 50 ms |
| `$source <file>` | Replay file lines as typed input (50 ms between tokens) |
| `$less <file>` | Page through a file (space/enter = next page, q = quit) |
| `$help` | List dollar-commands |

#### `$source` file format

Each line is submitted as a command.  Special in-line tokens:

| Token | Effect |
|-------|--------|
| `{enter}` | Submit current input |
| `{bs}` | Backspace |
| `{clear}` | Clear input line |

#### `$stream` file format

Lines prefixed with `#` are skipped.  Backslash escapes are expanded
(`\n`, `\t`, `\\`, `\xNN`).

## Keyboard shortcuts

| Key | Action |
|-----|--------|
| Enter | Submit |
| Up / Down | Command history |
| Ctrl-V | Paste (sanitised — truncated at first newline) |
| Ctrl-D | Speak current dream word (`say "<word>"`) |
| Escape | Clear input |
| F1–F12 | Bound macro (configurable via `.fkeys` or INI) |
| Shift/Ctrl + F1–F12 | Alternate macro sets |

## Status bar

Left side shows live character stats (stamina ♥, strength S, dexterity D,
magic M, score ★) colour-coded green/yellow/red by ratio.  Right side shows
dream word (cyan), reset timer, and connection spinner.

Stats are updated from FES packets (polled every 10 seconds while in-game)
and from plain-text stat lines in the server output.

## Headless mode

```
mucka -headless -profile mud2 -script actions.txt
```

Runs without a GUI.  Output goes to stdout; stats updates are printed as
`[STATS] ...` lines.  Script syntax matches `$source` minus the special
tokens.

## License

MIT — see [LICENSE](LICENSE).
