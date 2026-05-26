using MudSharp.Models;
using MudSharp.Session;
using System.Net.Sockets;
using System.Text;

namespace Mucka.Core;

/// <summary>
/// Mucka's TCP connection layer. Owns the socket and read loop, wraps MudSession,
/// and (in DEBUG builds) intercepts raw RX/TX bytes before they reach the parser.
///
/// THREADING:
/// - ConnectAsync/DisconnectAsync are called from any thread.
/// - The read loop runs on a ThreadPool thread.
/// - MudSession events (LineReady, StatsUpdated, etc.) fire on the read-loop thread.
///   Consumers must marshal to their UI thread.
/// - OutgoingBytes from MudSession are sent synchronously on the caller thread.
/// </summary>
public sealed class MuckaConnection : IAsyncDisposable
{
    private readonly MudSession _session;
    private MudLoginHandler? _loginHandler;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Task? _readLoop;
    private CancellationTokenSource? _cts;
    private string _host = string.Empty;
    private readonly object _writeLock = new();

#if DEBUG || WINDOWS
    private readonly SessionCapture _capture = new();
#endif

    // ── Public events (forwarded from MudSession) ─────────────────────────────
    public event Action<StyledLine>? LineReady;
    public event Action<GameStatsSnapshot>? StatsUpdated;
    public event Action? GameModeEntered;
    public event Action? GameModeExited;
    public event Action<string?>? DreamwordChanged;
    public event Action<string>? ClientModeReceived;
    public event Action<string>? SoundRequested;
    /// <summary>Fired when the connection is lost (read loop ended). Null = clean disconnect.</summary>
    public event Action<Exception?>? Disconnected;

    public bool IsConnected => _client?.Connected ?? false;

#if DEBUG || WINDOWS
    public bool IsCapturing => _capture.IsRecording;
    public string? CaptureFilePath => _capture.FilePath;
    /// <summary>Write a free-text annotation into the active capture log.</summary>
    public void Annotate(string message) => _capture.Annotate(message);
#else
    public bool IsCapturing => false;
    public string? CaptureFilePath => null;
    public void Annotate(string message) { }
#endif

    private int _windowCols;

    public MuckaConnection(string? accountId = null, string? password = null, int maxCols = 80)
    {
        _windowCols = Math.Clamp(maxCols, 20, 160);
        _session = new MudSession();
        _session.SetWindowSize(_windowCols, 21);
        WireSessionEvents();
        if (!string.IsNullOrEmpty(accountId))
            _loginHandler = new MudLoginHandler(this, accountId, password ?? string.Empty);
    }

    /// <summary>Connect to the server and start the read loop.</summary>
    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        _host = host;
        _client = new TcpClient();
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        await _client.ConnectAsync(host, port, linkedCts.Token).ConfigureAwait(false);
        _stream = _client.GetStream();
        _cts = new CancellationTokenSource();

        // Wire outgoing bytes → network write (+ capture if active)
        _session.OutgoingBytes += SendBytesSync;

        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    /// <summary>Disconnect cleanly.</summary>
    public async Task DisconnectAsync()
    {
        _session.OutgoingBytes -= SendBytesSync;
        _cts?.Cancel();
        if (_readLoop != null)
            await _readLoop.ConfigureAwait(false);
        _stream?.Close();
        _client?.Close();
        _session.Reset();
        _loginHandler?.Reset();
    }

    public bool TryStartCapture(string? hostOverride, out string? error)
    {
#if DEBUG || WINDOWS
        var host = string.IsNullOrWhiteSpace(hostOverride) ? _host : hostOverride!.Trim();
        if (string.IsNullOrWhiteSpace(host)) host = "unknown";
        return _capture.TryStart(host, out error);
#else
        error = "Capture is only available in debug builds.";
        return false;
#endif
    }

    public void StopCapture()
    {
#if DEBUG || WINDOWS
        _capture.Stop();
#endif
    }

    /// <summary>Send a line of text to the server (appends \r\n).</summary>
    public void SendLine(string line) => _session.SendLine(line);

    /// <summary>Send raw bytes to the server (no transformation applied).</summary>
    public void SendBytes(byte[] bytes) => _session.Send(bytes);

    /// <summary>
    /// Update the advertised terminal window size. May be called from any thread.
    /// Sends an updated NAWS subnegotiation if NAWS has been negotiated with the server.
    /// </summary>
    public void SetWindowSize(int cols, int rows)
    {
        _windowCols = cols;
        _session.SetWindowSize(cols, rows);
    }

    /// <summary>
    /// Send the NAWS window-size update followed by the client-mode entry interrupt:
    ///   ESC-[ ESC^F ESC-T ESC-N /T{cols} ESC-]
    /// This enters best-client mode, selects normal mode (so cols are honoured), and
    /// tells the MUD shell the effective terminal width — all without a trailing newline.
    /// </summary>
    internal void SendClientModeEntry()
    {
        _session.SetWindowSize(_windowCols, 21);

        // ESC-[  = begin command interrupt
        // ESC^F  = best-client mode (binary activation)
        // ESC-T  = text mode (color ANSI baseline)
        // ESC-N  = normal mode (server honours our column count)
        // /T{n}  = MUD shell command: set terminal width
        // ESC-]  = end command interrupt
        byte[] prefix = { 0x1B, 0x2D, 0x5B, 0x1B, 0x06, 0x1B, 0x2D, 0x54, 0x1B, 0x2D, 0x4E };
        byte[] colCmd = Encoding.ASCII.GetBytes($"/T{_windowCols}");
        byte[] suffix = { 0x1B, 0x2D, 0x5D };

        var seq = new byte[prefix.Length + colCmd.Length + suffix.Length];
        Buffer.BlockCopy(prefix, 0, seq, 0, prefix.Length);
        Buffer.BlockCopy(colCmd, 0, seq, prefix.Length, colCmd.Length);
        Buffer.BlockCopy(suffix, 0, seq, prefix.Length + colCmd.Length, suffix.Length);
        _session.Send(seq);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _loginHandler?.Detach();
        _session.Dispose();
        _cts?.Dispose();
#if DEBUG || WINDOWS
        _capture.Dispose();
#endif
    }

    // ── Private ────────────────────────────────────────────────────────────────

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buf = new byte[4096];
        Exception? error = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await _stream!.ReadAsync(buf, ct);
                if (read == 0) break; // server closed connection
#if DEBUG || WINDOWS
                _capture.RecordRx(buf.AsSpan(0, read));
#endif
                // Feed raw bytes into MudSession AFTER capturing — parser sees unmodified bytes.
                _session.Feed(buf.AsSpan(0, read));
                // Force-emit any buffered text that has no trailing newline as a partial line
                // (e.g. "Account ID:" login prompts that arrive without a C98 game-mode signal).
                _session.EmitPartial();
            }
        }
        catch (OperationCanceledException) { /* clean disconnect */ }
        catch (Exception ex) { error = ex; }
        finally
        {
            Disconnected?.Invoke(error);
        }
    }

    private void SendBytesSync(byte[] bytes)
    {
        lock (_writeLock)
        {
            try
            {
#if DEBUG || WINDOWS
                _capture.RecordTx(bytes);
#endif
                _stream?.Write(bytes, 0, bytes.Length);
            }
            catch { /* connection lost — read loop will handle */ }
        }
    }

    private void WireSessionEvents()
    {
        _session.LineReady          += l => LineReady?.Invoke(l);
        _session.StatsUpdated       += s => StatsUpdated?.Invoke(s);
        _session.GameModeEntered    += () => GameModeEntered?.Invoke();
        _session.GameModeExited     += () => GameModeExited?.Invoke();
        _session.DreamwordChanged   += w =>
        {
#if DEBUG || WINDOWS
            if (w != null)
                _capture.Annotate($"dreamword detected: {w}");
            else
                _capture.Annotate("dreamword cleared");
#endif
            DreamwordChanged?.Invoke(w);
        };
        _session.ClientModeReceived += d => ClientModeReceived?.Invoke(d);
        _session.SoundRequested     += s => SoundRequested?.Invoke(s);
    }
}
