using Microsoft.Data.Sqlite;

namespace Mucka.Core;

/// <summary>
/// The client's own combat database: ~/.mucka/combat/mucka.db, holding the per-swing stream and the
/// per-fight rollups that used to live in two append-only JSONL files.
///
/// <para><b>Why this replaced the JSONL pair.</b> SWING-LEDGER-SPEC.md section 1 chose flat files on
/// an explicit condition - "a writer that only ever appends" - and named the trigger to revisit:
/// "only if querying INSIDE the client turns out to be needed". A combat analysis view sitting
/// alongside the profile page is exactly that, and the corpus is nowhere near the scale the original
/// argument feared (hundreds of rows after months of play, not the millions a flat file would choke
/// on). Two stores would also have meant the analysis view joining SQL to a text file in app code, or
/// a second migration later; one store is one truth.</para>
///
/// <para><b>Why the raw brackets are kept unaggregated.</b> MUD2 only ever gives the player a range
/// for their own blows. Storing a midpoint would be a one-way door: a later pass that can CONSTRAIN
/// those ranges - a <c>diagnose</c> reading giving a known hitpoint band, or kill-total arithmetic
/// across a whole fight - can only narrow brackets that are still on disk as brackets. Every
/// aggregate here is therefore a view over rows that kept both ends.</para>
///
/// <para><b>Why so many columns of player/world state.</b> MUD2's creatures are not constants. Within
/// a reset they earn points and level up, so the same creature name hits harder late in a reset than
/// early; they take buffs, debuffs and drink; and weapons have per-creature effectiveness. Any
/// "this fight is going worse than usual" judgement is only as good as its baseline, and a baseline
/// that blends a fresh-reset zombie with a three-hour-old one is not a baseline. The dimensions
/// needed to slice that apart cannot be added retroactively to rows that never carried them, and they
/// are all free - already sitting on the stats snapshot the writers hold at swing time. Storage is
/// the one place where speculative columns are cheap and their absence is permanent.</para>
///
/// <para><b>Threading.</b> Writes go through a single connection owned by one background task (see
/// <see cref="SwingLedger"/>/<see cref="FightHistoryStore"/>, which keep the same "Feed thread
/// enqueues, background thread writes" discipline they always had). Reads open their own short-lived
/// connection and must never run on the UI thread (Invariant #1) - the live rail reads a warmed
/// in-memory cache, never SQL. WAL is on so a read can never block the writer or vice versa.</para>
/// </summary>
public static class CombatDb
{

    /// <summary>Standard file name. The DIRECTORY is supplied by the caller (see
    /// ClogPaths.GetCombatDirectory, which owns the platform lookup) so this type stays free of MAUI
    /// references and can be linked into mudsharp.Tests against a temp path - the same split
    /// FightHistoryStore and SwingLedger already use for their own file names.</summary>
    public const string DefaultFileName = "mucka.db";

    /// <summary>A connection string for <paramref name="path"/>. Pooling is left at its default (on),
    /// so the short-lived read connections cost an entry lookup rather than a file open.</summary>
    public static string ConnectionString(string path)
        => new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();

    /// <summary>Opens a connection, creating the file and directory if needed, and guarantees the
    /// schema is present and current. Safe to call concurrently from several threads: schema creation
    /// is idempotent (every statement is IF NOT EXISTS) and runs inside a transaction.</summary>
    public static SqliteConnection Open(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();

        // WAL: readers and the single writer never block each other, and a crash mid-write rolls back
        // to the last commit rather than leaving a torn row - strictly better than the append-only
        // text file this replaced, which could and did leave truncated final lines.
        Execute(connection, "PRAGMA journal_mode=WAL;");
        // NORMAL rather than FULL: one fsync per checkpoint instead of one per commit. A power cut can
        // lose the last transaction or two, which for a combat log is a couple of swings - the same
        // exposure the buffered text writer had, at a fraction of the I/O on the Feed thread's path.
        Execute(connection, "PRAGMA synchronous=NORMAL;");
        Execute(connection, "PRAGMA foreign_keys=ON;");
        // WAL permits many readers but still only ONE writer at a time, and this database has two
        // independent writers on two background tasks: the swing ledger commits every few seconds
        // during a fight, the fight store commits at every encounter close. Those collide. Without a
        // busy timeout the loser gets SQLITE_BUSY immediately and drops its batch - rare enough to
        // pass every test and to surface only as occasional missing rows in play, which is the worst
        // kind of bug for a dataset whose whole value is being continuous. Five seconds is far longer
        // than any commit here takes.
        Execute(connection, "PRAGMA busy_timeout=5000;");

        ApplySchema(connection);
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Creates every table, index and view if absent. Idempotent by construction (every statement is
    /// IF NOT EXISTS) and cheap enough to run on every open.
    ///
    /// <para><b>There is still no version-gated migration mechanism, deliberately.</b> A
    /// <c>user_version</c> gate would only skip these statements, not perform an ALTER, so it could
    /// never actually migrate anything - it would be a version number that looked like a plan.</para>
    ///
    /// <para><b>What replaced "delete the database file".</b> That instruction was cheap while the
    /// file was a convenience. It is not any more: the corpus in it is months of play and is the only
    /// evidence behind every stamina-pool figure the client shows, so a schema change may not cost it.
    /// <see cref="AddMissingColumns"/> below is the whole migration story - additive columns only,
    /// applied by reading the table's own shape rather than by trusting a stored version. A change
    /// that CANNOT be expressed as an added column (a renamed or retyped column, a new NOT NULL) still
    /// needs a real plan, and there is deliberately no machinery here pretending otherwise.</para>
    /// </summary>
    public static void ApplySchema(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = SchemaSql;
            command.ExecuteNonQuery();
        }
        AddMissingColumns(connection, transaction);
        transaction.Commit();
    }

    /// <summary>
    /// Columns added to a table after rows already existed in it, as (table, column, declaration).
    /// Every entry must also appear in <see cref="SchemaSql"/> - this list is what brings an OLD file
    /// up to that definition, not a second definition of its own.
    /// </summary>
    private static readonly (string Table, string Column, string Declaration)[] AddedColumns =
    [
        ("fights", "prev_same_name_ended_ms", "INTEGER"),
        ("score_events", "after_task_line", "INTEGER"),
    ];

    /// <summary>
    /// Adds any column in <see cref="AddedColumns"/> that the file does not already have. Idempotent:
    /// the existing shape is read from <c>pragma_table_info</c> first, so a current file does no work
    /// and an ALTER can never run twice. Nullable-only by construction (SQLite cannot add a NOT NULL
    /// column without a default), which is also the honest value for a fact nobody was recording when
    /// the older rows were written.
    /// </summary>
    private static void AddMissingColumns(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (var (table, column, declaration) in AddedColumns)
        {
            using var probe = connection.CreateCommand();
            probe.Transaction = transaction;
            probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;";
            probe.Parameters.AddWithValue("$table", table);
            probe.Parameters.AddWithValue("$column", column);
            if (Convert.ToInt64(probe.ExecuteScalar()) != 0)
                continue;

            using var alter = connection.CreateCommand();
            alter.Transaction = transaction;
            // Identifiers cannot be parameterised, and these three strings are compile-time literals
            // from AddedColumns - never anything a caller or the game supplies.
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration};";
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// The whole schema.
    ///
    /// <para>Column names deliberately echo tools/combat/schema.sql where the same fact already has a
    /// name there (npc_name, npc_group, weapon_used, approx_damage_done...), so a query written
    /// against the offline reducer's database mostly transfers, and the two halves of the pipeline
    /// stay legible as one system.</para>
    ///
    /// <para>Nothing is declared NOT NULL beyond the identity columns. Every stat here can genuinely be
    /// unknown - a swing landing before the first FES heartbeat has no strength reading, and recording
    /// a 0 for it would be a fabricated measurement. The same "never render unknown as zero" rule the
    /// rail obeys applies at the storage layer, where it matters more: a zero on disk outlives the
    /// session that invented it.</para>
    /// </summary>
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS swings (
            id                  INTEGER PRIMARY KEY,
            ts                  INTEGER NOT NULL,   -- unix ms, the tracker's own feed-thread stamp
            dir                 TEXT    NOT NULL,   -- 'out' (player swinging) | 'in' (creature swinging)
            encounter_started_at_ms INTEGER,        -- joins to fights.encounter_started_at_ms

            persona             TEXT,
            sex                 TEXT,

            -- Player state at the swing. str/dex are EFFECTIVE (what the hit-chance and damage
            -- formulas consume); the raw and max values ride along so the gap between them - which is
            -- what load and afflictions cost - is recoverable without a second source.
            sta                 INTEGER,
            sta_before          INTEGER,            -- dir=in hits only: stamina immediately before the blow
            sta_max             INTEGER,
            str                 INTEGER,
            str_raw             INTEGER,
            str_max             INTEGER,
            dex                 INTEGER,
            dex_raw             INTEGER,
            dex_max             INTEGER,
            level               INTEGER,
            score               INTEGER,
            objects_carried     INTEGER,
            weather             TEXT,

            -- Afflictions (FES-authoritative) and the independent buff/debuff slots. Stored as
            -- separate columns rather than a packed bitfield or a JSON blob: these are the exact
            -- dimensions a "why is this fight going badly" query groups by, and a query should not
            -- have to unpack anything to ask.
            blind               INTEGER,
            deaf                INTEGER,
            crippled            INTEGER,
            dumb                INTEGER,
            str_buff            INTEGER,
            str_debuff          INTEGER,
            dex_buff            INTEGER,
            dex_debuff          INTEGER,
            sta_buff            INTEGER,
            sta_debuff          INTEGER,
            glow                INTEGER,

            -- Reset context. MUD2 creatures earn points and level up WITHIN a reset, so the same name
            -- is a different opponent at different points in the cycle. time_to_reset is the reading
            -- as the game gave it, in MINUTES (FES field [13]); reset_epoch_ms is ts + ttr*60000, an
            -- ESTIMATE of the instant this reset ends. It is NOT constant across a reset: the reading
            -- is whole minutes, so successive swings land anywhere in a 60s-wide bucket around the
            -- true instant. Group on it BUCKETED (+/-30s, ResetClock's MinuteUncertaintySec) - raw
            -- equality splits one reset into many.
            time_to_reset       INTEGER,
            reset_epoch_ms      INTEGER,

            npc                 TEXT,               -- instance name as the game gave it ("rat0")
            npc_group           TEXT,               -- NpcGroups.Normalize, matching reduce_combat.py
            npc_weapon          TEXT,               -- the creature's own, which it arms independently
            rung                INTEGER,            -- creature health 1-7 BEFORE this swing
            rung_phrase         TEXT,

            weapon              TEXT,               -- dir=out: what the player had in hand
            hit                 INTEGER NOT NULL,
            -- dir=out hits only. Both ends, never a midpoint: a later constraint pass (a diagnose
            -- reading, kill-total arithmetic) can only narrow a bracket that is still a bracket.
            dmg_low             INTEGER,
            dmg_high            INTEGER,
            -- dir=in hits only: EXACT, from the stamina delta.
            dmg                 INTEGER
        );

        CREATE INDEX IF NOT EXISTS ix_swings_group_dir ON swings(npc_group, dir);
        CREATE INDEX IF NOT EXISTS ix_swings_npc_dir   ON swings(npc, dir);
        CREATE INDEX IF NOT EXISTS ix_swings_ts        ON swings(ts);
        CREATE INDEX IF NOT EXISTS ix_swings_encounter ON swings(encounter_started_at_ms);
        CREATE INDEX IF NOT EXISTS ix_swings_reset     ON swings(reset_epoch_ms);

        -- One row per per-NPC fight, as FightHistoryRecorder closes them. Column names follow
        -- tools/combat/schema.sql's live_fights table, which was ingested from the JSONL this
        -- replaces.
        CREATE TABLE IF NOT EXISTS fights (
            id                  INTEGER PRIMARY KEY,
            character_name      TEXT,
            encounter_started_at_ms INTEGER,
            started_at_ms       INTEGER NOT NULL,
            ended_at_ms         INTEGER NOT NULL,
            duration_ms         INTEGER NOT NULL,

            npc_name            TEXT NOT NULL,
            npc_group           TEXT NOT NULL,
            weapon_used         TEXT,
            outcome             TEXT NOT NULL,

            you_hits            INTEGER NOT NULL,
            you_misses          INTEGER NOT NULL,
            they_hits           INTEGER NOT NULL,
            they_misses         INTEGER NOT NULL,
            approx_damage_done  REAL    NOT NULL,
            approx_damage_taken REAL    NOT NULL,
            narrative_mode      INTEGER NOT NULL,

            room                TEXT,
            weather             TEXT,
            strength            INTEGER,
            raw_strength        INTEGER,
            dexterity           INTEGER,
            raw_dexterity       INTEGER,
            stamina_at_start    INTEGER,
            max_stamina         INTEGER,
            min_stamina         INTEGER,
            stamina_at_end      INTEGER,
            score_at_start      INTEGER,
            score_at_end        INTEGER,
            objects_carried     INTEGER,
            level               INTEGER,
            is_blind            INTEGER NOT NULL,
            is_deaf             INTEGER NOT NULL,
            is_crippled         INTEGER NOT NULL,
            is_dumb             INTEGER NOT NULL,
            effects             TEXT NOT NULL,       -- comma-separated, as FightRecord.Effects

            -- When the previous engagement against this same instance name ended, if the recorder saw
            -- one this session. See FightRecord.PrevSameNameEndedMs: MUD2 opens a fresh engagement
            -- every time a creature tries to flee, so the row holding the killing blow is routinely
            -- the last of several and holds only that blow. Without this column the table cannot tell
            -- a terminal link from a genuine one-hit kill.
            prev_same_name_ended_ms INTEGER
        );

        CREATE INDEX IF NOT EXISTS ix_fights_npc       ON fights(npc_name);
        CREATE INDEX IF NOT EXISTS ix_fights_group     ON fights(npc_group);
        CREATE INDEX IF NOT EXISTS ix_fights_weapon    ON fights(weapon_used);
        CREATE INDEX IF NOT EXISTS ix_fights_encounter ON fights(encounter_started_at_ms);

        -- Every `diagnose` reading the stethoscope has ever produced: "The water-snake5 has a stamina
        -- lying between 90 and 99."
        --
        -- This is the ONLY direct measurement of NPC stamina MUD2 gives, and until now the client
        -- parsed it and threw it away - four observations exist in the whole corpus and none of them
        -- was written down. It is the strongest constraint the remaining-stamina model has (see
        -- MudSharp.Combat.NpcRemainingStamina), and it is also the only way to CHECK a published
        -- creature stamina against the live game.
        --
        -- printed_low/printed_high are EXACTLY the two numbers in the sentence. Whether the bracket is
        -- aligned to tens, to a tenth of the pool, or to something else is unresolved at n=4, so
        -- nothing rounds or snaps them; the raw line rides along so a later pass can re-read the
        -- wording rather than trust this table's parse.
        CREATE TABLE IF NOT EXISTS npc_stamina_reads (
            id                  INTEGER PRIMARY KEY,
            ts                  INTEGER NOT NULL,   -- unix ms, the tracker's own feed-thread stamp
            encounter_started_at_ms INTEGER,        -- null: diagnosing something before fighting it is the point
            persona             TEXT,
            npc                 TEXT NOT NULL,      -- instance name as the game gave it
            npc_group           TEXT NOT NULL,      -- NpcGroups.Normalize, matching the other tables
            pool_key            TEXT NOT NULL,      -- NpcPoolKey.For - species AND size, which npc_group drops
            printed_low         INTEGER NOT NULL,
            printed_high        INTEGER NOT NULL,
            raw_text            TEXT
        );

        -- Every "(Persona saved on +38 = 19,214)." line: MUD2 stating, explicitly, that the score
        -- changed and by how much. The only place the game says what an event was WORTH - a kill's
        -- award and a flee's cost (-102, -872) both arrive here and nowhere else.
        --
        -- An EVENT table rather than a column on swings, because score is not a per-swing quantity:
        -- one blow can finish several creatures and the game scores them one line at a time. Nothing
        -- here attributes a row to a creature, deliberately. The timestamp and the encounter key are
        -- the context an analysis pass needs to do that against the swing stream, and doing it here -
        -- on the feed thread, with only the current line in hand - would be guessing.
        CREATE TABLE IF NOT EXISTS score_events (
            id                  INTEGER PRIMARY KEY,
            ts                  INTEGER NOT NULL,   -- unix ms, the ledger's own feed-thread stamp
            -- The encounter this line landed in, or - see encounter_open - the one that had just
            -- closed. A kill's award is printed on the line AFTER the kill, and the kill line is what
            -- closes the encounter, so the award for the last creature in a room ALWAYS arrives with
            -- no encounter open. Recording only the open key would leave the most interesting rows in
            -- the table unattributable.
            encounter_started_at_ms INTEGER,
            encounter_open      INTEGER NOT NULL,   -- 1: still fighting. 0: the key above is the encounter just ended
            persona             TEXT,
            -- Signed, exactly as printed. NULL - never 0 - for the 60 total-only forms, which are the
            -- reset and shell-exit saves rather than a scoring event.
            delta               INTEGER,
            total               INTEGER NOT NULL,   -- authoritative; what the game says the score IS now
            -- 1 when "You have completed a Task." was printed immediately before this line, with no
            -- score line between. One of the eight tasks discharged BY A KILL pays out on its own line
            -- FIRST and the creature's award second, so without this mark the two rises in that frame
            -- are indistinguishable and the task's payout reads as the kill's value. Ordering fixed by
            -- three observations - see MudSharp.Models.TaskCompletion. Nullable: rows written before
            -- the mark existed did not record it, and 0 would claim they did.
            after_task_line     INTEGER,
            raw_text            TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_score_events_ts        ON score_events(ts);
        CREATE INDEX IF NOT EXISTS ix_score_events_encounter ON score_events(encounter_started_at_ms);

        CREATE INDEX IF NOT EXISTS ix_stamina_reads_npc  ON npc_stamina_reads(npc);
        CREATE INDEX IF NOT EXISTS ix_stamina_reads_pool ON npc_stamina_reads(pool_key);
        CREATE INDEX IF NOT EXISTS ix_stamina_reads_ts   ON npc_stamina_reads(ts);

        -- How hard each creature hits, which is what the rail's per-opponent column reads (warmed into
        -- memory once per encounter - never queried per frame or per swing). Incoming only: dmg is
        -- exact on that side.
        CREATE VIEW IF NOT EXISTS v_incoming_by_npc AS
        SELECT npc AS name, COUNT(*) AS samples, MAX(dmg) AS max_dmg, SUM(dmg) AS sum_dmg
        FROM swings WHERE dir = 'in' AND hit = 1 AND dmg IS NOT NULL AND npc IS NOT NULL
        GROUP BY npc;

        CREATE VIEW IF NOT EXISTS v_incoming_by_group AS
        SELECT npc_group AS name, COUNT(*) AS samples, MAX(dmg) AS max_dmg, SUM(dmg) AS sum_dmg
        FROM swings WHERE dir = 'in' AND hit = 1 AND dmg IS NOT NULL AND npc_group IS NOT NULL
        GROUP BY npc_group;

        -- The same question again, but split by the weapon the CREATURE was holding. A creature arms
        -- itself independently of the player and that materially changes its output (see
        -- SwingRow.NpcWeapon), so "how hard does an ogre hit" and "how hard does an ogre with a great
        -- club hit" are different questions and the second is the one worth fleeing on.
        --
        -- Keyed on group and weapon joined by a tab, which cannot occur inside a MUD2 object name.
        -- The key's SHAPE must match SwingDamageIndex.GroupWeaponKey exactly - see its remarks; the
        -- two halves of this cache are written by the live fold and by this query, and a silent
        -- disagreement would show up only as history that resets on every restart.
        --
        -- rtrim(x, '0123456789') strips the object's INSTANCE digits, so every axe in the world folds
        -- into "axe" rather than becoming its own bucket that never reaches three samples. lower() is
        -- not cosmetic either: GROUP BY here is BINARY-collated where the C# dictionary is
        -- OrdinalIgnoreCase, so without it two case-differing rows arrive separately and the second
        -- OVERWRITES the first instead of merging.
        CREATE VIEW IF NOT EXISTS v_incoming_by_group_weapon AS
        SELECT npc_group || char(9) || lower(rtrim(npc_weapon, '0123456789')) AS name,
               COUNT(*) AS samples, MAX(dmg) AS max_dmg, SUM(dmg) AS sum_dmg
        FROM swings
        WHERE dir = 'in' AND hit = 1 AND dmg IS NOT NULL
          AND npc_group IS NOT NULL AND npc_weapon IS NOT NULL
        GROUP BY npc_group, lower(rtrim(npc_weapon, '0123456789'));

        -- The player's own output, kept as brackets throughout. Both ends are summed separately so a
        -- consumer can report "averages 12-16" rather than being handed a midpoint someone else chose.
        CREATE VIEW IF NOT EXISTS v_outgoing_by_npc AS
        SELECT npc AS name, COUNT(*) AS samples,
               SUM(dmg_low) AS sum_low, SUM(dmg_high) AS sum_high, MAX(dmg_high) AS max_high
        FROM swings WHERE dir = 'out' AND hit = 1 AND dmg_low IS NOT NULL AND npc IS NOT NULL
        GROUP BY npc;

        CREATE VIEW IF NOT EXISTS v_outgoing_by_group AS
        SELECT npc_group AS name, COUNT(*) AS samples,
               SUM(dmg_low) AS sum_low, SUM(dmg_high) AS sum_high, MAX(dmg_high) AS max_high
        FROM swings WHERE dir = 'out' AND hit = 1 AND dmg_low IS NOT NULL AND npc_group IS NOT NULL
        GROUP BY npc_group;

        -- Hit rate by creature group and weapon, both directions - the per-creature weapon
        -- effectiveness question, which no rollup could answer because the individual swings were
        -- never kept.
        CREATE VIEW IF NOT EXISTS v_swing_rate_by_weapon AS
        SELECT npc_group, weapon, dir,
               COUNT(*) AS swings,
               SUM(hit) AS hits,
               CAST(SUM(hit) AS REAL) / COUNT(*) AS hit_rate
        FROM swings
        GROUP BY npc_group, weapon, dir;
        """;
}
