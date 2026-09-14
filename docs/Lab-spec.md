# Lab - spec (draft)

A command-line tool for asking questions of captured MUD2 text. Lives **outside this repo**;
references `mudsharp`.

## What it is for

Human + LLM analysis of the log history, out of action. "Show me all the flee costs" should be one
command over the whole corpus, not an afternoon of one-off scripts.

## What it is not

**Not a client feature.** Mucka stays a client for playing MUD2: real-time, PvP-ish, permadeath, read
by people who do not want to be pulled off the scroll. The assistive surface is settled - glanceable
health, the opponent list, alt-weapon, flee cost, a for/against cue - and this tool is not a route to
growing it. Nothing here runs inside the client.

## Shape

A single `.cs` file, run directly - .NET 10 file-based app, no project, no solution entry:

```csharp
#!/usr/bin/env dotnet
#:package Microsoft.Data.Sqlite@10.*
#:project ../mucka/main/mudsharp/mudsharp.csproj
```

```
dotnet run lab.cs -- flees --scan
```

Verified against `mudsharp` (net10.0, no dependencies) on SDK 10.0.300. One file is the point: it can
be rewritten per question without a build system having an opinion.

## Relationship to mudsharp

**Use all of it.** C1 decoding, line analysis, the session layer - whatever makes sense of a line. The
tool is not required to keep its distance, and the traffic is expected to go both ways over time:
running the client's parsers against the whole corpus offline is the cheapest way to find where they
are wrong, and anything the tool needs that currently lives up in `Mucka` is a candidate for being
pushed down into `mudsharp` where both can reach it. That migration is a benefit of the arrangement,
not a cost of it.

What stays the tool's own is the *interpretation* above the parse - the extractors, the tables, the
fitting. Those are disposable by design.

## Input

**One file: `~/.mucka/mucka.db`.** Everything the client writes is in it - see
`docs/persistence-design.md`, which governs the schema. Two halves matter here:

- **Raw traffic.** `batches.data` is the server's own bytes with a couple of varint header bytes
  between records, **uncompressed and verbatim**. Read it with `WireLogFraming.Decode` over
  `SELECT base_ts_ms, data, records FROM batches ORDER BY seq`, or with `strings` if you are in a
  hurry. This is the ground truth, and running the client's own parsers over it is how the parsers
  get found to be wrong.
- **Classified encounter rows.** `encounter_events`, `encounter_stats`, `encounter_contents`,
  `encounter_lines` and `creature_values`, all keyed on `encounter_started_at_ms` - the same key
  `swings` and `fights` carry. Anything that used to mean re-reading `~/.mucka/clogs/*.jsonl` is SQL
  now. There are no `.jsonl` files any more, in either shape.

There is no `lines` table: `StyledLine` does not carry the C1 code that introduced a line, so a
code-level question still goes through `MudStreamParser`. See `TODO`.

## Output

Stdout as TSV for a one-off question, **and its own tables in the same file** for anything worth
keeping. Writing there is wanted, not merely tolerated: derived tables sitting beside `swings` and
`fights` are what let a finding be a stored query plus its result rather than a paragraph of prose.
Table names are prefixed `lab_` so what is exploratory stays visibly exploratory, and lab never writes
to a table the client reads.

## Pipeline

For a question about classified facts, the pipeline is a query. For a question about the raw stream:

1. `sessions` -> `batches` -> `WireRecord`s in capture order.
2. `rx` payloads into `MudStreamParser.Feed`; collect `LineReady`, `ScoreSaved`, and whatever else the
   question needs.
3. Cut into **frames** on the prompt. Everything the server says about one event is inside one frame.
   This is the most load-bearing protocol fact in the project and every extractor depends on it.
4. Run extractors over frames.

**The frame boundary this needs now exists.** `MudStreamParser.FrameClosed` was added 2026-09-11 (for
the combat panel's award pairing, which was drifting without it); it fires on a shown prompt only,
after `LineReady` for the frame's last line. Subscribe to it - do NOT reimplement prompt detection in
the tool, which would be a second answer to "where does a frame end" and is guaranteed to disagree
eventually.

## First use case: `flees --scan`

Walk the entire log history frame by frame and sample **every** flee. Not a sample of flees that
happened to be easy to attribute - all of them, with the ones it cannot resolve emitted as such.

### Method

Per frame: find the line saying the player fled. Take the **FIRST** `ScoreSave` after it in that same
frame. That is the flight cost. Anything later in the frame or in any following frame is a different
charge - a death, a failed task - and is discarded.

`ScoreSave` is the parser's event for a `(Persona saved on -371 = 1,345).` line: `Delta` (signed, null
when the line carried none) and `Total` (after the change). Its own remarks already say a flee cost
lands there and nowhere else. So:

```
cost         = -Delta
score_before = Total - Delta
```

Both off one line. **No score tracking outside the frame, at all.**

### What the previous attempt got wrong, because it is the trap

It took the **last** penalty in the frame and reconstructed score from a running total kept alongside.
Both halves fail on the same frames:

- **Last, not first.** The flee's own charge is followed by whatever else the frame reports. Taking the
  last one silently substitutes an unrelated penalty, and does so precisely on the interesting frames -
  the ones where something else also went wrong.
- **Ancillary score tracking.** A tracked total drifts on every save the tracker missed or double-
  counted, so the cost comes out as the difference between two numbers of unknown provenance instead of
  the number MUD2 printed. `ScoreSave` carries the delta and the total together; there is nothing to
  reconstruct.

A frame with a flee and no `ScoreSave` is a free flee. Record it as free. It is not a missing sample.

### Emit

`ts`, `session`, `score_before`, `stamina`, `stamina_max`, `cost`, plus `frame_id` so any row can be
read back in context. Not level, not str/dex, not the room, not the creature, not the fight's duration.

Stamina and its maximum come from the most recent `sc`/`qs` or inline `(cur/max)` before the flee, and
the row says how stale that reading is - a flee resolved against a stamina from four ticks ago is a
different quality of sample and must be distinguishable, not silently mixed in.

## The flee equation

The client prices a flee with `MudSharp.Combat.FleeWorth`, which implements Bartle's formula
(`MUD2-flee-cost.md`). `flees --scan` should produce a re-runnable table of every recorded flee
with the formula's prediction beside the observed charge, so the formula is checked against the
whole corpus rather than the handful of flights in `FleeWorthTests`.

### Observations to reproduce first

Hand-transcribed from the owner's scroll, so the first job is to recover these same events from
the store and check the tool agrees with the transcription. The two full-stamina rows match the
formula with zero bounties (371.7 and 438.9 predicted).

| fight | score before | sta | max | cost | cost/score |
|---|---|---|---|---|---|
| Bludgeon, mud2.com | 1,716 | 95 | 95 | 371 | 21.6% |
| banshee, mallet | 2,094 | 95 | 95 | 438 | 20.9% |
| banshee, mallet | 1,656 | <=55 | 95 | 90 | 5.4% |
| banshee, blade | 1,566 | <=17 | 85 | 86 | 5.5% |
| zombie0, staff0 | 492 | 13 | 68 | 38 | 7.7% |
| magpie | 200 | 58 | 58 | 102 | 51.0% |

- The two full-stamina flees agree closely across different personas and servers.
- The two `<=` rows had lines elided in transcription; their real stamina is at or below what is shown.
- **The magpie row may not be a flee.** No flee command appears in it, 51% is unlike anything else, and
  it lands on exactly 98 - one point under the level-100 threshold. Recover the frame before using it.

## Stat facts the extractor may rely on

From the owner, 2026-09-09.

- `sta`, `str`, `dex` are rolled 35-65 each and must total 150. **The maxima are otherwise independent
  of one another.**
- Each rises by `max(0, min(100 - current_max, 10))` per level gained, and falls by 10 per level lost.
  So one persona's stats keep the differences its roll gave them across every level change. Those
  differences are a property of that character and say nothing about the game.
- Permanent ceiling 100 for all three; stamina alone can be taken to 120 by the +5 permanent health
  potion available some resets.
- Low stamina drains *effective* strength and dexterity. That is the only live coupling between them.
