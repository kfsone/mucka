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

mucka can log raw session transcripts in jsonl format:

either:
`["...elided..."]`
to reduce context burden on agents for long sessions, or

`[{timestamp},{mode},{json-escaped-data}]`
- timestamp int in ms,
- mode "tx" (we sent), "rx" (we recv), "an" (higher-level code annotation by us)
- json-escaped-data is string

```
[1779464113440,"an","capture started: mud2.co.uk"]
[1779464113757,"rx","\u00FF\u00FD\u0018\u00FF\u00FD \u00FF\u00FD#\u00FF\u00FD\u0027"]
```

# Mapping

Parked. Live map use pulls the player off the scroll, which is the opposite of what this client
is for; the code (`Core/Mapping`, `Pages/MappingPage.cs`, the Windows-only `$map` window) stays
for possible offline use. The domain model and design are in `docs/archive/mapping/`. Any future
mapping work starts from `MUD-Cartography.md` there: MUD2's world is a directed labeled multigraph
of named places, not a grid.

# Notes:

NB: Do not rely on `*` being the prompt. Rather, the prompt is a sequence denoted by the
slot1 escape markup. This is a major hazard area for LLMs because both the MUD protocol and
inference are effectively using tokenization.

NB: Models which use non-ascii characters in code will be rejected.
