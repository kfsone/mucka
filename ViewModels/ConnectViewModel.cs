using System.Collections.ObjectModel;
using System.Windows.Input;
using Mucka.Core;

namespace Mucka.ViewModels;

public sealed class ConnectViewModel : BaseViewModel
{
    private readonly CommandLineArgs _cmdArgs = CommandLineArgs.Current;
    private readonly Task _loadProfilesTask;
    private string _profileName = string.Empty;
    private string _host = "mud2.co.uk";
    private int _port = 23;
    private string _statusText = string.Empty;
    private bool _isConnecting;
    private bool _hasError;
    private string _accountId = string.Empty;
    private string _password = string.Empty;
    private bool _rememberPassword;
    private bool _telnetLoginEnabled = true;
    private string _telnetLoginName = "mud";
    private bool _advancedVisible;
    private bool _guidedLogin;
    private string _guidedLoginPersona = string.Empty;
    private int _maxColumns = 80;
    private int _antiIdleSeconds = 0;
    private int _profileSelectionVersion;
    private int _profileLoadsInProgress;
    // Mobile defaults on - matches Profile.KeepScreenOn (screen-lock mid-session gets you swamped).
    private bool _keepScreenOn =
#if ANDROID || IOS
        true;
#else
        false;
#endif

    public string ProfileName { get => _profileName; set => Set(ref _profileName, value); }
    public string Host { get => _host; set => Set(ref _host, value); }
    public int Port { get => _port; set => Set(ref _port, value); }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public bool IsConnecting { get => _isConnecting; set => Set(ref _isConnecting, value); }
    public bool HasError { get => _hasError; set => Set(ref _hasError, value); }
    public string AccountId { get => _accountId; set => Set(ref _accountId, value); }
    public string Password { get => _password; set => Set(ref _password, value); }
    public bool RememberPassword { get => _rememberPassword; set => Set(ref _rememberPassword, value); }
    public bool TelnetLoginEnabled { get => _telnetLoginEnabled; set => Set(ref _telnetLoginEnabled, value); }
    public string TelnetLoginName { get => _telnetLoginName; set => Set(ref _telnetLoginName, value); }
    public int MaxColumns
    {
        get => _maxColumns;
        set
        {
            if (Set(ref _maxColumns, Math.Clamp(value, 0, 160)))
                OnPropertyChanged(nameof(MaxColumnsText));
        }
    }

    /// <summary>Display string for the MaxColumns entry - blank means "auto" (0).</summary>
    public string MaxColumnsText => _maxColumns == 0 ? string.Empty : _maxColumns.ToString();
    public int AntiIdleSeconds { get => _antiIdleSeconds; set => Set(ref _antiIdleSeconds, Math.Clamp(value, 0, 3600)); }
    public bool KeepScreenOn { get => _keepScreenOn; set => Set(ref _keepScreenOn, value); }

    /// <summary>Advanced feature: automate the MUD Shell (login through persona select/create)
    /// instead of requiring the player to drive it by hand after connecting.</summary>
    public bool GuidedLogin { get => _guidedLogin; set => Set(ref _guidedLogin, value); }
    public string GuidedLoginPersona { get => _guidedLoginPersona; set => Set(ref _guidedLoginPersona, value); }

    public bool AdvancedVisible
    {
        get => _advancedVisible;
        set
        {
            if (Set(ref _advancedVisible, value))
            {
                OnPropertyChanged(nameof(AdvancedChevron));
            }
        }
    }

    public string AdvancedChevron => (AdvancedVisible ? Glyph.TriangleDown : Glyph.TriangleRight) + "  Advanced";
    public bool CanConnect => !_isConnecting && Volatile.Read(ref _profileLoadsInProgress) == 0;
    public bool IsDirectConnectMode => _cmdArgs.Error == null && _cmdArgs.HasDirectConnectOptions;
    public Task LoadProfilesTask => _loadProfilesTask;

    public ObservableCollection<Profile> SavedProfiles { get; } = new();

    public ICommand ConnectCommand { get; }
    public ICommand SelectProfileCommand { get; }
    public ICommand ToggleAdvancedCommand { get; }
    public ICommand ShowTelnetHelpCommand { get; }
    public ICommand DeleteProfileCommand { get; }

    public Func<PasswordPromptArgs, Task<PasswordResult?>>? PasswordRequired;

    public event Action<MuckaConnection, Profile>? Connected;

    public ConnectViewModel()
    {
        ConnectCommand = new AsyncCommand(ConnectAsync);
        SelectProfileCommand = new Command<Profile>(SelectProfile);
        ToggleAdvancedCommand = new Command(() => AdvancedVisible = !AdvancedVisible);
        DeleteProfileCommand = new AsyncCommand(DeleteProfileAsync);
        ShowTelnetHelpCommand = new Command(async () =>
        {
            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page != null)
            {
                await page.DisplayAlertAsync(
                    "Telnet Login",
                    "MUSE games (MUD2, MUD1) require a telnet login of \"mud\" followed by your Account ID and password.\n\nLeave checked for automatic login. Uncheck to log in by hand.\n\nFor anything else, please file an issue on GitHub.",
                    "OK");
            }
        });
        _loadProfilesTask = LoadProfilesAsync();
    }

    private async Task ConnectAsync()
    {
        HasError = false;
        StatusText = string.Empty;
        IsConnecting = true;
        OnPropertyChanged(nameof(CanConnect));
        try
        {
            var accountId = AccountId.Trim();
            var loginName = TelnetLoginName.Trim();

            string resolvedPassword = Password;
            if (TelnetLoginEnabled && !string.IsNullOrEmpty(accountId) && string.IsNullOrEmpty(resolvedPassword))
            {
                if (PasswordRequired == null)
                {
                    StatusText = "Password required. Please enter your password.";
                    HasError = true;
                    return;
                }

                var result = await PasswordRequired(new PasswordPromptArgs(ProfileName, Host.Trim(), Port, accountId));
                if (result == null) return;
                resolvedPassword = result.Password;
                Password = resolvedPassword;
                if (result.Remember)
                {
                    RememberPassword = true;
                    // Password is persisted below via SaveCurrentProfileAsync,
                    // which is skipped in direct-connect mode - intentionally.
                }
            }

            var autoLogin = TelnetLoginEnabled && !string.IsNullOrEmpty(accountId) && !string.IsNullOrEmpty(resolvedPassword);
            // The host goes in here rather than only into ConnectAsync because the store - and with
            // it the wire log - opens with the connection, so the login exchange, the part of a
            // session most worth a byte-exact record of, is in the log like everything else. A store
            // that cannot be opened reports itself into the terminal and never blocks the connection.
            // Off the UI thread, because constructing this opens the database, and opening the
            // database runs any schema migration the file still needs (MuckaDb.ApplySchema). That is
            // synchronous SQLite work whose cost is unbounded in principle and measured in hundreds
            // of milliseconds in practice - the migration that introduced persona_sessions took over
            // half a second on the operator's own file, rewriting eleven tables.
            //
            // Not Invariant #1, which is about the command box and there is none on this page: this
            // is a frozen Connect button on the first launch after an update, which is a bad first
            // impression rather than a broken one. Worth fixing on its own merits.
            var conn = await Task.Run(() => new MuckaConnection(
                autoLogin ? accountId : null,
                autoLogin ? resolvedPassword : null,
                MaxColumns,
                loginName,
                Host.Trim())).ConfigureAwait(true);

            // Carry the persisted settings (fkeys, font, volume, ...) over from the saved
            // profile - they are not editable on this page but must not reset on connect.
            var saved = SavedProfiles.FirstOrDefault(p =>
                string.Equals(p.Name, ProfileName, StringComparison.OrdinalIgnoreCase));

            await conn.ConnectAsync(Host.Trim(), Port);

            var profile = new Profile
            {
                Name = ProfileName,
                Host = Host.Trim(),
                Port = Port,
                AccountId = accountId,
                RememberPassword = RememberPassword,
                TelnetLoginEnabled = TelnetLoginEnabled,
                TelnetLoginName = loginName,
                MaxColumns = MaxColumns,
                AntiIdleSeconds = AntiIdleSeconds,
                KeepScreenOn = KeepScreenOn,
                GuidedLogin = GuidedLogin,
                GuidedLoginPersona = GuidedLoginPersona.Trim(),
                DefaultHotkeys = saved?.DefaultHotkeys ?? true,
                FontSize = saved?.FontSize ?? 0,
                Volume = saved?.Volume ?? 75,
                StatUpdateFrequency = saved?.StatUpdateFrequency ?? 10,
                MuteBeepPermanently = saved?.MuteBeepPermanently ?? false,
                LogResetDiagnostics = saved?.LogResetDiagnostics ?? false,
                SettingsPerProfile = saved?.SettingsPerProfile ?? false,
                FkeysPerProfile = saved?.FkeysPerProfile ?? false,
                Fkeys = saved?.Fkeys ?? new string[36],
                // `saved` is already hydrated from mucka.ini by LoadProfilesAsync's ApplyTo overlay,
                // but this fresh Profile does not inherit from it automatically - every settings
                // field has to be carried across explicitly, or it silently reverts to its C#
                // default here and the ini sync below (SaveCurrentProfileAsync) writes that default
                // back to disk. Any new settings field must be added HERE as well as to
                // ClientSettings, Profile and SettingsStore.
                Sounds = saved?.Sounds ?? new SoundSettings(),
                // The Combat Rail's two flags. These come from `saved`'s own [profile:Name] section
                // and are NOT touched by the ini overlay below, whose scope is [settings].
                ShowCombatRail = saved?.ShowCombatRail ?? false,
                ShowCombatStats = saved?.ShowCombatStats ?? true,
                // The always-global Display block. Carried across for exactly the reason above, and
                // each fallback is the matching Profile default - a wrong fallback here is
                // indistinguishable from the field being missing altogether. What this failing
                // looks like in the hands: a value saved in [settings] is read into `saved` at
                // startup, dropped here, and the live default is what the session runs on, so the
                // ini keeps the player's answer and the client keeps ignoring it.
                DefaultFontSize = saved?.DefaultFontSize ?? 0,
                DefaultMaxColumns = saved?.DefaultMaxColumns ?? 0,
                DreamwordSizeOffset = saved?.DreamwordSizeOffset ?? 0,
                MeNameColor = saved?.MeNameColor ?? MudSharp.Models.SelfChatColorizer.DefaultNameHex,
                MeSpeechColor = saved?.MeSpeechColor ?? MudSharp.Models.SelfChatColorizer.DefaultSpeechHex,
                ShowOnline = saved?.ShowOnline ?? true,
                ShowInventory = saved?.ShowInventory ?? true,
                ShowItemsHere = saved?.ShowItemsHere ?? true,
                ShowMapCompass = saved?.ShowMapCompass ?? true,
                MaxOnlineDisplay = saved?.MaxOnlineDisplay ?? 0,
                OnlineNamesOnly = saved?.OnlineNamesOnly ?? false,
                OnlineForgetWindow = saved?.OnlineForgetWindow ?? 5,
                FloatOnline = saved?.FloatOnline ?? false,
                FloatCompass = saved?.FloatCompass ?? false,
            };
            // Overlay mucka.ini over the block above, on EVERY connect and not only for a brand-new
            // profile. The ini is the authoritative store; `saved` is a cache read once at app start
            // and never re-read for the life of the run, so a value the settings dialog's Save wrote
            // to the file after that is on disk while the cache still holds the old one, and a relog
            // inside one run restores what the player turned off. The fields above stay: ApplyTo only writes the
            // keys the file actually has, so they are the defaults for the ones it does not.
            // The page-edited column count wins over the stored one.
            try
            {
                var stored = await SettingsStore.LoadProfileAsync(profile.Name);
                if (stored is not null)
                {
                    stored.ApplyTo(profile);
                    profile.MaxColumns = MaxColumns;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConnectViewModel] mucka.ini seed failed for '{profile.Name}': {ex}");
            }
            if (!IsDirectConnectMode)
            {
                await SaveCurrentProfileAsync(profile, RememberPassword ? resolvedPassword : null);
            }

            Connected?.Invoke(conn, profile);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            HasError = true;
        }
        finally
        {
            IsConnecting = false;
            OnPropertyChanged(nameof(CanConnect));
        }
    }

    private void SelectProfile(Profile p)
    {
        if (IsConnecting)
        {
            return;
        }

        ApplyProfile(p);
        var _ = LoadProfilePasswordAsync(p).ContinueWith(
            t => System.Diagnostics.Debug.WriteLine($"[SelectProfile] password load failed: {t.Exception}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private int ApplyProfile(Profile p)
    {
        Password = string.Empty;
        ProfileName = p.Name;
        Host = p.Host;
        Port = p.Port;
        AccountId = p.AccountId;
        RememberPassword = p.RememberPassword;
        TelnetLoginEnabled = p.TelnetLoginEnabled;
        TelnetLoginName = string.IsNullOrEmpty(p.TelnetLoginName) ? "mud" : p.TelnetLoginName;
        MaxColumns = p.MaxColumns;
        AntiIdleSeconds = p.AntiIdleSeconds;
        KeepScreenOn = p.KeepScreenOn;
        GuidedLogin = p.GuidedLogin;
        GuidedLoginPersona = p.GuidedLoginPersona;
        return Interlocked.Increment(ref _profileSelectionVersion);
    }

    private async Task SelectProfileAsync(Profile p, bool loadPassword)
    {
        ApplyProfile(p);
        if (loadPassword)
        {
            await LoadProfilePasswordAsync(p);
        }
    }

    public async Task LaunchProfileAsync(Profile profile)
    {
        if (IsConnecting)
        {
            return;
        }

        var selectionVersion = ApplyProfile(profile);
        await LoadProfilePasswordAsync(profile);
        if (selectionVersion != Volatile.Read(ref _profileSelectionVersion))
        {
            return;
        }

        if (ConnectCommand.CanExecute(null))
        {
            ConnectCommand.Execute(null);
        }
    }

    private async Task LoadProfilePasswordAsync(Profile p)
    {
        Interlocked.Increment(ref _profileLoadsInProgress);
        OnPropertyChanged(nameof(CanConnect));
        try
        {
            await LoadProfilePasswordCoreAsync(p);
        }
        finally
        {
            Interlocked.Decrement(ref _profileLoadsInProgress);
            OnPropertyChanged(nameof(CanConnect));
        }
    }

    private async Task LoadProfilePasswordCoreAsync(Profile p)
    {
        if (p.RememberPassword)
        {
            string? pw;
            try
            {
                pw = await ProfileStore.GetPasswordAsync(p.Name);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConnectViewModel] password load failed for '{p.Name}': {ex}");
                // Guard against a stale load completing after the user switched profiles.
                if (ProfileName == p.Name)
                {
                    Password = string.Empty;
                    StatusText = "Password could not be retrieved from secure storage. Please re-enter your password or check your device security settings.";
                    HasError = true;
                }
                return;
            }
            // Guard against a stale load completing after the user switched profiles.
            if (ProfileName == p.Name)
                Password = pw ?? string.Empty;
        }
        else if (ProfileName == p.Name)
        {
            Password = string.Empty;
        }
    }

    private async Task DeleteProfileAsync()
    {
        if (IsConnecting)
        {
            return;
        }

        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page == null) return;

        var name = ProfileName;
        if (string.IsNullOrWhiteSpace(name)) return;

        var input = await page.DisplayPromptAsync(
            "Delete Profile",
            $"Type \"{name}\" to confirm deletion.",
            accept: "Delete",
            cancel: "Cancel",
            placeholder: name,
            initialValue: string.Empty);

        if (IsConnecting || input == null || input != name) return;

        var existing = SavedProfiles.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing == null) return;

        SavedProfiles.Remove(existing);
        await ProfileStore.SetPasswordAsync(name, null);
        // The profile's settings:/fkeys: ini sections are deliberately left behind -
        // SaveProfilesAsync only removes the [profile:] section and the order entry.

        if (SavedProfiles.Count == 0)
        {
            var def = new Profile { Name = "Default", Host = "mud2.co.uk", Port = 23 };
            SavedProfiles.Add(def);
            ApplyProfile(def);
        }
        else
        {
            ApplyProfile(SavedProfiles[0]);
        }

        await SettingsStore.SaveProfilesAsync(SavedProfiles.ToList());
    }

    private async Task LoadProfilesAsync()
    {
        List<Profile> list;
        try
        {
            list = await SettingsStore.LoadProfilesAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load profiles: {ex.Message}";
            HasError = true;
            return;
        }
        var loadPasswordFromStore = _cmdArgs.Password == null;
        SavedProfiles.Clear();
        foreach (var p in list)
        {
            // [profile:] sections carry identity only; overlay each profile's settings
            // and fkeys from their own [settings]/[fkeys] sections (per-profile or global).
            try
            {
                (await SettingsStore.LoadProfileAsync(p.Name))?.ApplyTo(p);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConnectViewModel] mucka.ini load failed for '{p.Name}': {ex}");
            }
            SavedProfiles.Add(p);
        }

        if (_cmdArgs.Error != null)
        {
            StatusText = _cmdArgs.Error;
            HasError = true;
            return;
        }

        if (!string.IsNullOrEmpty(_cmdArgs.Profile))
        {
            var match = SavedProfiles.FirstOrDefault(p =>
                string.Equals(p.Name, _cmdArgs.Profile, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                await SelectProfileAsync(match, loadPasswordFromStore);
            else if (SavedProfiles.Count > 0)
                await SelectProfileAsync(SavedProfiles[0], loadPasswordFromStore);
        }
        else if (SavedProfiles.Count > 0)
        {
            await SelectProfileAsync(SavedProfiles[0], loadPasswordFromStore);
        }

        // Apply individual command-line overrides on top of the selected profile.
        if (_cmdArgs.Host != null) Host = _cmdArgs.Host;
        if (_cmdArgs.Port.HasValue) Port = _cmdArgs.Port.Value;
        if (_cmdArgs.User != null) TelnetLoginName = _cmdArgs.User;
        if (_cmdArgs.Account != null) AccountId = _cmdArgs.Account;
        if (_cmdArgs.Password != null) Password = _cmdArgs.Password;
    }

    /// <summary>
    /// Saves a profile's settings and fkeys to mucka.ini and mirrors them onto the
    /// in-memory profile so a reconnect in the same run sees the new values.
    /// Used by GameViewModel's settings dialog via the saver delegate.
    /// </summary>
    public async Task SaveProfileSettingsAsync(string profileName, ClientSettings settings, string[] fkeys)
    {
        var existing = SavedProfiles.FirstOrDefault(p =>
            string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.FontSize            = settings.FontSize;
            existing.MaxColumns          = settings.MaxColumns;
            existing.Volume              = settings.Volume;
            existing.StatUpdateFrequency = settings.StatUpdateFrequency;
            existing.MuteBeepPermanently = settings.MuteBeepPermanently;
            existing.LogResetDiagnostics = settings.LogResetDiagnostics;
            existing.SettingsPerProfile  = settings.SettingsPerProfile;
            existing.FkeysPerProfile     = settings.FkeysPerProfile;
            existing.Fkeys               = fkeys;
            existing.Sounds              = settings.Sounds;
            if (string.Equals(existing.Name, ProfileName, StringComparison.OrdinalIgnoreCase))
                MaxColumns = settings.MaxColumns;
        }
        await SettingsStore.SaveProfileAsync(profileName, settings, fkeys);
    }

    /// <summary>
    /// Persists ONLY the Combat Rail's shown/hidden state, immediately, from a live overflow-menu
    /// toggle - used by <c>GameViewModel.PersistCombatRailVisibilityAsync</c> instead of
    /// <see cref="SaveProfileSettingsAsync"/> on purpose.
    ///
    /// <para><see cref="SaveProfileSettingsAsync"/> always writes the whole "Display tab globals"
    /// block, and the <c>ClientSettings</c> snapshot it is called with (<c>GameViewModel.CurrentSettings</c>)
    /// sources that block's Show* fields from LIVE <c>SidePanel</c> fold/pin state - correct when the
    /// player explicitly hit Save in the settings dialog, wrong for a one-click rail toggle, which must
    /// not promote whatever the Onlines section happens to be folded to this session into every
    /// profile's shared global default. A single-key write into the profile's own section cannot reach
    /// any of that.</para>
    ///
    /// <para>The in-memory mirror is updated as well as the file. ConnectPage builds GamePage once and
    /// never re-reads SavedProfiles from disk for the life of the run, so a relog would otherwise
    /// re-save the pre-toggle value over the file from <see cref="SaveCurrentProfileAsync"/>'s own
    /// profiles write.</para>
    /// </summary>
    public async Task PersistCombatRailVisibilityAsync(string profileName, bool showCombatRail)
    {
        var existing = SavedProfiles.FirstOrDefault(p =>
            string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            return;

        existing.ShowCombatRail = showCombatRail;
        await SettingsStore.SetProfileFlagAsync(profileName, "showcombatrail", showCombatRail);
    }

    /// <summary>Persists ONLY the Combat Rail's stat rows, from the same kind of live overflow-menu
    /// toggle and for the same reasons as <see cref="PersistCombatRailVisibilityAsync"/> above.</summary>
    public async Task PersistCombatStatsAsync(string profileName, bool showCombatStats)
    {
        var existing = SavedProfiles.FirstOrDefault(p =>
            string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            return;

        existing.ShowCombatStats = showCombatStats;
        await SettingsStore.SetProfileFlagAsync(profileName, "showcombatstats", showCombatStats);
    }

    private async Task SaveCurrentProfileAsync(Profile incoming, string? password)
    {
        var existing = SavedProfiles.FirstOrDefault(p =>
            string.Equals(p.Name, incoming.Name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            // Settings fields (FontSize/Volume/StatUpdateFrequency/mutes/scope flags, the Display
            // globals) are not copied here. They can differ from `existing` - ConnectAsync overlays
            // mucka.ini over the cached profile - but the file is the authoritative side of that
            // difference and every connect re-reads it, so the cache going stale changes nothing.
            // The ini sync below writes `incoming`, which is the file's own values.
            existing.Name = incoming.Name;
            existing.Host = incoming.Host;
            existing.Port = incoming.Port;
            existing.AccountId = incoming.AccountId;
            existing.RememberPassword = incoming.RememberPassword;
            existing.TelnetLoginEnabled = incoming.TelnetLoginEnabled;
            existing.TelnetLoginName = incoming.TelnetLoginName;
            existing.MaxColumns = incoming.MaxColumns;
            existing.AntiIdleSeconds = incoming.AntiIdleSeconds;
            existing.KeepScreenOn = incoming.KeepScreenOn;
            existing.GuidedLogin = incoming.GuidedLogin;
            existing.GuidedLoginPersona = incoming.GuidedLoginPersona;
            existing.Fkeys = incoming.Fkeys;
            var idx = SavedProfiles.IndexOf(existing);
            if (idx > 0)
            {
                SavedProfiles.Move(idx, 0);
            }
        }
        else
        {
            SavedProfiles.Insert(0, incoming);
        }

        await ProfileStore.SetPasswordAsync(incoming.Name, password);
        await SettingsStore.SaveProfilesAsync(SavedProfiles.ToList());

        // Keep mucka.ini in sync - it is the authoritative settings store, so a column
        // change made on this page must not be reverted by a stale ini section next launch.
        // The save lands in whichever scope the profile loaded from (global by default).
        var settings = new ClientSettings
        {
            FontSize            = incoming.FontSize,
            MaxColumns          = incoming.MaxColumns,
            Volume              = incoming.Volume,
            StatUpdateFrequency = incoming.StatUpdateFrequency,
            MuteBeepPermanently = incoming.MuteBeepPermanently,
            LogResetDiagnostics = incoming.LogResetDiagnostics,
            SettingsPerProfile  = incoming.SettingsPerProfile,
            FkeysPerProfile     = incoming.FkeysPerProfile,
            Sounds              = incoming.Sounds,
            // The Combat Rail's two flags are not here and cannot be: they are [profile:Name] keys,
            // written by the SaveProfilesAsync call above from `existing`'s own values.
        };
        // fkeys: null - hotkeys are not editable on this page, so their sections are never rewritten.
        // writeSounds: false - sounds are not editable here either; rewriting them from a profile
        // blob assembled on this page would overwrite the stored per-sound volume overrides.
        // writeDisplayGlobals: false - nothing in that block (default font/columns, dreamword
        // offset, the Show* toggles, online display options, float defaults, "me" chat colours) is
        // editable on this page; this partial ClientSettings leaves each of them at its C# default,
        // so writing the block unconditionally would reset a player's saved globals to those
        // defaults (DefaultFontSize -> 0, OnlineForgetWindow -> 0 killing the Recent list,
        // MeNameColor/MeSpeechColor -> the built-in colours, etc.).
        await SettingsStore.SaveProfileAsync(incoming.Name, settings, fkeys: null,
            writeSounds: false, writeDisplayGlobals: false);
    }
}
