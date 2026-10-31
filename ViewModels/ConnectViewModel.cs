using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Windows.Input;
using Mucka.Core;

namespace Mucka.ViewModels;

public sealed class ConnectViewModel : BaseViewModel
{
    private readonly CommandLineArgs _cmdArgs = CommandLineArgs.Current;

    // Picking a profile reads its remembered password straight away, except where reading it is what
    // asks the player for permission: the Mac keychain prompts on first use, so there it waits until
    // Connect, when the player has asked to log in.
    private static readonly bool LoadPasswordOnSelect = !OperatingSystem.IsMacCatalyst();
    private readonly Task _loadProfilesTask;
    // Static: every connection this app makes shares the one dreamword carry, whatever page
    // instance started it.
    private static readonly Mucka.Commands.DreamwordCarry DreamwordCarry = new();
    private string _profileName = string.Empty;
    private string _host = "mud2.co.uk";
    private int _port = 23;
    private bool _useSsh;
    private int _portBeforeSsh;
    private const int TelnetPort = 23;
    private const int SshPort = 22;
    private string _statusText = string.Empty;
    private string _progressText = string.Empty;
    private bool _passwordFetchAttempted;
    private bool _passwordFetchFailed;
    private Profile? _selectedProfile;
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
    private int _maxColumns;
    private int _antiIdleSeconds;
    private int _profileSelectionVersion;
    private int _profileLoadsInProgress;
    // Mobile defaults on - matches Profile.KeepScreenOn (screen-lock mid-session gets you swamped).
    [SuppressMessage("Performance", "CA1805:Do not initialize unnecessarily",
        Justification = "The initialiser is platform-conditional: false only on desktop, true on the mobile TFMs.")]
    private bool _keepScreenOn =
#if ANDROID || IOS
        true;
#else
        false;
#endif

    public string ProfileName { get => _profileName; set => Set(ref _profileName, value); }
    public string Host
    {
        get => _host;
        set { if (Set(ref _host, value)) OnPropertyChanged(nameof(AccountPlaceholder)); }
    }

    /// <summary>The account id box's hint: the shape of an account id on the two real servers.</summary>
    public string AccountPlaceholder
    {
        get
        {
            var host = _host.Trim();
            if (host.EndsWith("mudii.co.uk", StringComparison.OrdinalIgnoreCase)) return "Z000?????";
            if (host.EndsWith("mud2.com", StringComparison.OrdinalIgnoreCase)) return "P000?????";
            return "account id";
        }
    }
    /// <summary>0 means the port field is empty; connecting then uses the transport's own port.</summary>
    public int Port
    {
        get => _port;
        set
        {
            if (Set(ref _port, value))
                OnPropertyChanged(nameof(PortText));
        }
    }

    /// <summary>The port as typed, blank for 0. An Entry bound straight to the int never writes back
    /// when cleared (blank does not convert), so the parse is here: blank is 0, a number above 65535
    /// (or a run of digits too long to parse) is held at it, and anything else leaves the port as it was. The change is always announced, so
    /// the Entry is put back to the port in force. Invariant culture for the same reason as
    /// <see cref="MaxColumnsText"/>.</summary>
    public string PortText
    {
        get => _port == 0 ? string.Empty : _port.ToString(CultureInfo.InvariantCulture);
        set
        {
            var text = value?.Trim() ?? string.Empty;
            var port = _port;
            if (text.Length == 0)
                port = 0;
            else if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                port = n > 0 ? Math.Min(n, 65535) : _port;
            else if (text.All(char.IsAsciiDigit))
                port = 65535;   // digits too many for an int
            // A real edit forfeits the port UseSsh set aside. Every change the view model makes is
            // echoed back through here unchanged, and must not.
            if (port != _port)
                _portBeforeSsh = 0;
            Port = port;
            OnPropertyChanged(nameof(PortText));
        }
    }

    /// <summary>The SSH transport exists on every build but Android.</summary>
    public static bool SshAvailable =>
#if SSH
        true;
#else
        false;
#endif

    /// <summary>Connect over SSH. Switching it moves the port between the two standard ones, and
    /// empties any other port, which belongs to neither transport; switching back with the port still
    /// empty puts that port back. <see cref="ApplyProfile"/> sets the field directly so loading a
    /// profile leaves its port alone.</summary>
    public bool UseSsh
    {
        get => _useSsh;
        set
        {
            if (!Set(ref _useSsh, value)) return;
            var standard = Port is TelnetPort or SshPort;
            if (value)
            {
                _portBeforeSsh = standard ? 0 : Port;
                Port = standard ? SshPort : 0;
            }
            else
            {
                Port = standard ? TelnetPort : Port == 0 ? _portBeforeSsh : 0;
            }
        }
    }

    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public bool IsConnecting
    {
        get => _isConnecting;
        set
        {
            if (Set(ref _isConnecting, value))
                OnPropertiesChanged(nameof(IsNotConnecting), nameof(ConnectButtonText), nameof(CanConnect), nameof(IdleOpacity));
        }
    }

    public bool IsNotConnecting => !_isConnecting;

    /// <summary>The page's controls fade while a connection is being made: disabled alone, the
    /// buttons keep their own colours and look as live as before.</summary>
    public double IdleOpacity => _isConnecting ? 0.4 : 1.0;
    public string ConnectButtonText => _isConnecting ? "Connecting..." : "Connect  " + Glyph.TriangleRight;

    /// <summary>What the connection attempt is doing right now; shown while <see cref="IsConnecting"/>.</summary>
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }

    /// <summary>The profile row highlighted in the list. Assigned by the list when the player picks a
    /// row, and by <see cref="ApplyProfile"/> whenever the form is filled from a profile by any other
    /// route, so the highlight and the form cannot disagree.</summary>
    public Profile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (ReferenceEquals(_selectedProfile, value)) return;
            _selectedProfile = value;
            OnPropertyChanged();
            if (value is not null) SelectProfile(value);
        }
    }
    public bool HasError { get => _hasError; set => Set(ref _hasError, value); }
    /// <summary>An account id is what turns the automatic login on: blank, the player logs in by hand
    /// (or as a guest, by the profile's login name).</summary>
    public string AccountId
    {
        get => _accountId;
        set { if (Set(ref _accountId, value)) TelnetLoginEnabled = !string.IsNullOrWhiteSpace(value); }
    }
    public string Password
    {
        get => _password;
        set { if (Set(ref _password, value)) OnPropertiesChanged(nameof(PasswordPlaceholder), nameof(PasswordPlaceholderColor)); }
    }

    public bool RememberPassword
    {
        get => _rememberPassword;
        set { if (Set(ref _rememberPassword, value)) OnPropertiesChanged(nameof(PasswordPlaceholder), nameof(PasswordPlaceholderColor)); }
    }

    // A remembered password that has not been fetched yet (see LoadPasswordOnSelect) is shown as
    // dots in the empty box, so the player can see one is on file without the keychain being asked.
    private bool RememberedPasswordUnfetched
        => !LoadPasswordOnSelect && _rememberPassword && _selectedProfile is { RememberPassword: true }
           && string.IsNullOrEmpty(_password) && !_passwordFetchAttempted;

    public string PasswordPlaceholder
        => RememberedPasswordUnfetched ? string.Concat(Enumerable.Repeat(Glyph.Bullet, 10)) : "blank: ask when I connect";

    public Color PasswordPlaceholderColor
        => RememberedPasswordUnfetched ? Color.FromArgb("#cccccc") : Color.FromArgb("#444444");
    /// <summary>"MUD Login": the client logs in with the account and password. Off, those fields are
    /// disabled and not sent, and the player logs in by hand.</summary>
    public bool TelnetLoginEnabled
    {
        get => _telnetLoginEnabled;
        set { if (Set(ref _telnetLoginEnabled, value)) OnPropertiesChanged(nameof(LoginFieldsOpacity), nameof(IsGuestLogin), nameof(PersonaNameEnabled)); }
    }

    /// <summary>True when the form is a guest login: no account sent, and the guest Unix login.</summary>
    public bool IsGuestLogin => !_telnetLoginEnabled && IsGuestNameOnForm;

    private bool IsGuestNameOnForm
        => string.Equals(_telnetLoginName.Trim(), Profile.GuestLoginName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The persona row is dimmed with no account id: the fields set their own colours, so being
    /// disabled does not show on its own.</summary>
    public double LoginFieldsOpacity => _telnetLoginEnabled ? 1.0 : 0.4;
    /// <summary>The shell login (and SSH user). Not on the page: read from and written to the
    /// profile's <c>loginname</c> key in mucka.ini, so it can be changed there.</summary>
    public string TelnetLoginName
    {
        get => _telnetLoginName;
        set { if (Set(ref _telnetLoginName, value)) OnPropertyChanged(nameof(IsGuestLogin)); }
    }
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
    // Invariant, not the reader's culture: this text goes back into the entry and is parsed out of
    // it again, so the two ends have to agree on one format. See OnMaxColumnsEntryCompleted.
    public string MaxColumnsText =>
        _maxColumns == 0 ? string.Empty : _maxColumns.ToString(CultureInfo.InvariantCulture);
    /// <summary>This page's width, set by the page as it lays out. The game page has the same width,
    /// so the terminal's columns are worked out from it before connecting.</summary>
    public double PageWidthDp { get; set; }

    public int AntiIdleSeconds { get => _antiIdleSeconds; set => Set(ref _antiIdleSeconds, Math.Clamp(value, 0, 3600)); }
    public bool KeepScreenOn { get => _keepScreenOn; set => Set(ref _keepScreenOn, value); }

    /// <summary>Advanced feature: automate the MUD Shell (login through persona select/create)
    /// instead of requiring the player to drive it by hand after connecting.</summary>
    public bool GuidedLogin
    {
        get => _guidedLogin;
        set { if (Set(ref _guidedLogin, value)) OnPropertiesChanged(nameof(PersonaManual), nameof(PersonaNameEnabled)); }
    }

    /// <summary>"Manual": the player picks the persona in the game's own menu; off, Mucka does it.</summary>
    public bool PersonaManual { get => !_guidedLogin; set => GuidedLogin = !value; }

    /// <summary>The persona name only means something when Mucka logs in and picks the persona.</summary>
    public bool PersonaNameEnabled => _telnetLoginEnabled && _guidedLogin;
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

    public string AdvancedChevron => (AdvancedVisible ? Glyph.ChevronDown : Glyph.ChevronRight) + "  Advanced";
    public bool CanConnect => !_isConnecting && Volatile.Read(ref _profileLoadsInProgress) == 0;
    public bool IsDirectConnectMode => _cmdArgs.Error == null && _cmdArgs.HasDirectConnectOptions;
    public Task LoadProfilesTask => _loadProfilesTask;

    public ObservableCollection<Profile> SavedProfiles { get; } = new();

    public ICommand ConnectCommand { get; }
    public ICommand ToggleAdvancedCommand { get; }
    public ICommand ShowTelnetHelpCommand { get; }
    public ICommand ShowConnectionHelpCommand { get; }
    public ICommand ShowLoginNameHelpCommand { get; }

    private static async Task ShowHelpAsync(string title, string body)
    {
        var page = Application.Current?.Windows is [var window, ..] ? window.Page : null;
        if (page != null)
            await page.DisplayAlertAsync(title, body, "OK");
    }
    public ICommand DeleteProfileCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand RenameProfileCommand { get; }
    /// <summary>The transport choice: "ssh" or "telnet".</summary>
    public ICommand SetTransportCommand { get; }
    public ICommand DismissGuestIntroCommand { get; }
    public ICommand ToggleGuestPresetsCommand { get; }

    /// <summary>The form is a profile that has not been saved yet (from Quick Start): its name is
    /// typed, where a saved profile's is changed with Rename.</summary>
    public bool IsNewProfile
    {
        get => _isNewProfile;
        private set
        {
            if (Set(ref _isNewProfile, value))
                OnPropertiesChanged(nameof(IsExistingProfile), nameof(EditTitle), nameof(ShowEditorPane), nameof(ShowWelcome));
        }
    }
    private bool _isNewProfile;

    public bool IsExistingProfile => !_isNewProfile;
    public string EditTitle => _isNewProfile ? "New profile" : "Edit profile";

    /// <summary>Raised when the editor has nothing more to do: a save went through or the profile
    /// was deleted. A sheet showing the editor closes on it.</summary>
    public event Action? EditFinished;

    /// <summary>Raised when a row's Edit is pressed or a Quick Start fills the form.</summary>
    public event Action? EditRequested;

    // -- Guest access: shown once, on the first run that has it ---------------------------------
    private const string GuestIntroKey = "guestintroseen";
    private bool _guestIntroVisible;
    private bool _guestPresetsVisible;

    /// <summary>The one-time note about guest access on the profile list.</summary>
    public bool GuestIntroVisible
    {
        get => _guestIntroVisible;
        private set { if (Set(ref _guestIntroVisible, value)) OnPropertyChanged(nameof(ShowGuestIntro)); }
    }

    /// <summary>The guest note shows above profiles; with none, the welcome offers guest access itself.</summary>
    public bool ShowGuestIntro => _guestIntroVisible && HasProfiles;

    /// <summary>The editor has something to edit: a saved profile, or a new one being filled in.</summary>
    public bool ShowEditorPane => HasProfiles || _isNewProfile;

    /// <summary>No profiles and none being made: the welcome, with the Quick Start servers.</summary>
    public bool ShowWelcome => !ShowEditorPane;

    /// <summary>Leaves a profile that was never saved: the form goes back to the first saved one.</summary>
    public void CancelNewProfile()
    {
        if (!IsNewProfile) return;
        IsNewProfile = false;
        HasError = false;
        StatusText = string.Empty;
        if (SavedProfiles.Count > 0)
            ApplyProfile(SavedProfiles[0]);
    }

    /// <summary>Guest presets in Quick Start are folded away until asked for.</summary>
    public bool GuestPresetsVisible
    {
        get => _guestPresetsVisible;
        private set { if (Set(ref _guestPresetsVisible, value)) OnPropertyChanged(nameof(GuestPresetsToggleText)); }
    }

    /// <summary>The Quick Start presets. Key is what <see cref="BeginNewProfile"/> takes.</summary>
    public sealed record QuickStart(string Key, string Name, string Host, int Port, bool Ssh, bool Guest);

    public static readonly IReadOnlyList<QuickStart> QuickStarts =
    [
        new("uk", "MUD2 UK", "mudii.co.uk", 22, Ssh: true, Guest: false),
        new("ca", "MUD2 CA", "mud2.com", 27723, Ssh: false, Guest: false),
        new("guest-uk", "Try MUD2 UK", "mudii.co.uk", 23, Ssh: false, Guest: true),
        new("guest-ca", "Try MUD2 CA", "mud2.com", 27723, Ssh: false, Guest: true),
        new("blank", "New profile", "", 23, Ssh: false, Guest: false),
    ];

    /// <summary>Fills the form from a Quick Start preset as an unsaved profile.</summary>
    public void BeginNewProfile(string key)
    {
        if (IsConnecting) return;
        var q = QuickStarts.FirstOrDefault(x => x.Key == key) ?? QuickStarts[^1];
        var name = UnusedProfileName(q.Name);

        _selectedProfile = null;
        OnPropertyChanged(nameof(SelectedProfile));
        IsNewProfile = true;
        FillFromQuickStart(q, name);
        EditRequested?.Invoke();
    }

    /// <summary>A guest preset connects at once, as a profile that is never saved: it is not in the
    /// list, not in mucka.ini, and the form goes back to what it showed before.</summary>
    public async Task ConnectAsGuestAsync(string key)
    {
        if (IsConnecting || QuickStarts.FirstOrDefault(x => x.Key == key && x.Guest) is not { } q) return;
        var before = _selectedProfile;
        FillFromQuickStart(q, q.Name);
        _guestSession = true;
        try
        {
            await ConnectAsync();
        }
        finally
        {
            _guestSession = false;
            if (before is not null && SavedProfiles.Contains(before)) ApplyProfile(before);
            else if (SavedProfiles.Count > 0) ApplyProfile(SavedProfiles[0]);
        }
    }

    // Set while ConnectAsGuestAsync's connection is made: nothing about it is saved.
    private bool _guestSession;

    private void FillFromQuickStart(QuickStart q, string name)
    {
        _passwordFetchAttempted = false;
        _passwordFetchFailed = false;
        Password = string.Empty;
        ProfileName = name;
        Host = q.Host;
        _useSsh = q.Ssh && SshAvailable;
        _portBeforeSsh = 0;
        OnPropertyChanged(nameof(UseSsh));
        // Without SSH (Android) an SSH preset connects with telnet, on telnet's port.
        Port = q.Ssh && !SshAvailable ? TelnetPort : q.Port;
        AccountId = string.Empty;
        // The setter only acts on a change, and the form may already hold the same id.
        TelnetLoginEnabled = !string.IsNullOrWhiteSpace(AccountId);
        RememberPassword = false;
        TelnetLoginName = q.Guest ? Profile.GuestLoginName : "mud";
        MaxColumns = 0;
        GuidedLogin = true;
        GuidedLoginPersona = string.Empty;
        HasError = false;
        StatusText = string.Empty;
    }

    private string UnusedProfileName(string wanted)
    {
        var name = wanted;
        for (var n = 2; SavedProfiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
            name = $"{wanted} {n}";
        return name;
    }

    /// <summary>Row Duplicate: a copy of the profile as a new, unsaved one in the editor. The password
    /// is not copied; the copy asks for it, or keeps one typed in before Save.</summary>
    public void BeginDuplicate(Profile profile)
    {
        if (IsConnecting) return;
        ApplyProfile(profile);
        _selectedProfile = null;
        OnPropertyChanged(nameof(SelectedProfile));
        IsNewProfile = true;
        ProfileName = UnusedProfileName($"{profile.Name} copy");
        RememberPassword = false;
        HasError = false;
        StatusText = string.Empty;
        EditRequested?.Invoke();
    }

    /// <summary>Row Rename and Delete act on the highlighted profile, so the row is highlighted first.</summary>
    public void Highlight(Profile profile)
    {
        if (IsConnecting) return;
        SelectedProfile = profile;
        SelectProfile(profile);
    }

    /// <summary>Row Edit: the profile into the form, then ask for the editor.</summary>
    public void BeginEdit(Profile profile)
    {
        if (IsConnecting) return;
        SelectedProfile = profile;
        // Selecting the profile already in the form is a no-op for the property, so fill it again:
        // an abandoned edit must not reappear.
        SelectProfile(profile);
        HasError = false;
        StatusText = string.Empty;
        EditRequested?.Invoke();
    }

    // -- The connection: Secure (SSH), Oldschool (Telnet), or a port number ---------------------
    public const string ConnectionSsh = "ssh";
    public const string ConnectionTelnet = "telnet";

    /// <summary>What the connection picker shows: SSH or Telnet on their usual ports, otherwise the
    /// port number that was typed.</summary>
    public string ConnectionLabel
    {
        get
        {
            var port = Port;
            if (UseSsh && SshAvailable)
                return port is 0 or SshPort ? "SSH" : $"SSH {port}";
            return port is 0 or TelnetPort ? "Telnet" : port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Sets the connection from the picker: <see cref="ConnectionSsh"/>,
    /// <see cref="ConnectionTelnet"/>, or a typed port, which connects with telnet.</summary>
    public void SetConnection(string kind, int port = 0)
    {
        if (kind == ConnectionSsh && SshAvailable)
        {
            UseSsh = true;
            Port = SshPort;
        }
        else
        {
            UseSsh = false;
            Port = kind == ConnectionTelnet || port <= 0 ? TelnetPort : port;
        }
    }

    /// <summary>The profile page's layout: the list beside the editor, or the list alone. Set by the page
    /// from its width; the parts that belong to only one layout bind to the flags below.</summary>
    public bool IsWideLayout
    {
        get => _isWideLayout;
        set { if (Set(ref _isWideLayout, value)) RaiseLayoutFlags(); }
    }
    private bool _isWideLayout;

    public bool ShowNarrowGuestIntro => ShowGuestIntro && !_isWideLayout;
    public bool ShowNarrowWelcome => ShowWelcome && !_isWideLayout;
    public bool ShowWideEmptyNote => HasNoProfiles && _isWideLayout;

    private void RaiseLayoutFlags()
        => OnPropertiesChanged(nameof(ShowNarrowGuestIntro), nameof(ShowNarrowWelcome), nameof(ShowWideEmptyNote));

    /// <summary>The profile page's score history button: for the highlighted profile's server.</summary>
    public string ScoreHistoryText
        => Glyph.ScoreChart + "  " + (_selectedProfile is { } p ? $"Score history for {p.Name}" : "Score history");

    /// <summary>The server whose score history that button opens; empty with no profile highlighted.</summary>
    public string ScoreHistoryHost => _selectedProfile?.Host.Trim() ?? string.Empty;

    /// <summary>No profiles at all: the first run, or every profile deleted.</summary>
    public bool HasNoProfiles => SavedProfiles.Count == 0;
    public bool HasProfiles => SavedProfiles.Count > 0;

    /// <summary>The settings editor's starting point when it is opened from this page: what
    /// mucka.ini holds, for the highlighted profile's F-keys (the global set when there is none).</summary>
    public async Task<(ClientSettings Settings, string[] Fkeys, string ProfileName)> LoadSettingsForEditorAsync()
    {
        var name = _selectedProfile?.Name ?? string.Empty;
        var p = new Profile { Name = name };
        (await SettingsStore.LoadProfileAsync(name).ConfigureAwait(true))?.ApplyTo(p);
        return (p.ToClientSettings(UiFontScaleState.Current.Step), p.Fkeys, name);
    }

    public void SetGuestPresetsVisible(bool visible) => GuestPresetsVisible = visible;

    public string GuestPresetsToggleText
        => (_guestPresetsVisible ? Glyph.ChevronDown : Glyph.ChevronRight) + "  Just looking? Try it as a guest";

    private async Task LoadGuestIntroAsync()
    {
        if (IsDirectConnectMode) return;
        try
        {
            if (await SettingsStore.GetGlobalValueAsync(GuestIntroKey) is not null) return;
            GuestIntroVisible = true;
            await SettingsStore.SetGlobalFlagAsync(GuestIntroKey, true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConnectViewModel] guest intro flag failed: {ex}");
        }
    }

    /// <summary>"Save", or "Saved" for a moment after a save.</summary>
    public string SaveButtonText { get => _saveButtonText; private set => Set(ref _saveButtonText, value); }
    private string _saveButtonText = SaveLabel;
    private const string SaveLabel = "Save";

    public Func<PasswordPromptArgs, Task<PasswordResult?>>? PasswordRequired { get; set; }

    public event Action<MuckaConnection, Profile>? Connected;

    public ConnectViewModel()
    {
        ConnectCommand = new AsyncCommand(ConnectAsync);
        ToggleAdvancedCommand = new Command(() => AdvancedVisible = !AdvancedVisible);
        DeleteProfileCommand = new AsyncCommand(DeleteProfileAsync);
        SaveProfileCommand = new AsyncCommand(SaveProfileAsync);
        RenameProfileCommand = new AsyncCommand(RenameProfileAsync);
        SetTransportCommand = new Command<string>(t => UseSsh = t == "ssh" && SshAvailable);
        DismissGuestIntroCommand = new Command(() => GuestIntroVisible = false);
        ToggleGuestPresetsCommand = new Command(() => GuestPresetsVisible = !GuestPresetsVisible);
        _ = LoadGuestIntroAsync();
        SavedProfiles.CollectionChanged += (_, _) => OnPropertiesChanged(nameof(HasNoProfiles), nameof(HasProfiles),
            nameof(ShowGuestIntro), nameof(ShowEditorPane), nameof(ShowWelcome));
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Port) or nameof(UseSsh))
                OnPropertyChanged(nameof(ConnectionLabel));
            else if (e.PropertyName is nameof(SelectedProfile))
                OnPropertiesChanged(nameof(ScoreHistoryText), nameof(ScoreHistoryHost));
            else if (e.PropertyName is nameof(ShowGuestIntro) or nameof(ShowWelcome) or nameof(HasNoProfiles))
                RaiseLayoutFlags();
        };
        ShowTelnetHelpCommand = new Command(() => _ = ShowHelpAsync("Logging in",
            "MUD account ID: with it, Mucka answers the game's login prompts for you. Leave it blank "
            + "to type your login yourself when you connect.\n\n"
            + "Password: kept on this device so you are not asked again. Leave it blank to be asked each time you connect.\n\n"
            + "Persona: the persona Mucka logs into for you. Leave it blank to pick from your personas when you connect, "
            + "or tick Manual to choose it yourself in the game's own menu."));
        ShowConnectionHelpCommand = new Command(() => _ = ShowHelpAsync("Connection",
            "Secure (SSH): the connection is encrypted. The UK server offers it, on port 22.\n\n"
            + "Oldschool (Telnet): the classic connection, unencrypted, on port 23.\n\n"
            + "Manual: a port number of your own. Mucka connects to it with telnet. The CA server uses 27723."));
        ShowLoginNameHelpCommand = new Command(() => _ = ShowHelpAsync("Server login name",
            "Before the game itself, the server asks for a login name. \"mud\" leads to the account "
            + "prompt and \"mudguest\" is the guest login; over a secure connection it is the SSH user.\n\n"
            + "Leave it as it is unless the people who run your server tell you otherwise. "
            + "With an account login, Mucka never uses the guest name."));
        _loadProfilesTask = LoadProfilesAsync();
    }

    private async Task ConnectAsync()
    {
        HasError = false;
        StatusText = string.Empty;
        ProgressText = $"Connecting to {Host.Trim()}...";
        IsConnecting = true;
        OnPropertyChanged(nameof(CanConnect));
        try
        {
            var accountId = AccountId.Trim();
            var loginName = TelnetLoginName.Trim();
            var useSsh = UseSsh && SshAvailable;
            var port = Port > 0 ? Port : useSsh ? SshPort : TelnetPort;

            if (!LoadPasswordOnSelect && RememberPassword && accountId.Length > 0 && string.IsNullOrEmpty(Password)
                && SavedProfiles.FirstOrDefault(p => string.Equals(p.Name, ProfileName, StringComparison.OrdinalIgnoreCase))
                    is { RememberPassword: true } remembered)
            {
                await LoadProfilePasswordAsync(remembered);
                _passwordFetchAttempted = true;
                OnPropertiesChanged(nameof(PasswordPlaceholder), nameof(PasswordPlaceholderColor));
            }

            string resolvedPassword = Password;
            if (TelnetLoginEnabled && !string.IsNullOrEmpty(accountId) && string.IsNullOrEmpty(resolvedPassword))
            {
                if (PasswordRequired == null)
                {
                    StatusText = "Password required. Please enter your password.";
                    HasError = true;
                    return;
                }

                var result = await PasswordRequired(new PasswordPromptArgs(ProfileName, Host.Trim(), port, accountId));
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
            // The server wraps the login at the width it is told on connecting, before the game page
            // exists to measure; this page is as wide as that one, so the count is worked out here.
            // Auto used to reach the connection as 0 and be clamped to 20.
            var fontPx = SavedProfiles.FirstOrDefault(p =>
                    string.Equals(p.Name, ProfileName, StringComparison.OrdinalIgnoreCase)) is { } font
                ? (font.FontSize > 0 ? font.FontSize : GameViewModel.DefaultFontSizePx)
                : GameViewModel.DefaultFontSizePx;
#if MACCATALYST
            // The game page's terminal sits beside the side panel, so the page's width overstates
            // what the terminal gets by the panel and the gutter. Observed on the Mac with auto
            // columns: told ~150, the terminal wrapped the server's already-wrapped lines again.
            var terminalWidthDp = PageWidthDp
                                  - Mucka.Combat.CombatRailResize.SidePanelWidthAt(
                                      Mucka.Ui.UiFontScale.PanelScale(UiFontScaleState.Current.Step))
                                  - Mucka.Combat.CombatRailResize.TerminalGutterDp;
#else
            var terminalWidthDp = PageWidthDp;
#endif
            var displayableCols = Mucka.Terminal.TerminalColumns.Displayable(terminalWidthDp, fontPx);
#if MACCATALYST
            // The game window is sized to a fixed count's width, so it is not limited by this page.
            if (MaxColumns > 0) displayableCols = Math.Max(displayableCols, MaxColumns);
#endif
            var windowCols = Mucka.Terminal.TerminalColumns.Effective(MaxColumns, displayableCols);
            // The profile keeps its stored name; what the shell is shown depends on the login kind.
            var shellLogin = Mucka.Commands.ShellLogin.For(loginName, TelnetLoginEnabled && accountId.Length > 0);
            var conn = await Task.Run(() => new MuckaConnection(
                autoLogin ? accountId : null,
                autoLogin ? resolvedPassword : null,
                windowCols,
                shellLogin,
                Host.Trim(),
                DreamwordCarry)).ConfigureAwait(true);

            await conn.ConnectAsync(Host.Trim(), port, useSsh);

            var profile = await BuildFormProfileAsync(port, useSsh, accountId, loginName);
            if (!IsDirectConnectMode && !_guestSession)
            {
                await SaveCurrentProfileAsync(profile);
            }

            Connected?.Invoke(conn, profile);

            // After the session has been handed over, not before: a keychain that stops to ask the
            // player's permission would otherwise hold the whole login while the server waits.
            // Not with an empty password that was never fetched (denied, locked, or not yet asked
            // for): saving "nothing" would delete a stored password. A box the player cleared, with
            // the fetch having succeeded, does clear it.
            var storedPasswordUnread = RememberPassword && string.IsNullOrEmpty(resolvedPassword)
                && (_passwordFetchFailed || RememberedPasswordUnfetched);
            if (!IsDirectConnectMode && !_guestSession && !storedPasswordUnread)
                _ = SavePasswordAsync(profile.Name, RememberPassword ? resolvedPassword : null);
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

    /// <summary>The profile this page's form describes: its own fields, every setting carried over
    /// from the saved profile of the same name, and mucka.ini laid over both. What Connect starts a
    /// session with and what Save writes.</summary>
    private async Task<Profile> BuildFormProfileAsync(int port, bool useSsh, string accountId, string loginName)
    {
        // Carry the persisted settings (fkeys, font, volume, ...) over from the saved
        // profile - they are not editable on this page but must not reset on connect.
        var saved = SavedProfiles.FirstOrDefault(p =>
            string.Equals(p.Name, ProfileName, StringComparison.OrdinalIgnoreCase));

        var profile = new Profile
        {
            Name = ProfileName,
            Host = Host.Trim(),
            Port = port,
            UseSsh = useSsh,
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
            FkeysPerProfile = saved?.FkeysPerProfile ?? false,
            Fkeys = saved?.Fkeys ?? new string[36],
            // `saved` is already hydrated from mucka.ini by LoadProfilesAsync's ApplyTo overlay,
            // but this fresh Profile does not inherit from it automatically - every settings
            // field has to be carried across explicitly, or it silently reverts to its C#
            // default here and the ini sync below (SaveCurrentProfileAsync) writes that default
            // back to disk. Any new settings field must be added HERE as well as to
            // ClientSettings, Profile and SettingsStore.
            Sounds = saved?.Sounds ?? new SoundSettings(),
            // The always-global Display block and the Combat Rail's two global flags. Carried across for exactly the reason above, and
            // each fallback is the matching Profile default - a wrong fallback here is
            // indistinguishable from the field being missing altogether. What this failing
            // looks like in the hands: a value saved in [settings] is read into `saved` at
            // startup, dropped here, and the live default is what the session runs on, so the
            // ini keeps the player's answer and the client keeps ignoring it.
            DefaultMaxColumns = saved?.DefaultMaxColumns ?? 0,
            DreamwordSizeOffset = saved?.DreamwordSizeOffset ?? 0,
            EmoteColor = saved?.EmoteColor ?? MudSharp.Models.ChatTheme.Default.EmoteHex,
            SayColor = saved?.SayColor ?? MudSharp.Models.ChatTheme.Default.SayHex,
            ShoutColor = saved?.ShoutColor ?? MudSharp.Models.ChatTheme.Default.ShoutHex,
            TellColor = saved?.TellColor ?? MudSharp.Models.ChatTheme.Default.TellHex,
            ChatTheme = saved?.ChatTheme ?? MudSharp.Models.ChatTheme.Default,
            ChatFaces = saved?.ChatFaces ?? MudSharp.Models.ChatFaces.Default,
            ShowOnline = saved?.ShowOnline ?? true,
            ShowInventory = saved?.ShowInventory ?? true,
            ShowItemsHere = saved?.ShowItemsHere ?? true,
            ShowMapCompass = saved?.ShowMapCompass ?? true,
            MaxOnlineDisplay = saved?.MaxOnlineDisplay ?? 0,
            OnlineNamesOnly = saved?.OnlineNamesOnly ?? false,
            OnlineForgetWindow = saved?.OnlineForgetWindow ?? 5,
            FloatOnline = saved?.FloatOnline ?? false,
            FloatCompass = saved?.FloatCompass ?? false,
            ShowCombatRail = saved?.ShowCombatRail ?? false,
            ShowCombatStats = saved?.ShowCombatStats ?? true,
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
        return profile;
    }

    private void SelectProfile(Profile p)
    {
        if (IsConnecting)
        {
            return;
        }

        ApplyProfile(p);
        if (!LoadPasswordOnSelect) return;
        var _ = LoadProfilePasswordAsync(p).ContinueWith(
            t => System.Diagnostics.Debug.WriteLine($"[SelectProfile] password load failed: {t.Exception}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private int ApplyProfile(Profile p)
    {
        _selectedProfile = p;
        OnPropertyChanged(nameof(SelectedProfile));
        IsNewProfile = false;
        _passwordFetchAttempted = false;
        _passwordFetchFailed = false;
        Password = string.Empty;
        ProfileName = p.Name;
        Host = p.Host;
        Port = p.Port;
        _useSsh = p.UseSsh && SshAvailable;
        _portBeforeSsh = 0;
        OnPropertyChanged(nameof(UseSsh));
        AccountId = p.AccountId;
        // The setter only acts on a change, and the form may already hold the same id.
        TelnetLoginEnabled = !string.IsNullOrWhiteSpace(AccountId);
        RememberPassword = p.RememberPassword;
        TelnetLoginName = string.IsNullOrEmpty(p.TelnetLoginName) ? "mud" : p.TelnetLoginName;
        // Connecting always sizes the terminal to the window: the page no longer offers a width.
        MaxColumns = 0;
        AntiIdleSeconds = p.AntiIdleSeconds;
        KeepScreenOn = p.KeepScreenOn;
        GuidedLogin = p.GuidedLogin;
        GuidedLoginPersona = p.GuidedLoginPersona;
        return Interlocked.Increment(ref _profileSelectionVersion);
    }

    private async Task SelectProfileAsync(Profile p, bool loadPassword)
    {
        ApplyProfile(p);
        if (loadPassword && LoadPasswordOnSelect)
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
        // The keychain read below can take a moment; say what is happening before it, not after.
        ProgressText = $"Logging in to {profile.Name}...";
        IsConnecting = true;
        try { await LoadProfilePasswordAsync(profile); }
        finally { IsConnecting = false; }
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
                    _passwordFetchFailed = true;
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

    /// <summary>Writes the form to its profile without connecting.</summary>
    private async Task SaveProfileAsync()
    {
        if (IsConnecting || IsDirectConnectMode) return;
        var name = ProfileName.Trim();
        if (name.Length == 0)
        {
            StatusText = "Give the profile a name to save it.";
            HasError = true;
            return;
        }
        ProfileName = name;
        HasError = false;
        StatusText = string.Empty;
        if (IsNewProfile && SavedProfiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = $"There is already a profile called \"{name}\".";
            HasError = true;
            return;
        }
        try
        {
            var useSsh = UseSsh && SshAvailable;
            var port = Port > 0 ? Port : useSsh ? SshPort : TelnetPort;
            // The editor has no Remember box: a password typed in is kept, an emptied box forgets it.
            // A remembered one that has not been read yet is not an empty box.
            RememberPassword = !string.IsNullOrEmpty(Password) || RememberedPasswordUnfetched;
            var profile = await BuildFormProfileAsync(port, useSsh, AccountId.Trim(), TelnetLoginName.Trim());
            await SaveCurrentProfileAsync(profile);
            HighlightProfileRow(profile.Name);
            IsNewProfile = false;
            // As on connect: an empty box over a stored password that was never fetched is not a
            // request to delete it.
            var storedPasswordUnread = RememberPassword && string.IsNullOrEmpty(Password)
                && (_passwordFetchFailed || RememberedPasswordUnfetched);
            if (!storedPasswordUnread)
                await SavePasswordAsync(profile.Name, RememberPassword ? Password : null);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not save: {ex.Message}";
            HasError = true;
            return;
        }
        SaveButtonText = "Saved";
        EditFinished?.Invoke();
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        SaveButtonText = SaveLabel;
    }

    /// <summary>Renames the highlighted profile: its list entry, its sections in mucka.ini and its
    /// stored password all move to the new name.</summary>
    private async Task RenameProfileAsync()
    {
        if (IsConnecting || IsDirectConnectMode || _selectedProfile is not { } profile) return;
        var page = Application.Current?.Windows is [var window, ..] ? window.Page : null;
        if (page == null) return;

        var oldName = profile.Name;
        var input = await page.DisplayPromptAsync(
            "Rename Profile",
            $"New name for \"{oldName}\":",
            accept: "Rename",
            cancel: "Cancel",
            keyboard: Keyboard.Plain,
            initialValue: oldName);
        var newName = input?.Trim();
        if (IsConnecting || string.IsNullOrEmpty(newName) || newName == oldName) return;

        var taken = SavedProfiles.Any(p => !ReferenceEquals(p, profile)
            && string.Equals(p.Name, newName, StringComparison.OrdinalIgnoreCase));
        string? password = null;
        if (!taken)
        {
            // Read before the sections move: on a Mac the password can be in the profile's own section.
            try { password = await ProfileStore.GetPasswordAsync(oldName); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConnectViewModel] password read for rename failed for '{oldName}': {ex}");
            }
        }
        if (taken || !await SettingsStore.RenameProfileAsync(oldName, newName))
        {
            await page.DisplayAlertAsync("Rename Profile", $"There is already a profile called \"{newName}\".", "OK");
            return;
        }

        profile.Name = newName;
        if (!string.IsNullOrEmpty(password))
        {
            // The password is a nicety to carry over; failing to must not undo the rename.
            try
            {
                await ProfileStore.SetPasswordAsync(newName, password);
                await ProfileStore.SetPasswordAsync(oldName, null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConnectViewModel] password move failed for '{oldName}': {ex}");
            }
        }
        ProfileName = newName;
        HighlightProfileRow(newName);
    }

    // The highlight follows the profile that was just saved or renamed.
    private void HighlightProfileRow(string name)
    {
        var p = SavedProfiles.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (p is null) return;
        _selectedProfile = p;
        OnPropertyChanged(nameof(SelectedProfile));
    }

    private async Task DeleteProfileAsync()
    {
        if (IsConnecting)
        {
            return;
        }

        var page = Application.Current?.Windows is [var window, ..] ? window.Page : null;
        if (page == null) return;

        var name = ProfileName;
        if (string.IsNullOrWhiteSpace(name)) return;

        var input = await page.DisplayPromptAsync(
            "Delete Profile",
            $"Type \"{name}\" to confirm deletion.",
            accept: "Delete",
            cancel: "Cancel",
            placeholder: name,
            keyboard: Keyboard.Plain,
            initialValue: string.Empty);

        if (IsConnecting || input == null || input != name) return;

        var existing = SavedProfiles.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing == null) return;

        SavedProfiles.Remove(existing);
        // The password is a nicety to clear; failing to must not stop the profile being deleted.
        try { await ProfileStore.SetPasswordAsync(name, null); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConnectViewModel] password clear failed for '{name}': {ex}");
        }
        // The profile's settings:/fkeys: ini sections are deliberately left behind -
        // SaveProfilesAsync only removes the [profile:] section and the order entry.

        if (SavedProfiles.Count == 0)
        {
            _selectedProfile = null;
            OnPropertyChanged(nameof(SelectedProfile));
        }
        else
        {
            ApplyProfile(SavedProfiles[0]);
        }

        await SettingsStore.SaveProfilesAsync(SavedProfiles.ToList());
        EditFinished?.Invoke();
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

    /// <summary>The Game Font and chat colour theme chosen on the Text size page: global, so every
    /// cached profile takes them, and the next connection starts with them. A null theme leaves the
    /// colours alone.</summary>
    public void ApplyFontSetup(int px, MudSharp.Models.ChatTheme? theme)
    {
        foreach (var p in SavedProfiles)
        {
            p.FontSize = px;
            if (theme is null) continue;
            p.ChatTheme = theme;
            p.EmoteColor = theme.EmoteHex;
            p.SayColor = theme.SayHex;
            p.ShoutColor = theme.ShoutHex;
            p.TellColor = theme.TellHex;
        }
    }

    /// <summary>
    /// Saves a profile's settings and fkeys to mucka.ini and mirrors them onto the
    /// in-memory profile so a reconnect in the same run sees the new values.
    /// Used by GameViewModel's settings dialog via the saver delegate.
    /// </summary>
    public async Task SaveProfileSettingsAsync(string profileName, ClientSettings settings, string[] fkeys)
    {
        // Every setting but the fkeys is global, so every cached profile takes the new values.
        foreach (var p in SavedProfiles)
        {
            p.FontSize            = settings.FontSize;
            p.MaxColumns          = settings.MaxColumns;
            p.Volume              = settings.Volume;
            p.StatUpdateFrequency = settings.StatUpdateFrequency;
            p.MuteBeepPermanently = settings.MuteBeepPermanently;
            p.LogResetDiagnostics = settings.LogResetDiagnostics;
            p.Sounds              = settings.Sounds;
        }
        var existing = SavedProfiles.FirstOrDefault(p =>
            string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.FkeysPerProfile = settings.FkeysPerProfile;
            existing.Fkeys           = fkeys;
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
    /// not promote whatever the Onlines section happens to be folded to this session into the shared
    /// global default. A single-key write cannot reach any of that.</para>
    /// </summary>
    public static Task PersistCombatRailVisibilityAsync(bool showCombatRail)
        => SettingsStore.SetGlobalFlagAsync("showcombatrail", showCombatRail);

    /// <summary>Persists ONLY the Combat Rail's stat rows, from the same kind of live overflow-menu
    /// toggle and for the same reasons as <see cref="PersistCombatRailVisibilityAsync"/> above.</summary>
    public static Task PersistCombatStatsAsync(bool showCombatStats)
        => SettingsStore.SetGlobalFlagAsync("showcombatstats", showCombatStats);

    /// <summary>Stores the remembered password (or clears it). A store that cannot keep it must not
    /// turn a successful login into a failed one, so the profile is saved again as not remembering.</summary>
    private async Task SavePasswordAsync(string profileName, string? password)
    {
        try { await ProfileStore.SetPasswordAsync(profileName, password); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConnectViewModel] password save failed for '{profileName}': {ex}");
            var saved = SavedProfiles.FirstOrDefault(p =>
                string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
            if (saved is null) return;
            saved.RememberPassword = false;
            try { await SettingsStore.SaveProfilesAsync(SavedProfiles.ToList()); }
            catch (Exception saveEx) { CrashLog.Write("SavePassword", saveEx); }
        }
    }

    private async Task SaveCurrentProfileAsync(Profile incoming)
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
            existing.UseSsh = incoming.UseSsh;
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

        // Columns, anti-idle and keep-screen-on are global: the other cached profiles show the same
        // values the next time they are selected.
        foreach (var p in SavedProfiles)
        {
            p.MaxColumns      = incoming.MaxColumns;
            p.AntiIdleSeconds = incoming.AntiIdleSeconds;
            p.KeepScreenOn    = incoming.KeepScreenOn;
        }

        await SettingsStore.SaveProfilesAsync(SavedProfiles.ToList());
        await SettingsStore.SaveConnectGlobalsAsync(incoming.MaxColumns, incoming.AntiIdleSeconds, incoming.KeepScreenOn);

        // Keep mucka.ini in sync - it is the authoritative settings store, so a column
        // change made on this page must not be reverted by a stale ini section next launch.
        var settings = new ClientSettings
        {
            FontSize            = incoming.FontSize,
            MaxColumns          = incoming.MaxColumns,
            Volume              = incoming.Volume,
            StatUpdateFrequency = incoming.StatUpdateFrequency,
            MuteBeepPermanently = incoming.MuteBeepPermanently,
            LogResetDiagnostics = incoming.LogResetDiagnostics,
            FkeysPerProfile     = incoming.FkeysPerProfile,
            Sounds              = incoming.Sounds,
            // The Combat Rail's two flags are not here: they are single [settings] keys written only
            // by their own toggles (SettingsStore.SetGlobalFlagAsync).
        };
        // fkeys: null - hotkeys are not editable on this page, so their sections are never rewritten.
        // writeSounds: false - sounds are not editable here either; rewriting them from a profile
        // blob assembled on this page would overwrite the stored per-sound volume overrides.
        // writeDisplayGlobals: false - nothing in that block (UI font step, default columns,
        // dreamword offset, the Show* toggles, online display options, float defaults, chat colours) is
        // editable on this page; this partial ClientSettings leaves each of them at its C# default,
        // so writing the block unconditionally would reset a player's saved globals to those
        // defaults (OnlineForgetWindow -> 0 killing the Recent list,
        // the chat colours -> the built-in colours, etc.).
        await SettingsStore.SaveProfileAsync(incoming.Name, settings, fkeys: null,
            writeSounds: false, writeDisplayGlobals: false);
    }
}
