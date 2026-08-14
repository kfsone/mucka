# Per-swing ledger - spec

A row per swing, both directions, so every question about "how hard does this thing hit, how often
do I land, and under what conditions" becomes a query instead of a memory.

**Status: specified, not built.**

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

**b) The live spark-graph must not read the ledger.** Raw swing rows accumulate at roughly one per
tick per participant - a long session is thousands, a year is millions. Loading that to draw a
histogram beside a rat is the wrong shape. The client keeps a small **aggregate** index instead
(section 5); the raw ledger is for offline mining and for rebuilding those aggregates from scratch.

## 2. Files

| path | writer | contents |
|---|---|---|
| `~/.mucka/clogs/swings.jsonl` | client, append-only | one row per swing, both directions |
| `~/.mucka/clogs/swing-index.json` | client, rewritten periodically | per-(group, weapon) aggregates for the live panel |
| `~/.mucka/combat/combat.db` | offline reducer | `swings` table + summary views, ingested from the above |

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

Notes on specific fields:

- **`gender`** is not currently obtainable for an existing character. The only place the client knows
  it is `GuidedLoginController.ConfirmCreateSex`, i.e. characters created through this client.
  Ship the field nullable, populate it if a `score` parse turns out to carry it, and do not block on
  it.
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
