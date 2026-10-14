namespace Mucka.Settings;

/// <summary>
/// Which mucka.ini keys are global, and the one-time move of the keys older versions kept per
/// profile into <c>[settings]</c>.
///
/// <para>Per-profile copies came from two places: <c>[settings:Name]</c> sections (the old "Save to
/// profile only" checkbox on the Settings page) and four keys inside <c>[profile:Name]</c>
/// (<see cref="ProfileSectionGlobalKeys"/>). Both are read here as sources, in
/// most-recently-used order, and removed afterwards. Everything else in those sections - the
/// connection identity, a Mac plain text <c>password</c>, and any key this version does not know -
/// is left exactly where it is. <c>[fkeys:Name]</c> is not touched: macros are free text, there is
/// no merging two of them, and a profile's own set is the only per-profile setting that remains.</para>
/// </summary>
public static class GlobalSettingsMigration
{
    public const string GlobalSection = "settings";
    public const string ProfilesSection = "profiles";
    public const string ProfileSectionPrefix = "profile:";
    public const string SettingsOverridePrefix = "settings:";

    /// <summary>Single-value keys that were per profile in <c>[settings:Name]</c> and are now global.</summary>
    public static readonly IReadOnlyList<string> ScalarKeys =
        ["fontsize", "columns", "volume", "statupdate", "mutebeep", "logttr"];

    /// <summary>Keys that lived in <c>[profile:Name]</c> and are now global under the same name.</summary>
    public static readonly IReadOnlyList<string> ProfileSectionGlobalKeys =
        ["columns", "antiidle", "keepscreenon", "defaulthotkeys"];

    // The sound family: "sounds" is the master switch, the rest are dotted override prefixes.
    private const string SoundMasterKey = "sounds";
    private static readonly string[] s_soundPrefixes =
        ["sound.", "soundgroup.", "sounddefault.", "soundvol.", "soundgroupvol."];

    public static bool IsSoundKey(string key)
    {
        if (key.Equals(SoundMasterKey, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var prefix in s_soundPrefixes)
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsOverrideKey(string key)
        => IsSoundKey(key) || ScalarKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Profile names, most recently used first: the <c>[profiles]</c> list in order, then any
    /// <c>[profile:Name]</c> section it does not mention, in file order.
    /// </summary>
    public static List<string> ProfileNames(IniFile ini)
    {
        var names = new List<string>();
        foreach (var (_, value) in ini.Items(ProfilesSection))
            if (value.Length > 0 && !names.Contains(value, StringComparer.OrdinalIgnoreCase))
                names.Add(value);
        foreach (var section in ini.SectionNames())
        {
            if (!section.StartsWith(ProfileSectionPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var name = section[ProfileSectionPrefix.Length..].Trim();
            if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// Moves per-profile values into <c>[settings]</c> and removes the obsolete keys. A global key
    /// that is already present is never overwritten; one that is absent takes the value from the
    /// first source holding it, most recently used profile first, then the sections of profiles
    /// that no longer exist. The sound family moves as a unit, from the first source with any
    /// sound key, and only when <c>[settings]</c> has none. Idempotent. Returns true when the
    /// document changed, so the caller knows to write it.
    /// </summary>
    public static bool Apply(IniFile ini)
    {
        var overrideSections = ini.SectionNames()
            .Where(s => s.StartsWith(SettingsOverridePrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var profileSections = ini.SectionNames()
            .Where(s => s.StartsWith(ProfileSectionPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (overrideSections.Count == 0 && !profileSections.Any(s => ProfileSectionGlobalKeys.Any(k => ini.Get(s, k) is not null)))
            return false;

        // Adoption order: each profile's [settings:Name] then [profile:Name], profiles in MRU
        // order; override sections of deleted profiles last.
        var sources = new List<string>();
        foreach (var name in ProfileNames(ini))
        {
            sources.Add(SettingsOverridePrefix + name);
            sources.Add(ProfileSectionPrefix + name);
        }
        foreach (var s in overrideSections)
            if (!sources.Contains(s, StringComparer.OrdinalIgnoreCase)) sources.Add(s);

        var changed = false;
        var globalKeys = ScalarKeys.Concat(ProfileSectionGlobalKeys).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var key in globalKeys)
        {
            if (ini.Get(GlobalSection, key) is not null) continue;
            foreach (var source in sources)
            {
                if (ini.Get(source, key) is not { } value) continue;
                ini.Set(GlobalSection, key, value);
                changed = true;
                break;
            }
        }

        if (!ini.Items(GlobalSection).Any(kv => IsSoundKey(kv.Key)))
        {
            foreach (var source in sources.Where(s => s.StartsWith(SettingsOverridePrefix, StringComparison.OrdinalIgnoreCase)))
            {
                var sound = ini.Items(source).Where(kv => IsSoundKey(kv.Key)).ToList();
                if (sound.Count == 0) continue;
                foreach (var (k, v) in sound) ini.Set(GlobalSection, k, v);
                changed = true;
                break;
            }
        }

        foreach (var section in overrideSections)
        {
            foreach (var key in ini.Items(section).Select(kv => kv.Key).Where(IsOverrideKey).ToList())
            {
                ini.Remove(section, key);
                changed = true;
            }
            if (!ini.Items(section).Any())
            {
                ini.RemoveSection(section);
                changed = true;
            }
        }
        foreach (var section in profileSections)
            foreach (var key in ProfileSectionGlobalKeys)
                if (ini.Get(section, key) is not null)
                {
                    ini.Remove(section, key);
                    changed = true;
                }
        return changed;
    }
}
