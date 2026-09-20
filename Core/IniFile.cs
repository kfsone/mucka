namespace Mucka.Core;

/// <summary>
/// Line-preserving INI document. The file is held as raw lines and only the specific
/// key lines touched by <see cref="Set"/>/<see cref="Remove"/> are rewritten - whole-line
/// comments, blank lines, unknown sections (e.g. the hand-edited [watch] rules) and key order
/// all survive a round-trip. Section and key lookups are case-insensitive.
///
/// An inline comment on a key line ("fontsize=15 ; big") does not survive: it is stripped on read
/// (see StripInlineComment, without which the value parses as "15 ; big", GetInt returns null, and
/// null means "absent" to every caller) and <see cref="Set"/> rewrites the line as key=value, so
/// the annotation is gone at the next save.
///
/// An inline comment on a SECTION HEADER is a larger failure and is NOT handled here.
/// "[settings] ; globals" does not end in ']', so TryParseSectionHeader rejects it and TryParseKey
/// skips it as well: HasSection is false, the whole section reads as absent, and the next save
/// appends a second [settings] at end of file. Stripping there would corrupt a profile name that
/// contains a marker - "[profile:MUD2 # test]" would lose its closing bracket and take the profile
/// with it - and profile names are user-typed, so the two cases cannot share one rule.
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

    /// <summary>Names of all sections, in file order.</summary>
    public IEnumerable<string> SectionNames()
    {
        foreach (var line in _lines)
            if (TryParseSectionHeader(line, out var name))
                yield return name;
    }

    /// <summary>Value for key in section, or null when the section or key is absent.</summary>
    public string? Get(string section, string key)
    {
        var idx = FindKeyLine(section, key);
        if (idx < 0) return null;
        var line = _lines[idx];
        return StripInlineComment(line[(line.IndexOf('=') + 1)..]).Trim();
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

    /// <summary>Removes the section header and all its content lines, if present.</summary>
    public void RemoveSection(string section)
    {
        var header = FindSectionHeader(section);
        if (header < 0) return;
        var (_, end) = SectionRange(section);
        _lines.RemoveRange(header, end - header);
    }

    /// <summary>Writes the document atomically (tmp file + rename on the same volume).</summary>
    public async Task SaveAsync(string path)
    {
        var text = string.Join(Environment.NewLine, _lines) + Environment.NewLine;
        var tmpPath = path + ".tmp";
        await File.WriteAllTextAsync(tmpPath, text).ConfigureAwait(false);
        File.Move(tmpPath, path, overwrite: true);
    }

    // -- Private ----------------------------------------------------------------

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
        value = StripInlineComment(trimmed[(eq + 1)..]).Trim();
        return key.Length > 0;
    }

    /// <summary>
    /// Drops a trailing inline comment from a raw value. The markers are the two
    /// <see cref="IsComment"/> already accepts at the start of a line, and one only counts when
    /// whitespace precedes it.
    /// </summary>
    /// <remarks>
    /// The whitespace requirement is what makes this safe on values already in the file. Every
    /// value this class writes comes from <see cref="Set"/> as "key=value" with nothing appended,
    /// so a marker with no space in front of it belongs to the value - a hand-written
    /// "menamecolor=#e09840" keeps its hash. A value that itself contains whitespace-then-marker
    /// would be truncated here and then persisted truncated by the next Set; nothing in the tree
    /// writes one (fkey macros separate commands with commas).
    /// </remarks>
    private static string StripInlineComment(string value)
    {
        for (var i = 1; i < value.Length; i++)
            if ((value[i] == ';' || value[i] == '#') && char.IsWhiteSpace(value[i - 1]))
                return value[..i];
        return value;
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
