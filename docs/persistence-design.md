# Persistence - design

What Mucka writes to disk, where, and who owns the write. This is the governing document for the
store; the code answers to it.

Read `CLAUDE.md` first. Two things from it are load-bearing here and are not restated in every
section: the AI-written corpus is not evidence (every claim below that came off a code comment has
been re-read against the code, and the ones that are measurements carry their conditions), and there
is no shared database - each install has its own local file, and only code travels between machines.

## The shape, in one paragraph

One SQLite file, `~/.mucka/mucka.db`. It holds the combat corpus, the raw wire log and the
per-encounter combat logs. Everything is always on; nothing is armed by hand and no setting turns
anything off. One background task owns the only write connection; every producer thread does nothing
but build a row and hand it over. Readers open their own short-lived connections and never run on the
UI thread.

Nothing writes a text-file log of any kind. The one capture file left in the tree is the committed
wyvern fixture under `mudsharp.Tests/Fixtures/Data/` - six verbatim rx frames that three test
projects replay through the production parser. It is evidence, and the only reason its format
survives.

## The decisions this implements

These came from the operator and are decisions, not options.

- One database. Wire log, clogs, combat - all of it, one file: `~/.mucka/mucka.db`.
- Logging is not optional. There is no `logwiresession` setting, no hand-armed capture and no UI
  for either.
- Retention, pruning and text consolidation are not done. The direction, when they are: consolidate
  spans of text so that beyond a few weeks the store no longer holds raw literal text.

### What "parse-once" means here, and what it does not

The wire log captures the raw line; there is no parsed `lines` table.

The parse-once layer is the *clog* content - encounter events, stats snapshots, room contents,
creature values - which is parsed at capture time and lands in tables rather than in loose text
files that every analysis pass has to re-read and re-interpret.

The wire log is raw bytes (`wire.data`), one row per record. It is the ground truth, and
running the client's own parsers over it offline is how the parsers get found to be wrong. A raw
line is recoverable from it by decode.

A table of every line with the C1 code that introduced it does not exist, and the reason is worth
recording so it is not proposed as a small job: `StyledLine` does not carry the C1 code. It
carries `LineKind` (`mudsharp/Models/LineKind.cs`), which is a four-value semantic reduction
(`Normal`, `Chat`, `FightEnd`, `CreatureInvisible`), and
`C1Scope` is internal to the parser. Storing the code means surfacing it from `Mud2C1Decoder`'s
dispatch sites onto `StyledLine`, and per CLAUDE.md the code is the authority where the prose is not,
so a `lines` table without it would be the weaker artifact sitting next to the stronger one.

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

One database, because the logging and the combat data are one corpus and are most useful together.
The wire log lived in a file of its own while it was being developed, which was a scaffold for that
work and never an architectural position.

The figures that bound the decision, measured 2026-09-14:

- **Volume.** Over 13 sessions, 18.11 play-hours, 10-13 Sep 2026: 119,921 records, 10,141,176 bytes
  of payload, an average wire rate of 0.156 KB/s. One row per record stores that in about 13.4 MB -
  0.74 MB per play-hour, or about 1.1 GB a year at four hours of daily play. Against that, the combat
  tables are thousands of small rows. MUD2 is a small, frozen, 32-bit game and this is the only
  installation.
- **The four stores as they stood:** `mucka.db` 6.1 MB (24,402 swings, 2,790 fights, 2,285 score
  events and no `diagnose` readings at all, spanning 14 Aug to 13 Sep), `wire.db` 11.4 MB (13
  sessions, 10 Sep to 13 Sep, 1,074 framed batches, 119,921 records), 21 clog files, and 40 loose
  captures totalling 17 MB. The combat file itself was 6,123,520 bytes.
- **The traffic is lopsided, and that is what makes the volume small.** The same corpus in `wire`:
  76,633 Rx rows (9,668,270 bytes), 42,946 Tx rows (465,243 bytes - a typed command is about 11
  bytes), 342 annotations. Over 18.11 play-hours that is 0.156 KB/s.
- **Writer contention** was a property of four independent writers, not of one file. This design
  replaces them with one, which removes the collision `PRAGMA busy_timeout=5000` exists for rather
  than adding to it. See "One writer" below.
- **Blast radius is per-table, not per-file.** With one schema policy (below) nothing in the code
  drops anything, and clearing the wire log is `DELETE FROM wire` run by hand.

What one file does NOT buy, stated so it is not claimed: nothing in the store joins `swings` to
`fights`. All six views aggregate `swings` alone, and `encounter_started_at_ms` is a column and an
index, not a join anyone runs. The join is *possible*, across those two tables and the seven
encounter tables; that is the honest statement of the benefit.

## One owner

Nothing persistence-related is in the DI container; `MauiProgram.cs` registers only
`ConnectViewModel` and `ConnectPage`.

**`MuckaConnection` owns one store instance, for the life of one connection:**

- `MuckaConnection` is `new`ed per connection attempt (in `ConnectViewModel`), and `ConnectAsync`
  has exactly one call site, on a freshly constructed instance. So one
  `MuckaConnection` is one `mucka_runs` row, and store lifetime is one connection - not one app run.
  A LOGIN is a separate thing again: `persona_sessions`, opened on game-mode entry, and what every
  fact table keys to.
- Nothing reads the database outside that lifetime. The two readers are
  `FightHistoryStore.LoadAsync` and the swing ledger's warm-up (`SwingLedger.WarmCore`), both fired
  from `MuckaConnection` and both opening their own short-lived connection.
- No DI registration is added. A container entry would claim an app-run lifetime the store does not
  have.

The type is `MuckaStore`. It owns the write connection, the writer task, and the row queue; it is
constructed in `MuckaConnection` and disposed by it, and the writer classes (`SwingLedger`,
`FightHistoryStore`, `ClogWriter`, `WireLogWriter`) take it as a constructor argument rather than a
database path. `MuckaConnection` takes the host as a constructor argument for the same reason: the
store opens with the connection, so the login exchange is in the wire log like everything else.

`MuckaStore`, `MuckaDb` (schema and pragmas) and `IStoreRow` live in their own library,
`Mucka.Util/Mucka.Store`. It has to be its own: `Mucka.WireLog` and `Mucka.Combat` both write through
it, `Mucka.Combat` references `Mucka.WireLog`, and one database means one schema, applied once. A
command-line reader can take this library alone.

## One writer

The discipline, which is Invariant #1 in concrete form:

> The Feed/socket thread only serializes a row and `TryWrite`s it onto a `Channel<T>`. Exactly one
> background task owns a write connection and batches whatever is queued into one transaction.
> Readers take a lock only to swap an in-memory snapshot. `Dispose` blocks up to 5 s to drain. The
> live combat rail reads a warmed in-memory cache, never SQL.

There is **one** such task, not one per store.

### The arbiter

One `Channel<IStoreRow>`, unbounded, single-reader. One task, `DrainAsync`, owning one
`SqliteConnection` for the store's whole lifetime. Each pass drains whatever is queued into one
transaction, dispatching each row to the prepared statement for its own table.

`IStoreRow` is the seam: a row knows its own INSERT statement and how to bind itself. Row *building*
stays with the class that understands the data - the ledger still builds swing rows, the clog writer
still builds encounter rows - and only the connection and the transaction move.

**One channel, not one per producer.** Cross-stream ordering is evidence: `SwingLedger.AppendLocked`
notes that the order between a swing, a diagnose reading and a score announcement made in the same
breath is the whole basis on which an award is later attributed to a kill. Separate queues lose it, and with
the clog and wire streams joining, more of it is worth keeping.

**Unbounded, not bounded.** A bounded queue that drops the oldest on overflow defends against a
writer that is wedged rather than dead, and the wedge cause is SQLite lock contention, which one
arbiter removes. What is left is a stalled disk, and the arithmetic says that is not worth
machinery: at the measured 0.156 KB/s an hour of total write stall is about half a megabyte of
backlog. A gapless `seq` is still worth keeping as a per-session ordinal, because it is the order
things happened and a replay walks it.

**A dead writer stops accepting.** If the background task falls over it sets a faulted flag,
discards what is queued, and every producer's enqueue becomes a no-op. Without that, an unbounded
channel with no reader is a memory leak whose size is the rest of the session. Retrying is not
done: if the database cannot be written at all, a retry turns one failure into one a minute forever.

**One failure policy, not two.** A row that throws faults the store; there is no per-row catch that
reports and carries on with the batch. That reads as resilience and is not: with one arbiter, the
only two things that make a row throw are a dead database and a schema bug, and swallowing either
produces an error per row forever with nothing that ever says the recording has stopped being
trustworthy. Tested - drop `wire` out from under a live writer and the store
must go faulted rather than writing an error line per record for the rest of the session.

### What the per-encounter drains become

With tables there are no files, so `ClogWriter` runs no channel and no drain task of its own;
`OpenEncounter` carries `Closing`, `EndedUtc` and `ValuedNames` and no file path, queue or writer
task. The overlapping-clog machinery is what it always meant - at most two encounter keys in
flight, one live and one draining its tail - as two keys in a list rather than two files with two
background tasks.

### Connection pragmas

`journal_mode=WAL` (readers and the one
writer never block each other; a crash mid-write rolls back to the last commit),
`synchronous=NORMAL` (one fsync per checkpoint rather than per commit; the exposure is the last
transaction or two), `foreign_keys=ON`.

`busy_timeout` is 5000 ms. It does not arbitrate between two client writers - there is one - it
covers a reader (a warm-up query, or an offline tool) overlapping the writer, and any second
instance of the client the operator happens to start.

### Open eagerly

The store opens its connection in its constructor, not lazily on the first row. Lazy opening bought
"a session with no combat in it never creates a database file at all", and that property is gone
regardless: the wire log is always on, so the file exists from the first byte of every session.
Eager is also the right reason - a database that cannot be written says so at the one moment a
human is watching, rather than dying quietly on a background thread.

### Failure reporting

The crash log is not somewhere the operator looks, so the store reports its own failure to the
terminal, through `StoreFailed` / `StoreFailure`.

A store that cannot be opened does **not** block the connection, and does not throw out of the
constructor either - it reports through the same callback, comes up faulted, and swallows every row
for the rest of the session. Reported-and-carry-on rather than abort-the-connect, because the point
of the client is to play MUD2. It also keeps every writer in `MuckaConnection` non-nullable, so no
call site has to check.

## Schema

**Every schema change is a numbered, forward-only script** in `Mucka.Util/Mucka.Store/Migrations/`,
run once per database and recorded in DbUp's journal. `MuckaDb.ApplySchema` is the whole entry point.
There is no second description of the current shape anywhere - see `MigrationScripts` for why, and
for the rule that a script which has already been released is never edited.

- **Why a journal rather than converging on the file's own shape.** Releases go out on GitHub, so
  databases created by this client live on machines nobody here can inspect. Converging is a standing
  instruction re-evaluated at every open: a rule that drops a dead column keeps dropping it, on a
  stranger's file, unprompted and with no backup. Ordered replay runs a change once and then stops
  being a rule at all.
- **A file predating the journal is brought to one known shape by `LegacySchemaAdopter`**, once, and
  never again. It does only the thing no idempotent script can express - the two columns v0.20.0
  added by probe, because SQLite has no ADD COLUMN IF NOT EXISTS - and is frozen at that. Every
  database in the world therefore enters the journaled era at the same rung.
- **A migration that deletes rows bounds itself by a frozen literal.** One does:
  `0004_prune_unattributable.sql` removes fact rows older than the first record in the wire log,
  so that "every fact row belongs to a login" is a property queries can rely on. The cut is the
  literal instant, never `(SELECT MIN(ts_ms) FROM wire)` - clearing the wire log by hand is how
  space is reclaimed here, and that RAISES the minimum, so a computed cut would destroy more
  irreplaceable history every time an unrelated maintenance action ran. The script states what
  the cut costs. Filling attribution, as opposed to pruning it, is `PersonaSessionBackfill.Run`,
  which is idempotent and runs after migration because `Mucka.Store` has no game model and a DbUp
  script cannot reach the parser.
- **No `PRAGMA user_version`.** The journal is the version, and it records what actually ran rather
  than what someone asserted.

The guards are build failures, not policy: DDL outside `Migrations/` fails the build, released
scripts are SHA-256 pinned, script names must sort into execution order, the registered set must
equal the files on disk, the adopter's list is content-pinned, and a fresh file must reach the same
schema as an adopted one.

### Tables

The fact tables, and where each came from:

| Table | Came from | Notes |
|---|---|---|
| `swings` | the combat database | one row per blow, brackets kept as brackets |
| `fights` | the combat database | one row per per-NPC fight |
| `npc_stamina_reads` | the combat database | every `diagnose` reading |
| `score_events` | the combat database | every `(Persona saved on ...)` line |
| `mucka_runs` | the wire log | one row per run of the client - every login, logout and world reset inside one process |
| `persona_sessions` | added with the session key | one row per LOGIN, with its persona, host and how it ended; what every fact table above points at |
| `wire` | the wire log | one row per record: `ts_ms`, `direction`, and the bytes |

Plus the combat database's six views and the wire log's `v_mucka_run_sizes`. Everything
above is created by the baseline migration script, `Migrations/0001_baseline.sql`; later scripts in
that directory carry every change since. See `MuckaDb.ApplySchema`.

Added by later scripts:

| Table | Script | Notes |
|---|---|---|
| `some_kinds` | `0005_some_kinds.sql` | one row per species: whether MUD2 calls it "someone" or "something" when unseen; learned per install by `Mucka.Combat.SomeKindStore`, upserted on every reveal |

The seven encounter tables, which hold what the clog files used to:

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

- **`encounters` carries no stats or contents snapshot of its own.** Inlining both in the encounter
  row is the rejected alternative: it means one shape for the opening reading and a different one
  for every later reading of the same thing, and about twenty-five duplicated columns. The writer
  emits an ordinary `encounter_stats` row with `reason = 'start'` and an ordinary
  `encounter_contents` row when an encounter opens, so "the stats at encounter start" is
  `WHERE reason = 'start'` rather than a second representation.

- **`encounter_events` stores every kind, including the hit and miss kinds that `swings` also
  records.** They are different projections, not duplicates: `swings` carries the full player and
  world state at the blow and is indexed for aggregate questions, while `encounter_events` is the
  complete ordered narrative of one encounter with the server's own `raw` text on each row. Dropping
  the swing kinds would make "replay this encounter" a UNION of two differently-shaped tables. The
  duplication is 24,402 rows over a month of play.
- **Pre-roll and tail share one table**, with a `phase` column (`preroll` / `tail`) and an `ord`.
  They are the same thing - plain lines around the encounter - and the pre-roll carries no timestamp
  of its own, so `ts` is nullable on that half.
- **Room contents get a child table** rather than a delimited string. `fights.effects` sets the
  comma-separated precedent, but "which encounters had a thief in the room" is precisely the kind of
  question the merge is for, and it is a query against a child table and a string scan against a blob.
  One row per item, with `is_creature` (from the game's own C04 presence sentences) and `is_carried`
  (the FEI list's `========` split).
- **`encounter_stats` stores the seven effect booleans, not the tooltip messages.** Serializing the
  whole `StatusEffectState` carries eleven `string?` message fields
  (`MudSharp.Models.StatusEffectState`); a boolean flip is the whole of what a stats row needs.

### The encounter key

`swings`, `fights` and all seven encounter tables are keyed on `encounter_started_at_ms`, which
`MuckaConnection` derives once per encounter in `OnInCombatChanged` and passes to the fight
recorder, the swing ledger and the clog writer alike. **A writer that stamps its own
`DateTimeOffset.UtcNow` instead joins to nothing** - two clocks a few microseconds apart are two
different keys. `ClogWriter` falls back to a local reading only on the unit-test and design-time
path, the same shape `FightHistoryRecorder` uses.

Each table has its own `id INTEGER PRIMARY KEY`. Two encounters opening within one millisecond share
an `encounter_started_at_ms`, which is true of `swings` and `fights` as well and is not resolved.

### Timestamps

Everything is unix milliseconds UTC. The clog writer stamps `DateTimeOffset.UtcNow` on the Feed
thread; the wire capture stamps `DateTimeOffset.UtcNow` on the socket read and write loops
(`WireLogWriter`'s own emit path); the ledger stamps on the Feed
thread. Same clock, different threads - so a
wire record and the swing parsed out of it are comparable to within the parse, which is what any
cross-stream question needs.

## Paths, and what the store is allowed to assume

`MuckaPaths` is the single data-directory resolver: `~/.mucka` on Windows,
`FileSystem.AppDataDirectory` elsewhere, plus the database path inside it. **Nothing hardcodes a
Windows path, and nothing may start.** `MuckaDb` is the one schema file, and there is one
`DefaultFileName`.

### Android writes to app data, not to the cache

The mobile arm of `MuckaPaths.GetDataDirectory` is `FileSystem.AppDataDirectory` - the same
directory `mucka.ini` (`SettingsStore`) and the crash log (`CrashLog`) use. **Never
`FileSystem.Current.CacheDirectory`:** Android reclaims a cache directory under storage pressure
without asking and without telling the app, and "Clear cache" in the settings app empties it. That
is survivable for a hand-armed log and not for the corpus.

The consequence, recorded rather than solved: the wire log is always-on on Android too, so 0.74 MB
per play-hour is unconditional in app data, and nothing prunes it.

### The two recordings, and which is which

There are two, and confusing them is the trap this section exists to prevent.

- The **wire log** is the always-on, byte-exact record: every byte of every session into `wire`,
  nothing to arm and no setting. It is what evidence questions are answered from.
- **`rec`** is the hand-armed, human-readable one: a plain-text transcript of the screen, governed by
  `docs/session-rec-design.md`. It is lossy by design and answers to the wire log, never the reverse.

Both are current. A `-record` switch and an on-screen chip belong to `rec`; there is no opt-in
setting on the wire log and no text sink beside it.

## What an offline reader takes

One path and no decode step: `~/.mucka/mucka.db`, and the traffic query is
`SELECT ts_ms, direction, data FROM wire ORDER BY seq`. The rows are the records, so nothing has to
be unframed first, and no `ATTACH` is needed for a cross-store question. A question about classified
facts is SQL against `encounter_events`, `encounter_stats` and their siblings; a question that needs
frames goes through `MudStreamParser.Feed` / `FrameClosed` over the raw bytes, because frames come
from the parser.

`MudStreamParser` and `MudSession` are public for that reason. `WireLogExport.ListSessions` /
`ReadSession` are the seed of a CLI over the log. The `lab_` table prefix is reserved for derived
tables an offline tool writes; nothing outside the client may write to a table the client reads.
