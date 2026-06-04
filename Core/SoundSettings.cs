namespace Mucka.Core;

/// <summary>
/// Per-sound enablement and per-group fallback choices for the server-triggered clio
/// sound effects (the bell/beep keeps its own MuteBeep* flags). Stores overrides only:
/// a code or group absent from the sets is enabled, a group absent from the defaults
/// map has no fallback. Travels inside <see cref="ClientSettings"/>; treat an instance
/// as frozen once it is placed in a snapshot — use <see cref="Clone"/> to edit.
/// </summary>
public sealed class SoundSettings
{
    /// <summary>Synthetic group prefix for the terminal bell. Its on/off persists via the
    /// MuteBeep* flags, but its volume rides in <see cref="GroupVolumes"/> like the rest.</summary>
    public const string BellGroup = "bell";

    /// <summary>Master switch: when off, no sound effects play at all (bell included).</summary>
    public bool MasterEnabled { get; set; } = true;

    /// <summary>Group prefixes ("07", "13", ...) the user has switched off.</summary>
    public HashSet<string> DisabledGroups { get; } = new();

    /// <summary>Sound codes ("0703", "070001", ...) the user has switched off.</summary>
    public HashSet<string> DisabledSounds { get; } = new();

    /// <summary>Group prefix → sound code played when the server triggers a code in the
    /// group that has no wav of its own. No entry = stay silent for unknown codes.</summary>
    public Dictionary<string, string> GroupDefaults { get; } = new();

    /// <summary>Group prefix → volume percent override (0–100). Absent = the group
    /// inherits the master volume. A sound plays at its own override, else its group's,
    /// else the master volume.</summary>
    public Dictionary<string, int> GroupVolumes { get; } = new();

    /// <summary>Sound code → volume percent override (0–100). Absent = inherit the group.</summary>
    public Dictionary<string, int> SoundVolumes { get; } = new();

    public bool IsGroupEnabled(string prefix) => !DisabledGroups.Contains(prefix);
    public bool IsSoundEnabled(string code)   => !DisabledSounds.Contains(code);
    public string? GetGroupDefault(string prefix)
        => GroupDefaults.TryGetValue(prefix, out var code) ? code : null;
    /// <summary>The group's volume override, or null to inherit the master volume.</summary>
    public int? GetGroupVolume(string prefix)
        => GroupVolumes.TryGetValue(prefix, out var v) ? Math.Clamp(v, 0, 100) : null;
    /// <summary>The sound's volume override, or null to inherit the group/master volume.</summary>
    public int? GetSoundVolume(string code)
        => SoundVolumes.TryGetValue(code, out var v) ? Math.Clamp(v, 0, 100) : null;

    public SoundSettings Clone()
    {
        var copy = new SoundSettings { MasterEnabled = MasterEnabled };
        copy.DisabledGroups.UnionWith(DisabledGroups);
        copy.DisabledSounds.UnionWith(DisabledSounds);
        foreach (var (prefix, code) in GroupDefaults)
            copy.GroupDefaults[prefix] = code;
        foreach (var (prefix, vol) in GroupVolumes)
            copy.GroupVolumes[prefix] = vol;
        foreach (var (code, vol) in SoundVolumes)
            copy.SoundVolumes[code] = vol;
        return copy;
    }
}
