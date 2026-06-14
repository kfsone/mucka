using Microsoft.Maui.Storage;

namespace Mucka.Core;

/// <summary>
/// Persists client settings and fkey macros to mucka.ini — the same file that holds the
/// hand-edited [watch] rules, which are preserved verbatim (IniFile only rewrites the
/// lines it owns).
///
/// Layout: settings and fkeys are GLOBAL by default; a profile opts into its own copy
/// via the "Save to profile only" checkboxes (one per page), which switch the save
/// target to the suffixed sections. Loads prefer the per-profile section when present:
///
///   [settings]            ; globals — the default save target
///   fontsize=15
///   columns=80
///   volume=75
///   statupdate=10
///   mutebeep=no
///
///   [fkeys]               ; F1-F12 plain, F13-F24 shift, F25-F36 ctrl (clio.ini layout)
///   F1=l around
///
///   [settings:MUD2 UK]    ; per-profile override — "Save to profile only" checked
///   [fkeys:MUD2 UK]
///
///   [profiles]            ; connection profiles, most-recently-used first
///   1=MUD2 UK
///   2=MUD2.COM
///
///   [profile:MUD2 UK]     ; connection identity — settings/fkeys live above, passwords
///   host=mud2.co.uk       ; in SecureStorage (ProfileStore)
///   port=23
///
/// Windows: same lookup order as WatchwordStore (./mucka.ini beside the exe, then
/// ~/mucka.ini); new files are created in the user profile. Android: the app-data
/// directory (no shared home directory to put an ini in, but the format is the same).
/// All writes go through one gate and are atomic (tmp + rename).
/// </summary>
public static class SettingsStore
{
    private const int FkeyCount = 36;
    private const string ProfilesSection = "profiles";
    private const string ProfileSectionPrefix = "profile:";

    // Serializes read-modify-write cycles so concurrent saves cannot interleave on the
    // shared tmp file (the cause of the silent second-save failure).
    private static readonly SemaphoreSlim s_gate = new(1, 1);

    /// <summary>
    /// Settings read from mucka.ini; null members mean "not present in the file".
    /// The scope flags record which section each part came from (per-profile vs global)
    /// and drive the "Save to profile only" checkboxes.
    /// </summary>
    public sealed record StoredSettings(
        int? FontSize,
        int? MaxColumns,
        int? Volume,
        int? StatUpdateFrequency,
        bool? MuteBeepPermanently,
        SoundSettings? Sounds,
        string[]? Fkeys,
        bool SettingsPerProfile,
        bool FkeysPerProfile,
        // Display tab — always read/written to the global [settings] section.
        int? DefaultFontSize    = null,
        int? DefaultMaxColumns  = null,
        int? DreamwordSizeOffset = null,
        bool? ShowOnline        = null,
        bool? ShowInventory     = null,
        bool? ShowItemsHere     = null,
        bool? ShowMapCompass    = null)
    {
        /// <summary>Overlays the stored (ini) values onto a profile — ini wins when present.</summary>
        public void ApplyTo(Profile profile)
        {
            if (FontSize            is int fontSize) profile.FontSize            = fontSize;
            if (MaxColumns          is int cols)     profile.MaxColumns          = cols;
            if (Volume              is int volume)   profile.Volume              = volume;
            if (StatUpdateFrequency is int fes)      profile.StatUpdateFrequency = fes;
            if (MuteBeepPermanently is bool mute)    profile.MuteBeepPermanently = mute;
            if (Sounds              is not null)     profile.Sounds              = Sounds;
            if (Fkeys               is not null)     profile.Fkeys               = Fkeys;
            profile.SettingsPerProfile = SettingsPerProfile;
            profile.FkeysPerProfile    = FkeysPerProfile;
            // Display tab — always from the global [settings] section.
            if (DefaultFontSize   is int dfs)  profile.DefaultFontSize   = dfs;
            if (DefaultMaxColumns is int dmc)  profile.DefaultMaxColumns = dmc;
            if (DreamwordSizeOffset is int dso) profile.DreamwordSizeOffset = dso;
            if (ShowOnline    is bool so)  profile.ShowOnline    = so;
            if (ShowInventory is bool si)  profile.ShowInventory = si;
            if (ShowItemsHere is bool sh)  profile.ShowItemsHere = sh;
            if (ShowMapCompass is bool sm) profile.ShowMapCompass = sm;
        }
    }

    /// <summary>
    /// The mucka.ini path: first existing of (./mucka.ini, ~/mucka.ini) on Windows —
    /// mirroring WatchwordStore.Load — defaulting to ~/mucka.ini for new files.
    /// On Android (and others) the app-data directory.
    /// </summary>
    public static string ResolvePath()
    {
#if WINDOWS
        var beside = Path.Combine(AppContext.BaseDirectory, "mucka.ini");
        if (File.Exists(beside)) return beside;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "mucka.ini");
#else
        return Path.Combine(FileSystem.AppDataDirectory, "mucka.ini");
#endif
    }

    /// <summary>
    /// Loads the stored settings for a profile — preferring its per-profile sections,
    /// falling back to the globals — or null when mucka.ini has neither (first run —
    /// callers keep the profile's built-in defaults).
    /// </summary>
    public static async Task<StoredSettings?> LoadProfileAsync(string profileName)
    {
        await s_gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var ini = IniFile.Load(ResolvePath());
            // Per-profile section wins when present; otherwise the globals.
            var profileSettings = $"settings:{profileName}";
            var profileFkeys    = $"fkeys:{profileName}";
            var settingsPerProfile = ini.HasSection(profileSettings);
            var fkeysPerProfile    = ini.HasSection(profileFkeys);
            var settingsSection = settingsPerProfile ? profileSettings
                                : ini.HasSection("settings") ? "settings" : null;
            var fkeysSection    = fkeysPerProfile ? profileFkeys
                                : ini.HasSection("fkeys") ? "fkeys" : null;
            if (settingsSection is null && fkeysSection is null)
                return null;

            string[]? fkeys = null;
            if (fkeysSection is not null)
            {
                // Section presence distinguishes "saved with empty slots" from "never saved".
                fkeys = new string[FkeyCount];
                Array.Fill(fkeys, string.Empty);
                foreach (var (key, value) in ini.Items(fkeysSection))
                    if (TryParseFkeyIndex(key, out var index))
                        fkeys[index] = value;
            }

            return new StoredSettings(
                FontSize:            settingsSection is null ? null : GetInt(ini, settingsSection, "fontsize"),
                MaxColumns:          settingsSection is null ? null : GetInt(ini, settingsSection, "columns"),
                Volume:              settingsSection is null ? null : GetInt(ini, settingsSection, "volume"),
                StatUpdateFrequency: settingsSection is null ? null : GetInt(ini, settingsSection, "statupdate"),
                MuteBeepPermanently: settingsSection is null ? null : GetBool(ini, settingsSection, "mutebeep"),
                Sounds:              settingsSection is null ? null : ReadSoundSettings(ini, settingsSection),
                Fkeys:               fkeys,
                SettingsPerProfile:  settingsPerProfile,
                FkeysPerProfile:     fkeysPerProfile,
                // Display tab settings always come from the global [settings] section.
                DefaultFontSize:    ini.HasSection("settings") ? GetInt (ini, "settings", "defaultfontsize")    : null,
                DefaultMaxColumns:  ini.HasSection("settings") ? GetInt (ini, "settings", "defaultcolumns")     : null,
                DreamwordSizeOffset: ini.HasSection("settings") ? GetInt(ini, "settings", "dreamwordsizeoffset") : null,
                ShowOnline:         ini.HasSection("settings") ? GetBool(ini, "settings", "showonline")         : null,
                ShowInventory:      ini.HasSection("settings") ? GetBool(ini, "settings", "showinventory")      : null,
                ShowItemsHere:      ini.HasSection("settings") ? GetBool(ini, "settings", "showitemshere")      : null,
                ShowMapCompass:     ini.HasSection("settings") ? GetBool(ini, "settings", "showmapcompass")     : null);
        }
        finally
        {
            s_gate.Release();
        }
    }

    /// <summary>
    /// Writes the settings (and, when non-null, the fkeys) into mucka.ini, preserving
    /// everything else in the file (comments, [watch] rules, other sections). Each part
    /// goes to its per-profile section when the corresponding scope flag in
    /// <paramref name="settings"/> is set, otherwise to the globals. An existing section
    /// of the other scope is left alone (the saved-globals-while-profile-exists ambiguity
    /// is deliberately unresolved). Pass null <paramref name="fkeys"/> to leave all fkey
    /// sections untouched — used by the connect page, which cannot edit hotkeys.
    /// </summary>
    public static async Task SaveProfileAsync(string profileName, ClientSettings settings, string[]? fkeys)
    {
        await s_gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = ResolvePath();
            var ini  = IniFile.Load(path);

            var settingsSection = settings.SettingsPerProfile ? $"settings:{profileName}" : "settings";
            ini.Set(settingsSection, "fontsize",   settings.FontSize.ToString());
            ini.Set(settingsSection, "columns",    settings.MaxColumns.ToString());
            ini.Set(settingsSection, "volume",     settings.Volume.ToString());
            ini.Set(settingsSection, "statupdate", settings.StatUpdateFrequency.ToString());
            ini.Set(settingsSection, "mutebeep",   settings.MuteBeepPermanently ? "yes" : "no");
            WriteSoundSettings(ini, settingsSection, settings.Sounds);

            // Display tab settings always go to the global [settings] section.
            ini.Set("settings", "defaultfontsize",    settings.DefaultFontSize.ToString());
            ini.Set("settings", "defaultcolumns",     settings.DefaultMaxColumns.ToString());
            ini.Set("settings", "dreamwordsizeoffset", settings.DreamwordSizeOffset.ToString());
            ini.Set("settings", "showonline",         settings.ShowOnline    ? "yes" : "no");
            ini.Set("settings", "showinventory",      settings.ShowInventory ? "yes" : "no");
            ini.Set("settings", "showitemshere",      settings.ShowItemsHere ? "yes" : "no");
            ini.Set("settings", "showmapcompass",     settings.ShowMapCompass ? "yes" : "no");

            if (fkeys is not null)
            {
                // Section presence (even empty) marks "saved" — see LoadProfileAsync.
                var fkeysSection = settings.FkeysPerProfile ? $"fkeys:{profileName}" : "fkeys";
                ini.EnsureSection(fkeysSection);
                for (var i = 0; i < FkeyCount; i++)
                {
                    var value = i < fkeys.Length ? fkeys[i] ?? string.Empty : string.Empty;
                    if (value.Length > 0)
                        ini.Set(fkeysSection, $"F{i + 1}", value);
                    else
                        ini.Remove(fkeysSection, $"F{i + 1}");
                }
            }

            await ini.SaveAsync(path).ConfigureAwait(false);
        }
        finally
        {
            s_gate.Release();
        }
    }

    /// <summary>
    /// Loads the connection profiles from mucka.ini: [profiles] gives the MRU order,
    /// one [profile:Name] section each holds the identity fields. When the ini defines
    /// no profiles, falls back to a one-time migration from the legacy profiles.json
    /// (retired to *.unused afterwards), and failing that to the built-in defaults.
    /// </summary>
    public static async Task<List<Profile>> LoadProfilesAsync()
    {
        await s_gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = ResolvePath();
            var ini  = IniFile.Load(path);
            var profiles = ReadProfiles(ini);
            if (profiles.Count > 0)
                return profiles;

            // No profiles in the ini yet: import the legacy profiles.json once. Any
            // read failure is treated as "no legacy file" (TryLoadLegacyAsync).
            var legacy = await ProfileStore.TryLoadLegacyAsync().ConfigureAwait(false);
            if (legacy is { Count: > 0 })
            {
                WriteProfiles(ini, legacy);
                MigrateLegacySettings(ini, legacy[0]);
                await ini.SaveAsync(path).ConfigureAwait(false);
                ProfileStore.RetireLegacyFile();
                return legacy;
            }

            return DefaultProfiles();
        }
        finally
        {
            s_gate.Release();
        }
    }

    /// <summary>
    /// Writes the profiles to mucka.ini — MRU order into [profiles], identity fields
    /// into each [profile:Name] section — and removes [profile:] sections for profiles
    /// no longer in the list. Settings/fkeys/watch sections are untouched.
    /// </summary>
    public static async Task SaveProfilesAsync(List<Profile> profiles)
    {
        await s_gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = ResolvePath();
            var ini  = IniFile.Load(path);
            WriteProfiles(ini, profiles);
            await ini.SaveAsync(path).ConfigureAwait(false);
        }
        finally
        {
            s_gate.Release();
        }
    }

    // ── Private ────────────────────────────────────────────────────────────────

    private static List<Profile> DefaultProfiles() => new()
    {
        new Profile { Name = "MUD2 UK",  Host = "mud2.co.uk",  Port = 23    },
        new Profile { Name = "MUD2.COM", Host = "www.mud2.com", Port = 27723 },
    };

    private static List<Profile> ReadProfiles(IniFile ini)
    {
        // [profiles] gives the MRU order; any [profile:X] section it doesn't mention
        // (e.g. hand-added) is appended in file order.
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

        var profiles = new List<Profile>(names.Count);
        foreach (var name in names)
        {
            var section = ProfileSectionPrefix + name;
            if (!ini.HasSection(section)) continue; // ordered name without a section
            var p = new Profile { Name = name };
            if (ini.Get(section, "host") is { Length: > 0 } host)        p.Host               = host;
            if (GetInt (ini, section, "port")            is int port)    p.Port               = port;
            p.AccountId = ini.Get(section, "account") ?? string.Empty;
            if (GetBool(ini, section, "rememberpassword") is bool rem)   p.RememberPassword   = rem;
            if (GetBool(ini, section, "telnetlogin")      is bool tel)   p.TelnetLoginEnabled = tel;
            if (ini.Get(section, "loginname") is { Length: > 0 } login)  p.TelnetLoginName    = login;
            if (GetInt (ini, section, "columns")          is int cols)   p.MaxColumns         = cols;
            if (GetInt (ini, section, "antiidle")         is int idle)   p.AntiIdleSeconds    = idle;
            if (GetBool(ini, section, "keepscreenon")     is bool keep)  p.KeepScreenOn       = keep;
            if (GetBool(ini, section, "defaulthotkeys")   is bool defs)  p.DefaultHotkeys     = defs;
            profiles.Add(p);
        }
        return profiles;
    }

    private static void WriteProfiles(IniFile ini, List<Profile> profiles)
    {
        // Rewrite the MRU order as 1..N and drop any stale numeric keys beyond it.
        var staleKeys = ini.Items(ProfilesSection)
            .Select(kv => kv.Key)
            .Where(k => int.TryParse(k, out var n) && (n < 1 || n > profiles.Count))
            .ToList();
        foreach (var key in staleKeys)
            ini.Remove(ProfilesSection, key);
        for (var i = 0; i < profiles.Count; i++)
            ini.Set(ProfilesSection, (i + 1).ToString(), profiles[i].Name);

        // Sections for deleted profiles go away; their settings:/fkeys: sections are
        // deliberately left alone (same hands-off rule as saving globals over them).
        var staleSections = ini.SectionNames()
            .Where(s => s.StartsWith(ProfileSectionPrefix, StringComparison.OrdinalIgnoreCase))
            .Where(s => !profiles.Any(p => s[ProfileSectionPrefix.Length..].Trim()
                .Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (var section in staleSections)
            ini.RemoveSection(section);

        foreach (var p in profiles)
        {
            var section = ProfileSectionPrefix + p.Name;
            ini.Set(section, "host",             p.Host);
            ini.Set(section, "port",             p.Port.ToString());
            ini.Set(section, "account",          p.AccountId);
            ini.Set(section, "rememberpassword", p.RememberPassword   ? "yes" : "no");
            ini.Set(section, "telnetlogin",      p.TelnetLoginEnabled ? "yes" : "no");
            ini.Set(section, "loginname",        p.TelnetLoginName);
            ini.Set(section, "columns",          p.MaxColumns.ToString());
            ini.Set(section, "antiidle",         p.AntiIdleSeconds.ToString());
            ini.Set(section, "keepscreenon",     p.KeepScreenOn       ? "yes" : "no");
            ini.Set(section, "defaulthotkeys",   p.DefaultHotkeys     ? "yes" : "no");
        }
    }

    /// <summary>
    /// Pre-ini installs kept settings and fkeys only in profiles.json; carry the MRU
    /// profile's copies into the ini (global scope) when the ini has neither, so the
    /// upgrade doesn't reset them. Installs that already saved to the ini are left alone.
    /// </summary>
    private static void MigrateLegacySettings(IniFile ini, Profile first)
    {
        if (!ini.HasSection("settings") && !ini.HasSection($"settings:{first.Name}"))
        {
            ini.Set("settings", "fontsize",   first.FontSize.ToString());
            ini.Set("settings", "columns",    first.MaxColumns.ToString());
            ini.Set("settings", "volume",     first.Volume.ToString());
            ini.Set("settings", "statupdate", first.StatUpdateFrequency.ToString());
            ini.Set("settings", "mutebeep",   first.MuteBeepPermanently ? "yes" : "no");
        }
        if (!ini.HasSection("fkeys") && !ini.HasSection($"fkeys:{first.Name}") &&
            first.Fkeys.Any(f => !string.IsNullOrEmpty(f)))
        {
            ini.EnsureSection("fkeys");
            for (var i = 0; i < first.Fkeys.Length && i < FkeyCount; i++)
                if (!string.IsNullOrEmpty(first.Fkeys[i]))
                    ini.Set("fkeys", $"F{i + 1}", first.Fkeys[i]);
        }
    }

    // Sound enablement keys, all within the settings section. Override-only — defaults
    // (everything on at full volume, no fallbacks) leave no keys behind:
    //   sounds=yes               ; master switch (always written)
    //   soundgroup.07=off        ; a disabled group
    //   sound.0703=off           ; a disabled individual sound
    //   sounddefault.07=070000   ; a group's fallback sound for codes with no wav
    //   soundgroupvol.07=50      ; a group's volume override (absent = master volume)
    //   soundvol.0703=50         ; a sound's volume override (absent = group volume)
    private const string SoundKeyPrefix        = "sound.";
    private const string SoundGroupKeyPrefix   = "soundgroup.";
    private const string SoundDefaultKeyPrefix = "sounddefault.";
    private const string SoundVolKeyPrefix     = "soundvol.";
    private const string SoundGroupVolKeyPrefix = "soundgroupvol.";

    /// <summary>Reads the sound settings from a section; null when no sound keys exist
    /// (pre-feature ini — callers keep the built-in everything-on defaults).</summary>
    private static SoundSettings? ReadSoundSettings(IniFile ini, string section)
    {
        SoundSettings? sounds = null;
        SoundSettings Sounds() => sounds ??= new SoundSettings();

        if (GetBool(ini, section, "sounds") is bool master)
            Sounds().MasterEnabled = master;
        foreach (var (key, value) in ini.Items(section))
        {
            // Longest prefixes first — "soundgroupvol."/"soundvol." must not fall into
            // the "soundgroup."/"sound." branches.
            if (key.StartsWith(SoundGroupVolKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, out var vol))
                    Sounds().GroupVolumes[key[SoundGroupVolKeyPrefix.Length..]] = Math.Clamp(vol, 0, 100);
            }
            else if (key.StartsWith(SoundGroupKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (ParseOnOff(value) is false)
                    Sounds().DisabledGroups.Add(key[SoundGroupKeyPrefix.Length..]);
            }
            else if (key.StartsWith(SoundDefaultKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (value.Length > 0)
                    Sounds().GroupDefaults[key[SoundDefaultKeyPrefix.Length..]] = value;
            }
            else if (key.StartsWith(SoundVolKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, out var vol))
                    Sounds().SoundVolumes[key[SoundVolKeyPrefix.Length..]] = Math.Clamp(vol, 0, 100);
            }
            else if (key.StartsWith(SoundKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (ParseOnOff(value) is false)
                    Sounds().DisabledSounds.Add(key[SoundKeyPrefix.Length..]);
            }
        }
        return sounds;
    }

    /// <summary>Writes the sound settings into a section, removing stale override keys
    /// so re-enabling a sound erases its line rather than flipping it to "on".</summary>
    private static void WriteSoundSettings(IniFile ini, string section, SoundSettings sounds)
    {
        ini.Set(section, "sounds", sounds.MasterEnabled ? "yes" : "no");

        var stale = ini.Items(section)
            .Select(kv => kv.Key)
            .Where(k => k.StartsWith(SoundKeyPrefix,        StringComparison.OrdinalIgnoreCase) ||
                        k.StartsWith(SoundGroupKeyPrefix,   StringComparison.OrdinalIgnoreCase) ||
                        k.StartsWith(SoundDefaultKeyPrefix, StringComparison.OrdinalIgnoreCase) ||
                        k.StartsWith(SoundVolKeyPrefix,     StringComparison.OrdinalIgnoreCase) ||
                        k.StartsWith(SoundGroupVolKeyPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in stale)
            ini.Remove(section, key);

        foreach (var prefix in sounds.DisabledGroups)
            ini.Set(section, SoundGroupKeyPrefix + prefix, "off");
        foreach (var code in sounds.DisabledSounds)
            ini.Set(section, SoundKeyPrefix + code, "off");
        foreach (var (prefix, code) in sounds.GroupDefaults)
            ini.Set(section, SoundDefaultKeyPrefix + prefix, code);
        foreach (var (prefix, vol) in sounds.GroupVolumes)
            ini.Set(section, SoundGroupVolKeyPrefix + prefix, vol.ToString());
        foreach (var (code, vol) in sounds.SoundVolumes)
            ini.Set(section, SoundVolKeyPrefix + code, vol.ToString());
    }

    private static bool? ParseOnOff(string value)
        => value.ToLowerInvariant() switch
        {
            "yes" or "true" or "on" or "1" => true,
            "no" or "false" or "off" or "0" => false,
            _ => null,
        };

    private static int? GetInt(IniFile ini, string section, string key)
        => int.TryParse(ini.Get(section, key), out var v) ? v : null;

    private static bool? GetBool(IniFile ini, string section, string key)
        => ini.Get(section, key)?.ToLowerInvariant() switch
        {
            "yes" or "true" or "on" or "1" => true,
            "no" or "false" or "off" or "0" => false,
            _ => null,
        };

    /// <summary>Parses "F1".."F36" (case-insensitive) into a 0-based macro index.</summary>
    private static bool TryParseFkeyIndex(string key, out int index)
    {
        index = -1;
        if (key.Length < 2 || (key[0] != 'F' && key[0] != 'f'))
            return false;
        if (!int.TryParse(key[1..], out var n) || n < 1 || n > FkeyCount)
            return false;
        index = n - 1;
        return true;
    }
}
