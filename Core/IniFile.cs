namespace Mucka.Core;

/// <summary>
/// Line-preserving INI document. The file is held as raw lines and only the specific
/// key lines touched by <see cref="Set"/>/<see cref="Remove"/> are rewritten — comments,
/// blank lines, unknown sections (e.g. the hand-edited [watch] rules) and key order all
/// survive a round-trip. Section and key lookups are case-insensitive.
/// Not thread-safe; callers serialize access (see SettingsStore).
/// </summary>
public sealed class IniFile
{
    private readonly List<string> _lines = [];

    public static IniFile Load(string path)
    {
        var ini = new IniFile();
        if (File.Exists(path))
            ini._lines.AddRange(File.ReadAllLines(path));
        return ini;
    }

    public bool HasSection(string section) => FindSectionHeader(section) >= 0;

    /// <summary>Value for key in section, or null when the section or key is absent.</summary>
    public string? Get(string section, string key)
    {
        var idx = FindKeyLine(section, key);
        if (idx < 0) return null;
        var line = _lines[idx];
        return line[(line.IndexOf('=') + 1)..].Trim();
    }

    /// <summary>All key=value pairs in a section, in file order.</summary>
    public IEnumerable<(string Key, string Value)> Items(string section)
    {
        var (start, end) = SectionRange(section);
        for (var i = start; i < end; i++)
            if (TryParseKey(_lines[i], out var key, out var value))
                yield return (key, value);
    }

    /// <summary>
    /// Sets key=value in section, replacing the existing key line in place or appending
    /// to the section (creating the section at end-of-file if needed).
    /// </summary>
    public void Set(string section, string key, string value)
    {
        var idx = FindKeyLine(section, key);
        if (idx >= 0)
        {
            _lines[idx] = $"{key}={value}";
            return;
        }
        _lines.Insert(EndOfSectionContent(section), $"{key}={value}");
    }

    /// <summary>Removes the key line from the section, if present.</summary>
    public void Remove(string section, string key)
    {
        var idx = FindKeyLine(section, key);
        if (idx >= 0) _lines.RemoveAt(idx);
    }

    /// <summary>Creates the section header at end-of-file when absent (no-op otherwise).</summary>
    public void EnsureSection(string section) => EndOfSectionContent(section);

    /// <summary>Writes the document atomically (tmp file + rename on the same volume).</summary>
    public async Task SaveAsync(string path)
    {
        var text = string.Join(Environment.NewLine, _lines) + Environment.NewLine;
        var tmpPath = path + ".tmp";
        await File.WriteAllTextAsync(tmpPath, text).ConfigureAwait(false);
        File.Move(tmpPath, path, overwrite: true);
    }

    // ── Private ────────────────────────────────────────────────────────────────

    private static bool IsComment(string trimmed)
        => trimmed.Length > 0 && (trimmed[0] == ';' || trimmed[0] == '#');

    private static bool TryParseSectionHeader(string line, out string name)
    {
        name = string.Empty;
        var trimmed = line.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '[' || trimmed[^1] != ']')
            return false;
        name = trimmed[1..^1].Trim();
        return true;
    }

    private static bool TryParseKey(string line, out string key, out string value)
    {
        key = value = string.Empty;
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || IsComment(trimmed) || trimmed[0] == '[')
            return false;
        var eq = trimmed.IndexOf('=');
        if (eq <= 0) return false;
        key   = trimmed[..eq].Trim();
        value = trimmed[(eq + 1)..].Trim();
        return key.Length > 0;
    }

    private int FindSectionHeader(string section)
    {
        for (var i = 0; i < _lines.Count; i++)
            if (TryParseSectionHeader(_lines[i], out var name) &&
                name.Equals(section, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>Content line range (start inclusive, end exclusive) of a section; (0,0) when absent.</summary>
    private (int Start, int End) SectionRange(string section)
    {
        var header = FindSectionHeader(section);
        if (header < 0) return (0, 0);
        var end = header + 1;
        while (end < _lines.Count && !TryParseSectionHeader(_lines[end], out _))
            end++;
        return (header + 1, end);
    }

    /// <summary>Index of the key=value line within the section, or -1.</summary>
    private int FindKeyLine(string section, string key)
    {
        var (start, end) = SectionRange(section);
        for (var i = start; i < end; i++)
            if (TryParseKey(_lines[i], out var k, out _) &&
                k.Equals(key, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>
    /// Insertion point for a new key: just past the section's last content line (so a blank
    /// separator before the next section stays after our key). Creates the section at
    /// end-of-file when absent.
    /// </summary>
    private int EndOfSectionContent(string section)
    {
        if (FindSectionHeader(section) < 0)
        {
            // Separate the new section from existing content with one blank line.
            if (_lines.Count > 0 && _lines[^1].Trim().Length != 0)
                _lines.Add(string.Empty);
            _lines.Add($"[{section}]");
            return _lines.Count;
        }
        var (start, end) = SectionRange(section);
        while (end > start && _lines[end - 1].Trim().Length == 0)
            end--;
        return end;
    }
}
