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
    private int _dexterity;
    private long _score;
    private string _rank = string.Empty;
    private string _dreamword = string.Empty;
    private bool _isConnected = true;
    private bool _fkeysVisible = DeviceInfo.Platform != DevicePlatform.WinUI;

    // Lines from the TCP thread are enqueued here; the UI timer flushes them in batches.
    private readonly ConcurrentQueue<StyledLine> _pendingLines = new();
    // History buffer for the (future) history panel — kept separately from the live view.
    private readonly List<StyledLine> _historyBuffer = new();

    public string InputText { get => _inputText; set => Set(ref _inputText, value); }
    public int Stamina { get => _stamina; set { Set(ref _stamina, value); OnPropertyChanged(nameof(StaText)); } }
    public int MaxStamina { get => _maxStamina; set { Set(ref _maxStamina, value); OnPropertyChanged(nameof(StaText)); } }
    public int Strength { get => _strength; set { Set(ref _strength, value); OnPropertyChanged(nameof(StrText)); } }
    public int Dexterity { get => _dexterity; set { Set(ref _dexterity, value); OnPropertyChanged(nameof(DexText)); } }
    public long Score { get => _score; set { Set(ref _score, value); OnPropertyChanged(nameof(ScoreText)); } }
    public string Rank { get => _rank; set => Set(ref _rank, value); }
    public string Dreamword { get => _dreamword; set => Set(ref _dreamword, value); }
    public bool IsConnected { get => _isConnected; set => Set(ref _isConnected, value); }
    public bool FkeysVisible { get => _fkeysVisible; set => Set(ref _fkeysVisible, value); }

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

    public event Action? Disconnected;
    public event Action? RequestFocus;

    public GameViewModel(MudConnection conn, Profile profile)
    {
        _conn = conn;

        for (var i = 0; i < 10; i++)
        {
            FkeyItems.Add(new FkeyItem(i, i < profile.Fkeys.Length ? profile.Fkeys[i] ?? string.Empty : string.Empty));
        }

        conn.Stream.LineReady += OnLineReady;
        conn.Stream.StatsUpdated += OnStatsUpdated;
        conn.Disconnected += OnDisconnected;
        conn.ConnectionError += OnConnectionError;

        SendCommand           = new AsyncCommand(SendAsync);
        FkeyCommand           = new Command<string>(SendFkey);
        SpeakDreamwordCommand = new Command(SpeakDreamword);
        HistoryUpCommand      = new Command(HistoryUp);
        HistoryDownCommand    = new Command(HistoryDown);
        ToggleFkeysCommand    = new Command(() => FkeysVisible = !FkeysVisible);
    }

    // Called from the TCP read thread — must not touch UI directly.
    private void OnLineReady(StyledLine line) => _pendingLines.Enqueue(line);

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
            Dexterity = stats.Dexterity;
            Score = stats.Score;
            Rank = stats.Rank;
            if (!string.IsNullOrEmpty(stats.Dreamword))
            {
                Dreamword = stats.Dreamword;
            }
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

    public async ValueTask DisposeAsync()
    {
        _conn.Stream.LineReady -= OnLineReady;
        _conn.Stream.StatsUpdated -= OnStatsUpdated;
        _conn.Disconnected -= OnDisconnected;
        _conn.ConnectionError -= OnConnectionError;
        await _conn.DisposeAsync();
    }
}
