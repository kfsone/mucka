using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Maui.Graphics;
using Mucka.Audio;
using Mucka.Core;
using MudSharp.Models;

namespace Mucka.ViewModels;

public sealed class GameViewModel : BaseViewModel, IAsyncDisposable
{
    private readonly MuckaConnection _conn;
    private readonly Func<string[], Task>? _saveFkeysAsync;
    private readonly List<string> _history = new();
    private readonly string[] _allFkeys = new string[36];
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
    private string _rank = string.Empty;
    private string _dreamword = string.Empty;
    private bool _isConnected = true;
    private bool _fkeysVisible;
    private bool _isScrollMode;
    private bool _isCapturing;
    private bool _isInGameMode;
    private int _maxColumns;
    private int _effCols = 80;
    private double _widthDp;

    // Lines from the TCP thread are enqueued here; the UI timer flushes them in batches.
    private readonly ConcurrentQueue<StyledLine> _pendingLines = new();
    // History buffer for the (future) history panel — kept separately from the live view.
    private readonly List<StyledLine> _historyBuffer = new();

    public string InputText { get => _inputText; set => Set(ref _inputText, value); }
    public int Stamina { get => _stamina; set { Set(ref _stamina, value); OnPropertyChanged(nameof(StaText)); OnPropertyChanged(nameof(StaValue)); OnPropertyChanged(nameof(StaColor)); } }
    public int MaxStamina { get => _maxStamina; set { Set(ref _maxStamina, value); OnPropertyChanged(nameof(StaText)); OnPropertyChanged(nameof(StaValue)); } }
    public int Strength { get => _strength; set { Set(ref _strength, value); OnPropertyChanged(nameof(StrText)); OnPropertyChanged(nameof(StrValue)); OnPropertyChanged(nameof(StrColor)); } }
    public int MaxStrength { get => _maxStrength; set { Set(ref _maxStrength, value); OnPropertyChanged(nameof(StrText)); OnPropertyChanged(nameof(StrValue)); OnPropertyChanged(nameof(StrColor)); } }
    public int Dexterity { get => _dexterity; set { Set(ref _dexterity, value); OnPropertyChanged(nameof(DexText)); OnPropertyChanged(nameof(DexValue)); OnPropertyChanged(nameof(DexColor)); } }
    public int MaxDexterity { get => _maxDexterity; set { Set(ref _maxDexterity, value); OnPropertyChanged(nameof(DexText)); OnPropertyChanged(nameof(DexValue)); OnPropertyChanged(nameof(DexColor)); } }
    public int Magic { get => _magic; set { Set(ref _magic, value); OnPropertyChanged(nameof(MagText)); OnPropertyChanged(nameof(MagValue)); OnPropertyChanged(nameof(MagColor)); OnPropertyChanged(nameof(MagVisible)); } }
    public int MaxMagic { get => _maxMagic; set { Set(ref _maxMagic, value); OnPropertyChanged(nameof(MagText)); OnPropertyChanged(nameof(MagValue)); OnPropertyChanged(nameof(MagColor)); } }
    public int Score { get => _score; set { Set(ref _score, value); if (_baseScore < 0 && value > 0) _baseScore = value; OnPropertyChanged(nameof(ScoreText)); OnPropertyChanged(nameof(ScoreValue)); OnPropertyChanged(nameof(ScoreDisplayValue)); OnPropertyChanged(nameof(ScoreColor)); } }
    public bool Blind { get => _blind; set => Set(ref _blind, value); }
    public bool Deaf { get => _deaf; set => Set(ref _deaf, value); }
    public bool Crippled { get => _crippled; set => Set(ref _crippled, value); }
    public bool Dumb { get => _dumb; set => Set(ref _dumb, value); }
    public int TimeToReset { get => _timeToReset; set { Set(ref _timeToReset, value); OnPropertyChanged(nameof(TtrText)); OnPropertyChanged(nameof(TtrVisible)); OnPropertyChanged(nameof(AnyRightStatVisible)); } }
    public char Weather { get => _weather; set { Set(ref _weather, value); OnPropertyChanged(nameof(WeatherText)); OnPropertyChanged(nameof(WeatherGlyph)); OnPropertyChanged(nameof(WeatherTooltip)); OnPropertyChanged(nameof(WeatherDisplayText)); OnPropertyChanged(nameof(WeatherColor)); OnPropertyChanged(nameof(WeatherVisible)); OnPropertyChanged(nameof(AnyRightStatVisible)); } }
    /// <summary>Rank is no longer supplied by the mudsharp protocol layer; always empty.</summary>
    public string Rank { get => _rank; set => Set(ref _rank, value); }
    public string Dreamword { get => _dreamword; set { Set(ref _dreamword, value); OnPropertyChanged(nameof(DreamwordDisplay)); OnPropertyChanged(nameof(DreamwordIsPlaceholder)); } }
    public bool IsConnected { get => _isConnected; set => Set(ref _isConnected, value); }
    public bool FkeysVisible { get => _fkeysVisible; set => Set(ref _fkeysVisible, value); }
    public bool IsScrollMode { get => _isScrollMode; set { Set(ref _isScrollMode, value); OnPropertyChanged(nameof(IsNotScrollMode)); } }
    public bool IsNotScrollMode => !_isScrollMode;
    public bool IsCapturing { get => _isCapturing; private set => Set(ref _isCapturing, value); }
    public bool IsInGameMode { get => _isInGameMode; private set => Set(ref _isInGameMode, value); }
    public int MaxColumns => _maxColumns;
    public int EffCols => _effCols;

    /// <summary>True only in debug builds — controls visibility of the capture button.</summary>
    public bool IsCaptureFacilityAvailable { get; } =
#if DEBUG
        true;
#else
        false;
#endif

    public string StaText  => $"Sta: {Stamina}/{MaxStamina}";
    public string MagText  => $"Mag: {Magic}/{MaxMagic}";
    public string StrText  => $"Str: {Strength}/{MaxStrength}";
    public string DexText  => $"Dex: {Dexterity}/{MaxDexterity}";
    public string ScoreText => Score <= 0 ? "Score: —"
        : _baseScore < 0 ? $"Score: {Score}"
        : $"Score: {Score} ({ScoreDeltaStr(Score - _baseScore)})";

    // Value-only strings (no label prefix) for FormattedString spans in the status bar.
    public string StaValue   => $"{Stamina}/{MaxStamina}";
    public string MagValue   => $"{Magic}/{MaxMagic}";
    public string StrValue   => $"{Strength}/{MaxStrength}";
    public string DexValue   => $"{Dexterity}/{MaxDexterity}";
    public string ScoreValue => Score <= 0 ? "—"
        : _baseScore < 0 ? $"{Score}"
        : $"{Score} ({ScoreDeltaStr(Score - _baseScore)})";

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
    public bool IsVeryCompact     => _effCols < 50;
    /// <summary>Font size for stat values in compact layout — shrinks when effcols &lt; 50.</summary>
    public double StatsValueFontSize => _effCols < 50 ? 11.0 : 13.0;

    // ── Fkey toolbar density — three tiers shrinking with effcols ────────────
    // Returns (fontSize, buttonRightMargin, cfgWidth, totalHorizPad).
    private (double Font, double Bm, double Cfg, double PadH) FkeyDensity() => _effCols switch
    {
        >= 76 => (11.0, 3.0, 44.0, 8.0),
        >= 50 => (10.0, 2.0, 38.0, 4.0),
        _     => ( 9.0, 1.0, 32.0, 2.0),
    };

    public double    FkeyFontSize     => FkeyDensity().Font;
    public double    FkeyCfgWidth     => FkeyDensity().Cfg;
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
        // Subtract: symmetric bar padding (d.PadH total), Cfg button width + its right margin.
        double available = widthDp - d.PadH - d.Cfg - d.Bm;
        double slot      = Math.Floor(available / FkeyItems.Count);
        double btnWidth  = Math.Max(20.0, slot - d.Bm);
        foreach (var item in FkeyItems)
            item.Width = btnWidth;
    }

    // Campbell-palette colours matching Clio's terminal colour constants.
    private static readonly Color CampbellRed          = Color.FromArgb("#C50F1F");
    private static readonly Color CampbellGreen         = Color.FromArgb("#13A10E");
    private static readonly Color CampbellYellow        = Color.FromArgb("#C19C00");
    private static readonly Color CampbellWhite         = Color.FromArgb("#CCCCCC");
    private static readonly Color CampbellBrightBlack   = Color.FromArgb("#767676");
    private static readonly Color CampbellBrightRed     = Color.FromArgb("#E74856");
    private static readonly Color CampbellBrightGreen   = Color.FromArgb("#16C60C");
    private static readonly Color CampbellBrightYellow  = Color.FromArgb("#F9F1A5");

    /// <summary>Maps a Clio ANSI colour index (from the C99 0xFE stamina hint byte) to a display colour.</summary>
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

    /// <summary>Port of Clio's colourcode() — colours a stat by its eff/max ratio.</summary>
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
    public string DreamwordDisplay => string.IsNullOrEmpty(_dreamword) ? "..zzZZZzz.." : _dreamword;
    public bool DreamwordIsPlaceholder => string.IsNullOrEmpty(_dreamword);

    public ObservableCollection<FkeyItem> FkeyItems { get; } = new();
    public bool CanSaveFkeys => _saveFkeysAsync != null;

    public ICommand SendCommand { get; }
    public ICommand FkeyCommand { get; }
    public ICommand SpeakDreamwordCommand { get; }
    public ICommand HistoryUpCommand { get; }
    public ICommand HistoryDownCommand { get; }
    public ICommand ToggleFkeysCommand { get; }
    public ICommand ToggleCaptureCommand { get; }
    public ICommand EditFkeysCommand { get; }

    public event Action? Disconnected;
    public event Action? RequestFocus;
    public event Action? EditFkeysRequested;
    public event Action? ClearScreenRequested;

    public GameViewModel(MuckaConnection conn, Profile profile, Func<string[], Task>? saveFkeysAsync = null)
    {
        _conn = conn;
        _saveFkeysAsync = saveFkeysAsync;
        IsCapturing = _conn.IsCapturing;
        _maxColumns = Math.Clamp(profile.MaxColumns, 20, 160);
        _effCols = _maxColumns;

        ApplyFkeys(profile.Fkeys);

        // Pre-populate the input box with the account ID for manual login.
        if (!profile.TelnetLoginEnabled && !string.IsNullOrEmpty(profile.AccountId))
            _inputText = profile.AccountId;

        _conn.LineReady        += OnLineReady;
        _conn.StatsUpdated     += OnStatsUpdated;
        _conn.GameModeEntered  += OnGameModeEntered;
        _conn.GameModeExited   += OnGameModeExited;
        _conn.DreamwordChanged += OnDreamwordChanged;
        _conn.Disconnected     += OnDisconnected;
        _conn.SoundRequested   += OnSoundRequested;

        SendCommand           = new Command(SendNow);
        FkeyCommand           = new Command<string>(SendFkey);
        SpeakDreamwordCommand = new Command(SpeakDreamword);
        HistoryUpCommand      = new Command(HistoryUp);
        HistoryDownCommand    = new Command(HistoryDown);
        ToggleFkeysCommand    = new Command(() => { FkeysVisible = !FkeysVisible; RequestFocus?.Invoke(); });
        ToggleCaptureCommand  = new Command(ToggleCapture);
        EditFkeysCommand      = new Command(() => EditFkeysRequested?.Invoke());
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

    public async Task SaveFkeysAsync(string[] fkeys)
    {
        ApplyFkeys(fkeys);
        if (_saveFkeysAsync != null)
            await _saveFkeysAsync(GetAllFkeys());
    }

    // Called from the TCP read thread — must not touch UI directly.
    private void OnLineReady(StyledLine line) => _pendingLines.Enqueue(line);

    // Called from the TCP read thread — marshal IsInGameMode update onto the UI thread.
    private void OnGameModeEntered() => MainThread.BeginInvokeOnMainThread(() => IsInGameMode = true);
    private void OnGameModeExited()  => MainThread.BeginInvokeOnMainThread(() => IsInGameMode = false);

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
            Stamina      = stats.Stamina;
            MaxStamina   = stats.MaxStamina;
            Strength     = stats.Strength;
            MaxStrength  = stats.MaxStrength;
            Dexterity    = stats.Dexterity;
            MaxDexterity = stats.MaxDexterity;
            Magic        = stats.CurrentMagic;
            MaxMagic     = stats.MaxMagic;
            Score        = stats.Score;
            Blind        = stats.IsBlind;
            Deaf         = stats.IsDeaf;
            Crippled     = stats.IsCrippled;
            Dumb         = stats.IsDumb;
            TimeToReset  = stats.TimeToReset;
            Weather      = stats.Weather;
            _staminaColor = stats.StaminaColor;
            OnPropertyChanged(nameof(StaColor));
            if (stats.DreamWord != null)
                Dreamword = stats.DreamWord;
        });
    }

    private void OnDreamwordChanged(string? word)
        => MainThread.BeginInvokeOnMainThread(() => Dreamword = word ?? string.Empty);

    private void OnDisconnected(Exception? error)
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            IsConnected = false;
            Disconnected?.Invoke();
        });

    // Called from the TCP read thread — fire-and-forget, never block.
    private static void OnSoundRequested(string assetName) => SoundService.Play(assetName);

    private void SendNow()
    {
        var text = InputText;           // capture before any await
        InputText = string.Empty;       // clear synchronously
        RequestFocus?.Invoke();

        var trimmed = text.Trim();
        if (!HandleCommand(trimmed))
            _conn.SendLine(text);

        if (!string.IsNullOrWhiteSpace(trimmed))
        {
            _history.Add(trimmed);
            if (_history.Count > 200) _history.RemoveAt(0);
        }
        _historyIndex = _history.Count;
    }

    private bool HandleCommand(string text)
    {
        if (!text.StartsWith("/!"))
            return false;

        if (text.StartsWith("/!speak ", StringComparison.OrdinalIgnoreCase))
        {
            var slot = text[8..].Trim();
            AddSystemLine($"[speak] watchword engine not yet wired — slot '{slot}'", 14);
            return true;
        }

        if (text.Equals("/!sleep", StringComparison.OrdinalIgnoreCase))
        {
            AddSystemLine("[sleep] not yet implemented", 14);
            return true;
        }

        return false;
    }

    private void SendFkey(string indexStr)
    {
        if (!int.TryParse(indexStr, out var i) || i < 0 || i >= FkeyItems.Count)
        {
            RequestFocus?.Invoke();
            return;
        }

        var cmd = FkeyItems[i].Command;
        if (!string.IsNullOrWhiteSpace(cmd))
            _conn.SendLine(cmd.TrimEnd('\r', '\n'));

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
        var cmd = _allFkeys[absoluteIndex];
        if (!string.IsNullOrWhiteSpace(cmd))
            _conn.SendLine(cmd.TrimEnd('\r', '\n'));
        RequestFocus?.Invoke();
    }

    public void SpeakDreamword()
    {
        if (!string.IsNullOrEmpty(_dreamword))
        {
            _conn.Annotate($"dreamword spoken: {_dreamword}");
            _conn.SendLine($"\"{_dreamword}\"");
        }
        RequestFocus?.Invoke();
    }

    public void ClearScreen()
    {
        ClearScreenRequested?.Invoke();
        RequestFocus?.Invoke();
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
        var clamped       = Math.Clamp(Math.Min(_maxColumns, displayableCols), 20, 160);
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
            OnPropertyChanged(nameof(IsVeryCompact));
            OnPropertyChanged(nameof(StatsValueFontSize));
            OnPropertyChanged(nameof(FkeyFontSize));
            OnPropertyChanged(nameof(FkeyCfgWidth));
            OnPropertyChanged(nameof(FkeyButtonMargin));
            OnPropertyChanged(nameof(FkeyBarPadding));
            OnPropertyChanged(nameof(ScoreDisplayValue));
            OnPropertyChanged(nameof(WeatherDisplayText));
            UpdateFkeyItems();  // also calls UpdateFkeyItemWidths
        }
        else
        {
            UpdateFkeyItemWidths(widthDp);
        }
    }

    private void AddSystemLine(string msg, byte fg = 14)
    {
        var style = new TextStyle(Foreground: (AnsiColor)fg);
        var line = new StyledLine(new[] { new StyledSpan($"|mucka| {msg}", style) });
        OnLineReady(line);
    }

    public async ValueTask DisposeAsync()
    {
        _conn.LineReady        -= OnLineReady;
        _conn.StatsUpdated     -= OnStatsUpdated;
        _conn.GameModeEntered  -= OnGameModeEntered;
        _conn.GameModeExited   -= OnGameModeExited;
        _conn.DreamwordChanged -= OnDreamwordChanged;
        _conn.Disconnected     -= OnDisconnected;
        _conn.SoundRequested   -= OnSoundRequested;
        await _conn.DisposeAsync();
    }
}
