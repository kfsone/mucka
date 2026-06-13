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
    private int _maxColumns = 80;
    private int _antiIdleSeconds = 0;
    // Mobile defaults on — matches Profile.KeepScreenOn (screen-lock mid-session gets you swamped).
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
    public int MaxColumns { get => _maxColumns; set => Set(ref _maxColumns, Math.Clamp(value, 20, 160)); }
    public int AntiIdleSeconds { get => _antiIdleSeconds; set => Set(ref _antiIdleSeconds, Math.Clamp(value, 0, 3600)); }
    public bool KeepScreenOn { get => _keepScreenOn; set => Set(ref _keepScreenOn, value); }
    public bool IsCaptureRequested { get => _captureRequested; set => Set(ref _captureRequested, value); }

    public bool IsCaptureFacilityAvailable { get; } =
#if DEBUG
        true;
#else
        false;
#endif

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

    public string AdvancedChevron => AdvancedVisible ? "▼  Advanced" : "▶  Advanced";
    public bool CanConnect => !_isConnecting;
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
                    // which is skipped in direct-connect mode — intentionally.
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

            await conn.ConnectAsync(Host.Trim(), Port);

            // Carry the persisted settings (fkeys, font, volume, …) over from the saved
            // profile — they are not editable on this page but must not reset on connect.
            var saved = SavedProfiles.FirstOrDefault(p =>
                string.Equals(p.Name, ProfileName, StringComparison.OrdinalIgnoreCase));
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
                DefaultHotkeys = saved?.DefaultHotkeys ?? true,
                FontSize = saved?.FontSize ?? 0,
                Volume = saved?.Volume ?? 75,
                StatUpdateFrequency = saved?.StatUpdateFrequency ?? 10,
                MuteBeepPermanently = saved?.MuteBeepPermanently ?? false,
                SettingsPerProfile = saved?.SettingsPerProfile ?? false,
                FkeysPerProfile = saved?.FkeysPerProfile ?? false,
                Fkeys = saved?.Fkeys ?? new string[36]
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
        ApplyProfile(p);
        var _ = LoadProfilePasswordAsync(p).ContinueWith(
            t => System.Diagnostics.Debug.WriteLine($"[SelectProfile] password load failed: {t.Exception}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private void ApplyProfile(Profile p)
    {
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
    }

    private async Task SelectProfileAsync(Profile p, bool loadPassword)
    {
        ApplyProfile(p);
        if (loadPassword)
        {
            await LoadProfilePasswordAsync(p);
        }
    }

    private async Task LoadProfilePasswordAsync(Profile p)
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

        if (input == null || input != name) return;

        var existing = SavedProfiles.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing == null) return;

        SavedProfiles.Remove(existing);
        await ProfileStore.SetPasswordAsync(name, null);
        // The profile's settings:/fkeys: ini sections are deliberately left behind —
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
            existing.SettingsPerProfile  = settings.SettingsPerProfile;
            existing.FkeysPerProfile     = settings.FkeysPerProfile;
            existing.Fkeys               = fkeys;
            if (string.Equals(existing.Name, ProfileName, StringComparison.OrdinalIgnoreCase))
                MaxColumns = settings.MaxColumns;
        }
        await SettingsStore.SaveProfileAsync(profileName, settings, fkeys);
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

        // Keep mucka.ini in sync — it is the authoritative settings store, so a column
        // change made on this page must not be reverted by a stale ini section next launch.
        // The save lands in whichever scope the profile loaded from (global by default).
        var settings = new ClientSettings
        {
            FontSize            = incoming.FontSize,
            MaxColumns          = incoming.MaxColumns,
            Volume              = incoming.Volume,
            StatUpdateFrequency = incoming.StatUpdateFrequency,
            MuteBeepPermanently = incoming.MuteBeepPermanently,
            SettingsPerProfile  = incoming.SettingsPerProfile,
            FkeysPerProfile     = incoming.FkeysPerProfile,
            Sounds              = incoming.Sounds,
        };
        // fkeys: null — hotkeys are not editable on this page, so never rewrite their sections.
        await SettingsStore.SaveProfileAsync(incoming.Name, settings, fkeys: null);
    }
}
