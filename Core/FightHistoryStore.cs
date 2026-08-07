using System.Text.Json;
using System.Threading.Channels;
using MudSharp.Combat;

namespace Mucka.Core;

/// <summary>
/// Owns ~/.mucka/clogs/fights.jsonl: the append-only per-fight history index the live HUD contrasts
/// the current fight against, and the source of the NPC stamina-pool estimates any future
/// "are you winning" projection will need (MUD2 never reports NPC stamina, so prior kills are the
/// only route to a pool figure at all — see tools/combat/STATS_DESIGN.md).
///
/// <para>A flat JSONL file rather than SQLite in the client: no new dependency, no MAUI/Android
/// packaging question, small enough to hold in memory (one compact line per fight), and
/// tools/combat/ingest_clogs.py can read it directly so live and offline analysis agree by
/// construction instead of by reimplementation.</para>
///
/// <para>Threading: <see cref="Append"/> is called from the session Feed thread (same contract as
/// ClogWriter), <see cref="Snapshot"/> from the UI thread. Both take the same lock, which is only
/// ever held for a list add or a copy-reference — never across the file write, so a slow disk
/// cannot stall the UI thread (Invariant #1). The file write itself runs on a single dedicated
/// background task (<see cref="DrainAsync"/>): <see cref="Append"/> only serializes the record
/// (cheap, in-memory) and enqueues the line, so the Feed thread that parses incoming combat text
/// never pays for the actual disk I/O (DESIGN_FINAL.md section 7.5). <see cref="Dispose"/> blocks
/// briefly to drain whatever is still queued, so an app exit mid-fight cannot lose the row for the
/// fight that was open at that moment - see its remarks.</para>
/// </summary>
public sealed class FightHistoryStore : IDisposable
{
    private readonly object _lock = new();
    private readonly string _filePath;
    // Injected rather than calling CrashLog directly so this type stays free of MAUI references
    // and can be exercised against a temp directory in mudsharp.Tests.
    private readonly Action<string, Exception>? _onError;

    // One dedicated writer for the whole lifetime of this store (unlike ClogWriter's per-encounter
    // rotation, there is exactly one fights.jsonl for the whole session) - Append enqueues, DrainAsync
    // writes. Unbounded: a fight resolves at most a few times a minute even in a pack fight, so
    // there is no realistic burst this needs to backpressure.
    private readonly Channel<string> _writeQueue =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _writerTask;

    // Copy-on-write: readers take a reference under the lock and then enumerate freely, so a UI
    // thread query can never see a torn list mid-append and never blocks the Feed thread.
    private List<FightRecord> _records = [];

    // The incremental replacement for "filter the whole corpus, then scan it three times" (see
    // MudSharp.Combat.HistoryIndex's own remarks and DESIGN_FINAL.md section 7.3). Guarded by the
    // SAME _lock as _records - every mutation (LoadAsync's initial build, Append's per-fight insert)
    // and every read (GetHistoryContext) takes it, so this never needs its own synchronization.
    private readonly HistoryIndex _index = new();

    public FightHistoryStore(string filePath, Action<string, Exception>? onError = null)
    {
        _filePath = filePath;
        _onError = onError;
        _writerTask = Task.Run(DrainAsync);
    }

    /// <summary>Standard file name, alongside the per-encounter clogs. The directory is supplied by
    /// the caller (see MuckaConnection) so this type needs no platform/MAUI path lookup of its own
    /// and can be linked into mudsharp.Tests as-is.</summary>
    public const string DefaultFileName = "fights.jsonl";

    public string FilePath => _filePath;

    /// <summary>Set by <see cref="LoadAsync"/> when it discarded a stale-format file (see
    /// <see cref="FightRecord.FormatVersion"/>'s remarks). Null on an ordinary load. This is the
    /// "log clearly to the user" half of the discard: the owner authorised deleting old-format
    /// history outright, but never SILENTLY - a caller should surface this (e.g. GameViewModel
    /// prints it as a local system line once the load completes).</summary>
    public string? MigrationNotice { get; private set; }

    /// <summary>Rows loaded so far. Cheap: returns the current immutable-by-convention list.</summary>
    public IReadOnlyList<FightRecord> Snapshot()
    {
        lock (_lock)
            return _records;
    }

    /// <summary>Reads the whole index into memory. Call once at startup, OFF the UI thread. A
    /// malformed line is skipped rather than aborting the load: a single truncated row (killed
    /// mid-write by a crash) must not cost the user their entire accumulated history.
    ///
    /// <para>Format check: the first successfully-parsed row's format_version gates the WHOLE file.
    /// This is an append-only log written by one build at a time, so once one row predates the
    /// current schema every row before it does too - there is nothing to gain from parsing the rest
    /// before discarding it (see <see cref="MigrationNotice"/>). The presence check is against the
    /// RAW JSON, not <see cref="FightRecord.FormatVersion"/> on the deserialized object: that
    /// property has a C# default of <see cref="FightRecord.CurrentFormatVersion"/> so a freshly
    /// <c>new</c>'d record is correctly current-by-default, but the SAME default means System.Text.Json
    /// leaves it at that value for a row whose JSON never mentions "format_version" at all (init
    /// properties only get overwritten when the JSON actually contains the key) - reading it off the
    /// materialized object would therefore never detect a truly old row.</para></summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var loaded = new List<FightRecord>();
        var staleVersion = (int?)null;
        try
        {
            if (File.Exists(_filePath))
            {
                await foreach (var line in File.ReadLinesAsync(_filePath, cancellationToken).ConfigureAwait(false))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        // Absent entirely = a totally pre-versioning row; map that to "1" for the
                        // message/filename, matching the design's own ".v1.bak" example rather than
                        // surfacing an internal "v0". Present-but-old (a future v3+ build reading a
                        // v2 file) reads the real value straight from the JSON.
                        var hasVersion = document.RootElement.TryGetProperty("format_version", out var versionElement);
                        var version = hasVersion ? versionElement.GetInt32() : 1;
                        if (version < FightRecord.CurrentFormatVersion)
                        {
                            staleVersion = version;
                            break;
                        }

                        var record = JsonSerializer.Deserialize<FightRecord>(document.RootElement);
                        if (record is not null)
                            loaded.Add(record);
                    }
                    catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
                    {
                        // Skip and keep going - see method remarks. FormatException/
                        // InvalidOperationException cover a malformed "format_version" value (e.g.
                        // not a number) from GetInt32() above; JsonException covers everything else
                        // a truncated/corrupt line can throw.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort feature: an unreadable history file must never block getting into a game.
            _onError?.Invoke("FightHistoryStore.Load", ex);
            return;
        }

        if (staleVersion is int oldVersion)
        {
            // Discard every on-disk row (the owner explicitly authorised this rather than carrying
            // a schema gap forward) - but never silently: MigrationNotice records what happened and
            // why for the caller to surface, and the file itself is moved aside rather than deleted
            // outright wherever that is possible (RenameStaleFileAsideLocked falls back to clearing
            // it in place only if the rename itself cannot succeed).
            loaded.Clear();
            MigrationNotice = RenameStaleFileAside(oldVersion);
        }

        lock (_lock)
        {
            // Build the index from the freshly-read DISK rows now, BEFORE merging in anything that
            // was appended concurrently while this method was reading (those already inserted
            // themselves via Append's own _index.Insert call - see there - so inserting them again
            // here would double-count them). One-time cost, off the UI thread, same as the rest of
            // this method (DESIGN_FINAL.md 7.3's "startup" bullet).
            foreach (var record in loaded)
                _index.Insert(record);

            // Anything appended while we were reading would be lost by a blind assignment, so keep
            // the in-memory rows that the load did not already account for. These are always
            // current-format (BuildRecord only ever constructs current-format rows), so they are
            // kept even when the ON-DISK rows just above were discarded as stale.
            if (_records.Count > 0)
                loaded.AddRange(_records);
            _records = loaded;
        }
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

    /// <summary>Moves the stale-format file aside to "&lt;name&gt;.v{oldVersion}.bak" so a future
    /// Append starts a clean current-format file at the original path, without destroying the old
    /// data outright (the owner authorised deletion, but "prefer renaming aside" per the brief).
    /// Falls back to clearing the file in place if the rename itself cannot succeed (e.g. locked by
    /// another process) - continuing to append current-format rows onto a stale-format file would
    /// leave a mixed-schema file behind, which is worse than losing the old rows entirely. Returns
    /// the human-readable notice for the caller to surface; never throws.</summary>
    private string? RenameStaleFileAside(int oldVersion)
    {
        var fileName = Path.GetFileName(_filePath);
        var target = $"{_filePath}.v{oldVersion}.bak";
        try
        {
            if (File.Exists(target))
                // A previous migration already left a backup at the obvious name - stamp this one
                // with a timestamp instead of silently overwriting an earlier discard.
                target = $"{_filePath}.v{oldVersion}.{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
            File.Move(_filePath, target);
            return $"{fileName} was schema v{oldVersion} (current is v{FightRecord.CurrentFormatVersion}) - " +
                   $"moved aside to {Path.GetFileName(target)} and started a fresh history. " +
                   "The old file is still on disk if you want to re-ingest it with tools/combat.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _onError?.Invoke("FightHistoryStore.RenameStaleFileAside", ex);
            try
            {
                // Could not rename it (e.g. the file is locked) - clear it in place instead, so the
                // next Append at least starts a clean current-format file rather than silently
                // mixing schemas at the same path.
                File.WriteAllText(_filePath, string.Empty);
                return $"{fileName} was schema v{oldVersion} (current is v{FightRecord.CurrentFormatVersion}) - " +
                       "could not move it aside, so it was cleared in place. A fresh history starts now.";
            }
            catch (Exception clearEx) when (clearEx is IOException or UnauthorizedAccessException)
            {
                _onError?.Invoke("FightHistoryStore.RenameStaleFileAside.Clear", clearEx);
                return $"{fileName} is schema v{oldVersion} (current is v{FightRecord.CurrentFormatVersion}) and " +
                       "could not be moved or cleared - its old rows will keep being ignored on every load.";
            }
        }
    }

    /// <summary>Appends one completed fight: updates the in-memory snapshot immediately (so a
    /// same-thread Snapshot() right after this call sees it - Invariant #1 does not apply to the
    /// Feed thread doing its own cheap bookkeeping) and enqueues the disk write for
    /// <see cref="DrainAsync"/> to perform off-thread. Never throws: losing a history row is
    /// strictly less bad than disrupting play.</summary>
    public void Append(FightRecord record)
    {
        string line;
        try
        {
            line = JsonSerializer.Serialize(record);
        }
        catch (Exception ex)
        {
            _onError?.Invoke("FightHistoryStore.Serialize", ex);
            return;
        }

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

        // Off the Feed thread from here: DrainAsync owns the actual disk write. TryWrite on an
        // unbounded channel never blocks and only fails after Complete() (Dispose only - by which
        // point nothing should still be calling Append, since MuckaConnection disposes this after
        // the session/recorder that drives it).
        _writeQueue.Writer.TryWrite(line);
    }

    /// <summary>The single background writer for this store's whole lifetime. Drains lines in the
    /// order Append enqueued them and appends each to disk. Runs until <see cref="Dispose"/> closes
    /// the queue AND every already-queued line has been written - <c>ReadAllAsync</c> completes
    /// normally (no exception) only once both are true, which is exactly the "prove no data lost on
    /// shutdown" property this class needs.</summary>
    private async Task DrainAsync()
    {
        await foreach (var line in _writeQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                // No BOM, one object per line - ingest_clogs.py reads these with a plain json.loads
                // per line, which chokes on a BOM prefix.
                File.AppendAllText(_filePath, line + Environment.NewLine, new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _onError?.Invoke("FightHistoryStore.Append", ex);
            }
        }
    }

    /// <summary>Blocks (briefly - just draining whatever is already queued in memory, typically a
    /// handful of lines at most) until every fight <see cref="Append"/>ed so far has actually been
    /// written to disk. This is the fix for fight rows being lost when the app exits mid-fight:
    /// without this wait, an Append() immediately followed by process exit could beat the
    /// background writer to the punch, since Append only enqueues rather than writing directly.</summary>
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
