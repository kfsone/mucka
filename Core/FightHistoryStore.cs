using System.Text.Json;
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
/// cannot stall the UI thread (Invariant #1).</para>
/// </summary>
public sealed class FightHistoryStore
{
    private readonly object _lock = new();
    private readonly string _filePath;
    // Injected rather than calling CrashLog directly so this type stays free of MAUI references
    // and can be exercised against a temp directory in mudsharp.Tests.
    private readonly Action<string, Exception>? _onError;

    // Copy-on-write: readers take a reference under the lock and then enumerate freely, so a UI
    // thread query can never see a torn list mid-append and never blocks the Feed thread.
    private List<FightRecord> _records = [];

    public FightHistoryStore(string filePath, Action<string, Exception>? onError = null)
    {
        _filePath = filePath;
        _onError = onError;
    }

    /// <summary>Standard file name, alongside the per-encounter clogs. The directory is supplied by
    /// the caller (see MuckaConnection) so this type needs no platform/MAUI path lookup of its own
    /// and can be linked into mudsharp.Tests as-is.</summary>
    public const string DefaultFileName = "fights.jsonl";

    public string FilePath => _filePath;

    /// <summary>Rows loaded so far. Cheap: returns the current immutable-by-convention list.</summary>
    public IReadOnlyList<FightRecord> Snapshot()
    {
        lock (_lock)
            return _records;
    }

    /// <summary>Reads the whole index into memory. Call once at startup, OFF the UI thread. A
    /// malformed line is skipped rather than aborting the load: a single truncated row (killed
    /// mid-write by a crash) must not cost the user their entire accumulated history.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var loaded = new List<FightRecord>();
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
                        var record = JsonSerializer.Deserialize<FightRecord>(line);
                        if (record is not null)
                            loaded.Add(record);
                    }
                    catch (JsonException)
                    {
                        // Skip and keep going — see method remarks.
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

        lock (_lock)
        {
            // Anything appended while we were reading would be lost by a blind assignment, so keep
            // the in-memory rows that the load did not already account for.
            if (_records.Count > 0)
                loaded.AddRange(_records);
            _records = loaded;
        }
    }

    /// <summary>Appends one completed fight, in memory and to disk. Never throws: losing a history
    /// row is strictly less bad than disrupting play.</summary>
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
        }

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            // No BOM, one object per line — ingest_clogs.py reads these with a plain json.loads
            // per line, which chokes on a BOM prefix.
            File.AppendAllText(_filePath, line + Environment.NewLine, new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _onError?.Invoke("FightHistoryStore.Append", ex);
        }
    }
}
