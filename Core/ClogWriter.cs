using System.Text.Json;
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
/// </summary>
public sealed class ClogWriter : IDisposable
{
    private const int PreBufferLines = 30;

    private readonly object _lock = new();
    private readonly Queue<string> _recentLines = new();
    private StreamWriter? _writer;

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
            if (_writer == null)
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
            try
            {
                var dir = GetClogDirectory();
                Directory.CreateDirectory(dir);
                var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                FilePath = Path.Combine(dir, $"clog.{timestamp}.jsonl");
                // No BOM: these files are meant to be read line-by-line by plain JSON parsers
                // (json.loads chokes on a BOM prefixed to the first line without utf-8-sig).
                _writer = new StreamWriter(FilePath, append: false, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
                IsRecording = true;

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
                _writer?.Dispose();
                _writer = null;
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
            _writer?.Dispose();
            _writer = null;
            // The pre-roll buffer only ever holds non-combat lines (OnLineReady's guard), so it
            // is already empty of this encounter's own combat text — safe to keep accumulating
            // fresh context for the next encounter without an explicit clear.
        }
    }

    private void WriteEntryLocked(object entry)
    {
        if (_writer == null)
            return;
        _writer.WriteLine(JsonSerializer.Serialize(entry));
    }

    public void Dispose() => Stop();
}
