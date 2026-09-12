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
    private bool _captureRequested;
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
    public bool IsCaptureRequested { get => _captureRequested; set => Set(ref _captureRequested, value); }

    /// <summary>Advanced feature: automate the MUD Shell (login through persona select/create)
    /// instead of requiring the player to drive it by hand after connecting.</summary>
    public bool GuidedLogin { get => _guidedLogin; set => Set(ref _guidedLogin, value); }
    public string GuidedLoginPersona { get => _guidedLoginPersona; set => Set(ref _guidedLoginPersona, value); }

    /// <summary>Advanced feature: pre-connect capture arming is available on all builds/platforms.</summary>
    public bool IsCaptureFacilityAvailable { get; } = true;

    public string CaptureButtonText => IsCaptureRequested ? "Capture: Armed" : "Capture: Off";
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
    public ICommand ToggleCaptureCommand { get; }
    public ICommand DeleteProfileCommand { get; }

    public Func<PasswordPromptArgs, Task<PasswordResult?>>? PasswordRequired;

    public event Action<MuckaConnection, Profile>? Connected;

    public ConnectViewModel()
    {
        ConnectCommand = new AsyncCommand(ConnectAsync);
        SelectProfileCommand = new Command<Profile>(SelectProfile);
        ToggleAdvancedCommand = new Command(() => AdvancedVisible = !AdvancedVisible);
        ToggleCaptureCommand = new Command(() =>
        {
            IsCaptureRequested = !IsCaptureRequested;
            OnPropertyChanged(nameof(CaptureButtonText));
        });
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
            var conn = new MuckaConnection(
                autoLogin ? accountId : null,
                autoLogin ? resolvedPassword : null,
                MaxColumns,
                loginName);
            if (IsCaptureRequested && !conn.TryStartCapture(Host.Trim(), out var captureError))
            {
                StatusText = $"Capture start failed: {captureError}";
                HasError = true;
                return;
            }

            // Carry the persisted settings (fkeys, font, volume, ...) over from the saved
            // profile - they are not editable on this page but must not reset on connect.
            var saved = SavedProfiles.FirstOrDefault(p =>
                string.Equals(p.Name, ProfileName, StringComparison.OrdinalIgnoreCase));

            // The one-time global wire log. Started here rather than in GamePage because it must be
            // running before the socket opens, or the login exchange - the part of a session most
            // worth a byte-exact record of - is the one part missing from it. The key lives in the
            // single global [settings] section, so every profile carries the same value; the
            // fallback to any loaded profile only matters for a brand-new profile name typed on
            // this page, which has no SavedProfiles entry yet. Failure here is reported but never
            // blocks the connection, unlike the hand-armed capture above. StatusText covers the
            // case where the connection then fails too; MuckaConnection.WireLogFailure carries it
            // into the terminal if the connection succeeds.
            var wireLog = (saved ?? SavedProfiles.FirstOrDefault())?.LogWireSession ?? false;
            if (wireLog && !conn.TryStartWireLog(Host.Trim(), out var wireLogError))
                StatusText = $"Wire log failed to start: {wireLogError}";

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
                // Same as Sounds above.
                ShowCombatRail = saved?.ShowCombatRail ?? false,
                // Same as Sounds above.
                LogWireSession = saved?.LogWireSession ?? false,
            };
            if (saved is null)
            {
                // Brand-new profile: seed from the stored globals so the connect-time ini
                // sync below writes them back unchanged instead of clobbering them with
                // defaults. The page-edited column count wins over the stored one.
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

#if DEBUG
        if (_cmdArgs.Record)
        {
            IsCaptureRequested = true;
            OnPropertyChanged(nameof(CaptureButtonText));
        }
#endif
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
            // Same reasoning as Sounds above (and the settingsSection carve-out this field gets in
            // SettingsStore): ConnectPage builds GamePage once and never re-reads SavedProfiles from
            // disk for the life of the run (App.xaml.cs's single ConnectPage root, GamePage returning
            // via PopAsync), so if this in-memory mirror does not pick up a live toggle, the NEXT
            // SaveCurrentProfileAsync on reconnect writes the stale in-memory value straight back over
            // the ini's freshly-toggled one.
            existing.ShowCombatRail      = settings.ShowCombatRail;
            if (string.Equals(existing.Name, ProfileName, StringComparison.OrdinalIgnoreCase))
                MaxColumns = settings.MaxColumns;
        }
        // The wire-log switch is GLOBAL - one key in the one [settings] section - so it is mirrored
        // onto every loaded profile, not just the one being saved. Same in-memory-staleness reasoning
        // as ShowCombatRail above: SavedProfiles is never re-read from disk for the life of the run,
        // and a reconnect to any OTHER profile would otherwise still see the pre-toggle value.
        foreach (var p in SavedProfiles)
            p.LogWireSession = settings.LogWireSession;
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
    /// profile's shared global default. So this method writes <c>writeDisplayGlobals: false</c> and
    /// <c>writeSounds: false</c>, and sources every OTHER settingsSection field (FontSize, Volume, ...)
    /// from the in-memory profile's own already-correct values rather than from GameViewModel's live
    /// state, so nothing outside ShowCombatRail can drift through this path. <c>fkeys: null</c> for the
    /// same reason <see cref="SaveCurrentProfileAsync"/> uses it - a rail toggle does not edit hotkeys,
    /// so their section is left untouched rather than rewritten from a value not being changed here.
    /// </para>
    /// </summary>
    public async Task PersistCombatRailVisibilityAsync(string profileName, bool showCombatRail)
    {
        var existing = SavedProfiles.FirstOrDefault(p =>
            string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            return;

        existing.ShowCombatRail = showCombatRail;

        var settings = new ClientSettings
        {
            FontSize            = existing.FontSize,
            MaxColumns          = existing.MaxColumns,
            Volume              = existing.Volume,
            StatUpdateFrequency = existing.StatUpdateFrequency,
            MuteBeepPermanently = existing.MuteBeepPermanently,
            LogResetDiagnostics = existing.LogResetDiagnostics,
            SettingsPerProfile  = existing.SettingsPerProfile,
            FkeysPerProfile     = existing.FkeysPerProfile,
            ShowCombatRail      = showCombatRail,
        };
        await SettingsStore.SaveProfileAsync(profileName, settings, fkeys: null,
            writeSounds: false, writeDisplayGlobals: false);
    }

    private async Task SaveCurrentProfileAsync(Profile incoming, string? password)
    {
        var existing = SavedProfiles.FirstOrDefault(p =>
            string.Equals(p.Name, incoming.Name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            // Settings fields (FontSize/Volume/StatUpdateFrequency/mutes/scope flags) are not
            // copied here: incoming was built FROM existing for those, so they already match.
            // If ConnectAsync ever diverges them, copy them here too or the in-memory profile
            // and the ini sync below will disagree.
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
            // ShowCombatRail is a settingsSection field (per-profile like FontSize/Volume above via
            // SettingsPerProfile), NOT part of the "Display tab globals" block writeDisplayGlobals:
            // false below skips - so it is always written here regardless of that flag, and has to
            // be carried through explicitly for the same reason FontSize/Volume/Sounds already are:
            // omitting it would hand SaveProfileAsync the C# default (false/hidden), silently
            // wiping a player's saved "shown" preference on the next connect.
            ShowCombatRail      = incoming.ShowCombatRail,
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
