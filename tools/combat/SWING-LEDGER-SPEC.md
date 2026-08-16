# Per-swing ledger - spec

A row per swing, both directions, so every question about "how hard does this thing hit, how often
do I land, and under what conditions" becomes a query instead of a memory.

**Status: built, and section 1's premise has since been overturned.** `Core/SwingLedger.cs` writes
the swings; `Core/CombatDb.cs` owns the schema; `mudsharp/Combat/SwingDamageIndex.cs` is the
in-memory cache the rail reads.

The client now uses **SQLite**, not JSONL - see section 1, which conditioned the flat-file choice on
"a writer that only ever appends" and named its own revisit trigger ("only if querying *inside* the
client turns out to be needed"). A combat analysis view is that trigger.

The existing `swings.jsonl` / `fights.jsonl` corpus was migrated into `mucka.db` once, by hand, and
the originals left on disk as `*.imported`. **There is no importer in the app**, and no schema
migration mechanism either - this database exists on one machine, so back-compat machinery would be
maintenance with nothing to maintain. To change the schema: edit `CombatDb.SchemaSql` and delete the
file. The day it exists on two machines, that stops being true.

Other deviations, marked in place below: the row gained `sta_before` and a large set of
player/world/reset columns (section 3), outgoing damage IS profiled (section 5), and there is no
`swing-index.json` (section 5).

---

## 1. Two corrections to the premise

**a) The client has no SQLite.** `~/.mucka/combat/combat.db` exists, but it is written only by the
offline reducer (`tools/combat/reduce_combat.py`) from raw captures. The client's package references
are MAUI, Logging.Debug and SkiaSharp - nothing else. `FightHistoryStore` records the reasoning for
that in its own remarks: *"A flat JSONL file rather than SQLite in the client: no new dependency, no
MAUI/Android packaging question"*.

So "add rows to the existing sqlite file" would mean taking `Microsoft.Data.Sqlite` (plus its native
library) into the app, on both Windows and Android, for a writer that only ever appends.

**Recommendation: keep the split.** The client appends JSONL; the offline tool ingests it into the
same `combat.db` alongside everything else. Nothing is lost - the audit trail is complete either way,
the SQL is available where the analysis already lives, and the live panel does not need SQL at all
(see section 5). Revisit only if querying *inside* the client turns out to be needed.

> **Revisited, and reversed (2026-08-15).** The trigger this paragraph names has fired: a combat
> analysis view, sitting at the same level as the profile page, needs to query inside the client. The
> corpus argument also did not survive contact - the flat-file case assumed a scale ("millions of
> rows") that months of play get nowhere near.
>
> The dependency cost turned out to be smaller than feared, too. `Microsoft.Data.Sqlite` pulls
> `SQLitePCLRaw.bundle_e_sqlite3`, and this project's Android Release config already restricts
> `RuntimeIdentifiers` to `android-arm64` - so that is *one* `libe_sqlite3.so`, not one per ABI. It is
> trim-safe, so `PublishTrimmed` stays on.
>
> Two things the swap bought beyond queryability: WAL rolls back a torn write, where the append-only
> text file left truncated final lines that every reader had to tolerate; and putting swings and
> fights in one file means the analysis view can join a swing to the fight it belonged to, rather than
> joining SQL to a text file in app code.

**b) The live spark-graph must not read the ledger.** Raw swing rows accumulate at roughly one per
tick per participant - a long session is thousands, a year is millions. Loading that to draw a
histogram beside a rat is the wrong shape. The client keeps a small **aggregate** index instead
(section 5); the raw ledger is for offline mining and for rebuilding those aggregates from scratch.

## 2. Files

| path | writer | contents |
|---|---|---|
| `~/.mucka/combat/mucka.db` | client | `swings` + `fights` tables and their views - see `Core/CombatDb.cs` |
| ~~`~/.mucka/clogs/swings.jsonl`~~ | retired | migrated into `mucka.db` once, by hand; left on disk as `*.imported` |
| ~~`~/.mucka/clogs/fights.jsonl`~~ | retired | same - one store, so the analysis view can join a swing to its fight |
| ~~`~/.mucka/clogs/swing-index.json`~~ | *never built* | see section 5 - the aggregate is a query, not a file |
| `~/.mucka/combat/combat.db` | offline reducer | unchanged; still built from raw captures by `reduce_combat.py` |

**Follow-up:** `ingest_clogs.py` still reads `fights.jsonl`, which the client no longer writes. It
needs pointing at `mucka.db` (or retiring, since the client now produces the same rows in SQL
directly). Nothing breaks meanwhile - it simply has no new input.

Same directory and the same append-only discipline as `fights.jsonl`, which already works and is
already flushed on the Feed thread without touching the UI.

## 3. Row shape

One record type with a `dir` discriminator, rather than two files - they share almost every field and
a single ordered stream is what makes "what was happening around this swing" answerable.

```jsonc
{
  "v": 1,
  "ts": 1786400791230,          // unix ms, tracker's own feed-thread stamp
  "dir": "out" | "in",          // player swinging, or being swung at
  "persona": "Ollie",
  "gender": "m",                // see note below - may be null
  "sta": 81, "str": 94, "dex": 99,   // EFFECTIVE, from the FES heartbeat nearest this swing
  "sta_before": 88,             // v2. dir=in hits only: stamina immediately BEFORE the blow
  "sta_max": 105,
  "blind": false,
  "npc": "rat0",                // instance name as the game gave it
  "group": "rats",              // NpcGroups.Normalize - "dragon" normalises to itself
  "weapon": "axe0",             // dir=out: the player's. dir=in: the creature's, null when unarmed
  "hit": true,
  "dmg_low": 15, "dmg_high": 19,     // dir=out only: the game's bracket
  "dmg": 7,                          // dir=in only: EXACT, from the (cur/max) delta
  "rung": 4,                    // the creature's health rung BEFORE this swing, 1-7, null if unknown
  "rung_phrase": "covered in wounds"
}
```

> **As built, this row is much wider.** The shape above is the minimum. The `swings` table also
> carries raw and max strength/dexterity, level, score, objects carried, weather, the deaf/crippled/
> dumb afflictions, the seven independent buff/debuff flags, the creature's own weapon as its own
> column, the encounter id (joining to `fights`), and `time_to_reset` / `reset_epoch_ms`.
>
> That last pair is the important one. **MUD2 creatures earn points and level up within a reset**, so
> the same creature name is a materially different opponent early and late in the cycle; they are also
> subject to buffs, debuffs and drink. A lifetime average blends all of it. Any "this fight is going
> downhill faster than usual" judgement is a comparison against a baseline, and a baseline is only as
> good as the dimensions it can be sliced by - dimensions that cannot be added to rows that already
> happened. All of these were already on the stats snapshot the ledger holds at swing time, so the
> cost was a column each; the cost of omitting one would have been permanent.

Notes on specific fields:

- **`sta_before`** (added in v2, and the reason the row version moved) is stored rather than left to
  be reconstructed as `sta + dmg`. The two agree whenever both are present - but when no stamina
  baseline was available, `dmg` is null and the pre-hit figure is unrecoverable, so a consumer doing
  the arithmetic would silently produce nothing for exactly the rows where the question mattered,
  with no way to tell that apart from an honest zero. Storing the baseline that was actually used
  also records what the client *believed* when it attributed the damage, which is the thing worth
  auditing when a delta looks wrong. v1 rows simply lack the field; nothing migrates.
- ~~**`gender`** is not currently obtainable for an existing character.~~ **Stale.**
  `GameStatsSnapshot.Sex` parses it straight off the `score` sheet, so the column (`sex`) is
  populated. This note predates that parser.
- **`str` / `dex` are effective, not raw**, and that is the point - they are what the hit-chance and
  damage formulas actually consume, and they move with stamina (below 40 and below 30 respectively)
  and with what is being carried. Recording raw values would throw away the variable under test.
- **`rung`** uses `NpcHealthRungs`, which already normalises all three vocabularies onto one 1-7
  scale - the banshee's `fading rapidly` and a zombie's `close to expiry` both land on rung 1, and an
  unseen vocabulary still resolves by its severity word. Nothing extra is needed for ethereal
  creatures; that problem is already solved.
- **`dmg` is exact for incoming swings** (MUD2 prints post-hit stamina) and **a bracket for outgoing**
  (the game only gives a range). Do not average the bracket into a single number in the ledger -
  store both ends and let the consumer decide.

## 4. What this makes answerable

Directly, with one `GROUP BY`:

- Hit rate against effective dexterity, per creature - the live test of `Dy / (Dy + Do)`.
- Damage per weapon against a creature group, which is the hidden per-creature weapon modifier.
- Whether the stamina knees show up in *outcomes* and not just in the stat readout.
- How hard a given creature actually hits, and how that varies with its health rung.
- Whether a wounded creature hits softer - `MECHANICS-VERIFICATION.md` infers it from 15 rat blows;
  this would settle it.

## 5. The aggregate index and the spark-graph

> **As built.** `SwingDamageIndex` is an in-memory cache, warmed from the database's own `GROUP BY`
> views, not a file. It keeps **both** directions:
>
> - incoming as `(samples, max, sum)` - MUD2 prints the player an exact stamina figure for every blow
>   they take, so a mean is a mean;
> - outgoing as `(samples, sum_low, sum_high, max_high)` - the game only ever reports a **range** for
>   the player's own blows, so the average comes out as a range too. Nothing here collapses a bracket
>   to a midpoint, at any layer. That is a one-way door: narrowing these ranges later (a `diagnose`
>   reading giving a known hitpoint band, kill-total arithmetic across a fight) only works on ranges
>   that are still ranges.
>
> An earlier revision of this note said outgoing damage should not be profiled at all. That reasoning
> was about DISPLAY - not putting a derived midpoint beside a measured figure at equal weight on the
> rail - and it had no business deciding what gets stored or aggregated. The rail still shows only the
> incoming pair; the outgoing profile feeds the exchange bars and the analysis view.
>
> **There is no `swing-index.json`.** The aggregation is a query. The argument below - that the raw
> ledger must never be read to answer a live question - still holds, and SQL is how it is honoured:
> the warm-up cost is proportional to the number of distinct creatures, not to the number of swings,
> so the corpus grows without startup growing with it. That is the property the index file was trying
> to buy, without a second artifact that can disagree with the first.
>
> **The live encounter is excluded from its own baseline.** Blows are buffered and folded in only when
> the encounter closes - the swing-level twin of the guarantee `HistoryIndex`/`CombatHistoryCache`
> already establish for fights, and established the same way: by construction, not by filtering.
> Without it a creature's first-ever fight would show its own swings back as its history at n=1.
>
> **The cache is the cheap answer, not the last word.** It blends every reset, level and effect state
> together. Anything doing real risk assessment should slice the `swings` table on those columns
> instead - which is the entire reason they are stored.

Per `(group, weapon, dir)` keep: `count`, `hits`, `sum_dmg`, `sum_low`, `sum_high`, and a
**damage histogram** in buckets of 5: `bucket = min(dmg / 5, 6)`, so `0` = 1-4, `1` = 5-9, ...
`5` = 25-29, `6` = 30+. Seven buckets is enough for the observed range and keeps the sparkline
readable at rail width.

Drawn beside each opponent as a tiny bar chart of *how hard this kind of thing hits*, normalised to
its own maximum. Rules it must obey, all inherited from the rest of the rail:

- **Never render "unknown" as "zero".** With no samples, draw nothing - not an empty chart.
- Below a small sample floor it must read as provisional; a single 20-point hit is not a
  distribution.
- It is a static drawing that changes only on new data - no animation, no per-frame work.
- Fixed reserved width, like everything else in a slot.

Aggregates are updated in memory per swing and the index file rewritten on a low cadence (encounter
end is the natural point - `FightHistoryRecorder` already flushes there). Rebuildable from
`swings.jsonl` at any time, so a corrupt or deleted index costs nothing but a rescan.

## 6. Constraints

- **Feed thread only**, never the UI thread (Invariant #1). Append and forget, exactly as
  `ClogWriter` and `FightHistoryStore` already do.
- **Always on**, like `fights.jsonl` and unlike clogs - this is the dataset everything else depends
  on, and a switch means missing data precisely when something interesting happened.
- **Never blocks or throws on the caller.** A failed write loses a row; it must not lose a fight.
- One row is ~200 bytes. A busy hour is a few thousand rows, under a megabyte. Rotation is not needed
  yet, but the reader must tolerate a truncated final line (a crash mid-append).
