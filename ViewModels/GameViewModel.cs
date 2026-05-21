using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Mucka.Core;

namespace Mucka.ViewModels;

public sealed class GameViewModel : BaseViewModel, IAsyncDisposable
{
    private readonly MudConnection _conn;
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
    private long _score;
    private bool _blind;
    private bool _deaf;
    private bool _crippled;
    private bool _dumb;
    private int _minutesToReset;
    private char _weather;
    private byte _staminaColour;
    private string _rank = string.Empty;
    private string _dreamword = string.Empty;
    private bool _isConnected = true;
    private bool _fkeysVisible = DeviceInfo.Platform != DevicePlatform.WinUI;
    private bool _isCapturing;

    // Lines from the TCP thread are enqueued here; the UI timer flushes them in batches.
    private readonly ConcurrentQueue<StyledLine> _pendingLines = new();
    // History buffer for the (future) history panel — kept separately from the live view.
    private readonly List<StyledLine> _historyBuffer = new();
    // FES heartbeat — started once on game-mode entry, sends FES every 10 s.
    private IDispatcherTimer? _fesHeartbeat;

    public string InputText { get => _inputText; set => Set(ref _inputText, value); }
    public int Stamina { get => _stamina; set { Set(ref _stamina, value); OnPropertyChanged(nameof(StaText)); } }
    public int MaxStamina { get => _maxStamina; set { Set(ref _maxStamina, value); OnPropertyChanged(nameof(StaText)); } }
    public int Strength { get => _strength; set { Set(ref _strength, value); OnPropertyChanged(nameof(StrText)); } }
    public int MaxStrength { get => _maxStrength; set => Set(ref _maxStrength, value); }
    public int Dexterity { get => _dexterity; set { Set(ref _dexterity, value); OnPropertyChanged(nameof(DexText)); } }
    public int MaxDexterity { get => _maxDexterity; set => Set(ref _maxDexterity, value); }
    public int Magic { get => _magic; set => Set(ref _magic, value); }
    public int MaxMagic { get => _maxMagic; set => Set(ref _maxMagic, value); }
    public long Score { get => _score; set { Set(ref _score, value); OnPropertyChanged(nameof(ScoreText)); } }
    public bool Blind { get => _blind; set => Set(ref _blind, value); }
    public bool Deaf { get => _deaf; set => Set(ref _deaf, value); }
    public bool Crippled { get => _crippled; set => Set(ref _crippled, value); }
    public bool Dumb { get => _dumb; set => Set(ref _dumb, value); }
    public int MinutesToReset { get => _minutesToReset; set => Set(ref _minutesToReset, value); }
    public char Weather { get => _weather; set => Set(ref _weather, value); }
    public byte StaminaColour { get => _staminaColour; set => Set(ref _staminaColour, value); }
    public string Rank { get => _rank; set => Set(ref _rank, value); }
    public string Dreamword { get => _dreamword; set => Set(ref _dreamword, value); }
    public bool IsConnected { get => _isConnected; set => Set(ref _isConnected, value); }
    public bool FkeysVisible { get => _fkeysVisible; set => Set(ref _fkeysVisible, value); }
    public bool IsCapturing { get => _isCapturing; private set => Set(ref _isCapturing, value); }

    /// <summary>True only in debug builds — controls visibility of the capture button.</summary>
    public bool IsCaptureFacilityAvailable { get; } =
#if DEBUG
        true;
#else
        false;
#endif

    public string StaText => $"Sta: {Stamina}/{MaxStamina}";
    public string StrText => $"Str: {Strength}";
    public string DexText => $"Dex: {Dexterity}";
    public string ScoreText => Score > 0 ? $"Score: {Score:N0}" : "Score: —";

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

    public GameViewModel(MudConnection conn, Profile profile)
    {
        _conn = conn;

        for (var i = 0; i < 10; i++)
        {
            FkeyItems.Add(new FkeyItem(i, i < profile.Fkeys.Length ? profile.Fkeys[i] ?? string.Empty : string.Empty));
        }

        // Pre-populate the input box with the account ID for manual login.
        if (!profile.TelnetLoginEnabled && !string.IsNullOrEmpty(profile.AccountId))
            _inputText = profile.AccountId;

        conn.Stream.LineReady += OnLineReady;
        conn.Stream.StatsUpdated += OnStatsUpdated;
        conn.Stream.GameModeEntered += OnGameModeEntered;
        conn.Stream.GameModeExited += OnGameModeExited;
        conn.Disconnected += OnDisconnected;
        conn.ConnectionError += OnConnectionError;

        SendCommand           = new AsyncCommand(SendAsync);
        FkeyCommand           = new Command<string>(SendFkey);
        SpeakDreamwordCommand = new Command(SpeakDreamword);
        HistoryUpCommand      = new Command(HistoryUp);
        HistoryDownCommand    = new Command(HistoryDown);
        ToggleFkeysCommand    = new Command(() => FkeysVisible = !FkeysVisible);
        ToggleCaptureCommand  = new Command(ToggleCapture);
    }

    // Called from the TCP read thread — must not touch UI directly.
    private void OnLineReady(StyledLine line) => _pendingLines.Enqueue(line);

    private void OnGameModeEntered()
    {
        // Start the FES heartbeat on the UI thread (IDispatcherTimer requires it).
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_fesHeartbeat != null) return;
            _fesHeartbeat = Application.Current!.Dispatcher.CreateTimer();
            _fesHeartbeat.Interval = TimeSpan.FromSeconds(10);
            _fesHeartbeat.Tick += (_, _) => _conn.Stream.RequestFesSubscription();
            _fesHeartbeat.Start();
        });
    }

    private void OnGameModeExited()
    {
        // Stop the FES heartbeat so it doesn't fire while at the login menu.
        // _fesHeartbeat is set to null so OnGameModeEntered can start a fresh timer.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _fesHeartbeat?.Stop();
            _fesHeartbeat = null;
        });
    }

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
            if (!line.IsPartial)
            {
                _historyBuffer.Add(line);
                if (_historyBuffer.Count > 1000) _historyBuffer.RemoveAt(0);
            }
        }
        return batch;
    }

    private void OnStatsUpdated(GameStats stats)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Stamina = stats.Stamina;
            MaxStamina = stats.MaxStamina;
            Strength = stats.Strength;
            MaxStrength = stats.MaxStrength;
            Dexterity = stats.Dexterity;
            MaxDexterity = stats.MaxDexterity;
            Magic = stats.Magic;
            MaxMagic = stats.MaxMagic;
            Score = stats.Score;
            Blind = stats.Blind;
            Deaf = stats.Deaf;
            Crippled = stats.Crippled;
            Dumb = stats.Dumb;
            MinutesToReset = stats.MinutesToReset;
            Weather = stats.Weather;
            StaminaColour = stats.StaminaColour;
            Rank = stats.Rank;
            Dreamword = stats.Dreamword;
        });
    }

    private void OnDisconnected()
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            IsConnected = false;
            Disconnected?.Invoke();
        });

    private void OnConnectionError(string msg)
        => AddSystemLine($"Connection error: {msg}", 9);

    private async Task SendAsync()
    {
        var text = InputText;   // preserve as-typed; don't trim
        InputText = string.Empty;
        RequestFocus?.Invoke();

        var trimmed = text.Trim();
        if (!await HandleCommandAsync(trimmed))
            await _conn.SendAsync(text + "\r\n");

        if (!string.IsNullOrWhiteSpace(trimmed))
        {
            _history.Add(trimmed);
            if (_history.Count > 200) _history.RemoveAt(0);
        }
        _historyIndex = _history.Count;
    }

    private Task<bool> HandleCommandAsync(string text)
    {
        if (!text.StartsWith("/!"))
        {
            return Task.FromResult(false);
        }

        if (text.StartsWith("/!speak ", StringComparison.OrdinalIgnoreCase))
        {
            var slot = text[8..].Trim();
            AddSystemLine($"[speak] watchword engine not yet wired — slot '{slot}'", 14);
            return Task.FromResult(true);
        }

        if (text.Equals("/!sleep", StringComparison.OrdinalIgnoreCase))
        {
            AddSystemLine("[sleep] not yet implemented", 14);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    private void SendFkey(string indexStr)
    {
        if (!int.TryParse(indexStr, out var i) || i < 0 || i >= FkeyItems.Count)
        {
            return;
        }

        var cmd = FkeyItems[i].Command;
        if (!string.IsNullOrWhiteSpace(cmd))
        {
            _ = _conn.SendAsync(cmd.EndsWith("\r\n") ? cmd : cmd + "\r\n");
        }
    }

    private void SpeakDreamword()
    {
        if (!string.IsNullOrEmpty(Dreamword))
        {
            InputText = $"\"{Dreamword}";
        }
    }

    private void HistoryUp()
    {
        if (_history.Count == 0)
        {
            return;
        }

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

    private void AddSystemLine(string msg, byte fg = 14)
    {
        var line = new StyledLine();
        line.Add(new StyledSpan { Text = $"|mucka| {msg}", Fg = fg });
        OnLineReady(line);
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
            _conn.StartCapture();
            IsCapturing = true;
            AddSystemLine($"Capture started. File: {_conn.CaptureFilePath}", 10);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _fesHeartbeat?.Stop();
        _fesHeartbeat = null;
        _conn.Stream.LineReady -= OnLineReady;
        _conn.Stream.StatsUpdated -= OnStatsUpdated;
        _conn.Stream.GameModeEntered -= OnGameModeEntered;
        _conn.Stream.GameModeExited -= OnGameModeExited;
        _conn.Disconnected -= OnDisconnected;
        _conn.ConnectionError -= OnConnectionError;
        await _conn.DisposeAsync();
    }
}
