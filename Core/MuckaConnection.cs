using MudSharp.Models;
using MudSharp.Session;
using System.Net.Sockets;

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

#if DEBUG
    private readonly SessionCapture _capture = new();
#endif

    // ── Public events (forwarded from MudSession) ─────────────────────────────
    public event Action<StyledLine>? LineReady;
    public event Action<GameStatsSnapshot>? StatsUpdated;
    public event Action? GameModeEntered;
    public event Action? GameModeExited;
    public event Action<string?>? DreamwordChanged;
    public event Action<string>? ClientModeReceived;
    /// <summary>Fired when the connection is lost (read loop ended). Null = clean disconnect.</summary>
    public event Action<Exception?>? Disconnected;

    public bool IsConnected => _client?.Connected ?? false;

#if DEBUG
    public bool IsCapturing => _capture.IsRecording;
    public string? CaptureFilePath => _capture.FilePath;
#else
    public bool IsCapturing => false;
    public string? CaptureFilePath => null;
#endif

    public MuckaConnection(string? accountId = null, string? password = null)
    {
        _session = new MudSession();
        WireSessionEvents();
        if (!string.IsNullOrEmpty(accountId))
            _loginHandler = new MudLoginHandler(this, accountId, password ?? string.Empty);
    }

    /// <summary>Connect to the server and start the read loop.</summary>
    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        _host = host;
        _client = new TcpClient();
        await _client.ConnectAsync(host, port, cancellationToken);
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
#if DEBUG
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
#if DEBUG
        _capture.Stop();
#endif
    }

    /// <summary>Send a line of text to the server (appends \r\n).</summary>
    public void SendLine(string line) => _session.SendLine(line);

    /// <summary>Send raw bytes to the server (no transformation applied).</summary>
    public void SendBytes(byte[] bytes) => _session.Send(bytes);

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _loginHandler?.Detach();
        _session.Dispose();
        _cts?.Dispose();
#if DEBUG
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
#if DEBUG
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
        try
        {
#if DEBUG
            _capture.RecordTx(bytes);
#endif
            _stream?.Write(bytes, 0, bytes.Length);
        }
        catch { /* connection lost — read loop will handle */ }
    }

    private void WireSessionEvents()
    {
        _session.LineReady          += l => LineReady?.Invoke(l);
        _session.StatsUpdated       += s => StatsUpdated?.Invoke(s);
        _session.GameModeEntered    += () => GameModeEntered?.Invoke();
        _session.GameModeExited     += () => GameModeExited?.Invoke();
        _session.DreamwordChanged   += w => DreamwordChanged?.Invoke(w);
        _session.ClientModeReceived += d => ClientModeReceived?.Invoke(d);
    }
}
