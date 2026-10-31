using System.Globalization;
using Microsoft.Maui.Storage;
using Mucka.Settings;
#if UIKIT
using Mucka.Commands;
#endif

namespace Mucka.Core;

/// <summary>
/// Persists client settings and Fkeys to mucka.ini - the same file that holds the
/// hand-edited [watch] rules, which are preserved verbatim (IniFile only rewrites the
/// lines it owns).
///
/// Layout: every setting is GLOBAL, in [settings]. The one per-profile setting is a profile's own
/// F-key set, an opt-in copy made by the Hotkeys page's "Save to profile only" checkbox, which
/// switches the save target to [fkeys:Name]; loads prefer it when present. Older versions also kept
/// settings per profile ([settings:Name], and columns/antiidle/keepscreenon/defaulthotkeys inside
/// [profile:Name]); GlobalSettingsMigration moves those into [settings] on first load and removes
/// them.
///
///   [settings]            ; globals - the only save target for settings
///   fontsize=15            ; the Game Font size (Display tab)
///   columns=80
///   volume=75
///   statupdate=10
///   mutebeep=no
///   antiidle=0
///   keepscreenon=no
///
///   [fkeys]               ; F1-F12 plain, F13-F24 shift, F25-F36 ctrl (clio.ini layout)
///   F1=l around
///
///   [fkeys:MUD2 UK]       ; per-profile F-key set - "Save to profile only" checked
///
///   [profiles]            ; connection profiles, most-recently-used first
///   1=MUD2 UK
///   2=MUD2.COM
///
///   [profile:MUD2 UK]     ; connection identity - passwords in SecureStorage (ProfileStore); on
///   host=mud2.co.uk       ; macOS in the keychain, or as password=... here when securepasswords=no
///   port=23
///   usessh=no             ; yes = SSH to the port above instead of telnet
///
/// Windows: ./mucka.ini beside the exe, then ~/mucka.ini; new files are created in the
/// user profile. Android: the app-data directory (no shared home directory to put an ini
/// in, but the format is the same).
/// All writes go through one gate and are atomic (tmp + rename).
/// </summary>
public static class SettingsStore
{
    private const int FkeyCount = 36;
    private const string ProfilesSection = GlobalSettingsMigration.ProfilesSection;
    private const string ProfileSectionPrefix = GlobalSettingsMigration.ProfileSectionPrefix;
#if MACCATALYST
    private const string SecurePasswordsKey = "securepasswords";
#endif

    // Serializes read-modify-write cycles so concurrent saves cannot interleave on the
    // shared tmp file (the cause of the silent second-save failure).
    private static readonly SemaphoreSlim s_gate = new(1, 1);

    /// <summary>
    /// Settings read from mucka.ini; null members mean "not present in the file".
    /// <see cref="FkeysPerProfile"/> records whether the fkeys came from the profile's own
    /// [fkeys:Name] section and drives the Hotkeys page's "Save to profile only" checkbox.
    /// </summary>
    public sealed record StoredSettings(
        int? FontSize,
        int? MaxColumns,
        int? Volume,
        int? StatUpdateFrequency,
        bool? MuteBeepPermanently,
        SoundSettings? Sounds,
        string[]? Fkeys,
        bool FkeysPerProfile,
        // Display tab - always read/written to the global [settings] section.
        int? DefaultMaxColumns  = null,
        int? DreamwordSizeOffset = null,
        bool? ShowOnline        = null,
        bool? ShowInventory     = null,
        bool? ShowItemsHere     = null,
        bool? ShowMapCompass    = null,
        int? MaxOnlineDisplay   = null,
        bool? OnlineNamesOnly   = null,
        int? OnlineForgetWindow = null,
        bool? FloatOnline       = null,
        bool? FloatCompass      = null,
        bool? LogResetDiagnostics = null,
        string? EmoteColor    = null,
        string? SayColor        = null,
        string? ShoutColor      = null,
        string? TellColor       = null,
        MudSharp.Models.ChatTheme? ChatTheme = null,
        MudSharp.Models.ChatFaces? ChatFaces = null,
        bool? ShowCombatRail    = null,
        bool? ShowCombatStats   = null)
    {
        /// <summary>Overlays the stored (ini) values onto a profile - ini wins when present.</summary>
        public void ApplyTo(Profile profile)
        {
            if (FontSize            is int fontSize) profile.FontSize            = fontSize;
            if (MaxColumns          is int cols)     profile.MaxColumns          = cols;
            if (Volume              is int volume)   profile.Volume              = volume;
            if (StatUpdateFrequency is int fes)      profile.StatUpdateFrequency = fes;
            if (MuteBeepPermanently is bool mute)    profile.MuteBeepPermanently = mute;
            if (LogResetDiagnostics is bool logttr)  profile.LogResetDiagnostics = logttr;
            if (Sounds              is not null)     profile.Sounds              = Sounds;
            if (Fkeys               is not null)     profile.Fkeys               = Fkeys;
            profile.FkeysPerProfile    = FkeysPerProfile;
            // Display tab - always from the global [settings] section.
            if (DefaultMaxColumns is int dmc)  profile.DefaultMaxColumns = dmc;
            if (DreamwordSizeOffset is int dso) profile.DreamwordSizeOffset = dso;
            if (ShowOnline    is bool so)  profile.ShowOnline    = so;
            if (ShowInventory is bool si)  profile.ShowInventory = si;
            if (ShowItemsHere is bool sh)  profile.ShowItemsHere = sh;
            if (ShowMapCompass is bool sm) profile.ShowMapCompass = sm;
            if (MaxOnlineDisplay is int mod) profile.MaxOnlineDisplay = mod;
            if (OnlineNamesOnly  is bool ono) profile.OnlineNamesOnly  = ono;
            if (OnlineForgetWindow is int ofw) profile.OnlineForgetWindow = ofw;
            if (FloatOnline      is bool fo)  profile.FloatOnline      = fo;
            if (FloatCompass     is bool fc)  profile.FloatCompass     = fc;
            if (EmoteColor is { Length: > 0 } spc) profile.EmoteColor = spc;
            if (SayColor     is { Length: > 0 } sac) profile.SayColor     = sac;
            if (ShoutColor   is { Length: > 0 } shc) profile.ShoutColor   = shc;
            if (TellColor    is { Length: > 0 } tec) profile.TellColor    = tec;
            if (ChatTheme    is { } theme)           profile.ChatTheme    = theme;
            if (ChatFaces    is { } faces)           profile.ChatFaces    = faces;
            if (ShowCombatRail  is bool rail)  profile.ShowCombatRail  = rail;
            if (ShowCombatStats is bool stats) profile.ShowCombatStats = stats;
        }
    }

    /// <summary>
    /// The mucka.ini path: first existing of (./mucka.ini, ~/mucka.ini) on Windows,
    /// defaulting to ~/mucka.ini for new files. On Android (and others) the app-data
    /// directory. The one resolver for that file, whoever is reading it.
    /// </summary>
    public static string ResolvePath()
    {
#if WINDOWS
        var beside = Path.Combine(AppContext.BaseDirectory, "mucka.ini");
        if (File.Exists(beside)) return beside;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "mucka.ini");
#elif MACCATALYST
        // The directory is otherwise only made when the database first opens, which is after the
        // first read or write of this file.
        Directory.CreateDirectory(MuckaPaths.GetDataDirectory());
        return Path.Combine(MuckaPaths.GetDataDirectory(), "mucka.ini");
#else
        return Path.Combine(FileSystem.AppDataDirectory, "mucka.ini");
#endif
    }

    /// <summary>
    /// Loads the stored settings for a profile - the global [settings], and its own [fkeys:Name]
    /// in preference to the global [fkeys] when it has one - or null when mucka.ini has neither
    /// (first run - callers keep the profile's built-in defaults).
    /// </summary>
    public static async Task<StoredSettings?> LoadProfileAsync(string profileName)
    {
        await s_gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var ini = await LoadIniAsync().ConfigureAwait(false);
            var profileFkeys    = $"fkeys:{profileName}";
            var fkeysPerProfile = ini.HasSection(profileFkeys);
            var settingsSection = ini.HasSection("settings") ? "settings" : null;
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

            var colors = MudSharp.Models.ChatColorKeys.Read(ini.Get);
            return new StoredSettings(
                FontSize:            settingsSection is null ? null : GetInt(ini, settingsSection, "fontsize"),
                MaxColumns:          settingsSection is null ? null : GetInt(ini, settingsSection, "columns"),
                Volume:              settingsSection is null ? null : GetInt(ini, settingsSection, "volume"),
                StatUpdateFrequency: settingsSection is null ? null : GetInt(ini, settingsSection, "statupdate"),
                MuteBeepPermanently: settingsSection is null ? null : GetBool(ini, settingsSection, "mutebeep"),
                LogResetDiagnostics: settingsSection is null ? null : GetBool(ini, settingsSection, "logttr"),
                Sounds:              settingsSection is null ? null : ReadSoundSettings(ini, settingsSection),
                Fkeys:               fkeys,
                FkeysPerProfile:     fkeysPerProfile,
                // Display tab settings always come from the global [settings] section.
                DefaultMaxColumns:  ini.HasSection("settings") ? GetInt (ini, "settings", "defaultcolumns")     : null,
                DreamwordSizeOffset: ini.HasSection("settings") ? GetInt(ini, "settings", "dreamwordsizeoffset") : null,
                ShowOnline:         ini.HasSection("settings") ? GetBool(ini, "settings", "showonline")         : null,
                ShowInventory:      ini.HasSection("settings") ? GetBool(ini, "settings", "showinventory")      : null,
                ShowItemsHere:      ini.HasSection("settings") ? GetBool(ini, "settings", "showitemshere")      : null,
                ShowMapCompass:     ini.HasSection("settings") ? GetBool(ini, "settings", "showmapcompass")     : null,
                MaxOnlineDisplay:   ini.HasSection("settings") ? GetInt (ini, "settings", "maxonlinedisplay")   : null,
                OnlineNamesOnly:    ini.HasSection("settings") ? GetBool(ini, "settings", "onlinenamesonly")    : null,
                OnlineForgetWindow: ini.HasSection("settings") ? GetInt (ini, "settings", "onlineforgetwindow") : null,
                FloatOnline:        ini.HasSection("settings") ? GetBool(ini, "settings", "floatonline")        : null,
                FloatCompass:       ini.HasSection("settings") ? GetBool(ini, "settings", "floatcompass")       : null,
                EmoteColor:       colors.Emote,
                SayColor:           colors.Say,
                ShoutColor:         colors.Shout,
                TellColor:          colors.Tell,
                ChatTheme:          colors.Theme,
                ChatFaces:          MudSharp.Models.ChatColorKeys.ReadFaces(ini.Get),
                ShowCombatRail:     ini.HasSection("settings") ? GetBool(ini, "settings", "showcombatrail")     : null,
                ShowCombatStats:    ini.HasSection("settings") ? GetBool(ini, "settings", "showcombatstats")    : null);
        }
        finally
        {
            s_gate.Release();
        }
    }

    /// <summary>
    /// Writes the settings (and, when non-null, the fkeys) into mucka.ini, preserving
    /// everything else in the file (comments, [watch] rules, other sections). The settings
    /// always go to the global [settings]; the fkeys go to [fkeys:Name] when
    /// <see cref="ClientSettings.FkeysPerProfile"/> is set, otherwise to the global [fkeys] (an
    /// existing section of the other scope is left alone). Pass null <paramref name="fkeys"/> to leave all fkey
    /// sections untouched - used by the connect page, which cannot edit hotkeys.
    /// </summary>
    /// <param name="writeSounds">False leaves every sound key in the section untouched. Pass false
    /// from callers that cannot edit sounds (the connect page): <see cref="WriteSoundSettings"/>
    /// purges the whole <c>sound*</c> family before rewriting it, so passing a
    /// <see cref="ClientSettings"/> whose <see cref="ClientSettings.Sounds"/> was not deliberately
    /// populated silently erases the player's volume overrides. Mirrors <c>fkeys: null</c>.</param>
    /// <param name="writeDisplayGlobals">False leaves the whole always-global block (UI font
    /// step, default columns, dreamword offset, the Show* section toggles, online display options, the float
    /// defaults, the chat colours, the wire-log switch) untouched. Pass false from callers that cannot edit any of
    /// those fields (the connect page): unlike <paramref name="writeSounds"/>'s block, this one has
    /// no per-key purge-then-rewrite, but a <see cref="ClientSettings"/> assembled without deliberately
    /// populating this block still carries C# defaults for every field in it, and writing those
    /// unconditionally silently resets a player's saved globals back to their defaults on every call.
    /// Mirrors <c>writeSounds: false</c> / <c>fkeys: null</c>.</param>
    public static async Task SaveProfileAsync(string profileName, ClientSettings settings, string[]? fkeys,
        bool writeSounds = true, bool writeDisplayGlobals = true)
    {
        await s_gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = ResolvePath();
            var ini  = await LoadIniAsync(persist: false).ConfigureAwait(false);

            const string settingsSection = "settings";
            // mucka.ini is a stored file format, so every number in it is written and read back
            // invariant - the file must not change meaning when it is opened under another locale.
            ini.Set(settingsSection, "fontsize",   settings.FontSize.ToString(CultureInfo.InvariantCulture));
            ini.Set(settingsSection, "columns",    settings.MaxColumns.ToString(CultureInfo.InvariantCulture));
            ini.Set(settingsSection, "volume",     settings.Volume.ToString(CultureInfo.InvariantCulture));
            ini.Set(settingsSection, "statupdate", settings.StatUpdateFrequency.ToString(CultureInfo.InvariantCulture));
            ini.Set(settingsSection, "mutebeep",   settings.MuteBeepPermanently ? "yes" : "no");
            ini.Set(settingsSection, "logttr",     settings.LogResetDiagnostics ? "yes" : "no");
            if (writeSounds)
                WriteSoundSettings(ini, settingsSection, settings.Sounds);

            if (writeDisplayGlobals)
            {
                // Display tab settings always go to the global [settings] section.
                ini.Set("settings", Mucka.Ui.UiFontScale.IniKey, Mucka.Ui.UiFontScale.Format(settings.UiFontStep));
                ini.Set("settings", "defaultcolumns",     settings.DefaultMaxColumns.ToString(CultureInfo.InvariantCulture));
                ini.Set("settings", "dreamwordsizeoffset", settings.DreamwordSizeOffset.ToString(CultureInfo.InvariantCulture));
                ini.Set("settings", "showonline",         settings.ShowOnline    ? "yes" : "no");
                ini.Set("settings", "showinventory",      settings.ShowInventory ? "yes" : "no");
                ini.Set("settings", "showitemshere",      settings.ShowItemsHere ? "yes" : "no");
                ini.Set("settings", "showmapcompass",     settings.ShowMapCompass ? "yes" : "no");
                ini.Set("settings", "maxonlinedisplay",   settings.MaxOnlineDisplay.ToString(CultureInfo.InvariantCulture));
                ini.Set("settings", "onlinenamesonly",    settings.OnlineNamesOnly ? "yes" : "no");
                ini.Set("settings", "onlineforgetwindow", settings.OnlineForgetWindow.ToString(CultureInfo.InvariantCulture));
                ini.Set("settings", "floatonline",        settings.FloatOnline     ? "yes" : "no");
                ini.Set("settings", "floatcompass",       settings.FloatCompass    ? "yes" : "no");
                MudSharp.Models.ChatColorKeys.Write(ini.Set, settings.ChatTheme, settings.EmoteColor, settings.SayColor,
                    settings.ShoutColor, settings.TellColor);
                MudSharp.Models.ChatColorKeys.WriteFaces(ini.Set, settings.ChatFaces);
            }
            MudSharp.Models.ChatColorKeys.RemoveDeadKeys(ini.SectionNames(), ini.Remove);

            if (fkeys is not null)
            {
                // Section presence (even empty) marks "saved" - see LoadProfileAsync.
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
    /// Writes ONE key in the global <c>[settings]</c> section and leaves every other key in the file
    /// alone.
    ///
    /// <para>For a global flag a one-click control changes outside the settings dialog - the Combat
    /// Rail's shown/hidden state and its "Stats" row. <see cref="SaveProfileAsync"/> cannot serve
    /// those: its blocks are all-or-nothing, so a caller would have to reconstruct every other key
    /// from memory and would silently reset any it got wrong.</para>
    /// </summary>
    public static Task SetGlobalFlagAsync(string key, bool value)
        => SetGlobalValueAsync(key, value ? "yes" : "no");

    /// <summary>Writes one key of the global <c>[settings]</c> section and nothing else.</summary>
    public static Task SetGlobalValueAsync(string key, string value) => SetGlobalValuesAsync((key, value));

    /// <summary>Writes some keys of the global <c>[settings]</c> section, in one write, and nothing else.</summary>
    public static async Task SetGlobalValuesAsync(params (string Key, string Value)[] values)
    {
        await s_gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = ResolvePath();
            var ini  = await LoadIniAsync(persist: false).ConfigureAwait(false);
            foreach (var (key, value) in values)
                ini.Set("settings", key, value);
            await ini.SaveAsync(path).ConfigureAwait(false);
        }
        finally
        {
            s_gate.Release();
        }
    }

    /// <summary>Reads one key of the global <c>[settings]</c> section; null when absent.</summary>
    public static async Task<string?> GetGlobalValueAsync(string key)
        => (await GetGlobalValuesAsync(key).ConfigureAwait(false))[0];

    /// <summary>Reads some keys of the global <c>[settings]</c> section in one load, in the order
    /// asked; null for each one absent.</summary>
    public static async Task<string?[]> GetGlobalValuesAsync(params string[] keys)
    {
        await s_gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var ini = await LoadIniAsync().ConfigureAwait(false);
            return [.. keys.Select(k => ini.Get("settings", k))];
        }
        finally
        {
            s_gate.Release();
        }
    }

    /// <summary>The Text size page's answer: the UI step, the Game Font, the chat colour theme when one
    /// was picked (its own four colours, the faces left alone), and the key that says it was answered,
    /// in one write. See docs/font-setup-design.md.</summary>
    public static async Task SaveFontSetupAsync(Mucka.Ui.FontSetupChoice choice, MudSharp.Models.ChatTheme? theme)
    {
        await s_gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = ResolvePath();
            var ini  = await LoadIniAsync(persist: false).ConfigureAwait(false);
            ini.Set("settings", Mucka.Ui.UiFontScale.IniKey, Mucka.Ui.UiFontScale.Format(choice.UiStep));
            ini.Set("settings", "fontsize", choice.GameFontPx.ToString(CultureInfo.InvariantCulture));
            ini.Set("settings", Mucka.Ui.FontSetup.DoneKey, "yes");
            if (theme is not null)
                MudSharp.Models.ChatColorKeys.Write(ini.Set, theme, theme.EmoteHex, theme.SayHex, theme.ShoutHex, theme.TellHex);
            await ini.SaveAsync(path).ConfigureAwait(false);
        }
        finally
        {
            s_gate.Release();
        }
    }

    /// <summary>The stored chat colour theme, and the palette the stored colours and faces make.</summary>
    public static async Task<(MudSharp.Models.ChatTheme Theme, MudSharp.Models.ChatColorizer.Palette Palette)> LoadChatColoursAsync()
    {
        await s_gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var ini = await LoadIniAsync().ConfigureAwait(false);
            var c = MudSharp.Models.ChatColorKeys.Read(ini.Get);
            var faces = MudSharp.Models.ChatColorKeys.ReadFaces(ini.Get);
            return (c.Theme, MudSharp.Models.ChatColorizer.ResolvePalette(c.Theme, c.Emote, c.Say, c.Shout, c.Tell, faces));
        }
        finally
        {
            s_gate.Release();
        }
    }

    /// <summary>
    /// Loads the connection profiles from mucka.ini: [profiles] gives the MRU order,
    /// one [profile:Name] section each holds the identity fields. When the ini defines
    /// no profiles, falls back to the built-in defaults.
    /// </summary>
    public static async Task<List<Profile>> LoadProfilesAsync()
    {
        await s_gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var ini  = await LoadIniAsync().ConfigureAwait(false);
            var profiles = ReadProfiles(ini);
            return profiles.Count > 0 ? profiles : DefaultProfiles();
        }
        finally
        {
            s_gate.Release();
        }
    }

    /// <summary>
    /// Writes the profiles to mucka.ini - MRU order into [profiles], identity fields
    /// into each [profile:Name] section - and removes [profile:] sections for profiles
    /// no longer in the list. Settings/fkeys/watch sections are untouched.
    /// </summary>
    public static async Task SaveProfilesAsync(List<Profile> profiles)
    {
        await s_gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = ResolvePath();
            var ini  = await LoadIniAsync(persist: false).ConfigureAwait(false);
            WriteProfiles(ini, profiles);
            await ini.SaveAsync(path).ConfigureAwait(false);
        }
        finally
        {
            s_gate.Release();
        }
    }

    /// <summary>Moves a profile's sections in mucka.ini to a new name (<see cref="ProfileRename"/>).
    /// False, with the file unchanged, when the new name belongs to another profile.</summary>
    public static async Task<bool> RenameProfileAsync(string oldName, string newName)
    {
        await s_gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = ResolvePath();
            var ini = await LoadIniAsync(persist: false).ConfigureAwait(false);
            if (!ProfileRename.Apply(ini, oldName, newName))
                return false;
            await ini.SaveAsync(path).ConfigureAwait(false);
            return true;
        }
        finally
        {
            s_gate.Release();
        }
    }

#if UIKIT
    private const string ProfilePasswordKey = "password";

    /// <summary>The password kept as plain text in the profile's section; null when there is none.</summary>
    public static async Task<string?> GetPlainPasswordAsync(string profileName)
    {
        await s_gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var raw = (await LoadIniAsync().ConfigureAwait(false)).Get(ProfileSectionPrefix + profileName, ProfilePasswordKey);
            return string.IsNullOrEmpty(raw) ? null : IniPasswordCodec.Decode(raw);
        }
        finally { s_gate.Release(); }
    }
#endif

#if MACCATALYST
    /// <summary>The "Store Passwords Securely" choice: true keychain, false plain text in this
    /// file, null not asked yet (no securepasswords key in [settings]).</summary>
    public static async Task<bool?> GetSecurePasswordsAsync()
        => await GetGlobalValueAsync(SecurePasswordsKey).ConfigureAwait(false) is { } v ? ParseOnOff(v) : null;

    public static Task SetSecurePasswordsAsync(bool secure) => SetGlobalFlagAsync(SecurePasswordsKey, secure);

    /// <summary>Removes the plain text password of each named profile that has one, saving the file
    /// once, and only when something was removed.</summary>
    public static async Task RemovePlainPasswordsAsync(IReadOnlyCollection<string> profileNames)
    {
        if (profileNames.Count == 0) return;
        await s_gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = ResolvePath();
            var ini = await LoadIniAsync(persist: false).ConfigureAwait(false);
            var removed = false;
            foreach (var name in profileNames)
            {
                var section = ProfileSectionPrefix + name;
                if (string.IsNullOrEmpty(ini.Get(section, ProfilePasswordKey))) continue;
                ini.Remove(section, ProfilePasswordKey);
                removed = true;
            }
            if (removed)
                await ini.SaveAsync(path).ConfigureAwait(false);
        }
        finally { s_gate.Release(); }
    }

    /// <summary>Writes (or, for an empty password, removes) the profile's plain text password.</summary>
    public static async Task SetPlainPasswordAsync(string profileName, string? password)
    {
        await s_gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = ResolvePath();
            var ini = await LoadIniAsync(persist: false).ConfigureAwait(false);
            var section = ProfileSectionPrefix + profileName;
            if (string.IsNullOrEmpty(password))
            {
                if (ini.Get(section, ProfilePasswordKey) is null) return;
                ini.Remove(section, ProfilePasswordKey);
            }
            else
                ini.Set(section, ProfilePasswordKey, IniPasswordCodec.Encode(password));
            await ini.SaveAsync(path).ConfigureAwait(false);
        }
        finally { s_gate.Release(); }
    }
#endif

    // -- Private ----------------------------------------------------------------

    /// <summary>
    /// Loads mucka.ini with the per-profile settings of older versions already moved into
    /// [settings] (see <see cref="GlobalSettingsMigration"/>). Called with the gate held. With
    /// <paramref name="persist"/> the moved document is written straight away, so a read-only
    /// caller does not have to wait for a save; the value is in [settings] in the same atomic
    /// write that removes the old copy. A failed write is not fatal: the caller still gets the
    /// moved document, and the next save writes it.
    /// </summary>
    private static async Task<IniFile> LoadIniAsync(bool persist = true)
    {
        var path = ResolvePath();
        var ini  = IniFile.Load(path);
        if ((GlobalSettingsMigration.Apply(ini) | Mucka.Ui.UiFontScale.MigrateLegacyKey(ini.Get, ini.Set, ini.Remove)) && persist)
        {
            try { await ini.SaveAsync(path).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"[SettingsStore] migrated mucka.ini not written: {ex}");
            }
        }
        return ini;
    }

    /// <summary>
    /// Writes the three connect-page settings that are global: the advertised column count, the
    /// anti-idle interval and keep-screen-on. Every other key in the file is left alone.
    /// </summary>
    public static async Task SaveConnectGlobalsAsync(int maxColumns, int antiIdleSeconds, bool keepScreenOn)
    {
        await s_gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = ResolvePath();
            var ini  = await LoadIniAsync(persist: false).ConfigureAwait(false);
            ini.Set("settings", "columns",      maxColumns.ToString(CultureInfo.InvariantCulture));
            ini.Set("settings", "antiidle",     antiIdleSeconds.ToString(CultureInfo.InvariantCulture));
            ini.Set("settings", "keepscreenon", keepScreenOn ? "yes" : "no");
            await ini.SaveAsync(path).ConfigureAwait(false);
        }
        finally
        {
            s_gate.Release();
        }
    }

    private static List<Profile> DefaultProfiles() => new()
    {
        new Profile { Name = "MUD2 UK",  Host = "mud2.co.uk",  Port = 23    },
        new Profile { Name = "MUD2.COM", Host = "www.mud2.com", Port = 27723 },
    };

    private static List<Profile> ReadProfiles(IniFile ini)
    {
        var names = GlobalSettingsMigration.ProfileNames(ini);

        var profiles = new List<Profile>(names.Count);
        foreach (var name in names)
        {
            var section = ProfileSectionPrefix + name;
            if (!ini.HasSection(section)) continue; // ordered name without a section
            var p = new Profile { Name = name };
            if (ini.Get(section, "host") is { Length: > 0 } host)        p.Host               = host;
            if (GetInt (ini, section, "port")            is int port)    p.Port               = port;
            if (GetBool(ini, section, "usessh")          is bool ssh)    p.UseSsh             = ssh;
            p.AccountId = ini.Get(section, "account") ?? string.Empty;
            if (GetBool(ini, section, "rememberpassword") is bool rem)   p.RememberPassword   = rem;
            if (GetBool(ini, section, "telnetlogin")      is bool tel)   p.TelnetLoginEnabled = tel;
            if (ini.Get(section, "loginname") is { Length: > 0 } login)  p.TelnetLoginName    = login;
            if (GetInt (ini, "settings", "columns")        is int cols)   p.MaxColumns         = cols;
            if (GetInt (ini, "settings", "antiidle")       is int idle)   p.AntiIdleSeconds    = idle;
            if (GetBool(ini, "settings", "keepscreenon")   is bool keep)  p.KeepScreenOn       = keep;
            if (GetBool(ini, "settings", "defaulthotkeys") is bool defs)  p.DefaultHotkeys     = defs;
            if (GetBool(ini, section, "guidedlogin")      is bool gl)    p.GuidedLogin        = gl;
            p.GuidedLoginPersona = ini.Get(section, "guidedloginpersona") ?? string.Empty;
            profiles.Add(p);
        }
        return profiles;
    }

    private static void WriteProfiles(IniFile ini, List<Profile> profiles)
    {
        // Rewrite the MRU order as 1..N and drop any stale numeric keys beyond it.
        var staleKeys = ini.Items(ProfilesSection)
            .Select(kv => kv.Key)
            .Where(k => int.TryParse(k, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                        && (n < 1 || n > profiles.Count))
            .ToList();
        foreach (var key in staleKeys)
            ini.Remove(ProfilesSection, key);
        for (var i = 0; i < profiles.Count; i++)
            ini.Set(ProfilesSection, (i + 1).ToString(CultureInfo.InvariantCulture), profiles[i].Name);

        // Sections for deleted profiles go away; their settings:/fkeys: sections are
        // deliberately left alone (same hands-off rule as saving globals over them).
        var staleSections = ini.SectionNames()
            .Where(s => s.StartsWith(ProfileSectionPrefix, StringComparison.OrdinalIgnoreCase))
            .Where(s => !profiles.Any(p => s[ProfileSectionPrefix.Length..].Trim()
                .Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (var section in staleSections)
            ini.RemoveSection(section);

        // The two Combat Rail keys are global and live in [settings] only (SetGlobalFlagAsync).
        // Copies in [profile:Name] / [settings:Name] are read by nothing, so they are cleared: a key
        // left where a reader might look for it is a file that lies about its own state.
        foreach (var section in ini.SectionNames()
                     .Where(s => s.StartsWith(ProfileSectionPrefix, StringComparison.OrdinalIgnoreCase)
                              || s.StartsWith("settings:", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            ini.Remove(section, "showcombatrail");
            ini.Remove(section, "showcombatstats");
        }

        foreach (var p in profiles)
        {
            var section = ProfileSectionPrefix + p.Name;
            ini.Set(section, "host",             p.Host);
            ini.Set(section, "port",             p.Port.ToString(CultureInfo.InvariantCulture));
            ini.Set(section, "usessh",           p.UseSsh             ? "yes" : "no");
            ini.Set(section, "account",          p.AccountId);
            ini.Set(section, "rememberpassword", p.RememberPassword   ? "yes" : "no");
            ini.Set(section, "telnetlogin",      p.TelnetLoginEnabled ? "yes" : "no");
            ini.Set(section, "loginname",        p.TelnetLoginName);
            ini.Set(section, "guidedlogin",       p.GuidedLogin        ? "yes" : "no");
            ini.Set(section, "guidedloginpersona", p.GuidedLoginPersona);
        }
    }

    // Sound enablement keys, all within the settings section. Override-only - defaults
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
    /// (pre-feature ini - callers keep the built-in everything-on defaults).</summary>
    private static SoundSettings? ReadSoundSettings(IniFile ini, string section)
    {
        SoundSettings? sounds = null;
        SoundSettings Sounds() => sounds ??= new SoundSettings();

        if (GetBool(ini, section, "sounds") is bool master)
            Sounds().MasterEnabled = master;
        foreach (var (key, value) in ini.Items(section))
        {
            // Longest prefixes first - "soundgroupvol."/"soundvol." must not fall into
            // the "soundgroup."/"sound." branches.
            if (key.StartsWith(SoundGroupVolKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vol))
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
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vol))
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
            ini.Set(section, SoundGroupVolKeyPrefix + prefix, vol.ToString(CultureInfo.InvariantCulture));
        foreach (var (code, vol) in sounds.SoundVolumes)
            ini.Set(section, SoundVolKeyPrefix + code, vol.ToString(CultureInfo.InvariantCulture));
    }

    private static bool? ParseOnOff(string value)
        => value.ToLowerInvariant() switch
        {
            "yes" or "true" or "on" or "1" => true,
            "no" or "false" or "off" or "0" => false,
            _ => null,
        };

    private static int? GetInt(IniFile ini, string section, string key)
        => int.TryParse(ini.Get(section, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v : null;

    private static bool? GetBool(IniFile ini, string section, string key)
        => ini.Get(section, key)?.ToLowerInvariant() switch
        {
            "yes" or "true" or "on" or "1" => true,
            "no" or "false" or "off" or "0" => false,
            _ => null,
        };

    /// <summary>Parses "F1".."F36" (case-insensitive) into a 0-based Fkey index.</summary>
    private static bool TryParseFkeyIndex(string key, out int index)
    {
        index = -1;
        if (key.Length < 2 || (key[0] != 'F' && key[0] != 'f'))
            return false;
        if (!int.TryParse(key[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            || n < 1 || n > FkeyCount)
            return false;
        index = n - 1;
        return true;
    }
}
