using Microsoft.Data.Sqlite;

namespace Mucka.Store;

/// <summary>
/// The client's one database: <c>~/.mucka/mucka.db</c>. Combat corpus, raw wire log and
/// per-encounter combat logs, in one file. See <c>docs/persistence-design.md</c>, which governs this.
///
/// <para><b>Threading.</b> Writes go through a single connection owned by one background task - see
/// <see cref="MuckaStore"/>. Reads open their own short-lived connection and must never run on the UI
/// thread (Invariant #1); the live combat rail reads a warmed in-memory cache, never SQL. WAL is on so
/// a read can never block the writer or vice versa.</para>
///
/// <para><b>Migration policy: additive columns only.</b> <see cref="AddedColumns"/> is the whole of
/// it - a column is brought onto an old file by reading the table's own shape, never by trusting a
/// stored version. A change that cannot be expressed as a nullable added column is not made by the
/// code: the operator clears the affected table by hand and the next open recreates it.</para>
/// </summary>
public static class MuckaDb
{
    /// <summary>Standard file name. The DIRECTORY is supplied by the caller (see Core/MuckaPaths.cs,
    /// which owns the platform lookup) so this type stays free of MAUI references and can be tested
    /// against a temp path.</summary>
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
        // text files this replaced, which could and did leave truncated final lines.
        Execute(connection, "PRAGMA journal_mode=WAL;");
        // NORMAL rather than FULL: one fsync per checkpoint instead of one per commit. A power cut can
        // lose the last transaction or two, which is a couple of swings and the open wire batch.
        Execute(connection, "PRAGMA synchronous=NORMAL;");
        Execute(connection, "PRAGMA foreign_keys=ON;");
        // There is one client writer, so this no longer arbitrates between writers. It covers a
        // READER overlapping the writer - a warm-up query, or an offline tool - and a second instance
        // of the client. Five seconds is far longer than any commit here takes.
        Execute(connection, "PRAGMA busy_timeout=5000;");

        ApplySchema(connection);
        return connection;
    }

    /// <summary>Opens read-only. Throws if the file does not exist.</summary>
    public static SqliteConnection OpenRead(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Creates every table, index and view if absent, then brings an older file up to the
    /// current column set. Idempotent by construction and cheap enough to run on every open.</summary>
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
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = PostColumnSql;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>
    /// Schema that can only run once <see cref="AddMissingColumns"/> has been: an index over a column
    /// added to an existing file, and the retirement of the index it replaces.
    ///
    /// <para>It is a separate step because the ordering is load-bearing rather than tidy. Creating an
    /// index on a column an old file does not have yet throws, inside the schema transaction, so the
    /// store fails to open at all - Mucka would not start against the operator's own database.</para>
    /// </summary>
    private const string PostColumnSql = """
        -- A NEW name, not the old ix_swings_reset. IF NOT EXISTS matches on the NAME, so reusing it
        -- would leave an existing file indexed on the dead reset_epoch_ms and the live column with
        -- no index at all - silently, since nothing errors.
        CREATE INDEX IF NOT EXISTS ix_swings_reset_landed ON swings(reset_landed_at_ms);
        -- The index that keyed the world on ts + ttr. Dropped rather than left: it is the one piece
        -- of the old scheme that costs something to keep, since every swing insert maintains it.
        DROP INDEX IF EXISTS ix_swings_reset;
        """;

    /// <summary>
    /// Columns added to a table after rows already existed in it, as (table, column, declaration).
    /// Every entry must also appear in <see cref="SchemaSql"/> - this list is what brings an OLD file
    /// up to that definition, not a second definition of its own.
    /// </summary>
    private static readonly (string Table, string Column, string Declaration)[] AddedColumns =
    [
        ("fights", "prev_same_name_ended_ms", "INTEGER"),
        ("score_events", "after_task_line", "INTEGER"),
        // Replaced reset_epoch_ms, which was ts + ttr*60000 and therefore keyed rows to a quantity
        // wizards can move. The dead column is left on existing files rather than dropped: it is
        // nullable, nothing reads it, and dropping it would rewrite a 26k-row table to no purpose.
        ("swings", "reset_landed_at_ms", "INTEGER"),
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
    /// The whole schema, in three groups: the combat corpus, the raw wire log, and the per-encounter
    /// logs. Everything joins on <c>encounter_started_at_ms</c>, which one place derives
    /// (MuckaConnection) and hands to every writer.
    ///
    /// <para>Nothing is declared NOT NULL beyond identity columns and facts that cannot be absent.
    /// Every stat here can genuinely be unknown - a swing landing before the first FES heartbeat has
    /// no strength reading, and recording a 0 for it would be a fabricated measurement. A zero on disk
    /// outlives the session that invented it.</para>
    /// </summary>
    private const string SchemaSql = """
        -- ============================================================== combat corpus ==

        CREATE TABLE IF NOT EXISTS swings (
            id                  INTEGER PRIMARY KEY,
            ts                  INTEGER NOT NULL,   -- unix ms, the tracker's own feed-thread stamp
            dir                 TEXT    NOT NULL,   -- 'out' (player swinging) | 'in' (creature swinging)
            encounter_started_at_ms INTEGER,        -- joins to fights and the encounter tables

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

            -- Afflictions (FES-authoritative) and the independent buff/debuff slots. Separate columns
            -- rather than a packed bitfield or a JSON blob: these are the exact dimensions a "why is
            -- this fight going badly" query groups by, and a query should not have to unpack anything.
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

            -- Reset context. A reset is the server TERMINATING and reloading the world from scratch:
            -- every creature and object is destroyed and remade, so "rat16" either side of one is two
            -- different animals. Creatures also level WITHIN a reset by scoring, uncapped, so the same
            -- instance is a different opponent early and late. Both make this the grouping key.
            --
            -- time_to_reset is the countdown as the game gave it, in MINUTES (FES field [13]) - a
            -- reading, kept raw. reset_landed_at_ms is the client's clock at the last C06 C06 landing
            -- it watched, and is the only one of the two that is an IDENTITY.
            --
            -- Never derive a key from the countdown. Wizards delay and accelerate resets, so ts+ttr
            -- moves under you: the column that did this produced 118 distinct values inside one
            -- 113-second encounter, and the countdown was seen RISING 38 times inside ten minutes, by
            -- up to 105 minutes. Nor is session_id a substitute - one session has held three landings
            -- 107 minutes apart, so it MERGES worlds.
            time_to_reset       INTEGER,
            reset_landed_at_ms  INTEGER,

            npc                 TEXT,               -- instance name as the game gave it ("rat0")
            npc_group           TEXT,               -- NpcGroups.Normalize
            npc_weapon          TEXT,               -- the creature's own, which it arms independently
            rung                INTEGER,            -- creature health 1-7 BEFORE this swing
            rung_phrase         TEXT,

            weapon              TEXT,               -- dir=out: what the player had in hand
            hit                 INTEGER NOT NULL,
            -- dir=out hits only. MUD2 only ever gives the player a RANGE for their own blows. Both
            -- ends are kept, never a midpoint: a later pass that can CONSTRAIN those ranges - a
            -- `diagnose` reading giving a known hitpoint band, or kill-total arithmetic across a whole
            -- fight - can only narrow brackets that are still on disk as brackets.
            dmg_low             INTEGER,
            dmg_high            INTEGER,
            -- dir=in hits only: EXACT, from the stamina delta.
            dmg                 INTEGER
        );

        CREATE INDEX IF NOT EXISTS ix_swings_group_dir ON swings(npc_group, dir);
        CREATE INDEX IF NOT EXISTS ix_swings_npc_dir   ON swings(npc, dir);
        CREATE INDEX IF NOT EXISTS ix_swings_ts        ON swings(ts);
        CREATE INDEX IF NOT EXISTS ix_swings_encounter ON swings(encounter_started_at_ms);
        -- ix_swings_reset_landed is NOT here - see PostColumnSql. An index on a column that
        -- AddMissingColumns has not added yet cannot be created, and the attempt takes the whole
        -- schema transaction down with it.

        -- One row per per-NPC fight, as FightHistoryRecorder closes them.
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
            -- one this session. MUD2 opens a fresh engagement every time a creature tries to flee, so
            -- the row holding the killing blow is routinely the last of several and holds only that
            -- blow. Without this column the table cannot tell a terminal link from a genuine one-hit
            -- kill.
            prev_same_name_ended_ms INTEGER
        );

        CREATE INDEX IF NOT EXISTS ix_fights_npc       ON fights(npc_name);
        CREATE INDEX IF NOT EXISTS ix_fights_group     ON fights(npc_group);
        CREATE INDEX IF NOT EXISTS ix_fights_weapon    ON fights(weapon_used);
        CREATE INDEX IF NOT EXISTS ix_fights_encounter ON fights(encounter_started_at_ms);

        -- Every `diagnose` reading the stethoscope has ever produced: "The water-snake5 has a stamina
        -- lying between 90 and 99."
        --
        -- This is the ONLY direct measurement of NPC stamina MUD2 gives. It is the strongest
        -- constraint the remaining-stamina model has (see MudSharp.Combat.NpcRemainingStamina) and the
        -- only way to CHECK a published creature stamina against the live game.
        --
        -- printed_low/printed_high are EXACTLY the two numbers in the sentence. Whether the bracket is
        -- aligned to tens, to a tenth of the pool, or to something else is unresolved, so nothing
        -- rounds or snaps them; the raw line rides along so a later pass can re-read the wording
        -- rather than trust this table's parse.
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

        CREATE INDEX IF NOT EXISTS ix_stamina_reads_npc  ON npc_stamina_reads(npc);
        CREATE INDEX IF NOT EXISTS ix_stamina_reads_pool ON npc_stamina_reads(pool_key);
        CREATE INDEX IF NOT EXISTS ix_stamina_reads_ts   ON npc_stamina_reads(ts);

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

        -- ================================================================= wire log ==

        CREATE TABLE IF NOT EXISTS sessions (
            id             INTEGER PRIMARY KEY,
            started_ms     INTEGER NOT NULL,   -- unix ms, when the store opened
            ended_ms       INTEGER,            -- unix ms; NULL means the client died before closing it
            host           TEXT,               -- the server this session was against
            client_version TEXT                -- Mucka's version, so a decode can be told which client wrote it
        );

        -- The traffic. One row per record - one read off the socket, one write onto it, or one
        -- client-side note.
        --
        -- `data` is the bytes and NOTHING ELSE: no magic, no length prefix, no encoded timestamp, not
        -- compressed. `SELECT data FROM wire` prints the line. That is the whole design rule for this
        -- table, and it is why `ts_ms` and `direction` are columns: a fact encoded into the blob is a
        -- fact that costs a decoder to read, and a corpus that needs a decoder before the first
        -- question is a corpus in name only.
        --
        -- It is also what removes the one failure mode the framed form had. Varint framing carries no
        -- redundancy, so damage that leaves the buffer parseable decodes into a well-formed run of
        -- records that is simply not what was written - silently. Damaged bytes in a blob are visibly
        -- damaged bytes.
        --
        -- Measured over 13 sessions, 18.11 play-hours, 10-13 Sep 2026: 119,921 records, 10,141,176
        -- bytes of payload, 84 bytes average. Rx is 64% of rows and 95% of bytes; Tx is 36% of rows
        -- and 4.6%, because a typed command is about 11 bytes. Unindexed, a row costs 26.7 bytes of
        -- SQLite page overhead above its payload, so that corpus stores in about 13.3 MB against the
        -- framed form's 11.4 MB: 0.74 MB per play-hour rather than 0.63. Legibility costs 0.11 MB an
        -- hour, which is the whole of what the framing bought.
        CREATE TABLE IF NOT EXISTS wire (
            id          INTEGER PRIMARY KEY,
            session_id  INTEGER NOT NULL REFERENCES sessions(id),
            seq         INTEGER NOT NULL,   -- 0-based within the session, gapless; the order things happened
            ts_ms       INTEGER NOT NULL,   -- unix ms, stamped as the record was handed to the writer
            -- WireDirection: 0 Rx, 1 Tx, 2 Annotation. STORED - never renumber.
            direction   INTEGER NOT NULL,
            data        BLOB    NOT NULL    -- the bytes, verbatim
        );

        -- No indexes on `wire`, deliberately. Rebuilt and vacuumed over the 123,933-row corpus, the
        -- table is 13,783,040 bytes with no indexes and 17,207,296 with the two it used to carry -
        -- (session_id, seq) and (ts_ms). They cost 3,424,256 bytes, 19.9%, and took a row from 26.7
        -- bytes of overhead above its payload to 54.3. Nothing queried either one.
        --
        -- A session read walks the table in `id` order, which is arrival order within a session (see
        -- WireLogWriter.Emit), so it needs no index and no sort. A time-range question scans 13 MB,
        -- tens of milliseconds, on no path a human waits for. The schema policy is additive, so
        -- `CREATE INDEX` is there the day something needs one, justified on that day's corpus.

        -- =========================================================== encounter logs ==

        -- One row per encounter, as CombatTracker opens and closes them. encounter_started_at_ms is
        -- the key every other table in this group carries, and the one swings and fights carry.
        CREATE TABLE IF NOT EXISTS encounters (
            id                  INTEGER PRIMARY KEY,
            encounter_started_at_ms INTEGER NOT NULL,
            ended_at_ms         INTEGER,            -- NULL while it is still open or draining its tail
            room                TEXT,
            weather             TEXT,

            -- Which reset this encounter happened in. MUD2 creatures earn points and level up WITHIN
            -- a reset, so the same name is a different opponent at different points in the cycle.
            --
            -- reset_target_utc_ms is ResetClock's locked estimate of the reset instant: a single
            -- converged figure, so it stays put across every encounter in a reset. This is the one to
            -- group on. NULL before the clock has locked; uncertainty_sec and phase say whether the
            -- reading is a lock or a guess.
            reset_target_utc_ms INTEGER,
            reset_uncertainty_sec REAL,
            reset_phase         TEXT,
            -- The raw FES minutes-remaining reading (field [13] is MINUTES), always available.
            time_to_reset       INTEGER,
            -- ts + time_to_reset*60000. Recorded so an encounter with no lock is still groupable, and
            -- deliberately not presented as an identity: the reading is whole MINUTES, so the derived
            -- instant jitters by up to a minute between observations. Bucket to +/-30s (ResetClock's
            -- MinuteUncertaintySec) before grouping, or prefer reset_target_utc_ms.
            reset_derived_epoch_ms INTEGER
        );

        -- Not unique. Two encounters can open in the same millisecond (a new fight starting the
        -- instant the previous one's last NPC dies is routine, not a rare race), and they then share
        -- this key - which is already true of swings and fights. A unique index would turn that
        -- ambiguity into a dropped row; a duplicate is at least visible.
        CREATE INDEX IF NOT EXISTS ix_encounters_key ON encounters(encounter_started_at_ms);

        -- Plain prose around an encounter: the pre-roll (the last lines before combat began) and the
        -- tail (everything printed after the encounter closed, up to the next prompt - the server's
        -- own cleanup for the close: score, "The X has just passed on.", dropped items, level-up).
        -- Text inside an encounter is in encounter_events instead, with its classification.
        CREATE TABLE IF NOT EXISTS encounter_lines (
            id                  INTEGER PRIMARY KEY,
            encounter_started_at_ms INTEGER NOT NULL,
            phase               TEXT    NOT NULL,   -- 'preroll' | 'tail'
            ord                 INTEGER NOT NULL,   -- order within the phase
            ts                  INTEGER,            -- NULL on preroll: buffered before the encounter existed
            text                TEXT    NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_encounter_lines ON encounter_lines(encounter_started_at_ms);

        -- An absolute stats snapshot. Written at encounter start (reason 'start') and whenever a
        -- load-relevant value moves, so a fight that drops several items carries a reading after each
        -- one: each drop or take with a fresh reading either side yields that object's dexterity cost
        -- (keyed on item COUNT) and its strength cost (keyed on WEIGHT).
        --
        -- Snapshots, not deltas. A row is written only when a tracked value actually moved, so the
        -- count is bounded by how often the game's numbers change; and the measurement these exist for
        -- is "reading immediately before the drop, reading immediately after", which against absolute
        -- rows is a scan to the nearest row either side and against deltas is a replay from the start.
        --
        -- Stamina alone does not trigger a row (it rides along in every row that IS written): it
        -- changes on essentially every incoming blow, which encounter_events already records.
        --
        -- objects_carried and carried_count are both here and are NOT the same. FES carries no object
        -- count at all, so objects_carried reaches GameStatsSnapshot only by parsing a `score` sheet -
        -- typically the automatic one at character select - and then sits frozen for the rest of the
        -- session however much the player picks up or puts down. carried_count is the length of the
        -- live FEI carry list, which is the figure the dexterity burden is actually keyed on. NULL
        -- until a FEI list has completed, which is a real distinction from an empty pack.
        CREATE TABLE IF NOT EXISTS encounter_stats (
            id                  INTEGER PRIMARY KEY,
            encounter_started_at_ms INTEGER NOT NULL,
            ts                  INTEGER NOT NULL,
            -- Which rule fired, so a consumer can tell a reading forced after a loadout change (and
            -- which may show no movement at all - a genuine result) from one written because something
            -- moved: 'start' | 'change' | 'post_inventory' | 'effects'.
            reason              TEXT    NOT NULL,

            stamina             INTEGER,
            max_stamina         INTEGER,
            strength            INTEGER,
            raw_strength        INTEGER,
            max_strength        INTEGER,
            dexterity           INTEGER,
            raw_dexterity       INTEGER,
            max_dexterity       INTEGER,
            magic               INTEGER,
            max_magic           INTEGER,
            objects_carried     INTEGER,
            max_objects_carried INTEGER,
            carried_count       INTEGER,
            level               INTEGER,
            games_played        INTEGER,
            weather             TEXT,
            is_blind            INTEGER,
            is_deaf             INTEGER,
            is_crippled         INTEGER,
            is_dumb             INTEGER,

            -- The seven independent buff/debuff/glow slots, without the tooltip messages that ride
            -- with them - a boolean flip is the whole of what a stats row needs.
            str_buff            INTEGER,
            str_debuff          INTEGER,
            dex_buff            INTEGER,
            dex_debuff          INTEGER,
            sta_buff            INTEGER,
            sta_debuff          INTEGER,
            glow                INTEGER
        );

        CREATE INDEX IF NOT EXISTS ix_encounter_stats ON encounter_stats(encounter_started_at_ms);

        -- The structured FEI list (room contents + carried inventory), written at encounter start and
        -- whenever it differs from the last one written. The room description as prose is not a
        -- substitute: it cannot be diffed, it omits the pack entirely, and it is silent about anything
        -- that walked in afterwards. Re-writing on change is what makes a chase across rooms
        -- reconstructible.
        CREATE TABLE IF NOT EXISTS encounter_contents (
            id                  INTEGER PRIMARY KEY,
            encounter_started_at_ms INTEGER NOT NULL,
            ts                  INTEGER NOT NULL,
            room                TEXT,
            carried_count       INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_encounter_contents ON encounter_contents(encounter_started_at_ms);

        -- One row per item in that list. A child table rather than a delimited string because "which
        -- encounters had a thief in the room" is a query here and a string scan there.
        --
        -- is_creature comes from the game's own C04-coded presence sentences, which are the only
        -- evidence MUD2 gives: FEI returns creatures and objects as bare names in one undifferentiated
        -- list. It is the sentence-derived half only - "currently being fought" is NOT unioned in here,
        -- because encounter_events already records every fight by NPC name and merging the two sources
        -- at write time would destroy the provenance a later pass can reconstruct for itself.
        CREATE TABLE IF NOT EXISTS encounter_contents_items (
            id                  INTEGER PRIMARY KEY,
            contents_id         INTEGER NOT NULL REFERENCES encounter_contents(id),
            ord                 INTEGER NOT NULL,
            name                TEXT    NOT NULL,
            is_creature         INTEGER NOT NULL,
            is_carried          INTEGER NOT NULL    -- 0: in the room. 1: in the pack
        );

        CREATE INDEX IF NOT EXISTS ix_contents_items ON encounter_contents_items(contents_id);

        -- Every classified CombatEvent of an encounter, in order, with the server's own sentence.
        --
        -- The hit and miss kinds are also in `swings`, and that is deliberate rather than redundant:
        -- the two are different projections. swings carries the full player and world state at the
        -- blow and is indexed for aggregate questions across the whole corpus; this is the complete
        -- ordered narrative of ONE encounter, raw text included, which a replay needs whole.
        CREATE TABLE IF NOT EXISTS encounter_events (
            id                  INTEGER PRIMARY KEY,
            encounter_started_at_ms INTEGER NOT NULL,
            ts                  INTEGER NOT NULL,
            kind                TEXT    NOT NULL,   -- CombatEventKind
            actor               TEXT,
            npc                 TEXT,
            -- Also the moved object's name on the four loadout kinds (ItemDropped/ItemTaken/
            -- ItemStowed/ItemRetrieved).
            weapon              TEXT,
            -- ItemStowed/ItemRetrieved only: which container. Whether its weight left the player
            -- depends on whether the container was itself carried, which the nearest
            -- encounter_contents row answers.
            container           TEXT,
            range_low           INTEGER,
            range_high          INTEGER,
            -- NpcHealth only. Written even though raw_text carries the same sentence, because the rung
            -- is the interpretation and the health ladder's ordering was itself established from
            -- records like these.
            health_rung         INTEGER,
            health_phrase       TEXT,
            raw_text            TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_encounter_events     ON encounter_events(encounter_started_at_ms);
        CREATE INDEX IF NOT EXISTS ix_encounter_events_npc ON encounter_events(npc);

        -- The (creature, points) pairs a `value <name>` probe resolved, so the corpus accumulates
        -- these directly instead of only inferring them from kill awards (the score sheet's "this
        -- game" delta conflates every kill in the window rather than crediting one creature).
        --
        -- ambiguous = 1 when a row for that name was already written this encounter: unnumbered mobs
        -- (thief, banshee, coot, fox) have no instance number, so one probe can draw a reply from more
        -- than one live creature sharing the name, and nothing here can tell whose is whose.
        CREATE TABLE IF NOT EXISTS creature_values (
            id                  INTEGER PRIMARY KEY,
            encounter_started_at_ms INTEGER NOT NULL,
            ts                  INTEGER NOT NULL,
            npc                 TEXT    NOT NULL,
            value               INTEGER NOT NULL,
            ambiguous           INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_creature_values ON creature_values(encounter_started_at_ms);

        -- ==================================================================== views ==

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

        -- What the wire log is costing, per session, without decoding anything.
        CREATE VIEW IF NOT EXISTS v_session_sizes AS
        SELECT s.id, s.started_ms, s.ended_ms, s.host,
               COUNT(w.id)          AS records,
               SUM(LENGTH(w.data))  AS payload_bytes
        FROM sessions s LEFT JOIN wire w ON w.session_id = s.id
        GROUP BY s.id;
        """;
}
