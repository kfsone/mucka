using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace Mucka.Core;

public class Profile : INotifyPropertyChanged
{
    private string[] _fkeys = CreateEmptyFkeys();
    private string _name = "MUD2";
    private string _host = "mud2.co.uk";

    // The two fields the profile list shows. Without change notification a list cell that iOS reuses
    // for the same profile kept showing the name the profile had when the cell was built.
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); }
    }

    public string Host
    {
        get => _host;
        set { if (_host == value) return; _host = value; Raise(nameof(Host)); }
    }

    public int Port
    {
        get => _port;
        set { if (_port == value) return; _port = value; Raise(nameof(Port)); }
    }

    /// <summary>Connect over SSH instead of telnet. Not on Android; the login name is the SSH user.</summary>
    public bool UseSsh
    {
        get => _useSsh;
        set { if (_useSsh == value) return; _useSsh = value; Raise(nameof(UseSsh), nameof(KindSummary)); }
    }

    /// <summary>The profile list's third line: the connection, then how the login is done - with
    /// the account, as a guest (the <c>mudguest</c> login), or typed by the player.</summary>
    public string KindSummary
    {
        get
        {
            var transport = UseSsh && ViewModels.ConnectViewModel.SshAvailable ? "SSH" : "Telnet";
            var login = IsGuest ? "Guest" : AutoLogin ? "Account" : "Log in yourself";
            return $"{transport}  {Glyph.Bullet}  {login}";
        }
    }

    /// <summary>The guest Unix login on the real servers.</summary>
    public const string GuestLoginName = Mucka.Commands.ShellLogin.Guest;

    /// <summary>True when the client sends no account and the Unix login is the guest one.</summary>
    public bool IsGuest => !AutoLogin
        && string.Equals(TelnetLoginName, GuestLoginName, StringComparison.OrdinalIgnoreCase);

    private int _port = 23;
    private bool _useSsh;
    private bool _telnetLoginEnabled = true;
    private string _telnetLoginName = "mud";

    private void Raise(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new(n));
    }
    public string AccountId
    {
        get => _accountId;
        set { if (_accountId == value) return; _accountId = value; Raise(nameof(AccountId), nameof(KindSummary)); }
    }
    private string _accountId = string.Empty;

    /// <summary>The client answers the account prompts itself: MUD Login on (profiles saved before it
    /// left the page can have it off) and an account id to send.</summary>
    public bool AutoLogin => TelnetLoginEnabled && !string.IsNullOrWhiteSpace(AccountId);
    public bool RememberPassword { get; set; }
    public bool TelnetLoginEnabled
    {
        get => _telnetLoginEnabled;
        set { if (_telnetLoginEnabled == value) return; _telnetLoginEnabled = value; Raise(nameof(TelnetLoginEnabled), nameof(KindSummary)); }
    }

    public string TelnetLoginName
    {
        get => _telnetLoginName;
        set { if (_telnetLoginName == value) return; _telnetLoginName = value; Raise(nameof(TelnetLoginName), nameof(KindSummary)); }
    }
    /// <summary>Automate the MUD Shell (Option menu -> persona selection) after login instead of
    /// leaving the raw shell prompts for the user to type through manually.</summary>
    public bool GuidedLogin { get; set; }
    /// <summary>Persona name to auto-select (or offer to create, if a slot is free) when
    /// GuidedLogin is enabled. Empty = let the user choose interactively during connect.</summary>
    public string GuidedLoginPersona { get; set; } = string.Empty;
    /// <summary>Maximum terminal columns to advertise via NAWS. 0 = auto-size to window (default).</summary>
    public int MaxColumns { get; set; }
    /// <summary>Seconds of player inactivity before sending a blank keep-alive command. 0 = disabled.</summary>
    public int AntiIdleSeconds { get; set; }
    /// <summary>Keep the screen/display awake while connected to the game.
    /// Defaults on for mobile, where an idle screen-lock mid-session gets you swamped.</summary>
    [SuppressMessage("Performance", "CA1805:Do not initialize unnecessarily",
        Justification = "The initialiser is platform-conditional: false only on desktop, true on the mobile TFMs.")]
    public bool KeepScreenOn { get; set; } =
#if ANDROID || IOS
        true;
#else
        false;
#endif
    /// <summary>Fill unpopulated hotkey slots with built-in defaults at session start.</summary>
    public bool DefaultHotkeys { get; set; } = true;
    /// <summary>Terminal font size in pixels. 0 = use the built-in default (15px).</summary>
    public int FontSize { get; set; }
    /// <summary>Sound volume, 0-100. Default 75.</summary>
    public int Volume { get; set; } = 75;
    /// <summary>FES stats-update heartbeat interval in seconds. 0 = disabled. Default 10.</summary>
    public int StatUpdateFrequency { get; set; } = 10;
    /// <summary>Permanently suppress the bell/beep sound. Persisted per profile.</summary>
    public bool MuteBeepPermanently { get; set; }
    /// <summary>Debug diagnostics: log each reset-timer (TTR) reading into the active session
    /// recording. Only takes effect in Debug builds while recording; persisted per profile.</summary>
    public bool LogResetDiagnostics { get; set; }
    /// <summary>Per-sound enablement and group fallbacks. Defaults to everything on.</summary>
    public SoundSettings Sounds { get; set; } = new();
    /// <summary>Whether the Combat Rail (right-edge combat panel) is shown. Global, never per profile
    /// or per persona: one key in <c>[settings]</c>, read on every connect by
    /// <see cref="SettingsStore.LoadProfileAsync"/> and written immediately whenever the player
    /// toggles it (<see cref="SettingsStore.SetGlobalFlagAsync"/>, via
    /// GameViewModel.PersistCombatRailVisibilityAsync) rather than only through the settings
    /// dialog's Save button, since a menu toggle should not require a separate save step to survive a
    /// relog.
    ///
    /// <para>False (hidden) is the default - the panel is additive and does not appear until the
    /// toggle is first touched.</para></summary>
    public bool ShowCombatRail { get; set; }
    /// <summary>Whether the Combat Rail draws its two stat rows and the exchange spark. Off narrows
    /// the panel, which is the point: it is what the width is mostly spent on. Stored and restored
    /// exactly like <see cref="ShowCombatRail"/> above, in <c>[settings]</c>.
    ///
    /// <para>Default TRUE, unlike the rail beside it - the rail is additive and arrives hidden, and
    /// the stat rows are part of it arriving complete once it is switched on.</para></summary>
    public bool ShowCombatStats { get; set; } = true;
    public string[] Fkeys { get => _fkeys; set => _fkeys = NormalizeFkeys(value); }

    /// <summary>The settings this profile carries, as the settings editor takes them: all of them
    /// global but the F-key choice.</summary>
    public ClientSettings ToClientSettings(int uiFontStep) => new()
    {
        FontSize            = FontSize,
        MaxColumns          = MaxColumns,
        Volume              = Volume,
        StatUpdateFrequency = StatUpdateFrequency,
        MuteBeepPermanently = MuteBeepPermanently,
        LogResetDiagnostics = LogResetDiagnostics,
        FkeysPerProfile     = FkeysPerProfile,
        Sounds              = Sounds.Clone(),
        UiFontStep          = uiFontStep,
        DefaultMaxColumns   = DefaultMaxColumns,
        DreamwordSizeOffset = DreamwordSizeOffset,
        EmoteColor          = EmoteColor,
        SayColor            = SayColor,
        ShoutColor          = ShoutColor,
        TellColor           = TellColor,
        ChatTheme           = ChatTheme,
        ChatFaces           = ChatFaces,
        ShowOnline          = ShowOnline,
        ShowInventory       = ShowInventory,
        ShowItemsHere       = ShowItemsHere,
        ShowMapCompass      = ShowMapCompass,
        MaxOnlineDisplay    = MaxOnlineDisplay,
        OnlineNamesOnly     = OnlineNamesOnly,
        OnlineForgetWindow  = OnlineForgetWindow,
        FloatOnline         = FloatOnline,
        FloatCompass        = FloatCompass,
    };

    /// <summary>True when the fkeys came from a per-profile [fkeys:Name] ini section
    /// rather than the global [fkeys]. Derived from mucka.ini at load; not persisted here.</summary>
    public bool FkeysPerProfile { get; set; }

    // -- Display tab settings (global, loaded from [settings] section at connect time) --
    public int DefaultMaxColumns { get; set; }
    public int DreamwordSizeOffset { get; set; }
    /// <summary>Hex color (6 digits, no '#') for the speaker of every chat line and act/emote text.</summary>
    public string EmoteColor { get; set; } = MudSharp.Models.ChatTheme.Default.EmoteHex;
    /// <summary>Hex color for said words, everyone's.</summary>
    public string SayColor { get; set; } = MudSharp.Models.ChatTheme.Default.SayHex;
    /// <summary>Hex color for shouted words, everyone's.</summary>
    public string ShoutColor { get; set; } = MudSharp.Models.ChatTheme.Default.ShoutHex;
    /// <summary>Hex color for told words, both directions.</summary>
    public string TellColor { get; set; } = MudSharp.Models.ChatTheme.Default.TellHex;
    /// <summary>The chat colour theme the editor shows and resets to.</summary>
    public MudSharp.Models.ChatTheme ChatTheme { get; set; } = MudSharp.Models.ChatTheme.Default;
    /// <summary>The own/other face (italic, bold, dim) of each chat colour row.</summary>
    public MudSharp.Models.ChatFaces ChatFaces { get; set; } = MudSharp.Models.ChatFaces.Default;
    public bool ShowOnline { get; set; } = true;
    public bool ShowInventory { get; set; } = true;
    public bool ShowItemsHere { get; set; } = true;
    public bool ShowMapCompass { get; set; } = true;
    public int MaxOnlineDisplay { get; set; }
    public bool OnlineNamesOnly { get; set; }
    /// <summary>Minutes a player lingers in the "Recent" list after fading off Online before
    /// being forgotten. 0 = disabled (forget immediately). Range 0-10, default 5.</summary>
    public int OnlineForgetWindow { get; set; } = 5;
    /// <summary>Global default for floating (unpinning) the Online list. False = pinned in the side panel.</summary>
    public bool FloatOnline { get; set; }
    /// <summary>Global default for floating (unpinning) the compass. False = pinned in the side panel.</summary>
    public bool FloatCompass { get; set; }

    private static readonly Dictionary<int, string> s_defaultFkeys = new()
    {
        [0] = "qscan",
        [1] = "wield best weap",
        [2] = "open bag,g 1 wafer from bag,eat 1 wafer",
        [3] = "flee o",
        [4] = "qw,qs",
    };

    /// <summary>
    /// Returns the fkeys array with built-in defaults substituted for any empty slots,
    /// when <see cref="DefaultHotkeys"/> is enabled.
    /// </summary>
    public string[] GetEffectiveFkeys()
    {
        var result = (string[])_fkeys.Clone();
        if (!DefaultHotkeys)
            return result;
        foreach (var (i, def) in s_defaultFkeys)
            if (i < result.Length && string.IsNullOrEmpty(result[i]))
                result[i] = def;
        return result;
    }

    private static string[] CreateEmptyFkeys() => new string[36];

    private static string[] NormalizeFkeys(string[]? fkeys)
    {
        var normalized = CreateEmptyFkeys();
        if (fkeys == null)
            return normalized;

        for (var i = 0; i < normalized.Length; i++)
            normalized[i] = i < fkeys.Length ? fkeys[i] ?? string.Empty : string.Empty;

        return normalized;
    }
}
