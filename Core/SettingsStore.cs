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
/// Windows: same lookup order as WatchwordStore (./mucka.ini beside the exe, then
/// ~/mucka.ini); new files are created in the user profile. Android: the app-data
/// directory (no shared home directory to put an ini in, but the format is the same).
/// All writes go through one gate and are atomic (tmp + rename).
/// </summary>
public static class SettingsStore
{
    private const int FkeyCount = 36;

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
        string[]? Fkeys,
        bool SettingsPerProfile,
        bool FkeysPerProfile)
    {
        /// <summary>Overlays the stored (ini) values onto a profile — ini wins when present.</summary>
        public void ApplyTo(Profile profile)
        {
            if (FontSize            is int fontSize) profile.FontSize            = fontSize;
            if (MaxColumns          is int cols)     profile.MaxColumns          = cols;
            if (Volume              is int volume)   profile.Volume              = volume;
            if (StatUpdateFrequency is int fes)      profile.StatUpdateFrequency = fes;
            if (MuteBeepPermanently is bool mute)    profile.MuteBeepPermanently = mute;
            if (Fkeys               is not null)     profile.Fkeys               = Fkeys;
            profile.SettingsPerProfile = SettingsPerProfile;
            profile.FkeysPerProfile    = FkeysPerProfile;
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
    /// falling back to the globals — or null when mucka.ini has neither (first run /
    /// pre-ini installs — callers fall back to profiles.json values).
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
                Fkeys:               fkeys,
                SettingsPerProfile:  settingsPerProfile,
                FkeysPerProfile:     fkeysPerProfile);
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

    // ── Private ────────────────────────────────────────────────────────────────

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
