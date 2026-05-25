using MudSharp.Models;
using MudSharp.Session;
using System.Net.Sockets;

namespace MudSharp.Transport;

/// <summary>
/// TCP connection to a MUD2 server. Drives a MudSession from a background read-loop task.
///
/// THREADING:
/// - ConnectAsync/DisconnectAsync are called from any thread.
/// - The read loop runs on a ThreadPool thread.
/// - MudSession events (LineReady, StatsUpdated, etc.) fire on the read-loop thread.
///   Consumers must marshal to their UI thread.
/// - OutgoingBytes from MudSession are sent synchronously on whichever thread raised them.
/// </summary>
public sealed class TcpMudConnection : IAsyncDisposable
{
    private readonly MudSession _session;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Task? _readLoop;
    private CancellationTokenSource? _cts;

    // ── Public events (forwarded from MudSession) ─────────────────────────────
    public event Action<StyledLine>? LineReady;
    public event Action<GameStatsSnapshot>? StatsUpdated;
    public event Action? GameModeEntered;
    public event Action? GameModeExited;
    public event Action<string?>? DreamwordChanged;
    public event Action<string>? ClientModeReceived;
    /// <summary>Fired when the connection is lost (read loop ended).</summary>
    public event Action<Exception?>? Disconnected;

    public bool IsConnected => _client?.Connected ?? false;
    public MudSession Session => _session;

    public TcpMudConnection(MudSessionOptions? options = null)
    {
        _session = new MudSession(options);
        WireSessionEvents();
    }

    /// <summary>Connect to the MUD2 server and start the read loop.</summary>
    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        _client = new TcpClient();
        await _client.ConnectAsync(host, port, cancellationToken);
        _stream = _client.GetStream();
        _cts = new CancellationTokenSource();

        // Wire outgoing bytes → network write
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
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _session.Dispose();
        _cts?.Dispose();
    }

    /// <summary>Send a line of text to the server.</summary>
    public void SendLine(string line) => _session.SendLine(line);

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
                _session.Feed(buf.AsSpan(0, read));
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
        try { _stream?.Write(bytes, 0, bytes.Length); }
        catch { _cts?.Cancel(); }
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
