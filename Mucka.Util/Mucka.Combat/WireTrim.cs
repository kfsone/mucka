using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using MudSharp.Models;
using MudSharp.Protocol;
using Mucka.Store;
using Mucka.WireLog;

namespace Mucka.Combat;

/// <summary>
/// <c>$trimdb</c>: takes the noise of the periodic probe out of the wire log of runs that ended some
/// days ago, into the <c>trimmed</c> table, and puts it back on request.
///
/// <para><b>What a trim takes.</b> Every tx row that is a probe send (a command interrupt naming only
/// FES, FEW, FEI or FEX), and every rx row of a probe response that told the client nothing new: the
/// same stats, list or items as the response before it in the run, or the same but for the time to
/// reset. A response is kept when it is the first after a login, when anything in it changed (every
/// field of the stats but the time to reset, and each name in the who list with its colour), and when
/// it is the last response of its part - stats, who list, items - before the next login or the end of
/// the run. A response is taken whole: a <c>wire</c> row is one socket read or, once joined, one
/// prompt-bounded unit, and some rows carry a response and ordinary output together. A mixed row stays, because a response need not arrive in
/// one read and a cut within a row cannot see where it ends. Only runs <see cref="WireHeal"/> has
/// been over are touched.</para>
///
/// <para><b>The gate.</b> Each run is replayed twice through <see cref="MudStreamParser"/>, as it
/// stands and with the planned rows gone, and the two line sequences must agree once the lines that
/// belonged to the taken rows are dropped. A run that fails is left exactly as it was.</para>
///
/// <para><see cref="Unwrap"/> is the same machinery for a different rewrite: it takes the server's
/// own line wrapping out of old recordings, changing rows in place after copying their originals.</para>
///
/// <para><b>Reversible.</b> A row is copied into <c>trimmed</c> with its own id, and the time and
/// rule that took it, in the same transaction that deletes it. <see cref="Revert"/> puts rows back by
/// id; <see cref="Expire"/> makes the removal final once it has aged. <see cref="Trim"/>,
/// <see cref="Unwrap"/> take no backup, because <see cref="Revert"/> undoes them. <see cref="Expire"/>
/// takes it (<see cref="DatabaseBackup.TakeOnce"/>) before its first change, and none when nothing is
/// to change, because it makes the removal final. The work is committed in small
/// transactions with <see cref="WireMaintenance.PauseBetweenTransactions"/> between them (MUST GO AWAY
/// with the verb: see there), so the
/// client's own writer, which faults the store when it cannot get the lock, gets it.</para>
/// </summary>
public static class WireTrim
{
    /// <summary>The age, in days, a run's end must have before <see cref="Trim"/> touches it and a
    /// trimmed row's removal must have before <see cref="Expire"/> makes it final.</summary>
    public const int DefaultDays = 30;

    /// <summary>The rows moved per transaction.</summary>
    public const int RowsPerTransaction = 500;

    /// <summary>Why a row was taken: the send of a probe, or the response to one that said nothing new.</summary>
    public const string ReasonSend = "probe send";

    /// <inheritdoc cref="ReasonSend"/>
    public const string ReasonRepeat = "probe repeat";

    /// <summary>What a trim pass did.</summary>
    public sealed record TrimResult(
        int RunsExamined, int RunsTrimmed, int RunsRefused, long RowsMoved, long BytesMoved);

    /// <summary>A row the plan takes, and the rule that takes it.</summary>
    public readonly record struct Planned(long Id, long Seq, string Reason);

    private const byte C1Lead = 0xA7, C1Sub8 = 0xA3, C1Fes = 0x9C;

    /// <summary>The probe forms whose responses are compared with the one before. Bare FEX is sent
    /// too, but a FEX reply is mostly the exits list the server sends unasked with every room, so it
    /// is no answer to anything.</summary>
    private static readonly HashSet<string> RepeatForms = ["FES", "FES,FEI", "FES,FEW", "FES,FEW,FEI"];

    /// <summary>The parts whose last response in a segment is kept.</summary>
    private static readonly string[] ProbeParts = ["FES", "FEW", "FEI"];

    // -- Passes -------------------------------------------------------------------------------------

    /// <summary>Trims every run that ended more than <paramref name="days"/> days before
    /// <paramref name="now"/>. Off the UI thread: it replays the runs it touches.</summary>
    public static TrimResult Trim(string dbPath, int days, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        using var held = DatabaseLock.Acquire(dbPath, Maintenance.Current, canGiveUp: false, cancellationToken: cancellationToken);
        lock (WireMaintenance.Gate)
            return TrimLocked(dbPath, days, now, cancellationToken);
    }

    private static TrimResult TrimLocked(string dbPath, int days, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var connection = Open(dbPath);
        var cutoff = now.ToUnixTimeMilliseconds() - days * MillisecondsPerDay;

        var runs = new List<long>();
        using (var find = connection.CreateCommand())
        {
            find.CommandText = "SELECT id FROM mucka_runs WHERE ended_ms IS NOT NULL AND ended_ms < $cutoff AND wire_healed_ms IS NOT NULL ORDER BY id;";
            find.Parameters.AddWithValue("$cutoff", cutoff);
            using var reader = find.ExecuteReader();
            while (reader.Read())
                runs.Add(reader.GetInt64(0));
        }

        int trimmed = 0, refused = 0;
        long moved = 0, bytes = 0;
        foreach (var run in runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = ReadRows(connection, run);
            var plan = Plan(rows);
            if (plan.Count == 0) continue;
            if (!Verify(rows, plan))
            {
                refused++;
                continue;
            }
            var planned = plan.Select(p => p.Id).ToHashSet();
            var size = rows.Where(r => planned.Contains(r.Id)).Sum(r => (long)r.Data.Length);
            moved += Move(connection, run, plan, now.ToUnixTimeMilliseconds(), cancellationToken);
            bytes += size;
            trimmed++;
        }
        return new TrimResult(runs.Count, trimmed, refused, moved, bytes);
    }

    /// <summary>What an unwrap pass did.</summary>
    public sealed record UnwrapResult(int RunsExamined, int RunsUnwrapped, int RunsRefused, long RowsChanged);

    /// <summary>The rule <see cref="Unwrap"/> files a changed row under.</summary>
    public const string ReasonUnwrap = "unwrap";

    /// <summary>Takes the server's line wrapping out of every run that ended more than
    /// <paramref name="days"/> days before <paramref name="now"/> - see <see cref="WireUnwrap"/>. A row it
    /// changes keeps its place in <c>wire</c> with its new bytes, and its original is copied into
    /// <c>trimmed</c> first, so <see cref="Revert"/> puts the original bytes back.</summary>
    public static UnwrapResult Unwrap(string dbPath, int days, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        using var held = DatabaseLock.Acquire(dbPath, Maintenance.Current, canGiveUp: false, cancellationToken: cancellationToken);
        lock (WireMaintenance.Gate)
            return UnwrapLocked(dbPath, days, now, cancellationToken);
    }

    private static UnwrapResult UnwrapLocked(string dbPath, int days, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var connection = Open(dbPath);
        var cutoff = now.ToUnixTimeMilliseconds() - days * MillisecondsPerDay;

        var runs = new List<long>();
        using (var find = connection.CreateCommand())
        {
            find.CommandText = "SELECT id FROM mucka_runs WHERE ended_ms IS NOT NULL AND ended_ms < $cutoff AND wire_healed_ms IS NOT NULL ORDER BY id;";
            find.Parameters.AddWithValue("$cutoff", cutoff);
            using var reader = find.ExecuteReader();
            while (reader.Read())
                runs.Add(reader.GetInt64(0));
        }

        int unwrapped = 0, refused = 0;
        long changed = 0;
        foreach (var run in runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A trim has taken the sends and responses that mark where one turn ends and the next
            // begins; joining lines across the gap it left would join lines of different turns.
            if (HasTrimmedProbes(connection, run))
            {
                refused++;
                continue;
            }
            var plan = WireUnwrap.Plan(ReadRows(connection, run));
            if (plan is null)
            {
                refused++;
                continue;
            }
            if (plan.Count == 0) continue;
            changed += Rewrite(connection, run, plan, now.ToUnixTimeMilliseconds(), cancellationToken);
            unwrapped++;
        }
        return new UnwrapResult(runs.Count, unwrapped, refused, changed);
    }

    /// <summary>Removes the rows <see cref="Trim"/> took more than <paramref name="days"/> days before
    /// <paramref name="now"/>. Aged by when they were trimmed, not by when they were recorded: a
    /// trimmed row is always from an old run, so the recording's own age would make every one of them
    /// final the moment it was taken. Returns how many went.</summary>
    public static long Expire(string dbPath, int days, DateTimeOffset now)
    {
        using var held = DatabaseLock.Acquire(dbPath, Maintenance.Current, canGiveUp: false);
        lock (WireMaintenance.Gate)
        {
            using var connection = Open(dbPath);
            var cutoff = now.ToUnixTimeMilliseconds() - days * MillisecondsPerDay;
            using (var any = connection.CreateCommand())
            {
                any.CommandText = "SELECT EXISTS(SELECT 1 FROM trimmed WHERE trimmed_ms < $cutoff);";
                any.Parameters.AddWithValue("$cutoff", cutoff);
                if (Convert.ToInt64(any.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                    return 0;
            }
            DatabaseBackup.TakeOnce(dbPath);
            long removed = 0;
            while (true)
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    $"DELETE FROM trimmed WHERE id IN (SELECT id FROM trimmed WHERE trimmed_ms < $cutoff LIMIT {RowsPerTransaction});";
                command.Parameters.AddWithValue("$cutoff", cutoff);
                var n = command.ExecuteNonQuery();
                removed += n;
                if (n < RowsPerTransaction) return removed;
                WireMaintenance.Pause();   // MUST GO AWAY
            }
        }
    }

    /// <summary>Puts every trimmed row back into <c>wire</c> under its own id and takes it out of
    /// <c>trimmed</c>. A row whose id has since been taken by another row stays in <c>trimmed</c> and is
    /// counted in <c>Skipped</c>.</summary>
    public static (long Restored, long Skipped) Revert(string dbPath, CancellationToken cancellationToken = default)
    {
        using var held = DatabaseLock.Acquire(dbPath, Maintenance.Current, canGiveUp: false, cancellationToken: cancellationToken);
        lock (WireMaintenance.Gate)
            return RevertLocked(dbPath, cancellationToken);
    }

    private static (long Restored, long Skipped) RevertLocked(string dbPath, CancellationToken cancellationToken)
    {
        using var connection = Open(dbPath);
        long restored = 0, skipped = 0, last = long.MinValue;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = new List<(long Id, bool Unwrapped)>();
            using (var next = connection.CreateCommand())
            {
                next.CommandText = $"SELECT id, reason FROM trimmed WHERE id > $last ORDER BY id LIMIT {RowsPerTransaction};";
                next.Parameters.AddWithValue("$last", last);
                using var reader = next.ExecuteReader();
                while (reader.Read())
                    ids.Add((reader.GetInt64(0), reader.GetString(1) == ReasonUnwrap));
            }
            if (ids.Count == 0) break;

            using var transaction = connection.BeginTransaction();
            using var copy = connection.CreateCommand();
            copy.Transaction = transaction;
            copy.CommandText = """
                INSERT OR IGNORE INTO wire (id, mucka_run_id, seq, ts_ms, direction, data)
                SELECT id, mucka_run_id, seq, ts_ms, direction, data FROM trimmed WHERE id = $id;
                """;
            var copyId = copy.Parameters.Add("$id", SqliteType.Integer);
            using var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM trimmed WHERE id = $id;";
            var clearId = clear.Parameters.Add("$id", SqliteType.Integer);
            // An unwrapped row never left wire: its original bytes go back over the new ones.
            using var overwrite = connection.CreateCommand();
            overwrite.Transaction = transaction;
            overwrite.CommandText =
                "UPDATE wire SET data = (SELECT data FROM trimmed WHERE id = $id) WHERE id = $id" +
                " AND mucka_run_id = (SELECT mucka_run_id FROM trimmed WHERE id = $id)" +
                " AND seq = (SELECT seq FROM trimmed WHERE id = $id);";
            var overwriteId = overwrite.Parameters.Add("$id", SqliteType.Integer);
            foreach (var (id, unwrapped) in ids)
            {
                copyId.Value = id;
                overwriteId.Value = id;
                if ((unwrapped && overwrite.ExecuteNonQuery() == 1) || copy.ExecuteNonQuery() == 1)
                {
                    clearId.Value = id;
                    clear.ExecuteNonQuery();
                    restored++;
                }
                else
                {
                    skipped++;
                }
            }
            transaction.Commit();
            last = ids[^1].Id;
            WireMaintenance.Pause();   // MUST GO AWAY
        }
        return (restored, skipped);
    }

    /// <summary>How many rows are waiting in <c>trimmed</c>, and the payload they hold.</summary>
    public static (long Rows, long Bytes) Waiting(string dbPath)
    {
        using var connection = Open(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COALESCE(SUM(LENGTH(data)), 0) FROM trimmed;";
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    // -- The plan -----------------------------------------------------------------------------------

    /// <summary>One run's rows, in id order, as the plan reads them.</summary>
    public readonly record struct Row(long Id, long Seq, int Direction, byte[] Data);

    /// <summary>The rows a trim takes out of one run: every probe send, and every response group that
    /// repeats the one before and is nothing but the response. Pure: it replays bytes and touches no
    /// database.</summary>
    public static List<Planned> Plan(IReadOnlyList<Row> rows)
    {
        var parser = new MudStreamParser();
        bool openFes = false, openFew = false, openFei = false, openFex = false;
        GameStatsSnapshot? lastStats = null;
        List<string>? lastFewList = null, lastRoomItems = null, lastCarriedItems = null;
        var fewNames = new List<string>();
        var feiItems = new List<string>();
        var baselineNext = true;   // the first probe after entering game mode is the baseline
        var segmentBroken = false; // a login has begun a new segment since the last group closed

        var partsDone = new HashSet<string>();
        var changedParts = new HashSet<string>();
        var ttrOnly = true;

        // A login starts a segment: its first response of each part is compared with nothing, so it
        // is kept whatever the session before it last said.
        void BeginSegment()
        {
            baselineNext = true;
            segmentBroken = true;
            lastStats = null;
            lastFewList = lastRoomItems = lastCarriedItems = null;
        }
        parser.GameModeEntered += BeginSegment;
        parser.PersonaWiped += BeginSegment;

        parser.FewListStarting += () => { openFew = true; fewNames.Clear(); };
        // The colour is part of what the list said, as the name is.
        parser.FewPlayerReady += (name, color) => fewNames.Add($"{color}|{name}");
        parser.FewListComplete += () =>
        {
            openFew = false;
            partsDone.Add("FEW");
            if (lastFewList is null || !SameSet(lastFewList, fewNames)) { changedParts.Add("FEW"); ttrOnly = false; }
            lastFewList = [.. fewNames];
        };

        parser.FeiListStarting += () => { openFei = true; feiItems.Clear(); };
        parser.FeiItemReady += item => feiItems.Add(item);
        parser.FeiListComplete += () =>
        {
            openFei = false;
            partsDone.Add("FEI");
            // "========" separates what is in the ROOM from what is CARRIED. Comparing the flat list
            // reports a change on every room transition, which is not an inventory change at all.
            var separator = feiItems.IndexOf("========");
            var room = separator < 0 ? [.. feiItems] : feiItems.Take(separator).ToList();
            var carried = separator < 0 ? [] : feiItems.Skip(separator + 1).ToList();
            var changed = lastRoomItems is null
                || !SameSet(lastRoomItems, room) || !SameSet(lastCarriedItems!, carried);
            if (changed) { changedParts.Add("FEI"); ttrOnly = false; }
            lastRoomItems = room;
            lastCarriedItems = carried;
        };

        parser.FexListStarting += () => openFex = true;
        parser.FexListComplete += () => openFex = false;

        parser.StatsUpdated += stats =>
        {
            if (!openFes) return;   // a text-analysis snapshot, not an FES packet
            openFes = false;
            partsDone.Add("FES");
            var (changed, onlyTtr) = StatsChange(lastStats, stats);
            lastStats = stats;
            if (changed) { changedParts.Add("FES"); if (!onlyTtr) ttrOnly = false; }
        };

        var planned = new List<Planned>();
        byte prev2 = 0, prev1 = 0;

        string? pendingForm = null;
        var pendingWanted = new HashSet<string>();
        var pendingRxRows = new List<(long Id, long Seq, bool PureSelfContained)>();

        // The responses of the segment so far, each with the parts it carried and the rows a trim would
        // take for it (none when it is kept anyway). They wait for the segment's end so the last
        // response of each part can be spared.
        var segment = new List<(HashSet<string> Reported, List<Planned> Take)>();

        void EndSegment()
        {
            var spared = new HashSet<int>();
            foreach (var part in ProbeParts)
            {
                var last = segment.FindLastIndex(g => g.Reported.Contains(part));
                if (last >= 0) spared.Add(last);
            }
            for (var i = 0; i < segment.Count; i++)
                if (!spared.Contains(i))
                    planned.AddRange(segment[i].Take);
            segment.Clear();
        }

        void CloseGroup()
        {
            if (segmentBroken)
            {
                EndSegment();
                segmentBroken = false;
            }
            if (pendingForm is null) return;
            var unchanged = changedParts.Count == 0;
            var eligible = RepeatForms.Contains(pendingForm) && (unchanged || ttrOnly);
            if (eligible && baselineNext) eligible = false;

            // Whole response or nothing: every row of it must be pure probe bytes with no scope
            // crossing its edges.
            var take = new List<Planned>();
            if (eligible && pendingRxRows.Count > 0 && pendingRxRows.All(r => r.PureSelfContained))
                foreach (var r in pendingRxRows)
                    take.Add(new Planned(r.Id, r.Seq, ReasonRepeat));
            segment.Add(([.. partsDone], take));

            baselineNext = false;
            pendingForm = null;
            pendingRxRows.Clear();
            partsDone.Clear();
            changedParts.Clear();
            ttrOnly = true;
        }

        foreach (var row in rows)
        {
            if (row.Direction == (int)WireDirection.Tx)
            {
                var form = ProbeForm(row.Data);
                if (form is null) continue;
                planned.Add(new Planned(row.Id, row.Seq, ReasonSend));
                CloseGroup();   // an unanswered previous probe ends here
                // What arrived with no probe pending was no answer to this one.
                partsDone.Clear();
                changedParts.Clear();
                ttrOnly = true;
                pendingForm = form;
                pendingWanted = [.. form.Split(',')];
                continue;
            }
            if (row.Direction != (int)WireDirection.Rx) continue;

            var scopeOpenAtStart = openFes || openFew || openFei || openFex;
            var probeByte = new bool[row.Data.Length];

            for (var i = 0; i < row.Data.Length; i++)
            {
                var b = row.Data[i];
                var before = openFes || openFew || openFei || openFex;

                if (prev2 == C1Lead && prev1 == C1Sub8 && b == C1Fes)
                {
                    openFes = true;
                    if (i >= 2) { probeByte[i - 2] = true; probeByte[i - 1] = true; }
                }
                // FEW/FEI/FEX open on the closing FF FF of their code, so the code bytes are already
                // behind the cursor when the event fires.
                if (prev2 == C1Lead && prev1 == C1Sub8 && (b == 0x9D || b == 0x9E || b == 0xA0)
                    && i >= 2)
                {
                    probeByte[i - 2] = true;
                    probeByte[i - 1] = true;
                    probeByte[i] = true;
                }

                parser.Feed(new ReadOnlySpan<byte>(row.Data, i, 1));

                if (before || openFes || openFew || openFei || openFex) probeByte[i] = true;
                prev2 = prev1;
                prev1 = b;
            }

            var scopeOpenAtEnd = openFes || openFew || openFei || openFex;
            var anyProbe = probeByte.Any(x => x);

            // "Nothing but the probe" means no ORDINARY OUTPUT, not literally no non-span bytes: the
            // server puts a prompt after the response, and the prompt is that turn's boundary. So the
            // test is that every byte outside the span is C1 framing, whitespace or the prompt's own
            // '*'. Any other printable character is real output and the row stays.
            var residueIsFramingOnly = true;
            for (var i = 0; i < row.Data.Length; i++)
            {
                if (!probeByte[i] && row.Data[i] is >= 0x21 and < 0x7f and not (byte)'*')
                {
                    residueIsFramingOnly = false;
                    break;
                }
            }

            // Self-contained as well: a row that continues a span from the previous read, or leaves
            // one open for the next, cannot be removed on its own.
            var pureSelfContained = residueIsFramingOnly && !scopeOpenAtStart && !scopeOpenAtEnd;

            if (pendingForm is not null && anyProbe)
                pendingRxRows.Add((row.Id, row.Seq, pureSelfContained));

            if (pendingForm is not null && pendingWanted.All(partsDone.Contains))
                CloseGroup();
        }
        CloseGroup();
        EndSegment();
        planned.Sort((a, b) => a.Id.CompareTo(b.Id));
        return planned;
    }

    /// <summary>The acceptance gate. The run is replayed as it was, recording which row each finished
    /// line came from, and again without the planned rows; the line sequences must match once the
    /// lines that belonged to planned rows are dropped. Any scope drift shows up as a divergence.</summary>
    public static bool Verify(IReadOnlyList<Row> rows, IReadOnlyList<Planned> plan)
    {
        var gone = plan.Select(p => p.Id).ToHashSet();

        var expected = new List<string>();
        var original = new MudStreamParser();
        var keepLine = true;
        original.LineReady += line => { if (keepLine && !line.IsPartial) expected.Add(line.PlainText); };
        foreach (var row in rows.Where(r => r.Direction == (int)WireDirection.Rx))
        {
            keepLine = !gone.Contains(row.Id);
            original.Feed(row.Data);
        }

        var actual = new List<string>();
        var replay = new MudStreamParser();
        replay.LineReady += line => { if (!line.IsPartial) actual.Add(line.PlainText); };
        foreach (var row in rows.Where(r => r.Direction == (int)WireDirection.Rx && !gone.Contains(r.Id)))
            replay.Feed(row.Data);

        return expected.SequenceEqual(actual);
    }

    /// <summary>The comma list of a probe send, or null when these bytes are not one: the form is
    /// <c>ESC - [ FES[,FEW][,FEI] ESC - ]</c> and nothing else - the client-mode entry and a
    /// <c>value</c> command are command interrupts too, and are not probes.</summary>
    public static string? ProbeForm(byte[] data)
    {
        if (data.Length < 8 || data[0] != 0x1b || data[1] != (byte)'-' || data[2] != (byte)'[') return null;
        var text = Encoding.Latin1.GetString(data);
        var close = text.IndexOf("\x1b-]", StringComparison.Ordinal);
        if (close < 3 || close + 3 != text.Length) return null;
        var body = text[3..close];
        foreach (var part in body.Split(','))
            if (part is not ("FES" or "FEW" or "FEI" or "FEX")) return null;
        return body;
    }

    // -- Storage ------------------------------------------------------------------------------------

    private const long MillisecondsPerDay = 86_400_000L;

    private static SqliteConnection Open(string dbPath)
    {
        var connection = new SqliteConnection(MuckaDb.ConnectionString(dbPath));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=10000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static bool HasTrimmedProbes(SqliteConnection connection, long run)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS (SELECT 1 FROM trimmed WHERE mucka_run_id = $run AND reason IN ($send, $repeat));";
        command.Parameters.AddWithValue("$run", run);
        command.Parameters.AddWithValue("$send", ReasonSend);
        command.Parameters.AddWithValue("$repeat", ReasonRepeat);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static List<Row> ReadRows(SqliteConnection connection, long run)
    {
        var rows = new List<Row>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, seq, direction, data FROM wire WHERE mucka_run_id = $run ORDER BY id;";
        command.Parameters.AddWithValue("$run", run);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add(new Row(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetFieldValue<byte[]>(3)));
        return rows;
    }

    private static long Move(SqliteConnection connection, long run, List<Planned> plan, long nowMs,
        CancellationToken cancellationToken)
    {
        long moved = 0;
        for (var at = 0; at < plan.Count; at += RowsPerTransaction)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var transaction = connection.BeginTransaction();
            using var copy = connection.CreateCommand();
            copy.Transaction = transaction;
            copy.CommandText = """
                INSERT OR IGNORE INTO trimmed (id, mucka_run_id, seq, ts_ms, direction, data, trimmed_ms, reason)
                SELECT id, mucka_run_id, seq, ts_ms, direction, data, $now, $reason FROM wire
                WHERE id = $id AND mucka_run_id = $run AND seq = $seq;
                """;
            copy.Parameters.AddWithValue("$now", nowMs);
            copy.Parameters.AddWithValue("$run", run);
            var reason = copy.Parameters.Add("$reason", SqliteType.Text);
            var copyId = copy.Parameters.Add("$id", SqliteType.Integer);
            var copySeq = copy.Parameters.Add("$seq", SqliteType.Integer);
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = SameRowInTrimmed("DELETE FROM wire WHERE id = $id AND mucka_run_id = $run AND seq = $seq");
            delete.Parameters.AddWithValue("$run", run);
            var deleteId = delete.Parameters.Add("$id", SqliteType.Integer);
            var deleteSeq = delete.Parameters.Add("$seq", SqliteType.Integer);

            foreach (var item in plan.Skip(at).Take(RowsPerTransaction))
            {
                reason.Value = item.Reason;
                copyId.Value = item.Id;
                copySeq.Value = item.Seq;
                deleteId.Value = item.Id;
                deleteSeq.Value = item.Seq;
                // The row goes only once trimmed holds a copy of this very row. A copy already there
                // from an unwrap is the original bytes and stands; a different row's copy on the same
                // id leaves the row where it is.
                copy.ExecuteNonQuery();
                moved += delete.ExecuteNonQuery();
            }
            transaction.Commit();
            WireMaintenance.Pause();   // MUST GO AWAY
        }
        return moved;
    }

    // Narrows a statement on one wire row to when trimmed holds a copy of that same row: its id, run
    // and seq agree. Ids of the last rows of wire are reused once deleted, so an id alone does not
    // say whose copy it is.
    private static string SameRowInTrimmed(string statement)
        => statement + " AND EXISTS (SELECT 1 FROM trimmed t WHERE t.id = wire.id"
           + " AND t.mucka_run_id = wire.mucka_run_id AND t.seq = wire.seq);";

    private static long Rewrite(SqliteConnection connection, long run, List<WireUnwrap.Changed> changes, long nowMs,
        CancellationToken cancellationToken)
    {
        long changed = 0;
        for (var at = 0; at < changes.Count; at += RowsPerTransaction)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var transaction = connection.BeginTransaction();
            using var copy = connection.CreateCommand();
            copy.Transaction = transaction;
            copy.CommandText = """
                INSERT OR IGNORE INTO trimmed (id, mucka_run_id, seq, ts_ms, direction, data, trimmed_ms, reason)
                SELECT id, mucka_run_id, seq, ts_ms, direction, data, $now, $reason FROM wire
                WHERE id = $id AND mucka_run_id = $run AND seq = $seq;
                """;
            copy.Parameters.AddWithValue("$now", nowMs);
            copy.Parameters.AddWithValue("$run", run);
            copy.Parameters.AddWithValue("$reason", ReasonUnwrap);
            var copyId = copy.Parameters.Add("$id", SqliteType.Integer);
            var copySeq = copy.Parameters.Add("$seq", SqliteType.Integer);
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = SameRowInTrimmed("UPDATE wire SET data = $data WHERE id = $id AND mucka_run_id = $run AND seq = $seq");
            update.Parameters.AddWithValue("$run", run);
            var data = update.Parameters.Add("$data", SqliteType.Blob);
            var updateId = update.Parameters.Add("$id", SqliteType.Integer);
            var updateSeq = update.Parameters.Add("$seq", SqliteType.Integer);

            foreach (var item in changes.Skip(at).Take(RowsPerTransaction))
            {
                copyId.Value = item.Id;
                copySeq.Value = item.Seq;
                copy.ExecuteNonQuery();
                data.Value = item.Data;
                updateId.Value = item.Id;
                updateSeq.Value = item.Seq;
                changed += update.ExecuteNonQuery();
            }
            transaction.Commit();
            WireMaintenance.Pause();   // MUST GO AWAY
        }
        return changed;
    }

    // -- What a probe response said ------------------------------------------------------------------

    private static bool SameSet(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        var left = a.OrderBy(x => x, StringComparer.Ordinal);
        var right = b.OrderBy(x => x, StringComparer.Ordinal);
        return left.SequenceEqual(right);
    }

    // (did anything change, was ttr the only thing that changed)
    private static (bool Changed, bool TtrOnly) StatsChange(GameStatsSnapshot? before, GameStatsSnapshot after)
    {
        if (before is null) return (true, false);
        var ttrMoved = before.TimeToReset != after.TimeToReset;
        // Every member but the time to reset, by the record's own equality: a field added to the
        // snapshot is compared without anyone remembering to list it here.
        var other = before with { TimeToReset = after.TimeToReset } != after;
        return (ttrMoved || other, ttrMoved && !other);
    }
}
