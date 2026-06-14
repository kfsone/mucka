using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Microsoft.Maui.Graphics;
using Mucka.Audio;
using Mucka.Core;
using MudSharp.Models;

namespace Mucka.ViewModels;

public sealed class GameViewModel : BaseViewModel, IAsyncDisposable
{
    private readonly MuckaConnection _conn;
    private readonly Func<ClientSettings, string[], Task>? _saveSettingsAsync;
    private readonly List<string> _history = new();
    private readonly string[] _allFkeys = new string[36];
#if WINDOWS
    private readonly WatchwordStore _watchwords;
    private readonly string _profileName;
    private readonly string _profileHost;
    private Mucka.Core.Mapping.MappingSession? _mapSession;
#endif
    private int _historyIndex = -1;

    private string _inputText = string.Empty;
    private int _stamina;
    private int _maxStamina;
    private int _strength;
    private int _maxStrength;
    private int _dexterity;
    private int _maxDexterity;
    private int _magic;
    private int _maxMagic;
    private int _score;
    private int _baseScore = -1;
    private byte _staminaColor;
    private bool _blind;
    private bool _deaf;
    private bool _crippled;
    private bool _dumb;
    private int _timeToReset;
    private char _weather;
    private string _dreamword = string.Empty;
    private bool _isConnected = true;
    private bool _fkeysVisible;
    private bool _isCapturing;
    private int _maxColumns;
    private int _effCols = 80;
    private double _widthDp;
    private int _antiIdleSeconds;
    private bool _keepScreenOn;
    private int _dreamwordSizeOffset;
    private int _defaultFontSize;
    private int _defaultMaxColumns;
    private bool _inGameMode;
    private DateTime _lastSentUtc;
    private DateTime _lastBellUtc = DateTime.MinValue;
    private int _fontSize;
    private int _volume;
    private int _statUpdateFrequency;
    private bool _muteBeepSession;
    private bool _muteBeepPermanently;
    private SoundSettings _sounds = new();
    private bool _settingsPerProfile;
    private bool _fkeysPerProfile;

    // Lines from the TCP thread are enqueued here; the UI timer flushes them in batches.
    private readonly ConcurrentQueue<StyledLine> _pendingLines = new();
    // History buffer for the (future) history panel — kept separately from the live view.
    private readonly List<StyledLine> _historyBuffer = new();

    // INPUT_DIAG: the setter should fire only on deliberate pushes (send-clear, history nav,
    // Escape) — NOT once per typed character. Per-character firing here proves the Entry's Text
    // binding has regressed from the OneWay fast path back to TwoWay (the recurring lag bug).
    public string InputText
    {
        get => _inputText;
        set
        {
            value ??= string.Empty;   // InputText is never null; also makes the assignment below non-null
            Core.InputDiag.Log($"VM.InputText set len={value.Length} \"{value}\"");
            Set(ref _inputText, value);
        }
    }
    public int Stamina => _stamina;
    public int MaxStamina => _maxStamina;
    public int Strength => _strength;
    public int MaxStrength => _maxStrength;
    public int Dexterity => _dexterity;
    public int MaxDexterity => _maxDexterity;
    public int Magic => _magic;
    public int MaxMagic => _maxMagic;
    public int Score => _score;
    public bool Blind    { get => _blind;    set => SetAndNotify(ref _blind,    value, [nameof(AnyEffectVisible), nameof(EffectsGlyphs)]); }
    public bool Deaf     { get => _deaf;     set => SetAndNotify(ref _deaf,     value, [nameof(AnyEffectVisible), nameof(EffectsGlyphs)]); }
    public bool Crippled { get => _crippled; set => SetAndNotify(ref _crippled, value, [nameof(AnyEffectVisible), nameof(EffectsGlyphs)]); }
    public bool Dumb     { get => _dumb;     set => SetAndNotify(ref _dumb,     value, [nameof(AnyEffectVisible), nameof(EffectsGlyphs)]); }
    public int TimeToReset { get => _timeToReset; set => SetAndNotify(ref _timeToReset, value, [nameof(TtrText), nameof(TtrVisible), nameof(AnyRightStatVisible)]); }
    public char Weather { get => _weather; set => SetAndNotify(ref _weather, value, [nameof(WeatherText), nameof(WeatherGlyph), nameof(WeatherTooltip), nameof(WeatherDisplayText), nameof(WeatherColor), nameof(WeatherVisible), nameof(AnyRightStatVisible)]); }
    public string Dreamword { get => _dreamword; set => SetAndNotify(ref _dreamword, value, [nameof(DreamwordDisplay), nameof(DreamwordIsPlaceholder), nameof(DreamwordActive)]); }
    public bool IsConnected  { get => _isConnected;  set => Set(ref _isConnected,  value); }
    public bool FkeysVisible { get => _fkeysVisible; set => Set(ref _fkeysVisible, value); }
    public bool IsCapturing { get => _isCapturing; private set => Set(ref _isCapturing, value); }
    public int MaxColumns => _maxColumns;
    public int EffCols => _effCols;
    public bool KeepScreenOn => _keepScreenOn;
    public int FontSize => _fontSize;
    // Advance width of one Cascadia Mono cell per pixel of font size: 1200/2048 em units
    // (from the embedded TTF's hmtx/head tables — a true monospace, so every glyph shares
    // this advance). The previous 8.0/15 (≈0.533) calibration dated from the WebView renderer
    // and under-measured the Skia cell by ~10%, leaving windows sized from it 4-6 columns short.
    public const double CharWidthPerFontPx = 1200.0 / 2048.0;
    /// <summary>Default terminal font size in pixels when the profile does not override it.</summary>
    public const int DefaultFontSizePx = 15;
    // Character width in MAUI logical pixels for the current font size.
    public double CharWidthDp => _fontSize * CharWidthPerFontPx;
    public int Volume => _volume;
    public int StatUpdateFrequency => _statUpdateFrequency;
    public bool MuteBeepSession     { get => _muteBeepSession;     set => _muteBeepSession = value; }
    public bool MuteBeepPermanently => _muteBeepPermanently;

    /// <summary>True in debug builds and on Windows release — controls visibility of the capture button.</summary>
    public bool IsCaptureFacilityAvailable { get; } =
#if DEBUG || WINDOWS
        true;
#else
        false;
#endif

    public bool IsInGameMode => _inGameMode;

    /// <summary>True when the capture button should be shown. Debug: always when facility available; Release: also requires game mode.</summary>
    public bool IsRecordingButtonVisible =>
#if DEBUG
        IsCaptureFacilityAvailable;
#else
        IsCaptureFacilityAvailable && _inGameMode;
#endif

    // Value-only strings (no label prefix) for FormattedString spans in the status bar.
    // Current and "/max" are separate spans so the max half renders one font point smaller.
    // Below 50 cols, hide the /max for any stat with a 3-digit max (saves 4 chars per stat).
    public string StaCurValue => $"{Stamina}";
    public string StaMaxValue => (_effCols < 50 && _maxStamina >= 100) ? string.Empty : $"/{MaxStamina}";
    public string MagCurValue => $"{Magic}";
    public string MagMaxValue => (_effCols < 50 && _maxMagic    >= 100) ? string.Empty : $"/{MaxMagic}";
    public string StrCurValue => $"{Strength}";
    public string StrMaxValue => (_effCols < 50 && _maxStrength  >= 100) ? string.Empty : $"/{MaxStrength}";
    public string DexCurValue => $"{Dexterity}";
    public string DexMaxValue => (_effCols < 50 && _maxDexterity >= 100) ? string.Empty : $"/{MaxDexterity}";
    // Score number and reset-delta are separate spans so the score proper can render bold
    // while the delta stays regular weight.
    public string ScoreValue => Score <= 0 ? "—" : $"{Score}";
    public string ScoreDeltaValue => Score <= 0 || _baseScore < 0 ? string.Empty
        : $" ({ScoreDeltaStr(Score - _baseScore)})";

    /// <summary>Score value for display — drops the delta suffix when effcols &lt; 70.</summary>
    public string ScoreDisplayValue => Score <= 0 ? "—"
        : _baseScore < 0 || _effCols < 70 ? $"{Score}"
        : $"{Score} ({ScoreDeltaStr(Score - _baseScore)})";

    public bool   MagVisible  => _magic > 0;
    public string TtrText     => TimeToReset > 0 ? $"{TimeToReset}m" : string.Empty;
    public bool   TtrVisible  => _timeToReset > 0;
    public bool   WeatherVisible => _weather is not (' ' or '\0' or (char)0);
    public bool   AnyRightStatVisible => WeatherVisible || TtrVisible;

    /// <summary>Effective columns: min(160, MaxColumns, displayable chars). Updated by GamePage on resize.</summary>
    public bool IsCompactStats    => _effCols < 76;
    public bool IsNotCompactStats => _effCols >= 76;
    public bool IsCompactWeather  => _effCols < 80;
    /// <summary>Font size for stat values in compact layout — shrinks when effcols &lt; 50.</summary>
    public double StatsValueFontSize => _effCols < 50 ? 12.0 : 13.0;
    /// <summary>Font size for the "/max" half of a stat pair — two points below the current value.</summary>
    public double StatsMaxValueFontSize => StatsValueFontSize - 2.0;
    /// <summary>Font size for the dreamword pill — one point larger in wide mode.</summary>
    public double DreamwordFontSize => (_effCols < 50 ? 12.0 : 13.0) + _dreamwordSizeOffset;

    // ── Fkey toolbar density — three tiers shrinking with effcols ────────────
    // Returns (fontSize, buttonRightMargin, totalHorizPad).
    private (double Font, double Bm, double PadH) FkeyDensity() => _effCols switch
    {
        >= 76 => (11.0, 3.0, 8.0),
        >= 50 => (10.0, 2.0, 4.0),
        _     => ( 9.0, 1.0, 2.0),
    };

    public double    FkeyFontSize     => FkeyDensity().Font;
    public Thickness FkeyButtonMargin => new Thickness(0, 0, FkeyDensity().Bm, FkeyDensity().Bm);
    public Thickness FkeyBarPadding   { get { var d = FkeyDensity(); return new Thickness(d.PadH / 2, d.Bm, d.PadH / 2, d.Bm); } }

    // How many F-keys to show — drop from the high end as space shrinks, min 8.
    private int  FkeyCount() => _effCols switch
    {
        >= 76 => 12,
        >= 63 => 11,
        >= 50 => 10,
        >= 38 =>  9,
        _     =>  8,
    };
    // Whether to show the "F" prefix on buttons (loses it below 50 cols).
    private bool FkeyShowPrefix() => _effCols >= 50;

    /// <summary>
    /// Adds/removes items from FkeyItems and refreshes labels to match current effcols tier.
    /// Safe to call any time _effCols changes; must run on the UI thread.
    /// </summary>
    private void UpdateFkeyItems()
    {
        int  count = FkeyCount();
        bool showF = FkeyShowPrefix();

        while (FkeyItems.Count > count)
            FkeyItems.RemoveAt(FkeyItems.Count - 1);

        for (int i = 0; i < FkeyItems.Count; i++)
            FkeyItems[i].Label = showF ? $"F{i + 1}" : $"{i + 1}";

        while (FkeyItems.Count < count)
        {
            int i = FkeyItems.Count;
            FkeyItems.Add(new FkeyItem(i, _allFkeys[i], showF));
        }
        UpdateFkeyItemWidths(_widthDp);
    }

    /// <summary>Recomputes each button's WidthRequest from the current page width and density tier.</summary>
    private void UpdateFkeyItemWidths(double widthDp)
    {
        if (FkeyItems.Count == 0 || widthDp <= 0) return;
        var d = FkeyDensity();
        // Subtract the symmetric bar padding (d.PadH total).
        double available = widthDp - d.PadH;
        double slot      = Math.Floor(available / FkeyItems.Count);
        double btnWidth  = Math.Max(20.0, slot - d.Bm);
        foreach (var item in FkeyItems)
            item.Width = btnWidth;
    }

    // Campbell-palette cs matching Clio's terminal c constants.
    private static readonly Color CampbellRed          = Color.FromArgb("#C50F1F");
    private static readonly Color CampbellGreen         = Color.FromArgb("#13A10E");
    private static readonly Color CampbellYellow        = Color.FromArgb("#C19C00");
    private static readonly Color CampbellWhite         = Color.FromArgb("#CCCCCC");
    private static readonly Color CampbellBrightBlack   = Color.FromArgb("#767676");
    private static readonly Color CampbellBrightRed     = Color.FromArgb("#E74856");
    private static readonly Color CampbellBrightGreen   = Color.FromArgb("#16C60C");
    private static readonly Color CampbellBrightYellow  = Color.FromArgb("#F9F1A5");

    /// <summary>Maps a Clio ANSI color index (from the C99 0xFE stamina hint byte) to a display color.</summary>
    private static Color AnsiToColor(byte ansi) => ansi switch
    {
        1  => CampbellRed,
        2  => CampbellGreen,
        3  => CampbellYellow,
        9  => CampbellBrightRed,
        10 => CampbellBrightGreen,
        11 => CampbellBrightYellow,
        _  => CampbellBrightGreen,
    };

    /// <summary>Port of Clio's colorcode() — colors a stat by its eff/max ratio.</summary>
    private static Color StatColor(int eff, int max)
    {
        if (eff <= 0 || max <= 0) return CampbellBrightGreen;
        int ratio = eff * 100 / max;
        if (ratio >= 100) return CampbellBrightGreen;
        if (ratio >= 76)  return CampbellGreen;
        if (ratio >= 36)  return CampbellBrightYellow;
        if (ratio >= 16)  return CampbellYellow;
        if (ratio >= 6)   return CampbellRed;
        return CampbellBrightRed;
    }

    public Color StaColor     => _staminaColor == 0 ? CampbellBrightGreen : AnsiToColor(_staminaColor);
    public Color MagColor     => StatColor(Magic, MaxMagic);
    public Color StrColor     => StatColor(Strength, MaxStrength);
    public Color DexColor     => StatColor(Dexterity, MaxDexterity);
    public Color ScoreColor   => _baseScore < 0 || _score == _baseScore ? CampbellYellow
        : _score > _baseScore ? CampbellGreen
        : CampbellRed;
    public Color WeatherColor => _weather switch
    {
        'F' => CampbellBrightYellow,   // Sunny  — LT_YELLOW
        'C' => CampbellWhite,           // Cloud  — WHITE
        'R' => CampbellGreen,           // Rain   — GREEN
        'S' => CampbellWhite,           // Snow   — BLACK on WHITE → white
        'O' => CampbellBrightBlack,     // Ocast  — LT_BLACK (dark grey)
        'T' => CampbellGreen,           // Storm  — GREEN
        'B' => CampbellWhite,           // Blizd  — BLACK on WHITE → white
        _   => CampbellWhite,
    };

    public string WeatherText => _weather switch
    {
        'F' => "Sunny",
        'C' => "Cloud",
        'R' => "Rain",
        'S' => "Snow",
        'O' => "Ocast",
        'T' => "Storm",
        'B' => "Blizd",
        _   => string.Empty,
    };

    public string WeatherGlyph => _weather switch
    {
        'F' => "\u2600\uFE0E",  // Sun
        'C' => "\u26C5\uFE0E",  // Sun behind cloud
        'R' => "\u2602\uFE0E",  // Umbrella (rain)
        'S' => "\u2744\uFE0E",  // Snowflake
        'O' => "\u2601\uFE0E",  // Cloud (overcast)
        'T' => "\u26A1\uFE0E",  // Lightning (storm)
        'B' => "\u2603\uFE0E",  // Snowman (blizzard)
        _   => string.Empty,
    };

    public string WeatherTooltip => _weather switch
    {
        'F' => "Fine weather",
        'C' => "Cloudy",
        'R' => "Raining",
        'S' => "Snowing",
        'O' => "Overcast",
        'T' => "Stormy",
        'B' => "Blizzard",
        _   => string.Empty,
    };

    public string WeatherDisplayText => WeatherVisible
        ? (IsCompactWeather ? WeatherGlyph : $"{WeatherGlyph} {WeatherText}")
        : string.Empty;

    // ── Status effects (afflictions) — shown after the score group ───────────
    private const string DeafGlyph     = "\U0001F442";        // ear
    private const string BlindGlyph    = "\U0001F441\uFE0F"; // eye (FE0F: emoji presentation)
    private const string DumbGlyph     = "\U0001F444";        // mouth
    private const string CrippledGlyph = "\u267F";             // wheelchair symbol

    /// <summary>Glyph-only status effects on phones or when effcols &lt; 80.</summary>
    public bool IsCompactEffects => _effCols < 80 || DeviceInfo.Idiom == DeviceIdiom.Phone;

    public string DeafDisplay     => IsCompactEffects ? DeafGlyph     : $"{DeafGlyph} Deaf";
    public string BlindDisplay    => IsCompactEffects ? BlindGlyph    : $"{BlindGlyph} Blind";
    public string DumbDisplay     => IsCompactEffects ? DumbGlyph     : $"{DumbGlyph} Dumb";
    public string CrippledDisplay => IsCompactEffects ? CrippledGlyph : $"{CrippledGlyph} Crippled";

    public bool AnyEffectVisible => _deaf || _blind || _dumb || _crippled;

    /// <summary>Active effect glyphs joined for the compact (two-row) layout.</summary>
    public string EffectsGlyphs => string.Join(' ', new[]
    {
        _deaf     ? DeafGlyph     : null,
        _blind    ? BlindGlyph    : null,
        _dumb     ? DumbGlyph     : null,
        _crippled ? CrippledGlyph : null,
    }.Where(g => g != null));

    public string DreamwordDisplay => string.IsNullOrEmpty(_dreamword) ? "..zzZZZzz.." : _dreamword;
    /// <summary>Compact-stats variant: the full placeholder crowds the two-row bar.</summary>
    public bool DreamwordIsPlaceholder => string.IsNullOrEmpty(_dreamword);
    public bool DreamwordActive        => !string.IsNullOrEmpty(_dreamword);

    public ObservableCollection<FkeyItem> FkeyItems { get; } = new();
    public bool CanSaveSettings => _saveSettingsAsync != null;
    public SidePanelViewModel SidePanel { get; }

    /// <summary>Snapshot of the current client settings, for the settings dialog.</summary>
    public ClientSettings CurrentSettings => new()
    {
        FontSize            = _fontSize,
        MaxColumns          = _maxColumns,
        Volume              = _volume,
        StatUpdateFrequency = _statUpdateFrequency,
        MuteBeepSession     = _muteBeepSession,
        MuteBeepPermanently = _muteBeepPermanently,
        SettingsPerProfile  = _settingsPerProfile,
        FkeysPerProfile     = _fkeysPerProfile,
        Sounds              = _sounds.Clone(),
        // Display tab globals (stored on the profile struct at load time).
        DefaultFontSize     = _defaultFontSize,
        DefaultMaxColumns   = _defaultMaxColumns,
        DreamwordSizeOffset = _dreamwordSizeOffset,
        ShowOnline    = SidePanel.IsOnlineExpanded,
        ShowInventory = SidePanel.IsInventoryExpanded,
        ShowItemsHere = SidePanel.IsItemsHereExpanded,
        ShowMapCompass = SidePanel.IsMapExpanded,
    };

    public ICommand SendCommand { get; }
    public ICommand FkeyCommand { get; }
    public ICommand SpeakDreamwordCommand { get; }
    public ICommand HistoryUpCommand { get; }
    public ICommand HistoryDownCommand { get; }
    public ICommand ToggleFkeysCommand { get; }
    public ICommand ToggleCaptureCommand { get; }
    public ICommand ConfigCommand { get; }

    public event Action? Disconnected;
    public event Action? RequestFocus;
    public event Action? ConfigRequested;
    public event Action? ClearScreenRequested;
    /// <summary>Raised after settings have been persisted — GamePage shows a confirmation toast.</summary>
    public event Action? SettingsSaved;
#if WINDOWS
    public event Action? OpenRawConsoleRequested;
    /// <summary>Raised by $map — GamePage opens (or surfaces) the mapping panel window.</summary>
    public event Action? MapPanelRequested;
    public event Action<byte[]>? RawBytesReceived
    {
        add    => _conn.RawBytesReceived += value;
        remove => _conn.RawBytesReceived -= value;
    }
    public event Action<byte[]>? RawBytesSent
    {
        add    => _conn.RawBytesSent += value;
        remove => _conn.RawBytesSent -= value;
    }
    public void SendRawBytes(byte[] bytes) => _conn.SendBytes(bytes);
#endif

    public GameViewModel(MuckaConnection conn, Profile profile, Func<ClientSettings, string[], Task>? saveSettingsAsync = null)
    {
        _conn = conn;
        _saveSettingsAsync = saveSettingsAsync;
        IsCapturing = _conn.IsCapturing;
#if WINDOWS
        _watchwords = WatchwordStore.Load();
        _profileName = profile.Name;
        _profileHost = profile.Host;
#endif
        _maxColumns = Math.Clamp(profile.MaxColumns, 0, 160);  // 0 = auto
        _effCols = _maxColumns > 0 ? _maxColumns : 80;  // sensible until OnSizeAllocated fires
        _antiIdleSeconds = Math.Clamp(profile.AntiIdleSeconds, 0, 3600);
        _keepScreenOn = profile.KeepScreenOn;
        _lastSentUtc = DateTime.UtcNow;
        _fontSize = profile.FontSize > 0 ? profile.FontSize
                  : profile.DefaultFontSize > 0 ? profile.DefaultFontSize
                  : DefaultFontSizePx;
        _volume = Math.Clamp(profile.Volume, 0, 100);
        _statUpdateFrequency = Math.Clamp(profile.StatUpdateFrequency, 0, 30);
        _muteBeepPermanently = profile.MuteBeepPermanently;
        _muteBeepSession     = profile.MuteBeepPermanently;
        _settingsPerProfile  = profile.SettingsPerProfile;
        _fkeysPerProfile     = profile.FkeysPerProfile;
        _sounds              = profile.Sounds;
        _dreamwordSizeOffset = Math.Clamp(profile.DreamwordSizeOffset, -2, 4);
        _defaultFontSize     = profile.DefaultFontSize;
        _defaultMaxColumns   = profile.DefaultMaxColumns;
        SoundService.SetVolume(_volume);
        SoundService.SetSoundSettings(_sounds);

        SidePanel = new SidePanelViewModel();
        SidePanel.IsOnlineExpanded    = profile.ShowOnline;
        SidePanel.IsInventoryExpanded = profile.ShowInventory;
        SidePanel.IsItemsHereExpanded = profile.ShowItemsHere;
        SidePanel.IsMapExpanded       = profile.ShowMapCompass;
        SidePanel.SubscriptionOptionsChanged += (few, fei) => _conn.UpdateSubscriptionOptions(few, fei);
        _conn.UpdateSubscriptionOptions(profile.ShowOnline, profile.ShowInventory || profile.ShowItemsHere);

        // Align session FES timer with profile value (overrides MudSessionOptions default).
        _conn.SetFesInterval(_statUpdateFrequency);

        ApplyFkeys(profile.GetEffectiveFkeys());

        // Pre-populate the input box with the account ID for manual login.
        if (!profile.TelnetLoginEnabled && !string.IsNullOrEmpty(profile.AccountId))
            _inputText = profile.AccountId;

        SubscribeConnectionEvents();

        SendCommand           = new Command(SendNow);
        FkeyCommand           = new Command<string>(SendFkey);
        SpeakDreamwordCommand = new Command(SpeakDreamword);
        HistoryUpCommand      = new Command(HistoryUp);
        HistoryDownCommand    = new Command(HistoryDown);
        ToggleFkeysCommand    = new Command(() => { FkeysVisible = !FkeysVisible; RequestFocus?.Invoke(); });
        ToggleCaptureCommand  = new Command(ToggleCapture);
        ConfigCommand         = new Command(() => ConfigRequested?.Invoke());
    }

    public string[] GetAllFkeys()
    {
        var fkeys = new string[_allFkeys.Length];
        Array.Copy(_allFkeys, fkeys, _allFkeys.Length);
        return fkeys;
    }

    public void ApplyFkeys(string[] fkeys)
    {
        for (var i = 0; i < _allFkeys.Length; i++)
            _allFkeys[i] = i < fkeys.Length ? fkeys[i] ?? string.Empty : string.Empty;

        for (var i = 0; i < FkeyItems.Count; i++)
            FkeyItems[i].Command = _allFkeys[i];

        UpdateFkeyItems();
    }

    /// <summary>
    /// Applies a full settings snapshot (plus fkeys) to the live session — the settings
    /// dialog's Apply path. Every member of <see cref="ClientSettings"/> takes effect here;
    /// persistence is <see cref="SaveSettingsAsync"/>'s job.
    /// </summary>
    public void ApplyClientSettings(ClientSettings settings, string[] fkeys)
    {
        ApplyFkeys(fkeys);
        ApplyFontSize(settings.FontSize);
        ApplyVolume(settings.Volume);
        ApplyMaxColumns(settings.MaxColumns);
        ApplyStatUpdateFrequency(settings.StatUpdateFrequency);
        _muteBeepPermanently = settings.MuteBeepPermanently;
        _muteBeepSession     = settings.MuteBeepSession || settings.MuteBeepPermanently;
        _settingsPerProfile  = settings.SettingsPerProfile;
        _fkeysPerProfile     = settings.FkeysPerProfile;
        _sounds              = settings.Sounds;
        SoundService.SetSoundSettings(_sounds);

        // Apply side-panel section visibility (suppress event to avoid double subscription update).
        SidePanel.IsOnlineExpanded    = settings.ShowOnline;
        SidePanel.IsInventoryExpanded = settings.ShowInventory;
        SidePanel.IsItemsHereExpanded = settings.ShowItemsHere;
        SidePanel.IsMapExpanded       = settings.ShowMapCompass;
        _conn.UpdateSubscriptionOptions(settings.ShowOnline, settings.ShowInventory || settings.ShowItemsHere);

        _dreamwordSizeOffset = Math.Clamp(settings.DreamwordSizeOffset, -2, 4);
        _defaultFontSize     = settings.DefaultFontSize;
        _defaultMaxColumns   = settings.DefaultMaxColumns;
        OnPropertyChanged(nameof(DreamwordFontSize));
    }

    /// <summary>Applies the snapshot, persists it (mucka.ini via the saver delegate), and
    /// raises <see cref="SettingsSaved"/>. Exceptions propagate to the caller for display.</summary>
    public async Task SaveSettingsAsync(ClientSettings settings, string[] fkeys)
    {
        ApplyClientSettings(settings, fkeys);
        if (_saveSettingsAsync != null)
        {
            await _saveSettingsAsync(CurrentSettings, GetAllFkeys());
            SettingsSaved?.Invoke();
        }
    }

    private void OnLineReady(StyledLine line) => _pendingLines.Enqueue(line);

    // MudSession owns the FES heartbeat — nothing to do in GameViewModel on mode transitions
    // beyond tracking game mode for anti-idle. Events fire on the TCP thread; marshal to UI.
    private void OnGameModeEntered()
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            _inGameMode = true;
            _lastSentUtc = DateTime.UtcNow;
            OnPropertiesChanged(nameof(IsInGameMode), nameof(IsRecordingButtonVisible));
        });

    private void OnGameModeExited()
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            _inGameMode = false;
            OnPropertiesChanged(nameof(IsInGameMode), nameof(IsRecordingButtonVisible));
        });

    /// <summary>
    /// Called by GamePage's 50ms timer on the UI thread.
    /// Returns the lines to inject, or null if nothing pending.
    /// Also maintains the history buffer for the (future) history panel.
    /// </summary>
    public List<StyledLine>? FlushPendingLines()
    {
        if (_pendingLines.IsEmpty) return null;

        var batch = new List<StyledLine>();
        while (_pendingLines.TryDequeue(out var line))
        {
            batch.Add(line);
            if (!line.IsPartial && !line.PlainText.Contains('\f'))
            {
                _historyBuffer.Add(line);
                if (_historyBuffer.Count > 1000) _historyBuffer.RemoveAt(0);
            }
        }
        return batch;
    }

    // Stats come exclusively from StatsUpdatedEvent — no text-based stat extraction.
    private void OnStatsUpdated(GameStatsSnapshot stats)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Core.InputDiag.Log("STATS update");
            // Write all backing fields directly to avoid per-property cascading notifications,
            // then raise all affected property-changed events in one batch at the end.
            _stamina      = stats.Stamina      ?? 0;
            _maxStamina   = stats.MaxStamina   ?? 0;
            _strength     = stats.Strength     ?? 0;
            _maxStrength  = stats.MaxStrength  ?? 0;
            _dexterity    = stats.Dexterity    ?? 0;
            _maxDexterity = stats.MaxDexterity ?? 0;
            _magic        = stats.CurrentMagic ?? 0;
            _maxMagic     = stats.MaxMagic     ?? 0;

            var prevScore = _score;
            _score        = stats.Score ?? 0;
            if (_baseScore < 0 && _score > 0) _baseScore = _score;

            _blind        = stats.IsBlind;
            _deaf         = stats.IsDeaf;
            _crippled     = stats.IsCrippled;
            _dumb         = stats.IsDumb;
            _timeToReset  = stats.TimeToReset ?? 0;
            _weather      = stats.Weather;
            _staminaColor = stats.StaminaColor ?? 0;

            if (stats.DreamWord != null)
                _dreamword = stats.DreamWord;

            // Single consolidated batch of notifications.
            OnPropertiesChanged(
                nameof(Stamina),    nameof(MaxStamina),
                nameof(StaCurValue), nameof(StaMaxValue), nameof(StaColor),
                nameof(Strength),   nameof(MaxStrength),
                nameof(StrCurValue), nameof(StrMaxValue), nameof(StrColor),
                nameof(Dexterity),  nameof(MaxDexterity),
                nameof(DexCurValue), nameof(DexMaxValue), nameof(DexColor),
                nameof(Magic),      nameof(MaxMagic),
                nameof(MagCurValue), nameof(MagMaxValue), nameof(MagColor),    nameof(MagVisible),
                nameof(Score),
                nameof(ScoreValue), nameof(ScoreDeltaValue), nameof(ScoreDisplayValue), nameof(ScoreColor),
                nameof(Blind),      nameof(Deaf),        nameof(Crippled),    nameof(Dumb),
                nameof(AnyEffectVisible), nameof(EffectsGlyphs),
                nameof(TimeToReset),
                nameof(TtrText),    nameof(TtrVisible),
                nameof(Weather),
                nameof(WeatherText), nameof(WeatherGlyph), nameof(WeatherTooltip),
                nameof(WeatherDisplayText), nameof(WeatherColor), nameof(WeatherVisible),
                nameof(AnyRightStatVisible),
                nameof(Dreamword),  nameof(DreamwordDisplay), nameof(DreamwordIsPlaceholder), nameof(DreamwordActive)
            );
        });
    }

    private void OnDreamwordChanged(string? word)
        => MainThread.BeginInvokeOnMainThread(() => Dreamword = word ?? string.Empty);

    private void OnDisconnected(Exception? error)
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            _inGameMode = false;
            IsConnected = false;
            OnPropertiesChanged(nameof(IsInGameMode), nameof(IsRecordingButtonVisible));
            Disconnected?.Invoke();
        });

    // Called from the TCP read thread — fire-and-forget, never block.
    // PlayServerSound applies the Sounds-tab gating (master/group/sound + fallback).
    private static void OnSoundRequested(string assetName) => SoundService.PlayServerSound(assetName);

    private void OnBellReceived()
    {
        if (_muteBeepSession || _muteBeepPermanently || !_sounds.MasterEnabled) return;
        var now = DateTime.UtcNow;
        if (now - _lastBellUtc < TimeSpan.FromSeconds(2)) return;
        _lastBellUtc = now;
        SoundService.PlayBell();
    }

    private void SendNow()
    {
        var text = InputText;           // capture before any await
        InputText = string.Empty;       // clear synchronously
        RequestFocus?.Invoke();

        var trimmed = text.Trim();
        if (!HandleCommand(trimmed))
        {
#if WINDOWS
            _conn.SendLine(_watchwords.ExpandSlots(trimmed));
#else
            _conn.SendLine(trimmed);
#endif
        }

        _lastSentUtc = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(trimmed))
        {
            _history.Add(trimmed);
            if (_history.Count > 200) _history.RemoveAt(0);
        }
        _historyIndex = _history.Count;
    }

    private bool HandleCommand(string text)
    {
#if WINDOWS
        if (text.StartsWith('$'))
        {
            var name = text[1..];
            if (name == "?")
            {
                var names = _watchwords.SlotNames;
                AddSystemLine(names.Length == 0
                    ? "[watchword] no slots loaded"
                    : $"[watchword] {string.Join("  ", names.Select(n => $"${n}"))}", 14);
            }
            else if (name == "<")
                ScanHistory();
            else if (name == "con")
                OpenRawConsoleRequested?.Invoke();
            else if (name == "map" || name.StartsWith("map ", StringComparison.OrdinalIgnoreCase))
                HandleMapCommand(name.Length > 3 ? name[4..].Trim() : string.Empty);
            else
                SpeakWatchword(name);
            return true;
        }
#endif

        return false;
    }

#if WINDOWS
    private void ScanHistory()
    {
        var count     = _historyBuffer.Count;
        var scanStart = Math.Max(0, count - 80);

        // Join all recent lines into one string, skipping blank lines.
        // No block-splitting: a blank line inside a wrapped paragraph would otherwise
        // break matching when the trigger's opening quote and capture span the gap.
        var sb = new System.Text.StringBuilder();
        for (var i = scanStart; i < count; i++)
        {
            var plain = _historyBuffer[i].PlainText.TrimEnd();
            if (plain.Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(plain);
        }

        int found = 0;
        foreach (var (slotName, answer) in _watchwords.ScanAll(sb.ToString()))
        {
            AddSystemLine($"[watchword] queued \"{answer}\" → ${slotName}", 14);
            found++;
        }

        if (found == 0)
            AddSystemLine("[watchword] no matches in recent history", 14);
    }

    private void SpeakWatchword(string slotName)
    {
        var answer = _watchwords.Speak(slotName);
        if (answer != null)
        {
            _conn.SendLine($"\"{answer}");
            _lastSentUtc = DateTime.UtcNow;
        }
        else
            AddSystemLine($"[watchword] nothing queued for ${slotName}", 14);
    }

    /// <summary>The mapping data directory for this profile (mucka.ini mappingdir, or default).</summary>
    public string MappingDirectory => Mucka.Core.Mapping.MappingStore.ResolveDirectory(_profileName);

    /// <summary>The mapping operation console (created on first use; lives until dispose).
    /// All map capture goes through its operations -- see MappingSession.</summary>
    public Mucka.Core.Mapping.MappingSession MapSession
    {
        get
        {
            if (_mapSession is null)
            {
                _mapSession = new Mucka.Core.Mapping.MappingSession(_conn, MappingDirectory, _profileHost);
                _mapSession.Status += s =>
                    MainThread.BeginInvokeOnMainThread(() => AddSystemLine($"[map] {s}", 14));
            }
            return _mapSession;
        }
    }

    private void HandleMapCommand(string arg)
    {
        switch (arg)
        {
            case "":
                MapPanelRequested?.Invoke();
                break;

            case "probe":
                if (!MapSession.TryStartProbe(out var probeError))
                    AddSystemLine($"[map] {probeError}", 9);
                break;

            case "dir":
                AddSystemLine($"[map] directory: {MappingDirectory}", 14);
                break;

            case "reload":
                var summary = Mucka.Core.Mapping.MappingStore.Reload(MappingDirectory);
                AddSystemLine(summary.FileCount == 0
                    ? $"[map] no captures in {MappingDirectory}"
                    : $"[map] {summary.FileCount} file(s), {summary.EntryCount} entries; newest: {summary.NewestFile}", 14);
                break;

            default:
                AddSystemLine("[map] usage: $map (panel) | $map probe | $map dir | $map reload", 14);
                break;
        }
    }
#endif

    private void SendFkey(string indexStr)
    {
        if (!int.TryParse(indexStr, out var i) || i < 0 || i >= FkeyItems.Count)
        {
            RequestFocus?.Invoke();
            return;
        }

        SendMacro(FkeyItems[i].Command);

        RequestFocus?.Invoke();
    }

    /// <summary>
    /// Send the fkey macro at the given absolute index (0-11=None, 12-23=Shift, 24-35=Ctrl).
    /// Called by the keyboard handler in GamePage on Windows.
    /// </summary>
    public void SendFkeyAbsolute(int absoluteIndex)
    {
        if (absoluteIndex < 0 || absoluteIndex >= _allFkeys.Length)
        {
            RequestFocus?.Invoke();
            return;
        }

        SendMacro(_allFkeys[absoluteIndex]);
        RequestFocus?.Invoke();
    }

    private void SendMacro(string? cmd)
    {
        if (!string.IsNullOrWhiteSpace(cmd))
        {
            cmd = cmd.TrimEnd('\r', '\n');
#if WINDOWS
            if (cmd.StartsWith('$'))
                SpeakWatchword(cmd[1..]);
            else
            {
                _conn.SendLine(_watchwords.ExpandSlots(cmd));
                _lastSentUtc = DateTime.UtcNow;
            }
#else
            _conn.SendLine(cmd);
            _lastSentUtc = DateTime.UtcNow;
#endif
        }
    }

    public void SpeakDreamword()
    {
        if (!string.IsNullOrEmpty(_dreamword))
        {
            _conn.Annotate($"dreamword spoken: {_dreamword}");
            _conn.SendLine($"\"{_dreamword}\"");
            _lastSentUtc = DateTime.UtcNow;
        }
        RequestFocus?.Invoke();
    }

    public void ClearScreen()
    {
        ClearScreenRequested?.Invoke();
        RequestFocus?.Invoke();
    }

    public void AntiIdleTick()
    {
        if (_antiIdleSeconds <= 0 || !_inGameMode || !_conn.IsConnected)
            return;
        if ((DateTime.UtcNow - _lastSentUtc).TotalSeconds < _antiIdleSeconds)
            return;
        _lastSentUtc = DateTime.UtcNow;
        _conn.SendLine(string.Empty);
    }

    private void HistoryUp()
    {
        if (_history.Count == 0) return;
        _historyIndex = Math.Max(0, _historyIndex - 1);
        InputText = _history[_historyIndex];
    }

    private void HistoryDown()
    {
        if (_historyIndex >= _history.Count - 1)
        {
            InputText = string.Empty;
            _historyIndex = _history.Count;
            return;
        }
        _historyIndex++;
        InputText = _history[_historyIndex];
    }

    private void ToggleCapture()
    {
        if (_conn.IsCapturing)
        {
            var path = _conn.CaptureFilePath;
            _conn.StopCapture();
            IsCapturing = false;
            AddSystemLine($"Capture stopped. File: {path}", 14);
        }
        else
        {
            if (_conn.TryStartCapture(null, out var error))
            {
                IsCapturing = true;
                AddSystemLine($"Capture started. File: {_conn.CaptureFilePath}", 10);
            }
            else
            {
                AddSystemLine($"Capture failed: {error}", 9);
            }
        }
        RequestFocus?.Invoke();
    }

    private static string ScoreDeltaStr(int delta) =>
        delta >= 0 ? $"+{delta}" : $"{delta}";

    /// <summary>
    /// Called by GamePage on each SizeAllocated. Updates effective columns and notifies
    /// the server of the new terminal width if it changed.
    /// </summary>
    public void NotifyWindowSize(double widthDp, int displayableCols)
    {
        // 0 = auto: use window width directly; otherwise cap to user-set max.
        var clamped       = _maxColumns > 0
            ? Math.Clamp(Math.Min(_maxColumns, displayableCols), 20, 160)
            : Math.Clamp(displayableCols, 20, 160);
        var effColChanged = clamped != _effCols;
        _widthDp = widthDp;
        if (effColChanged)
        {
            _effCols = clamped;
            _conn.SetWindowSize(_effCols, 21);
            OnPropertyChanged(nameof(EffCols));
            OnPropertyChanged(nameof(IsCompactStats));
            OnPropertyChanged(nameof(IsNotCompactStats));
            OnPropertyChanged(nameof(IsCompactWeather));
            OnPropertyChanged(nameof(StatsValueFontSize));
            OnPropertyChanged(nameof(StatsMaxValueFontSize));
            OnPropertyChanged(nameof(DreamwordFontSize));
            OnPropertyChanged(nameof(StaMaxValue));
            OnPropertyChanged(nameof(MagMaxValue));
            OnPropertyChanged(nameof(StrMaxValue));
            OnPropertyChanged(nameof(DexMaxValue));
            OnPropertyChanged(nameof(FkeyFontSize));
            OnPropertyChanged(nameof(FkeyButtonMargin));
            OnPropertyChanged(nameof(FkeyBarPadding));
            OnPropertyChanged(nameof(ScoreDisplayValue));
            OnPropertyChanged(nameof(WeatherDisplayText));
            OnPropertyChanged(nameof(IsCompactEffects));
            OnPropertyChanged(nameof(DeafDisplay));
            OnPropertyChanged(nameof(BlindDisplay));
            OnPropertyChanged(nameof(DumbDisplay));
            OnPropertyChanged(nameof(CrippledDisplay));
            UpdateFkeyItems();  // also calls UpdateFkeyItemWidths
        }
        else
        {
            UpdateFkeyItemWidths(widthDp);
        }
    }

    /// <summary>Updates the FES heartbeat interval. Zero disables the heartbeat.</summary>
    public void ApplyStatUpdateFrequency(int secs)
    {
        _statUpdateFrequency = Math.Clamp(secs, 0, 30);
        _conn.SetFesInterval(_statUpdateFrequency);
        OnPropertyChanged(nameof(StatUpdateFrequency));
    }

    /// <summary>
    /// Applies a new terminal font size and recomputes the column layout for the new
    /// glyph width. GamePage reacts to the FontSize change (terminal + window resize).
    /// </summary>
    public void ApplyFontSize(int px)
    {
        px = px > 0 ? Math.Clamp(px, 9, 24) : DefaultFontSizePx;
        if (px == _fontSize) return;
        _fontSize = px;
        OnPropertiesChanged(nameof(FontSize), nameof(CharWidthDp));
        if (_widthDp > 0)
            NotifyWindowSize(_widthDp, (int)Math.Floor(_widthDp / CharWidthDp));
    }

    /// <summary>Applies a new sound volume to subsequently played sounds.</summary>
    public void ApplyVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 100);
        SoundService.SetVolume(_volume);
        OnPropertyChanged(nameof(Volume));
    }

    /// <summary>
    /// Applies a new maximum column count, sends the /T escape sequence to the server,
    /// and recalculates the effective column layout.
    /// </summary>
    public void ApplyMaxColumns(int cols)
    {
        cols = Math.Clamp(cols, 0, 160);  // 0 = auto
        _maxColumns = cols;
        // In auto mode don't send an explicit NAWS width; NotifyWindowSize will send the
        // real value once the window is measured. In explicit mode send immediately.
        if (cols > 0)
            _conn.SendTerminalWidth(cols);
        OnPropertyChanged(nameof(MaxColumns));
        if (_widthDp > 0)
            NotifyWindowSize(_widthDp, (int)(_widthDp / CharWidthDp));
    }

    public void Annotate(string message) => _conn.Annotate(message);

    private void AddSystemLine(string msg, byte fg = 14)
    {
        var style = new TextStyle(Foreground: (AnsiColor)fg);
        var line = new StyledLine(new[] { new StyledSpan($"|mucka| {msg}", style) });
        OnLineReady(line);
    }

    private void SubscribeConnectionEvents()
    {
        _conn.LineReady        += OnLineReady;
        _conn.StatsUpdated     += OnStatsUpdated;
        _conn.GameModeEntered  += OnGameModeEntered;
        _conn.GameModeExited   += OnGameModeExited;
        _conn.GameModeExited   += SidePanel.OnGameModeExited;
        _conn.DreamwordChanged += OnDreamwordChanged;
        _conn.Disconnected     += OnDisconnected;
        _conn.SoundRequested   += OnSoundRequested;
        _conn.BellReceived     += OnBellReceived;
        _conn.RoomEntered      += SidePanel.OnRoomEntered;
        _conn.RoomShortReady   += SidePanel.OnRoomNameReady;
        _conn.FewPlayerReady   += SidePanel.OnFewPlayerReceived;
        _conn.FewListStarting  += SidePanel.OnFewListStarting;
        _conn.FewListComplete  += SidePanel.OnFewListComplete;
        _conn.FeiListStarting  += SidePanel.OnFeiListStarting;
        _conn.FeiItemReady     += SidePanel.OnFeiItemReady;
        _conn.FeiListComplete  += SidePanel.OnFeiListComplete;
        _conn.FexListStarting  += SidePanel.OnFexListStarting;
        _conn.FexItemReady     += SidePanel.OnFexItemReady;
        _conn.FexListComplete  += SidePanel.OnFexListComplete;
    }

    private void UnsubscribeConnectionEvents()
    {
        _conn.LineReady        -= OnLineReady;
        _conn.StatsUpdated     -= OnStatsUpdated;
        _conn.GameModeEntered  -= OnGameModeEntered;
        _conn.GameModeExited   -= OnGameModeExited;
        _conn.GameModeExited   -= SidePanel.OnGameModeExited;
        _conn.DreamwordChanged -= OnDreamwordChanged;
        _conn.Disconnected     -= OnDisconnected;
        _conn.SoundRequested   -= OnSoundRequested;
        _conn.BellReceived     -= OnBellReceived;
        _conn.RoomEntered      -= SidePanel.OnRoomEntered;
        _conn.RoomShortReady   -= SidePanel.OnRoomNameReady;
        _conn.FewPlayerReady   -= SidePanel.OnFewPlayerReceived;
        _conn.FewListStarting  -= SidePanel.OnFewListStarting;
        _conn.FewListComplete  -= SidePanel.OnFewListComplete;
        _conn.FeiListStarting  -= SidePanel.OnFeiListStarting;
        _conn.FeiItemReady     -= SidePanel.OnFeiItemReady;
        _conn.FeiListComplete  -= SidePanel.OnFeiListComplete;
        _conn.FexListStarting  -= SidePanel.OnFexListStarting;
        _conn.FexItemReady     -= SidePanel.OnFexItemReady;
        _conn.FexListComplete  -= SidePanel.OnFexListComplete;
    }

    public async ValueTask DisposeAsync()
    {
        SidePanel.Dispose();
        UnsubscribeConnectionEvents();
#if WINDOWS
        _mapSession?.Dispose();
#endif
        await _conn.DisposeAsync();
    }
}
