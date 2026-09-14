# Persistence - design

What Mucka writes to disk, where, and who owns the write. This is the governing document for the
store; the code answers to it.

Read `CLAUDE.md` first. Two things from it are load-bearing here and are not restated in every
section: the AI-written corpus is not evidence (every claim below that came off a code comment has
been re-read against the code, and the ones that are measurements carry their conditions), and there
is one operator with one installation, so there is no migration path and no compatibility shim.

## The shape, in one paragraph

One SQLite file, `~/.mucka/mucka.db`. It holds the combat corpus, the raw wire log and the
per-encounter combat logs. Everything is always on; nothing is armed by hand and no setting turns
anything off. One background task owns the only write connection; every producer thread does nothing
but build a row and hand it over. Readers open their own short-lived connections and never run on the
UI thread. There is no JSONL anywhere.

## The decisions this implements

These came from the operator and are decisions, not options.

- One database. Wire log, clogs, combat - all of it, one file, and the file moves from
  `~/.mucka/combat/mucka.db` to `~/.mucka/mucka.db`.
- Logging stops being optional. The `logwiresession` setting, the hand-armed capture, and their UI
  all go away.
- JSONL is eliminated.
- The existing combat corpus is kept (moved, not re-read by code). The existing wire data is
  integrated into the new file by hand. The 40 JSONL captures go.
- Retention, pruning and text consolidation are explicitly NOT in this stage.

### What "parse-once" means here, and what it does not

The stage brief said "parse-once tables with async offload so analysis does not re-parse raw lines".
The operator has since narrowed it: **the wire log keeps capturing the raw line; a parsed `lines`
table comes later.**

So the parse-once layer in this design is the *clog* content - encounter events, stats snapshots,
room contents, creature values - which is already parsed at capture time and today is thrown into
JSONL files that every analysis pass has to re-read and re-interpret. Those become tables.

The wire log stays raw bytes (`wire.data`), one row per record. It is the ground truth, and
running the client's own parsers over it offline is how the parsers get found to be wrong
(`docs/Lab-spec.md`). A raw line is recoverable from it by decode.

A table of every line with the C1 code that introduced it is **not** in this stage, and the reason is
worth recording so it is not re-proposed as a small job: `StyledLine` does not carry the C1 code. It
carries `LineKind` (`mudsharp/Models/LineKind.cs`), which is a four-value semantic reduction
(`Normal`, `Chat`, `FightEnd`, `CreatureInvisible`), and
`C1Scope` is internal to the parser. Storing the code means surfacing it from `Mud2C1Decoder`'s
dispatch sites onto `StyledLine`, and per CLAUDE.md the code is the authority where the prose is not,
so a `lines` table without it would be the weaker artifact sitting next to the stronger one. That is a
stage of its own.

### Why the wire log has no framing

The wire log used to pack runs of records into one blob behind a varint framing: a `MWL1` magic, then
per record a zigzag timestamp delta and a length-plus-direction varint, then the payload. That is
gone. `wire` has one row per record, `ts_ms` and `direction` are columns, and `data` is the bytes and
nothing else.

The framing's own rationale was legibility - it argued against compression on the grounds that "a blob
you cannot look at without writing a decoder first is a corpus in name only" - and then defeated the
same property itself. The demonstration is one line: `SELECT data FROM batches` printed `MWL1` and
stopped, because the fifth byte of every batch is the first record's zero timestamp delta and sqlite3
renders a blob as a C string. Every batch in the corpus looked identical.

What the framing bought, weighed honestly:

- **Direction** - real, and now a column, which is strictly better than two bits inside a varint.
- **Per-record arrival time** - real, and now a column. It was never the reason for the framing.
- **Read boundaries** - where one socket read ended. That is TCP segmentation, not protocol structure;
  `MudStreamParser` handles arbitrary splits, so a replay never needed them. `seq` preserves the order
  the records were handed over, which is the part that is evidence.

What it cost, beyond legibility: the framing carries no redundancy at all, so damage that leaves the
buffer parseable decodes into a well-formed run of records that is not what was written - over 200,000
mutated batches, 39,789 decoded into silent garbage. `batches.records` existed as the only guard
against that, and the guard is unnecessary once damaged bytes are just damaged bytes in one row.

The price is storage: unindexed, one row per record costs about 23.5 bytes of SQLite page overhead, so
the same measured corpus is 13.4 MB rather than 11.4 MB - 0.74 MB per play-hour against 0.63. That is
the trade, made deliberately and in that direction.

### Why `wire` carries no indexes

Two measurements, kept apart because they answer different questions. Rebuilt and vacuumed over the
123,933-row corpus, the `wire` table is **13,783,040 bytes with no indexes and 17,207,296 with the
two it used to carry** - `(session_id, seq)` and `(ts_ms)`. They cost **3,424,256 bytes, 19.9%**, and
took a row from 26.7 bytes of overhead above its payload to 54.3. Separately, `dbstat` on the live
file before the drop put those two indexes at 2,093,056 and 1,798,144 bytes of pages - 3,891,200
between them, higher than the rebuilt figure because a live file carries free space the rebuild does
not. Nothing queried either index.

The two reads that exist are covered without them. `ReadSession` walks `id`, which is the rowid, so
it is the table's own order - no index and no sort. A time-range question scans 13 MB, tens of
milliseconds, on no path a human waits for. The schema policy is additive, so `CREATE INDEX` is
there the day something needs one, justified on that day's corpus.

Two things were measured and NOT done:

- **`WITHOUT ROWID` keyed on `(session_id, seq)`** is *worse* - 13,869,056 bytes against 13,783,040
  for a plain rowid table, both rebuilt and vacuumed over the same rows - because SQLite's own
  guidance is that it suits rows below about 1/20 of a page (204 bytes here) and the wire log's p90
  is 187 with a 3,464-byte maximum. It fragments. This was expected to win and did not; the
  measurement is recorded so it is not re-attempted from first principles.
- **Dropping `seq`** saves a further 299 KB, and is refused. `seq` is per-session and assigned by that
  session's own writer, so it stays gapless however many clients run; `id` is global and interleaves,
  because the operator runs two Muckas side by side. That makes `seq` the only thing that can show a
  record went missing, and 299 KB is not worth the only integrity signal the table has.

`id`, `seq` and `ts_ms` are all the same order, and that is load-bearing now that a reader walks `id`.
`WireLogWriter.Emit` reads the clock, takes the `seq` and hands the row to the store under one lock
for exactly that reason. Both halves of that were wrong before it:

- With the increment outside the lock, a thread preempted between it and the hand-over lets a later
  `seq` reach the queue first. Reproducible on every run, a few records per ten thousand.
- With the clock reading outside the lock, an earlier `seq` can carry a later timestamp. Reproducible
  on about half of runs. Nothing would have caught this one: no index ever covered `ts_ms` ordering
  and no test looked at it.

So all three come from one serialization point and describe the same moment - when the record entered
the log, a few microseconds after the byte arrived rather than at it. That is the right trade: an
order that disagrees with itself is worse than one that is uniformly a hair late, and cross-stream
ordering is the evidence a swing, a diagnose reading and an award are attributed by.
`Seq_id_and_timestamp_all_agree_even_under_concurrent_taps` is the test; it fails without the lock.

## Why one file

`WireLogDb.cs` argues at length that the wire log must be its own file. The operator has overruled it.
The argument is summarised here so nothing rediscovers it as new:

- **Growth.** Measured over 13 sessions, 18.11 play-hours, 10-13 Sep 2026: 119,921 records,
  10,141,176 bytes of payload, an average wire rate of 0.156 KB/s. One row per record stores that in
  about 13.4 MB - 0.74 MB per play-hour, or about 1.1 GB a year at four hours of daily play. Against
  that, the combat tables are thousands of small rows.
- **Blast radius.** Deleting the wire log must never be an operation that can touch the combat corpus.
- **Writer contention.** A third writer on a file that already needed `PRAGMA busy_timeout=5000`
  (`CombatDb.cs`) to stop two writers dropping each other's rows.

What actually decides it:

1. The volume does not justify the split. MUD2 is a small, frozen, 32-bit game and the operator's is
   the only installation. The four stores measured on 2026-09-14 were: `mucka.db` 6.1 MB (24,402
   swings, 2,790 fights, 2,285 score events and no `diagnose` readings at all, spanning 14 Aug to
   13 Sep), `wire.db` 11.4 MB (13 sessions, 10 Sep to 13 Sep, 1,074 framed batches, 119,921 records), 21
   clog files, and 40 JSONL captures totalling 17 MB.
2. **The contention argument is an argument against the current writer design, not against one file.**
   This design replaces four independent writers with one, which removes the collision the busy
   timeout exists for rather than adding to it. See "One writer" below.
3. **Blast radius is per-table, not per-file** - once the schema policy is one policy (below), nothing
   in the code drops anything, and clearing the wire log is `DELETE FROM wire` run by hand.

One claim in the tree is NOT carried forward, because it is not true: `MuckaConnection.cs:44-46`
justifies the existing shared file by saying it lets "the analysis view join a swing to the fight it
belonged to". Nothing in `CombatDb.cs` joins `swings` to `fights` - all six views aggregate `swings`
alone, and `encounter_started_at_ms` is a column comment and an index, not a join anyone runs. The
join is *possible*, and after this stage it is possible across six more tables; that is the honest
statement of the benefit.

## One owner

There was no single owner. `MuckaConnection` field-initialised three stores and lazily constructed a
fourth in `TryStartWireLog`. Nothing persistence-related is in the DI container; `MauiProgram.cs`
registers only `ConnectViewModel` and `ConnectPage`.

**`MuckaConnection` owns one store instance, for the life of one connection.** That is what it
already is, made explicit:

- `MuckaConnection` is `new`ed per connection attempt (`ViewModels/ConnectViewModel.cs:155`), and
  `ConnectAsync` has exactly one call site (`:167`) on a freshly constructed instance. So one
  `MuckaConnection` is one `sessions` row, and store lifetime is one connection - not one app run.
- Nothing reads the database outside that lifetime. The two readers are
  `FightHistoryStore.LoadAsync` and the swing ledger's warm-up (`SwingLedger.WarmCore`), both fired
  from `MuckaConnection` and both opening their own short-lived connection.
- No DI registration is added. A container entry would claim an app-run lifetime the store does not
  have.

The type is `MuckaStore`. It owns the write connection, the writer task, and the row queue; it is
constructed in `MuckaConnection` and disposed by it, and the writer classes (`SwingLedger`,
`FightHistoryStore`, `ClogWriter`, `WireLogWriter`) take it as a constructor argument instead of a
database path. `MuckaConnection` takes the host as a constructor argument for the same reason: the
store opens with the connection, so the login exchange is in the wire log like everything else.

`MuckaStore`, `MuckaDb` (schema and pragmas) and `IStoreRow` live in a new library,
`Mucka.Util/Mucka.Store`. It has to be its own: `Mucka.WireLog` and `Mucka.Combat` both write through
it, `Mucka.Combat` already references `Mucka.WireLog`, and one database means one schema, applied
once. A command-line reader can take this library alone.

## One writer

Every store but one already follows the same discipline, and it is Invariant #1 in concrete form:

> The Feed/socket thread only serializes a row and `TryWrite`s it onto a `Channel<T>`. Exactly one
> background task owns a write connection and batches whatever is queued into one transaction.
> Readers take a lock only to swap an in-memory snapshot. `Dispose` blocks up to 5 s to drain. The
> live combat rail reads a warmed in-memory cache, never SQL.

That discipline is kept. What changes is that there is now **one** such task instead of four.

The exception today is `JsonlWireLogSink`, the only synchronous unbatched writer left (`WriteLine`
under a lock with `AutoFlush = true`). It is on the socket thread rather than the UI thread, so it
does not breach Invariant #1, and it is deleted by this stage anyway.

### The arbiter

One `Channel<IStoreRow>`, unbounded, single-reader. One task, `DrainAsync`, owning one
`SqliteConnection` for the store's whole lifetime. Each pass drains whatever is queued into one
transaction, dispatching each row to the prepared statement for its own table. This is exactly what
`SwingLedger.WriteBatch` already did for its three row types, widened to all of them.

`IStoreRow` is the seam: a row knows its own INSERT statement and how to bind itself. Row *building*
stays with the class that understands the data - the ledger still builds swing rows, the clog writer
still builds encounter rows - and only the connection and the transaction move.

**One channel, not one per producer.** Cross-stream ordering is evidence: `SwingLedger.AppendLocked`
notes that the order between a swing, a diagnose reading and a score announcement made in the same
breath is the whole basis on which an award is later attributed to a kill. Separate queues lose it, and with
the clog and wire streams joining, more of it is worth keeping.

**Unbounded, not bounded.** `SqliteWireLogSink` bounded its queue at 256 batches with
`FullMode.DropOldest` (`SqliteWireLogSink.cs`), defending against a writer that is wedged rather than
dead - and the wedge cause it names is SQLite lock contention, which one arbiter removes. What is left
is a stalled disk, and the arithmetic says that is not worth machinery: at the measured 0.156 KB/s an
hour of total write stall is about half a megabyte of backlog. The `DroppedBatches` counter and the
drop report go with the bound; a gapless `seq` is still worth keeping as a per-session ordinal,
because it is the order things happened and a replay walks it.

**A dead writer still stops accepting.** `SqliteWireLogSink`'s `_faulted` flag is kept and widened to
the store: if the background task falls over, it sets the flag, discards what is queued, and every
producer's enqueue becomes a no-op. Without that, an unbounded channel with no reader is a memory leak
whose size is the rest of the session. Retrying is not done, for the reason the existing code gives:
if the database cannot be written at all, a retry turns one failure into one a minute forever.

**One failure policy, not two.** `SwingLedger` and `FightHistoryStore` each caught a failing row,
reported it and carried on with the batch; the wire sink faulted on anything. The per-row catch is
dropped and a row that throws faults the store. It reads as resilience and is not: with one arbiter,
the only two things that make a row throw are a dead database and a schema bug, and swallowing either
produces an error per row forever with nothing that ever says the recording has stopped being
trustworthy. This is testable and tested - drop `wire` out from under a live writer and the store
must go faulted rather than writing an error line per record for the rest of the session.

### What the per-encounter drains become

`ClogWriter` currently runs one `Channel<string>` and one `DrainAsync` task per open encounter, each
owning its own `StreamWriter`, because each encounter was its own file (`ClogWriter.DrainAsync`). With
tables there are no files, so `OpenEncounter` keeps `Closing`, `EndedUtc` and `ValuedNames` and loses
`FilePath`, `Queue` and `WriterTask`; `_writerTasksForTests` goes. The overlapping-clog machinery
survives as what it always meant - at most two encounter keys in flight, one live and one draining its
tail - but it is now two keys in a list rather than two files with two background tasks.

This is the largest single simplification in the stage and it is what makes one arbiter natural rather
than forced.

### Connection pragmas

Unchanged from `CombatDb.Open`, and for the reasons it gives: `journal_mode=WAL` (readers and the one
writer never block each other; a crash mid-write rolls back to the last commit),
`synchronous=NORMAL` (one fsync per checkpoint rather than per commit; the exposure is the last
transaction or two), `foreign_keys=ON`.

`busy_timeout` is kept at 5000 ms but its justification changes and the comment must change with it.
It no longer arbitrates between two client writers - there is one - it covers a reader (a warm-up
query, or Lab) overlapping the writer, and any second instance of the client the operator happens to
start.

### Open eagerly

`SwingLedger.DrainAsync` opened its connection lazily, on the first row that arrived, so that "a
session with no combat in it never creates a database file at all". That
property is gone regardless: the wire log is always on, so the file exists from the first byte of
every session. The store therefore opens its connection in its constructor, which is what
`SqliteWireLogSink` already does and for the right reason - a database that cannot be written says so
at the one moment a human is watching, rather than dying quietly on a background thread.

### Failure reporting

`MuckaConnection.WireLogFailed` / `WireLogFailure` exist because the wire log was a feature switched
on once and trusted forever, and the crash log is not somewhere the operator looks. Under one arbiter
that is true of the whole store, so the pair is generalized to `StoreFailed` / `StoreFailure` and
reports to the terminal exactly as it does now.

A store that cannot be opened does **not** block the connection, and does not throw out of the
constructor either - it reports through the same callback, comes up faulted, and swallows every row
for the rest of the session. That is the wire log's current behaviour (reported, carried on) rather
than the hand-armed capture's (abort the connect), and it is the right one: the point of the client is
to play MUD2. It also keeps every writer in `MuckaConnection` non-nullable, so no call site has to
check.

## Schema

One file cannot run two migration policies. Today `CombatDb` is additive-columns-only and never drops
data (`AddedColumns` + a `pragma_table_info` probe + `ALTER TABLE ADD COLUMN`), while `WireLogDb`
drops and recreates both tables and the view on any column mismatch because the wire log is
disposable (`DiscardOnSchemaChange`). Both are argued as correct in their own file.

**The policy is additive-only, everywhere.** `DiscardOnSchemaChange` is deleted.

- It is the policy that cannot lose the irreplaceable half, and there is no mechanism that can be
  told which half a table belongs to without becoming the migration framework CLAUDE.md forbids.
- Two regimes in one file is the sediment CLAUDE.md names: a rule that has to be remembered per table
  is a rule that rots.
- What the discard policy actually bought was a guard against a NOT NULL insert failing against an old
  file and faulting the log out for the session with a swallowed exception. Additive-only gets the
  same protection differently: a change that cannot be expressed as a nullable added column is not
  made by the code at all. The operator runs `DELETE FROM wire` (or drops the table) by hand and
  the next open recreates it.
- There is still no `PRAGMA user_version`. A version gate would only skip `IF NOT EXISTS` statements,
  never perform an ALTER, so it could not migrate anything - it would be a version number that looked
  like a plan. `AddedColumns` reads the table's own shape instead.

### Tables

Verbatim from today, unchanged in shape:

| Table | Came from | Notes |
|---|---|---|
| `swings` | the combat database | one row per blow, brackets kept as brackets |
| `fights` | the combat database | one row per per-NPC fight |
| `npc_stamina_reads` | the combat database | every `diagnose` reading |
| `score_events` | the combat database | every `(Persona saved on ...)` line |
| `sessions` | the wire log | one row per connection |
| `wire` | the wire log | one row per record: `ts_ms`, `direction`, and the bytes |

Plus the combat database's six views and the wire log's `v_session_sizes`, all unchanged. Everything
above now lives in `MuckaDb.SchemaSql`, which is the whole schema in one place.

New, replacing the clog files:

```
encounters                  one row per encounter; the clog's "encounter_start" + "encounter_end"
encounter_lines             the 30-line pre-roll and the post-close tail ("line")
encounter_stats             "stats" - an absolute snapshot when a load-relevant value moved
encounter_contents          "contents" - the FEI list, when it changed
encounter_contents_items    one row per item in that list
encounter_events            "event" - every classified CombatEvent
creature_values             "creature_value" - the (creature, points) pairs
```

Every one of them carries `encounter_started_at_ms`, which is the key `swings` and `fights` already
carry. Column sets come straight from `ClogWriter`'s payloads: its stats block and its seven effect
booleans for `encounter_stats`, its reset block for `encounters`, and `OnCombatEvent`'s payload for
`encounter_events`.

The index on `encounters(encounter_started_at_ms)` is NOT unique. Two encounters can open in the same
millisecond - a new fight starting the instant the previous one's last NPC dies is routine, not a rare
race - and they then share the key, which is already true of `swings` and `fights`. A unique index
would turn that ambiguity into a dropped row; a duplicate is at least visible.

Five shape decisions, each of which had a real alternative:

- **`encounters` carries no stats or contents snapshot of its own.** The JSONL header inlined both,
  which meant one shape for the opening reading and a different one for every later reading of the
  same thing. Instead the writer emits an ordinary `encounter_stats` row with `reason = 'start'` and
  an ordinary `encounter_contents` row when an encounter opens, so "the stats at encounter start" is
  `WHERE reason = 'start'` rather than a second representation. It also removes about twenty-five
  duplicated columns.

- **`encounter_events` stores every kind, including the hit and miss kinds that `swings` also
  records.** They are different projections, not duplicates: `swings` carries the full player and
  world state at the blow and is indexed for aggregate questions, while `encounter_events` is the
  complete ordered narrative of one encounter with the server's own `raw` text on each row. Dropping
  the swing kinds would make "replay this encounter" a UNION of two differently-shaped tables. The
  duplication is 24,402 rows over a month of play.
- **Pre-roll and tail share one table**, with a `phase` column (`preroll` / `tail`) and an `ord`.
  They are the same thing - plain lines around the encounter - and the pre-roll carries no timestamp
  of its own (`ClogWriter` wrote it as a bare string array), so `ts` is nullable on that half.
- **Room contents get a child table** rather than a delimited string. `fights.effects` sets the
  comma-separated precedent, but "which encounters had a thief in the room" is precisely the kind of
  question the merge is for, and it is a query against a child table and a string scan against a blob.
  One row per item, with `is_creature` (from the game's own C04 presence sentences) and `is_carried`
  (the FEI list's `========` split).
- **`encounter_stats` stores the seven effect booleans, not the tooltip messages.** The JSONL header
  serialized the whole `StatusEffectState`, which carries eleven `string?` message fields
  (`mudsharp/Models/StatusEffect.cs:66-77`). `ClogWriter`'s own remark on its effect-flags block
  already says a boolean flip is the whole of what a stats row needs; the same holds here.

### The encounter key, and the one bug the merge exposes

`swings` and `fights` are keyed on `encounter_started_at_ms`, which `MuckaConnection` derives once per
encounter in `OnInCombatChanged` and passes to both the fight recorder and the swing ledger.

It did **not** pass it to the clog writer, which stamped its own `DateTimeOffset.UtcNow` in
`Start()` instead. While clogs were files that nobody joined, that cost nothing. The moment they are
tables keyed for the join, two clocks a few microseconds apart join to nothing. **`ClogWriter` takes
the encounter key from its caller, exactly as the other two do,** and falls back to a local reading
only on the unit-test and design-time path - the same shape `FightHistoryRecorder.cs:134` already uses
and for the same stated reason.

`ClogWriter._startSequence` exists to keep two encounters that start in the same millisecond from
colliding on a filename. With no filenames it goes, and each table gets its own
`id INTEGER PRIMARY KEY`. Two encounters opening within one millisecond would share an
`encounter_started_at_ms`; that is already true of `swings` and `fights` today and is not fixed here.

### Timestamps

Everything is unix milliseconds UTC. The clog writer stamps `DateTimeOffset.UtcNow` on the Feed
thread; the wire capture stamps `DateTimeOffset.UtcNow` on the socket read and write loops
(`SessionCapture.Emit`); the ledger stamps on the Feed thread. Same clock, different threads - so a
wire record and the swing parsed out of it are comparable to within the parse, which is what any
cross-stream question needs.

## What is removed

The whole hand-armed and opt-in apparatus goes. Enumerated, because a half-removed setting is worse
than the setting:

**JSONL and the sink fan-out**
- `Mucka.Util/Mucka.WireLog/JsonlWireLogSink.cs` - deleted.
- `IWireLogSink` - deleted. It existed so the backend could be swapped between the JSONL sink and the
  SQLite one; with one sink there is nothing to swap.
- `SessionCapture` - deleted, and its job folded into `WireLogWriter`, which is now the tap and the
  batcher in one: it stamps the time, tags the direction, and owns `Annotate`. The class existed to
  fan one record out to several sinks and to keep the two arming decisions apart; with one always-on
  destination it was a layer with nothing in it. The annotations themselves stay - the dreamword,
  reset and terminal-width notes are cheap and are in the log where they are useful.
- `WireLogExport.ExportSessionToJsonl` and `SuggestFileName` - deleted. `ListSessions` and
  `ReadSession` are kept: they have no production call site today, and they are the seed of the CLI
  that the `TODO` entry names.

**The setting**
- `logwiresession` in `Core/SettingsStore.cs:197,269`, `ClientSettings.LogWireSession` (`:93`),
  `Profile.LogWireSession` (`:94`), `FkeyEditorViewModel.LogWireSession` (`:147,336,487`), the
  checkbox in `Pages/FkeyEditorPage.xaml:275`, and the reads in `ConnectViewModel.cs:189,228,523` and
  `GameViewModel.cs:548,622,743`.

**The capture button and its plumbing**
- `ConnectViewModel.IsCaptureRequested`, `CaptureButtonText`, `ToggleCaptureCommand` and the
  `TryStartCapture` call; the `DataTrigger` in `Pages/ConnectPage.xaml:357,367`.
- `GameViewModel.IsCapturing`, `ToggleCaptureCommand`, `ToggleCapture`; the badge in
  `Pages/GamePage.xaml:183-199`; the branch in `Pages/RawConsolePage.cs:442`.
- `MuckaConnection.IsCapturing`, `CaptureFilePath`, `TryStartCapture`, `StopCapture`,
  `TryStartWireLog` (the store is no longer started separately from the connection).
- The `--record` command-line argument and its `#if DEBUG` block at `ConnectViewModel.cs:478-484`.

**Paths**
- `Core/ClogPaths.cs` becomes `Core/MuckaPaths.cs`, a single data-directory resolver: one method
  returning `~/.mucka` on Windows and `FileSystem.AppDataDirectory` elsewhere, and one
  giving the database path inside it. `GetCombatDirectory`, `GetWireLogDirectory`,
  `GetCaptureDirectory` and `GetClogDirectory` all go, and with them the two dangling comments
  referring to an `items.jsonl` "$eval" log (deleted in `72c6ebe`) and a `~/.mucka/mapping` store
  (removed in `5a72a6d`).
- The two `DefaultFileName` constants collapse to one, on `MuckaDb`. Nothing hardcodes a Windows path
  today and nothing may start.

**Schema machinery**
- `WireLogDb.DiscardOnSchemaChange`, `ExpectedBatchColumns` and `ColumnsOf` - deleted with the policy.
- `CombatDb.cs` and `WireLogDb.cs` merge into `MuckaDb.cs`. It keeps the observations from both (why
  brackets are unaggregated, why there are so many state columns, what `records` is for) and drops the
  "why this is its own file" argument, which this document has now superseded.

### Android writes to app data, not to the cache

The clogs and the wire log both used `FileSystem.Current.CacheDirectory` on mobile. That is wrong for
a store: Android reclaims a cache directory under storage pressure without asking and without telling
the app, and "Clear cache" in the settings app empties it. It was survivable for a hand-armed log and
is not survivable for the corpus, so the mobile arm of `MuckaPaths.GetDataDirectory` is
`FileSystem.AppDataDirectory` - the same directory `mucka.ini` (`SettingsStore.cs`) and the crash log
(`CrashLog.cs`) already use.

The consequence that remains, recorded rather than solved: the wire log is always-on on Android too,
so 0.74 MB per play-hour is now unconditional in app data. Retention is a later stage.

### One consequence the operator has already accepted

The hand-armed `rec` capture is the reason the JSONL sink exists, and stage 7 rebuilds it as plain
text. Between this stage and that one there is no session recording at all beyond the wire log
itself. A JSONL sink is not kept alive to cover the gap.

## What Lab reads

`docs/Lab-spec.md` currently reads `wire.db` by name and column
(`SELECT base_ts_ms, data, records FROM batches ORDER BY seq`), re-parses the raw bytes through
`MudStreamParser.Feed` / `FrameClosed`, reserves the `lab_` table prefix, and expects to write into
the same file the client reads.

Almost all of that survives. What changes:

- **One path, and no decode step.** `~/.mucka/mucka.db` instead of `wire.db` plus `mucka.db`, and the
  traffic query is `SELECT ts_ms, direction, data FROM wire ORDER BY seq` - the rows are the records,
  so nothing has to be unframed first. `ATTACH` is no longer needed for a cross-store question.
- **The clog corpus is queryable.** Every question Lab would previously have answered by re-reading
  `~/.mucka/clogs/*.jsonl` is now SQL against `encounter_events`, `encounter_stats` and their
  siblings. The `flees --scan` pipeline still goes through the raw bytes, because it needs frames and
  frames come from the parser.
- **Also reads the `.jsonl` captures** comes out of the Input section. There will be none.
- `lab_` stays the prefix, and Lab still never writes to a table the client reads.
- `MudStreamParser` and `MudSession` stay public. That is decided, not open.

## Landing it

Done on 2026-09-14, recorded because the result has to be checkable and because nothing is deleted
until the new build has been played.

1. **The combat corpus moved.** With Mucka closed: `~/.mucka/combat/mucka.db` (6,123,520 bytes) ->
   `~/.mucka/mucka.db`; there was no `-wal` or `-shm` beside it. The additive schema picks the file up
   on the next open with zero code - the four combat tables were already exactly right, and the wire
   and encounter tables are created into it by `CREATE TABLE IF NOT EXISTS`. Doing this before the
   first run saves having to overwrite a freshly created empty one. From this point the new build is
   the one to launch: an older build still reads `~/.mucka/combat/mucka.db`, would create an empty one
   there, and would record that session into a file nothing reads afterwards.
2. **The wire data was decoded in, not copied in.** The old `wire.db` holds `MWL1`-framed batches and
   the new table holds records, so `ATTACH` plus `INSERT ... SELECT` could not do it: every batch had
   to pass through `WireLogFraming.Decode` on the way. That is why the framing code was deleted
   *after* this step and not before. `seq` was renumbered per session across the session's whole record
   sequence, replacing the old per-batch ordinal. The session id offset was read once, before any
   insert, so the two id spaces shift together.

   Result: **13 sessions, 1,074 batches decoded, 119,921 records, 10,141,176 bytes of payload** - the
   record count the old file reported, so nothing was lost or invented.
3. **Checked.** `~/.mucka/mucka.db` held the 6.1 MB corpus plus the wire log: 76,633 Rx rows
   (9,668,270 bytes), 42,946 Tx rows (465,243 bytes - a typed command is about 11 bytes), 342
   annotations. Over 18.11 play-hours that is 0.156 KB/s. `SELECT data FROM wire` prints MUD2 text.
4. **The two `wire` indexes were dropped by hand**, since the schema policy is additive and the code
   no longer creates them but cannot remove what is already there:

   ```sql
   DROP INDEX IF EXISTS ix_wire_session;
   DROP INDEX IF EXISTS ix_wire_ts;
   VACUUM;
   ```

   `VACUUM` is what returns the pages; without it the file keeps its size and reuses the space later.
5. **Still to delete**, once the new build has been played: `~/.mucka/combat/` (now empty - the stale
   `combat.db` the operator deleted, and the moved `mucka.db`), `~/.mucka/wire/`, `~/.mucka/clogs/`
   (21 files), and the 40 JSONL captures in `%LOCALAPPDATA%\Temp\mucka`. A full copy of all three
   databases as they were sits in `~/.mucka/backup-20260914-100508/`.

If a file is locked at any of these steps, Mucka is running. Ask the operator to close it; never kill
it.

## Deliberately not in this stage

- **Retention, pruning, vacuum, archival.** Recorded here only so it is not re-invented as a
  requirement: the idea for later is to consolidate spans of text so that beyond a few weeks the store
  no longer has to hold raw literal text.
- **A parsed `lines` table with the C1 code**, and the parser change that would feed it.
- **The CLI that surfaces logs from the database.** Its `TODO` entry is added by this stage;
  `WireLogExport.ListSessions` / `ReadSession` are kept as its seed.
- **Stage 7's plain-text `rec`.**
