Project:      mucka  ("a muddy client")
Intent:       Win/Droid realization of 'Clio' MUD telnet-client (G:/Source/clio-1.8a/...)
Scope:        MUD2 only; full/correct telnet/FES/ansi/color/sound support
Audience:     Me, my wife, my daughter, two friends
Long term:    No: just the five of us
Status:       Pre-alpha, unreleased
Platforms:    Windows 11, Android
Developers:   AI agents only. Human involvement as Project Lead/Senior Producer only
Team Size:    Reiterating this is an ALL AI translation/port of Clio
Tooling:      Visual Studio 2022, net10 sdk

Senior Producer (Oliver "kfsone" Smith) notes:
- 40 years development experience in C, C++, Python, Perl, etc. Negligible past C# experience,
- I really just want Clio to run on Android,
- I have no experience developing MAUI,
- I have no experience developing C# on Github,
- Github is our CI and build platform,
- Agents will need to guide/inform me on the C#, dotnet, MAUI aspects of this implementation,

# Session Capture

mucka logs every byte of every session into `~/.mucka/mucka.db`, always, with nothing to arm and no
setting to forget. One row per record in `wire`: when, which direction, and `data` - the server's own
bytes, verbatim and uncompressed, with nothing wrapped around them. `SELECT data FROM wire` prints
the line, and a `strings` pass over the file reads as MUD2 text. See `docs/persistence-design.md`.

The `.jsonl` transcripts this used to write are gone. The committed
`mudsharp.Tests/Fixtures/Data/wyvern-poison-death.jsonl` is test data in that older shape - one line
per record, `[{timestamp_ms},{"tx"|"rx"|"an"},{json-escaped-data}]` - and is the only thing in the
repo that still reads it:

```
[1779464113440,"an","capture started: mud2.co.uk"]
[1779464113757,"rx","\u00FF\u00FD\u0018\u00FF\u00FD \u00FF\u00FD#\u00FF\u00FD\u0027"]
```

# Mapping

Removed. Live map use pulls the player off the scroll, which is the opposite of what this client
is for. The capture layer, the map graph, the `$map` window and the archived design docs are all
gone; git history has them. If mapping is ever wanted again it gets rebuilt from the session log
rather than revived. What survives of the domain: MUD2's world is a directed labeled multigraph
of named places, not a grid.

# Notes:

NB: Do not rely on `*` being the prompt. Rather, the prompt is a sequence denoted by the
slot1 escape markup. This is a major hazard area for LLMs because both the MUD protocol and
inference are effectively using tokenization.

NB: Models which use non-ascii characters in code will be rejected.
