using System.Linq;
using System.Text.RegularExpressions;

namespace Mucka.Core;

/// <summary>
/// Parses watchword rules from mucka.ini and matches incoming game lines,
/// queuing answers that can be retrieved via $slotname expansion in outgoing text.
///
/// Config format (./mucka.ini or ~/mucka.ini):
///
///   [watch]
///   slotname = trigger prefix...trigger suffix
///
///   [watch slotname]
///   key...   = answer    ; prefix match (key ends with ...)
///   key      = answer    ; exact match
///
/// A slot without a [watch slotname] subsection uses echo mode: the captured text
/// between the trigger delimiters is queued directly as the answer.
///
/// In outgoing command text, $slotname is replaced by the queued answer (then cleared).
/// </summary>
internal sealed class WatchwordStore
{
    private const int MaxCapture = 80;
    private const int MaxAnswer  = 48;
    private const int MaxSlots   = 20;
    private const int MaxAnswers = 32;

    private sealed class AnswerEntry
    {
        public required string Key    { get; init; }
        public required bool   Prefix { get; init; }
        public required string Answer { get; init; }
    }

    private sealed class Slot
    {
        public required string        Name    { get; init; }
        public required string        TrigPre { get; init; }
        public required string        TrigSuf { get; init; }
        public          bool          Echo    { get; set; } = true;
        public          List<AnswerEntry> Answers { get; } = [];
        public          string?       Queued  { get; set; }
    }

    private readonly List<Slot> _slots = [];
    private readonly object     _lock  = new();

    // Matches $identifier tokens: letter start, then letters/digits/underscores
    private static readonly Regex SlotRefRegex = new(
        @"\$([A-Za-z][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Any run of whitespace — collapsed to a single space before matching so that
    // server-side line wrapping (and double spacing) cannot break trigger matches.
    private static readonly Regex WhitespaceRunRegex = new(
        @"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Collapses every run of whitespace to a single space (no trimming, so a trigger's
    /// meaningful leading/trailing space survives). Applied to both the scanned text and
    /// the configured trigger/key strings so the two sides always agree on spacing,
    /// regardless of where the server wrapped a line.
    /// </summary>
    private static string NormalizeWhitespace(string s)
        => WhitespaceRunRegex.Replace(s, " ");

    private WatchwordStore() { }

    public static WatchwordStore Load()
    {
        var store = new WatchwordStore();
        var paths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "mucka.ini"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "mucka.ini"),
        };
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                store.LoadFile(path);
                break;
            }
        }
        return store;
    }

    private void LoadFile(string path)
    {
        int   state   = 0;   // 0=none  1=[watch]  2=[watch name]
        Slot? current = null;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                continue;

            if (line[0] == '[')
            {
                var header = line.Trim('[', ']').Trim();
                if (header.Equals("watch", StringComparison.OrdinalIgnoreCase))
                {
                    state   = 1;
                    current = null;
                }
                else if (header.StartsWith("watch ", StringComparison.OrdinalIgnoreCase))
                {
                    var name = header[6..].Trim();
                    current = FindSlot(name);
                    if (current != null)
                    {
                        current.Echo = false;
                        state        = 2;
                    }
                    else
                        state = 0;
                }
                else
                    state = 0;
                continue;
            }

            var eqIdx = line.IndexOf(" = ", StringComparison.Ordinal);
            if (eqIdx < 0) continue;

            var key = line[..eqIdx].TrimEnd();
            var val = line[(eqIdx + 3)..].TrimStart();

            if (state == 1)
            {
                if (_slots.Count >= MaxSlots) continue;
                ParseTrigger(key, val);
            }
            else if (state == 2 && current != null)
            {
                if (current.Answers.Count >= MaxAnswers) continue;
                if (key.Length >= 2 && key[0] == '"' && key[^1] == '"')
                    key = key[1..^1];
                var prefix    = key.EndsWith("...", StringComparison.Ordinal);
                var entryKey  = NormalizeWhitespace(prefix ? key[..^3] : key);
                var entryAns  = val.Length > MaxAnswer ? val[..MaxAnswer] : val;
                current.Answers.Add(new AnswerEntry { Key = entryKey, Prefix = prefix, Answer = entryAns });
            }
        }
    }

    private void ParseTrigger(string name, string pattern)
    {
        var dotIdx = pattern.IndexOf("...", StringComparison.Ordinal);
        var pre    = dotIdx >= 0 ? pattern[..dotIdx]      : pattern;
        var suf    = dotIdx >= 0 ? pattern[(dotIdx + 3)..] : string.Empty;
        _slots.Add(new Slot { Name = name, TrigPre = NormalizeWhitespace(pre), TrigSuf = NormalizeWhitespace(suf) });
    }

    private Slot? FindSlot(string name)
        => _slots.Find(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Scan <paramref name="text"/> against all loaded triggers, queuing every match found.
    /// Returns one entry per matched slot. Called on the UI thread via $&lt;.
    /// </summary>
    public List<(string Slot, string Answer)> ScanAll(string text)
    {
        var results = new List<(string, string)>();
        if (_slots.Count == 0) return results;

        // Triggers and answer keys are whitespace-normalized at load; normalize the scanned
        // text the same way so wrap-induced spacing differences cannot defeat a match.
        text = NormalizeWhitespace(text);

        lock (_lock)
        {
            foreach (var slot in _slots)
            {
                var capture = MatchTrigger(text, slot.TrigPre, slot.TrigSuf);
                if (capture == null) continue;

                string answer;
                if (slot.Echo)
                {
                    answer = capture;
                }
                else
                {
                    var a = LookupAnswer(slot, capture);
                    if (a == null) continue;
                    answer = a;
                }

                slot.Queued = answer;
                results.Add((slot.Name, answer));
            }
        }
        return results;
    }

    private static string? MatchTrigger(string text, string pre, string suf)
    {
        if (pre.Length == 0 && suf.Length == 0) return null;

        var start = pre.Length > 0
            ? text.IndexOf(pre, StringComparison.Ordinal)
            : 0;
        if (start < 0) return null;
        start += pre.Length;

        var end = suf.Length > 0
            ? text.IndexOf(suf, start, StringComparison.Ordinal)
            : text.Length;
        if (end < 0) return null;

        // Trim both ends: rejoining wrapped lines can leave a stray space at either
        // boundary of the capture (e.g. when the wrap fell right after the prefix).
        // Clamp over-long captures rather than rejecting them: inscriptions can run
        // well past MaxCapture (the north-tomb canal puzzle is ~150 chars) and prefix
        // keys only need the opening; an exact-match key couldn't have matched anyway.
        var capture = text[start..end].Trim();
        return capture.Length > MaxCapture ? capture[..MaxCapture].TrimEnd() : capture;
    }

    private static string? LookupAnswer(Slot slot, string capture)
    {
        foreach (var e in slot.Answers)
        {
            if (e.Prefix ? capture.StartsWith(e.Key, StringComparison.Ordinal)
                         : capture.Equals(e.Key, StringComparison.Ordinal))
                return e.Answer;
        }
        return null;
    }

    /// <summary>
    /// Expand $slotname tokens in <paramref name="text"/>.
    /// Each recognised slot whose queued answer is non-null has its $token replaced
    /// and its queue cleared. Unrecognised tokens or empty slots are left as-is.
    /// Thread-safe — called from the UI thread.
    /// </summary>
    public string ExpandSlots(string text)
    {
        if (_slots.Count == 0 || !text.Contains('$'))
            return text;

        lock (_lock)
        {
            return SlotRefRegex.Replace(text, m =>
            {
                var slot = FindSlot(m.Groups[1].Value);
                if (slot?.Queued is { } queued)
                {
                    slot.Queued = null;
                    return queued;
                }
                return m.Value;
            });
        }
    }

    /// <summary>
    /// Retrieve and clear the queued answer for <paramref name="name"/>.
    /// Returns the answer string, or null if nothing is queued.
    /// </summary>
    public string? Speak(string name)
    {
        lock (_lock)
        {
            var slot = FindSlot(name);
            if (slot?.Queued is { } queued)
            {
                slot.Queued = null;
                return queued;
            }
            return null;
        }
    }

    /// <summary>Returns the names of all loaded slots (in config order).</summary>
    public string[] SlotNames
    {
        get { lock (_lock) return _slots.Select(s => s.Name).ToArray(); }
    }

    public bool IsEmpty => _slots.Count == 0;
}
