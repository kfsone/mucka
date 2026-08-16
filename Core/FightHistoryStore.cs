using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using MudSharp.Combat;

namespace Mucka.Core;

/// <summary>
/// Owns the <c>fights</c> table of the client combat database (see <see cref="CombatDb"/>): the
/// per-fight history the live HUD contrasts the current fight against, and the source of the NPC
/// stamina-pool estimates any "are you winning" projection needs (MUD2 never reports NPC stamina over
/// the wire, so prior kills are the only route to a pool figure the CLIENT can derive - though the
/// figures themselves are published, see tools/combat/MUD2-PUBLISHED-MECHANICS.md and
/// tools/combat/STATS_DESIGN.md).
///
/// <para><b>SQLite rather than the fights.jsonl this used to write.</b> The original reasoning - no
/// new dependency, no MAUI/Android packaging question, small enough to hold in memory - was sound
/// while nothing needed to QUERY the data. A combat analysis view does, and splitting the corpus so
/// that swings lived in SQL and fights in a text file would have meant joining them in app code. The
/// rows are still small enough to hold in memory, and still are: the in-memory
/// <see cref="HistoryIndex"/> below is unchanged, it is only its source that moved.</para>
///
/// <para>Threading: <see cref="Append"/> is called from the session Feed thread (same contract as
/// ClogWriter), <see cref="Snapshot"/> from the UI thread. Both take the same lock, which is only ever
/// held for a list add or a copy-reference - never across the database write, so a slow disk cannot
/// stall the UI thread (Invariant #1). The write itself runs on a single dedicated background task
/// (<see cref="DrainAsync"/>) which owns the only write connection, so the Feed thread that parses
/// incoming combat text never pays for the I/O (DESIGN_FINAL.md section 7.5). <see cref="Dispose"/>
/// blocks briefly to drain whatever is still queued, so an app exit mid-fight cannot lose the row for
/// the fight that was open at that moment.</para>
/// </summary>
public sealed class FightHistoryStore : IDisposable
{
    private readonly object _lock = new();
    private readonly string _dbPath;
    // Injected rather than calling CrashLog directly so this type stays free of MAUI references
    // and can be exercised against a temp directory in mudsharp.Tests.
    private readonly Action<string, Exception>? _onError;

    private readonly Channel<FightRecord> _writeQueue =
        Channel.CreateUnbounded<FightRecord>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _writerTask;

    // Copy-on-write: readers take a reference under the lock and then enumerate freely, so a UI
    // thread query can never see a torn list mid-append and never blocks the Feed thread.
    private List<FightRecord> _records = [];

    // The incremental replacement for "filter the whole corpus, then scan it three times" (see
    // MudSharp.Combat.HistoryIndex's own remarks and DESIGN_FINAL.md section 7.3). Guarded by the
    // SAME _lock as _records - every mutation (LoadAsync's initial build, Append's per-fight insert)
    // and every read (GetHistoryContext) takes it, so this never needs its own synchronization.
    private readonly HistoryIndex _index = new();

    public FightHistoryStore(string dbPath, Action<string, Exception>? onError = null)
    {
        _dbPath = dbPath;
        _onError = onError;
        _writerTask = Task.Run(DrainAsync);
    }

    public string DatabasePath => _dbPath;


    /// <summary>Rows loaded so far. Cheap: returns the current immutable-by-convention list.</summary>
    public IReadOnlyList<FightRecord> Snapshot()
    {
        lock (_lock)
            return _records;
    }

    /// <summary>Reads the whole table into memory. Call once at startup, OFF the UI thread.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        // Task.Run rather than relying on the awaits inside to yield. This is called fire-and-forget
        // from GameViewModel's constructor, i.e. ON the UI thread, and every step below (opening the
        // file, applying the schema, the one-time import, the read) is synchronous SQLite work. An
        // async method whose awaits all complete synchronously never leaves the thread it started on,
        // which would put a file open and a whole-table scan directly on the typing path
        // (Invariant #1).
        var loaded = await Task.Run(LoadCore, cancellationToken).ConfigureAwait(false);
        if (loaded is null)
            return;

        lock (_lock)
        {
            // Build the index from the freshly-read rows now, BEFORE merging in anything that was
            // appended concurrently while this method was reading (those already inserted themselves
            // via Append's own _index.Insert call - see there - so inserting them again here would
            // double-count them). One-time cost, off the UI thread (DESIGN_FINAL.md 7.3's "startup").
            foreach (var record in loaded)
                _index.Insert(record);

            // Anything appended while we were reading would be lost by a blind assignment, so keep
            // the in-memory rows the load did not already account for.
            if (_records.Count > 0)
                loaded.AddRange(_records);
            _records = loaded;
        }
    }

    /// <summary>The synchronous body of <see cref="LoadAsync"/>. Returns null when the database could
    /// not be read at all, which the caller treats as "leave the in-memory history alone" rather than
    /// as an empty history.</summary>
    private List<FightRecord>? LoadCore()
    {
        var loaded = new List<FightRecord>();
        try
        {
            // CombatDb.Open rather than a bare SqliteConnection: it creates the directory (absent on a
            // fresh install, and SQLite will not create a file inside one that does not exist) and
            // applies the same PRAGMAs every other connection uses. Skipping it made the whole load
            // fail silently on first run, taking the one-time legacy import down with it.
            using var connection = CombatDb.Open(_dbPath);

            using var command = connection.CreateCommand();
            command.CommandText = SelectSql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                loaded.Add(ReadRecord(reader));
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // Best-effort feature: an unreadable history must never block getting into a game.
            _onError?.Invoke("FightHistoryStore.Load", ex);
            return null;
        }

        return loaded;
    }

    /// <summary>The incremental history context for one NPC instance/group, as seen by the index
    /// RIGHT NOW. Callers that need this frozen for the duration of a live encounter (so a fight
    /// closing mid-comparison cannot change the answer they are already showing) must cache the
    /// result themselves keyed on something that changes only between encounters - see
    /// SidePanelViewModel.ResolveHistory, which is the only production caller and exists specifically
    /// to explain that caching contract.</summary>
    public (FightHistorySummary Instance, FightHistorySummary Group,
            IReadOnlyList<WeaponHistorySummary> ByWeapon, FightHistorySummary CurrentWeaponGlobal)
        GetHistoryContext(string instanceName, string groupName, string? currentWeapon)
    {
        lock (_lock)
        {
            return (
                _index.GetInstanceSummary(instanceName),
                _index.GetGroupSummary(groupName),
                _index.GetByWeapon(groupName),
                _index.GetWeaponGlobalSummary(currentWeapon));
        }
    }

    /// <summary>Whether <paramref name="name"/> is on file as something the player has fought with -
    /// see <see cref="HistoryIndex.IsKnownWeapon"/> for why that is the client's whole notion of
    /// "this object is a weapon". Cheap enough to call per carried item on the refresh path.</summary>
    public bool IsKnownWeapon(string? name)
    {
        lock (_lock)
            return _index.IsKnownWeapon(name);
    }

    /// <summary>Appends one completed fight: updates the in-memory snapshot immediately (so a
    /// same-thread Snapshot() right after this call sees it - Invariant #1 does not apply to the
    /// Feed thread doing its own cheap bookkeeping) and enqueues the database write for
    /// <see cref="DrainAsync"/> to perform off-thread. Never throws: losing a history row is
    /// strictly less bad than disrupting play.</summary>
    public void Append(FightRecord record)
    {
        lock (_lock)
        {
            var updated = new List<FightRecord>(_records.Count + 1);
            updated.AddRange(_records);
            updated.Add(record);
            _records = updated;

            // The incremental update DESIGN_FINAL.md 7.3 asks for: O(log bucket-size), never a
            // rescan. This is also THE reason a live encounter can never compare against itself
            // (see HistoryIndex's class remarks) - Insert only ever runs from here, and this method
            // only ever runs once a fight has fully closed and been handed to FlushLocked.
            _index.Insert(record);
        }

        _writeQueue.Writer.TryWrite(record);
    }

    internal const string Columns =
        "character_name, encounter_started_at_ms, started_at_ms, ended_at_ms, duration_ms, " +
        "npc_name, npc_group, weapon_used, outcome, " +
        "you_hits, you_misses, they_hits, they_misses, approx_damage_done, approx_damage_taken, " +
        "narrative_mode, room, weather, strength, raw_strength, dexterity, raw_dexterity, " +
        "stamina_at_start, max_stamina, min_stamina, stamina_at_end, score_at_start, score_at_end, " +
        "objects_carried, level, is_blind, is_deaf, is_crippled, is_dumb, effects";

    private const string SelectSql = $"SELECT {Columns} FROM fights ORDER BY started_at_ms;";

    private const string InsertSql = $"""
        INSERT INTO fights ({Columns}) VALUES (
            $character_name, $encounter, $started, $ended, $duration,
            $npc_name, $npc_group, $weapon_used, $outcome,
            $you_hits, $you_misses, $they_hits, $they_misses, $dmg_done, $dmg_taken,
            $narrative, $room, $weather, $strength, $raw_strength, $dexterity, $raw_dexterity,
            $sta_start, $sta_max, $sta_min, $sta_end, $score_start, $score_end,
            $objects, $level, $blind, $deaf, $crippled, $dumb, $effects
        );
        """;

    private static FightRecord ReadRecord(SqliteDataReader reader) => new()
    {
        CharacterName = Str(reader, 0),
        EncounterStartedAtMs = Long(reader, 1),
        StartedAtMs = reader.GetInt64(2),
        EndedAtMs = reader.GetInt64(3),
        DurationMs = reader.GetInt64(4),
        NpcName = reader.GetString(5),
        NpcGroup = reader.GetString(6),
        WeaponUsed = Str(reader, 7),
        Outcome = reader.GetString(8),
        YouHits = reader.GetInt32(9),
        YouMisses = reader.GetInt32(10),
        TheyHits = reader.GetInt32(11),
        TheyMisses = reader.GetInt32(12),
        ApproxDamageDone = reader.GetDouble(13),
        ApproxDamageTaken = reader.GetDouble(14),
        NarrativeMode = reader.GetInt64(15) != 0,
        Room = Str(reader, 16),
        Weather = Str(reader, 17),
        Strength = Int(reader, 18),
        RawStrength = Int(reader, 19),
        Dexterity = Int(reader, 20),
        RawDexterity = Int(reader, 21),
        StaminaAtStart = Int(reader, 22),
        MaxStamina = Int(reader, 23),
        MinStamina = Int(reader, 24),
        StaminaAtEnd = Int(reader, 25),
        ScoreAtStart = Int(reader, 26),
        ScoreAtEnd = Int(reader, 27),
        ObjectsCarried = Int(reader, 28),
        Level = Int(reader, 29),
        IsBlind = reader.GetInt64(30) != 0,
        IsDeaf = reader.GetInt64(31) != 0,
        IsCrippled = reader.GetInt64(32) != 0,
        IsDumb = reader.GetInt64(33) != 0,
        Effects = SplitEffects(reader.IsDBNull(34) ? null : reader.GetString(34)),
    };

    private static string? Str(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
    private static int? Int(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetInt32(i);
    private static long? Long(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);

    /// <summary>Effects are stored as one comma-separated string rather than a child table. They are
    /// only ever read back as a whole set for one fight - nothing groups or joins on an individual
    /// effect at the FIGHT level, because the per-SWING columns answer that question far better (see
    /// the swings table's own flags). A join table here would be structure with no query behind it.</summary>
    private static string JoinEffects(string[] effects) => string.Join(",", effects);

    private static string[] SplitEffects(string? stored)
        => string.IsNullOrEmpty(stored) ? [] : stored.Split(',', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Binds one record onto a command using <see cref="InsertSql"/>'s parameter names.</summary>
    private static void Bind(SqliteCommand command, FightRecord record)
    {
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$character_name", Value(record.CharacterName));
        command.Parameters.AddWithValue("$encounter", Value(record.EncounterStartedAtMs));
        command.Parameters.AddWithValue("$started", record.StartedAtMs);
        command.Parameters.AddWithValue("$ended", record.EndedAtMs);
        command.Parameters.AddWithValue("$duration", record.DurationMs);
        command.Parameters.AddWithValue("$npc_name", record.NpcName);
        command.Parameters.AddWithValue("$npc_group", record.NpcGroup);
        command.Parameters.AddWithValue("$weapon_used", Value(record.WeaponUsed));
        command.Parameters.AddWithValue("$outcome", record.Outcome);
        command.Parameters.AddWithValue("$you_hits", record.YouHits);
        command.Parameters.AddWithValue("$you_misses", record.YouMisses);
        command.Parameters.AddWithValue("$they_hits", record.TheyHits);
        command.Parameters.AddWithValue("$they_misses", record.TheyMisses);
        command.Parameters.AddWithValue("$dmg_done", record.ApproxDamageDone);
        command.Parameters.AddWithValue("$dmg_taken", record.ApproxDamageTaken);
        command.Parameters.AddWithValue("$narrative", record.NarrativeMode ? 1 : 0);
        command.Parameters.AddWithValue("$room", Value(record.Room));
        command.Parameters.AddWithValue("$weather", Value(record.Weather));
        command.Parameters.AddWithValue("$strength", Value(record.Strength));
        command.Parameters.AddWithValue("$raw_strength", Value(record.RawStrength));
        command.Parameters.AddWithValue("$dexterity", Value(record.Dexterity));
        command.Parameters.AddWithValue("$raw_dexterity", Value(record.RawDexterity));
        command.Parameters.AddWithValue("$sta_start", Value(record.StaminaAtStart));
        command.Parameters.AddWithValue("$sta_max", Value(record.MaxStamina));
        command.Parameters.AddWithValue("$sta_min", Value(record.MinStamina));
        command.Parameters.AddWithValue("$sta_end", Value(record.StaminaAtEnd));
        command.Parameters.AddWithValue("$score_start", Value(record.ScoreAtStart));
        command.Parameters.AddWithValue("$score_end", Value(record.ScoreAtEnd));
        command.Parameters.AddWithValue("$objects", Value(record.ObjectsCarried));
        command.Parameters.AddWithValue("$level", Value(record.Level));
        command.Parameters.AddWithValue("$blind", record.IsBlind ? 1 : 0);
        command.Parameters.AddWithValue("$deaf", record.IsDeaf ? 1 : 0);
        command.Parameters.AddWithValue("$crippled", record.IsCrippled ? 1 : 0);
        command.Parameters.AddWithValue("$dumb", record.IsDumb ? 1 : 0);
        command.Parameters.AddWithValue("$effects", JoinEffects(record.Effects));
    }

    private static object Value(object? value) => value ?? DBNull.Value;

    /// <summary>The single background writer for this store's whole lifetime.</summary>
    private async Task DrainAsync()
    {
        SqliteConnection? connection = null;
        var reader = _writeQueue.Reader;

        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                connection ??= CombatDb.Open(_dbPath);

                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = InsertSql;

                var written = 0;
                while (reader.TryRead(out var record))
                {
                    try
                    {
                        Bind(command, record);
                        command.ExecuteNonQuery();
                        written++;
                    }
                    catch (SqliteException ex)
                    {
                        _onError?.Invoke("FightHistoryStore.Insert", ex);
                    }
                }

                if (written > 0)
                    transaction.Commit();
            }
        }
        catch (Exception ex)
        {
            _onError?.Invoke("FightHistoryStore.Drain", ex);
        }
        finally
        {
            connection?.Dispose();
        }
    }

    /// <summary>Blocks (briefly - just draining whatever is already queued in memory, typically a
    /// handful of rows at most) until every fight <see cref="Append"/>ed so far has actually been
    /// written. This is the fix for fight rows being lost when the app exits mid-fight: without this
    /// wait, an Append() immediately followed by process exit could beat the background writer to the
    /// punch, since Append only enqueues rather than writing directly.</summary>
    public void Dispose()
    {
        _writeQueue.Writer.TryComplete();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Best-effort: Dispose must never throw during shutdown. Whatever did not get written
            // in time is lost, same as any other best-effort I/O failure in this class.
        }
    }
}
