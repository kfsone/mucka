using System.Text.Json;
using System.Threading.Channels;
using MudSharp.Combat;
using MudSharp.Models;

namespace Mucka.Core;

/// <summary>
/// Records one JSONL "clog" (combat log) per encounter under ~/.mucka/clogs/, so real fights can
/// be analyzed offline with the same tooling used on RESEARCH/mud2-multi-combat.jsonl
/// (tools/combat/). Driven entirely by MudSession/MuckaConnection events — see MuckaConnection's
/// wiring for CombatTracker's InCombatChanged/CombatEventOccurred.
///
/// <para>Each file has one header line (type "encounter_start": the previous ~30 non-combat
/// lines plus a snapshot of stats/status-effects/room at the moment combat began — everything
/// a later analysis pass needs to answer "was the player invisible / what was the weather / what
/// were their stats" without replaying the whole session), one line per classified CombatEvent
/// (type "event"), and one footer line (type "encounter_end").</para>
///
/// <para>Deliberately partial: this is not a full raw capture (SessionCapture already covers
/// that, opt-in, for debugging). A clog is intentionally reduced to what tools/combat's analysis
/// needs, per the user's request to keep these lightweight enough to accumulate over many
/// sessions.</para>
///
/// <para>Opt-in via <see cref="SetEnabled"/> (the "$clog on"/"$clog off" command — see
/// GameViewModel). Disabled by default: encounters are only recorded, and the item-eval
/// ("$clog eval") data collection only writes to items.jsonl, while a session has explicitly
/// turned clogging on.</para>
///
/// <para>Threading: all On* methods are called from MudSession's Feed thread (same contract as
/// EffectTracker/CombatTracker — see MudSession's class doc comment). Does not touch UI types.</para>
///
/// <para>File I/O runs off the Feed thread entirely: <see cref="WriteEntryLocked"/> only serializes
/// the entry (cheap, in-memory) and enqueues the line; a per-encounter background task
/// (<see cref="DrainAsync"/>) owns the actual <see cref="StreamWriter"/> and does the blocking disk
/// write. Before this, every clog line paid a synchronous open/flush on the SAME thread that parses
/// incoming combat text - stalling that thread delays the combat text itself, which no UI-side
/// throttle can fix (DESIGN_FINAL.md section 7.5). <see cref="Dispose"/> waits (briefly - just
/// draining whatever is already queued in memory) for the most recent encounter's drain to finish,
/// so an app exit mid-fight cannot lose the encounter_end line or anything queued just before it.
/// </para>
/// </summary>
public sealed class ClogWriter : IDisposable
{
    private const int PreBufferLines = 30;

    private readonly object _lock = new();
    private readonly Queue<string> _recentLines = new();
    // Non-null exactly while an encounter is being recorded - this encounter's own queue, drained
    // by its own DrainAsync task. Stop() completes it and hands the underlying StreamWriter's
    // lifetime entirely to that task (see Stop()'s remarks); a NEW Start() gets a fresh queue/task,
    // never reusing a completed one.
    private Channel<string>? _writeQueue;
    // The most recently started encounter's drain task. Dispose() waits on THIS, not on every task
    // ever created - by the time Dispose runs, at most one can still be draining (Stop() always
    // finishes the previous one's queue before Start() can begin a new one, both under _lock).
    private Task? _writerTask;

    private GameStatsSnapshot _lastStats = GameStatsSnapshot.Empty;
    private StatusEffectState _lastEffects = StatusEffectState.Empty;
    private string? _lastRoom;

    // Off by default: clogging (and the item-eval data collection it enables) is now an
    // opt-in "$clog on" session, not an always-on background feature — see GameViewModel's
    // $clog command. Gates Start/Stop and the pre-roll buffer entirely so an idle, un-clogged
    // session does zero extra work per line.
    private bool _enabled;

    public bool Enabled => _enabled;
    public bool IsRecording { get; private set; }
    public string? FilePath { get; private set; }

    /// <summary>Turn clogging on/off (the "$clog on"/"$clog off" command). Disabling while an
    /// encounter is mid-recording closes it immediately (writes encounter_end) so a clog file
    /// is never left dangling because the user turned clogging off mid-fight.</summary>
    public void SetEnabled(bool enabled)
    {
        if (OperatingSystem.IsAndroid() && enabled)
            enabled = false; // Android keeps clogging explicitly disabled for now.

        lock (_lock)
        {
            if (_enabled == enabled)
                return;
            _enabled = enabled;
            if (!_enabled && IsRecording)
                Stop();
        }
    }

    /// <summary>Human-readable status line for "$clog" / "$clog status".</summary>
    public string DescribeStatus()
    {
        lock (_lock)
        {
            if (!_enabled)
                return "off (use '$clog on' to start recording combat encounters)";
            return IsRecording
                ? $"on — recording {FilePath}"
                : "on — armed, waiting for the next encounter";
        }
    }

    /// <summary>~/.mucka/clogs (desktop) — shared by encounter clogs and the "$clog eval"
    /// item-stats log (items.jsonl), so both live side by side under the same opt-in toggle.</summary>
    internal static string GetClogDirectory()
    {
        // Desktop: literally ~/.mucka/clogs, matching the offline research tooling's
        // ~/.mucka/mapping and ~/.mucka/combat convention (tools/mapping, tools/combat).
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mucka", "clogs");

        // Mobile: no home-directory concept — use the platform cache directory instead,
        // same rationale as SessionCapture.GetCaptureDirectory.
        return Path.Combine(FileSystem.Current.CacheDirectory, "mucka", "clogs");
    }

    /// <summary>Feed every non-combat line so the pre-roll buffer stays fresh. Cheap: a bounded
    /// queue, no allocation beyond the string already produced by the parser.</summary>
    public void OnLineReady(StyledLine line)
    {
        if (!_enabled || IsRecording)
            return; // combat lines are recorded via OnCombatEvent instead — no double-logging
        var text = line.PlainText;
        if (string.IsNullOrEmpty(text))
            return;
        lock (_lock)
        {
            _recentLines.Enqueue(text);
            while (_recentLines.Count > PreBufferLines)
                _recentLines.Dequeue();
        }
    }

    public void OnStatsUpdated(GameStatsSnapshot stats) => _lastStats = stats;
    public void OnStatusEffectsChanged(StatusEffectState effects) => _lastEffects = effects;
    public void OnRoomShortReady(string room) => _lastRoom = room;

    /// <summary>Wire directly to MudSession/MuckaConnection's InCombatChanged.</summary>
    public void OnInCombatChanged(bool inCombat)
    {
        if (!_enabled)
            return;
        if (inCombat)
            Start();
        else
            Stop();
    }

    /// <summary>Wire directly to MudSession/MuckaConnection's CombatEventOccurred.</summary>
    public void OnCombatEvent(CombatEvent e)
    {
        lock (_lock)
        {
            if (_writeQueue is null)
                return;
            WriteEntryLocked(new
            {
                type = "event",
                ts = new DateTimeOffset(e.TimestampUtc).ToUnixTimeMilliseconds(),
                kind = e.Kind.ToString(),
                actor = e.Actor?.ToString(),
                npc = e.NpcName,
                weapon = e.Weapon,
                rangeLow = e.RangeLow,
                rangeHigh = e.RangeHigh,
                // NpcHealth only. Written even though `raw` carries the same sentence, because the
                // rung is the interpretation and it is worth being able to re-read the corpus without
                // re-deriving it - the health ladder's ordering was itself established from records
                // like these.
                healthRung = e.HealthRung,
                healthPhrase = e.HealthPhrase,
                raw = e.RawText,
            });
        }
    }

    private void Start()
    {
        lock (_lock)
        {
            if (IsRecording)
                return;
            StreamWriter? writer = null;
            try
            {
                var dir = GetClogDirectory();
                Directory.CreateDirectory(dir);
                var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                FilePath = Path.Combine(dir, $"clog.{timestamp}.jsonl");
                // No BOM: these files are meant to be read line-by-line by plain JSON parsers
                // (json.loads chokes on a BOM prefixed to the first line without utf-8-sig).
                // AutoFlush is deliberately OFF: DrainAsync below owns flushing, off this thread.
                writer = new StreamWriter(FilePath, append: false, new System.Text.UTF8Encoding(false)) { AutoFlush = false };
                var queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                });
                _writeQueue = queue;
                IsRecording = true;
                // One background task per encounter, owning `writer` for its whole lifetime -
                // Stop() below hands it off entirely (never touches `writer` or `_writeQueue` again
                // once this is running) rather than sharing mutable state across threads.
                _writerTask = Task.Run(() => DrainAsync(queue, writer));

                WriteEntryLocked(new
                {
                    type = "encounter_start",
                    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    preroll = _recentLines.ToArray(),
                    room = _lastRoom,
                    weather = _lastStats.Weather.ToString(),
                    stats = new
                    {
                        stamina = _lastStats.Stamina,
                        maxStamina = _lastStats.MaxStamina,
                        strength = _lastStats.Strength,
                        rawStrength = _lastStats.RawStrength,
                        maxStrength = _lastStats.MaxStrength,
                        dexterity = _lastStats.Dexterity,
                        rawDexterity = _lastStats.RawDexterity,
                        maxDexterity = _lastStats.MaxDexterity,
                        magic = _lastStats.CurrentMagic,
                        maxMagic = _lastStats.MaxMagic,
                        weightCarriedGrams = _lastStats.WeightCarriedGrams,
                        maxWeightGrams = _lastStats.MaxWeightGrams,
                        objectsCarried = _lastStats.ObjectsCarried,
                        maxObjectsCarried = _lastStats.MaxObjectsCarried,
                        level = _lastStats.Level,
                        gamesPlayed = _lastStats.GamesPlayed,
                        isBlind = _lastStats.IsBlind,
                        isDeaf = _lastStats.IsDeaf,
                        isCrippled = _lastStats.IsCrippled,
                        isDumb = _lastStats.IsDumb,
                    },
                    effects = _lastEffects,
                });
            }
            catch
            {
                // _writerTask is deliberately NOT started at this point in any failure path (it is
                // the last statement before WriteEntryLocked, itself after everything that can
                // throw), so there is nothing running to cancel - just clean up what did get created.
                writer?.Dispose();
                _writeQueue = null;
                IsRecording = false;
                FilePath = null;
                // Best-effort feature: a clogging failure must never disrupt play.
            }
        }
    }

    private void Stop()
    {
        lock (_lock)
        {
            if (!IsRecording)
                return;
            WriteEntryLocked(new { type = "encounter_end", ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            IsRecording = false;
            // Signals DrainAsync that no more lines are coming for THIS encounter; it drains
            // whatever is already queued (including the encounter_end line just above) and then
            // flushes+disposes the writer itself, off this thread. _writerTask is left running -
            // Dispose() is what actually waits for it (see its remarks) - and _writeQueue/_writer
            // ownership both pass to that task now, so this class touches neither again for this
            // encounter.
            _writeQueue?.Writer.TryComplete();
            _writeQueue = null;
            // The pre-roll buffer only ever holds non-combat lines (OnLineReady's guard), so it
            // is already empty of this encounter's own combat text — safe to keep accumulating
            // fresh context for the next encounter without an explicit clear.
        }
    }

    /// <summary>Drains one encounter's queued lines to disk and closes its writer. Runs entirely
    /// off the Feed thread (Task.Run from Start()) - this is the actual fix for AutoFlush-per-line
    /// blocking the thread that parses incoming combat text (DESIGN_FINAL.md section 7.5).
    /// ReadAllAsync completes normally (no exception) once the channel is both completed (Stop's
    /// TryComplete) AND fully drained, so every queued line - including encounter_end - is written
    /// before the writer is flushed and disposed.</summary>
    private static async Task DrainAsync(Channel<string> queue, StreamWriter writer)
    {
        try
        {
            await foreach (var line in queue.Reader.ReadAllAsync().ConfigureAwait(false))
                writer.WriteLine(line);
        }
        catch
        {
            // Best-effort feature: a write fault here must never crash the drain loop or the
            // process - the try/finally below still flushes+closes whatever DID make it to disk.
        }
        finally
        {
            try { writer.Flush(); } catch { }
            try { writer.Dispose(); } catch { }
        }
    }

    private void WriteEntryLocked(object entry)
    {
        if (_writeQueue is null)
            return;
        // Cheap, in-memory, stays on the Feed thread; the actual disk write happens in DrainAsync.
        // TryWrite on an unbounded channel never blocks and only fails after Complete() - which
        // Stop() only calls after setting _writeQueue to null, so this branch is never reachable
        // with an already-completed queue.
        _writeQueue.Writer.TryWrite(JsonSerializer.Serialize(entry));
    }

    /// <summary>Stops any open encounter, then blocks (briefly - just draining whatever is already
    /// queued in memory, typically a handful of lines) until its background writer has actually
    /// flushed to disk. This is the fix for fight/clog rows being lost when the app exits mid-fight:
    /// without this wait, Stop()'s TryComplete only SIGNALS the drain to finish - it does not wait
    /// for it - so a process exit immediately after could beat DrainAsync to the punch.</summary>
    public void Dispose()
    {
        Task? pending;
        lock (_lock)
        {
            Stop();
            pending = _writerTask;
        }
        try
        {
            pending?.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Best-effort: Dispose must never throw during shutdown. Whatever did not get written
            // in time is lost, same as any other best-effort I/O failure in this class.
        }
    }
}
