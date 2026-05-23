using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Maui.Graphics;
using Mucka.Core;
using MudSharp.Models;

namespace Mucka.ViewModels;

public sealed class GameViewModel : BaseViewModel, IAsyncDisposable
{
    private readonly MuckaConnection _conn;
    private readonly List<string> _history = new();
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
    private bool _fkeysVisible = DeviceInfo.Platform != DevicePlatform.WinUI;
    private bool _isScrollMode;
    private bool _isCapturing;

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
    public int Score { get => _score; set { Set(ref _score, value); if (_baseScore < 0 && value > 0) _baseScore = value; OnPropertyChanged(nameof(ScoreText)); OnPropertyChanged(nameof(ScoreValue)); OnPropertyChanged(nameof(ScoreColor)); } }
    public bool Blind { get => _blind; set => Set(ref _blind, value); }
    public bool Deaf { get => _deaf; set => Set(ref _deaf, value); }
    public bool Crippled { get => _crippled; set => Set(ref _crippled, value); }
    public bool Dumb { get => _dumb; set => Set(ref _dumb, value); }
    public int TimeToReset { get => _timeToReset; set { Set(ref _timeToReset, value); OnPropertyChanged(nameof(TtrText)); OnPropertyChanged(nameof(TtrVisible)); OnPropertyChanged(nameof(AnyRightStatVisible)); } }
    public char Weather { get => _weather; set { Set(ref _weather, value); OnPropertyChanged(nameof(WeatherText)); OnPropertyChanged(nameof(WeatherColor)); OnPropertyChanged(nameof(WeatherVisible)); OnPropertyChanged(nameof(AnyRightStatVisible)); } }
    /// <summary>Rank is no longer supplied by the mudsharp protocol layer; always empty.</summary>
    public string Rank { get => _rank; set => Set(ref _rank, value); }
    public string Dreamword { get => _dreamword; set { Set(ref _dreamword, value); OnPropertyChanged(nameof(DreamwordDisplay)); OnPropertyChanged(nameof(DreamwordIsPlaceholder)); } }
    public bool IsConnected { get => _isConnected; set => Set(ref _isConnected, value); }
    public bool FkeysVisible { get => _fkeysVisible; set => Set(ref _fkeysVisible, value); }
    public bool IsScrollMode { get => _isScrollMode; set { Set(ref _isScrollMode, value); OnPropertyChanged(nameof(IsNotScrollMode)); } }
    public bool IsNotScrollMode => !_isScrollMode;
    public bool IsCapturing { get => _isCapturing; private set => Set(ref _isCapturing, value); }

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

    public bool   MagVisible  => _magic > 0;
    public string TtrText     => TimeToReset > 0 ? $"{TimeToReset}m" : string.Empty;
    public bool   TtrVisible  => _timeToReset > 0;
    public bool   WeatherVisible => _weather is not (' ' or '\0' or (char)0);
    public bool   AnyRightStatVisible => WeatherVisible || TtrVisible;

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
    public string DreamwordDisplay => string.IsNullOrEmpty(_dreamword) ? "..zzZZZzz.." : _dreamword;
    public bool DreamwordIsPlaceholder => string.IsNullOrEmpty(_dreamword);

    public ObservableCollection<FkeyItem> FkeyItems { get; } = new();

    public ICommand SendCommand { get; }
    public ICommand FkeyCommand { get; }
    public ICommand SpeakDreamwordCommand { get; }
    public ICommand HistoryUpCommand { get; }
    public ICommand HistoryDownCommand { get; }
    public ICommand ToggleFkeysCommand { get; }
    public ICommand ToggleCaptureCommand { get; }

    public event Action? Disconnected;
    public event Action? RequestFocus;

    public GameViewModel(MuckaConnection conn, Profile profile)
    {
        _conn = conn;
        IsCapturing = _conn.IsCapturing;

        for (var i = 0; i < 10; i++)
        {
            FkeyItems.Add(new FkeyItem(i, i < profile.Fkeys.Length ? profile.Fkeys[i] ?? string.Empty : string.Empty));
        }

        // Pre-populate the input box with the account ID for manual login.
        if (!profile.TelnetLoginEnabled && !string.IsNullOrEmpty(profile.AccountId))
            _inputText = profile.AccountId;

        _conn.LineReady        += OnLineReady;
        _conn.StatsUpdated     += OnStatsUpdated;
        _conn.GameModeEntered  += OnGameModeEntered;
        _conn.GameModeExited   += OnGameModeExited;
        _conn.DreamwordChanged += OnDreamwordChanged;
        _conn.Disconnected     += OnDisconnected;

        SendCommand           = new Command(SendNow);
        FkeyCommand           = new Command<string>(SendFkey);
        SpeakDreamwordCommand = new Command(SpeakDreamword);
        HistoryUpCommand      = new Command(HistoryUp);
        HistoryDownCommand    = new Command(HistoryDown);
        ToggleFkeysCommand    = new Command(() => { FkeysVisible = !FkeysVisible; RequestFocus?.Invoke(); });
        ToggleCaptureCommand  = new Command(ToggleCapture);
    }

    // Called from the TCP read thread — must not touch UI directly.
    private void OnLineReady(StyledLine line) => _pendingLines.Enqueue(line);

    // MudSession owns the FES heartbeat — nothing to do in GameViewModel on mode transitions.
    private void OnGameModeEntered() { }
    private void OnGameModeExited()  { }

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

    private void SpeakDreamword()
    {
        if (!string.IsNullOrEmpty(_dreamword))
        {
            _conn.Annotate($"dreamword spoken: {_dreamword}");
            _conn.SendLine($"\"{_dreamword}\"");
        }
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
        await _conn.DisposeAsync();
    }
}
