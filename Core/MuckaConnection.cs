using MudSharp.Models;
using MudSharp.Session;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace Mucka.Core;

/// <summary>
/// Mucka's TCP connection layer. Owns the socket and read loop, wraps MudSession,
/// and (in DEBUG builds) intercepts raw RX/TX bytes before they reach the parser.
///
/// THREADING:
/// - ConnectAsync/DisconnectAsync are called from any thread.
/// - The read loop runs on a ThreadPool thread.
/// - The write loop runs on a ThreadPool thread and drains queued outbound bytes in order.
/// - MudSession events (LineReady, StatsUpdated, etc.) fire on the read-loop thread.
///   Consumers must marshal to their UI thread.
/// - OutgoingBytes from MudSession are enqueued on the caller thread and written asynchronously.
/// </summary>
public sealed class MuckaConnection : IAsyncDisposable
{
    private readonly MudSession _session;
    private MudLoginHandler? _loginHandler;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Task? _readLoop;
    private Task? _writerTask;
    private CancellationTokenSource? _cts;
    private Channel<byte[]>? _sendChannel;
    private string _host = string.Empty;

#if DEBUG || WINDOWS
    private readonly SessionCapture _capture = new();
#endif

    // ── Public events (forwarded from MudSession) ─────────────────────────────
    public event Action<StyledLine>? LineReady;
    public event Action<GameStatsSnapshot>? StatsUpdated;
    public event Action? BellReceived;
    public event Action? GameModeEntered;
    public event Action? GameModeExited;
    public event Action<string?>? DreamwordChanged;
    public event Action<string>? SoundRequested;
    public event Action<string, AnsiColor>? FewPlayerReady;
    /// <summary>Fired when a FEW-response context opens (C12+C08+C05). Start accumulating names.</summary>
    public event Action? FewListStarting;
    /// <summary>Fired when the FEW-response context closes — all names delivered. Replace the visible list now.</summary>
    public event Action? FewListComplete;
    /// <summary>Fired when a room short (C02+C01) appears at frame start — player is at or has entered a room.</summary>
    public event Action? RoomEntered;
    /// <summary>Fired when a room short description line is received (LT_GREEN foreground). Payload is the room name.</summary>
    public event Action<string>? RoomShortReady;
    /// <summary>Fired when a FEI-response context opens. Start accumulating items.</summary>
    public event Action? FeiListStarting;
    /// <summary>Fired for each item line in the FEI response. "========" is the room/carry separator.</summary>
    public event Action<string>? FeiItemReady;
    /// <summary>Fired when the FEI-response context closes — all items delivered.</summary>
    public event Action? FeiListComplete;
    /// <summary>Fired when a FEX-response context opens. Start accumulating exit keywords.</summary>
    public event Action? FexListStarting;
    /// <summary>Fired for each exit keyword in the FEX response.</summary>
    public event Action<string>? FexItemReady;
    /// <summary>Fired when the FEX-response context closes — all exit keywords delivered.</summary>
    public event Action? FexListComplete;
    /// <summary>An exits-verb line "direction: Destination." was parsed. Payload: (direction, destination name).</summary>
    public event Action<string, string>? ExitLineReady;
    /// <summary>Fired when a FES/FEW/FEI probe interrupt was just sent -- its response
    /// (ending in a prompt redraw) is about to contend with anything else on the wire.</summary>
    public event Action? FesProbeSent;
    /// <summary>Fired when the connection is lost (read loop ended). Null = clean disconnect.</summary>
    public event Action<Exception?>? Disconnected;
#if WINDOWS
    /// <summary>Fires on the read-loop thread with each raw chunk received from the server.</summary>
    public event Action<byte[]>? RawBytesReceived;
    /// <summary>Fires on the writer-task thread with each raw chunk about to be written to the server.</summary>
    public event Action<byte[]>? RawBytesSent;
#endif

    public bool IsConnected => _client?.Connected ?? false;
    public bool InGameMode => _session.InGameMode;

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

    public MuckaConnection(string? accountId = null, string? password = null, int maxCols = 80, string loginName = "mud")
    {
        _windowCols = Math.Clamp(maxCols, 20, 160);
        _session = new MudSession();
        _session.SetWindowSize(_windowCols, 21);
        _session.SetLoginUser(loginName);
        WireSessionEvents();
        if (!string.IsNullOrEmpty(accountId))
            _loginHandler = new MudLoginHandler(this, loginName, accountId, password ?? string.Empty);
    }

    /// <summary>Connect to the server and start the read loop.</summary>
    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        _host = host;
        var client = new TcpClient();
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            await client.ConnectAsync(host, port, linkedCts.Token).ConfigureAwait(false);
            client.NoDelay = true;

            var stream = client.GetStream();
            var cts = new CancellationTokenSource();
            var sendChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            _client = client;
            _stream = stream;
            _cts = cts;
            _sendChannel = sendChannel;

            // Wire outgoing bytes → queued network write (+ capture if active)
            _session.OutgoingBytes -= EnqueueBytes;
            _session.OutgoingBytes += EnqueueBytes;

            _writerTask = Task.Run(() => WriteLoopAsync(stream, sendChannel.Reader, cts.Token));
            _readLoop = Task.Run(() => ReadLoopAsync(stream, cts.Token));
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Disconnect cleanly.</summary>
    public async Task DisconnectAsync()
    {
        _session.OutgoingBytes -= EnqueueBytes;

        var writerTask = _writerTask;
        var readLoop = _readLoop;
        var sendChannel = _sendChannel;
        var cts = _cts;
        var stream = _stream;
        var client = _client;

        _writerTask = null;
        _readLoop = null;
        _sendChannel = null;
        _cts = null;
        _stream = null;
        _client = null;

        sendChannel?.Writer.TryComplete();
        cts?.Cancel();

        if (writerTask != null)
            await writerTask.ConfigureAwait(false);
        if (readLoop != null)
            await readLoop.ConfigureAwait(false);

        stream?.Dispose();
        client?.Dispose();
        cts?.Dispose();
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
    /// Sends the terminal-width MUD shell command: ESC-[ /T{cols} ESC-]
    /// Used to notify the server of a column-count change mid-session.
    /// </summary>
    public void SendTerminalWidth(int cols)
    {
        byte[] prefix = { 0x1B, 0x2D, 0x5B };
        byte[] colCmd = Encoding.ASCII.GetBytes($"/T{cols}");
        byte[] suffix = { 0x1B, 0x2D, 0x5D };
        var seq = new byte[prefix.Length + colCmd.Length + suffix.Length];
        Buffer.BlockCopy(prefix, 0, seq, 0, prefix.Length);
        Buffer.BlockCopy(colCmd, 0, seq, prefix.Length, colCmd.Length);
        Buffer.BlockCopy(suffix, 0, seq, prefix.Length + colCmd.Length, suffix.Length);
        _session.Send(seq);
    }

    /// <summary>
    /// Update the FES stats-update heartbeat interval. Zero disables the heartbeat.
    /// Takes effect immediately if already in game mode.
    /// </summary>
    public void SetFesInterval(int seconds)
        => _session.UpdateFesInterval(seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds));

    /// <summary>
    /// Update which components are included in the periodic heartbeat probe.
    /// When <paramref name="includeFew"/> is false the online list (FEW) is omitted.
    /// When <paramref name="includeFei"/> is false the inventory/room-items list (FEI) is omitted.
    /// </summary>
    public void UpdateSubscriptionOptions(bool includeFew, bool includeFei)
        => _session.UpdateSubscriptionOptions(includeFew, includeFei);

    /// <summary>Hold/release the FES/FEW/FEI probe machinery (see MudSession.SetProbeHold).</summary>
    public void SetProbeHold(bool held) => _session.SetProbeHold(held);

    /// <summary>Mapping window focus changed -- collapses the heartbeat to FEW-only while
    /// focused (see MudSession.SetMappingFocus).</summary>
    public void SetMappingFocus(bool focused) => _session.SetMappingFocus(focused);

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
        await DisconnectAsync().ConfigureAwait(false);
        _loginHandler?.Detach();
        _session.Dispose();
#if DEBUG || WINDOWS
        _capture.Dispose();
#endif
    }

    // ── Private ────────────────────────────────────────────────────────────────

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[4096];
        Exception? error = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
                if (read == 0) break; // server closed connection
#if DEBUG || WINDOWS
                _capture.RecordRx(buf.AsSpan(0, read));
#endif
#if WINDOWS
                RawBytesReceived?.Invoke(buf[..read]);
#endif
                // Feed raw bytes into MudSession AFTER capturing — parser sees unmodified bytes.
                _session.Feed(buf.AsSpan(0, read));
                // In pre-game mode, surface partial lines (e.g. "Account ID:") that arrive
                // without a trailing newline. In game mode, the C01+C02 protocol owns prompt
                // emission; unconditional EmitPartial would fragment lines split across packets.
                if (!_session.InGameMode)
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

    private async Task WriteLoopAsync(NetworkStream stream, ChannelReader<byte[]> reader, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var bytes = await reader.ReadAsync(ct).ConfigureAwait(false);
#if DEBUG || WINDOWS
                _capture.RecordTx(bytes);
#endif
#if WINDOWS
                RawBytesSent?.Invoke(bytes);
#endif
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
        catch
        {
            try { _cts?.Cancel(); } catch { }
            try { _client?.Close(); } catch { }
        }
    }

    private void EnqueueBytes(byte[] bytes)
    {
        var writer = _sendChannel?.Writer;
        if (writer == null)
            return;

        _ = writer.TryWrite(bytes.ToArray());
    }

    private void WireSessionEvents()
    {
        _session.LineReady          += l => LineReady?.Invoke(l);
        _session.StatsUpdated       += s => StatsUpdated?.Invoke(s);
        _session.BellReceived       += () => BellReceived?.Invoke();
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
        _session.SoundRequested     += s => SoundRequested?.Invoke(s);
        _session.FewPlayerReady     += (n, c) => FewPlayerReady?.Invoke(n, c);
        _session.FewListStarting    += () => FewListStarting?.Invoke();
        _session.FewListComplete    += () => FewListComplete?.Invoke();
        _session.RoomEntered        += () => RoomEntered?.Invoke();
        _session.RoomShortReady     += name => RoomShortReady?.Invoke(name);
        _session.FeiListStarting    += () => FeiListStarting?.Invoke();
        _session.FeiItemReady       += item => FeiItemReady?.Invoke(item);
        _session.FeiListComplete    += () => FeiListComplete?.Invoke();
        _session.FexListStarting    += () => FexListStarting?.Invoke();
        _session.FexItemReady       += item => FexItemReady?.Invoke(item);
        _session.FexListComplete    += () => FexListComplete?.Invoke();
        _session.ExitLineReady      += (dir, dest) => ExitLineReady?.Invoke(dir, dest);
        _session.ProbeSent          += () => FesProbeSent?.Invoke();
        _session.TerminalWidthConfirmed += OnTerminalWidthConfirmed;
    }

    private void OnTerminalWidthConfirmed(int confirmedWidth)
    {
        if (confirmedWidth != _windowCols)
            System.Diagnostics.Debug.WriteLine(
                $"[MuckaConnection] Terminal width mismatch: requested {_windowCols}, confirmed {confirmedWidth}");
#if DEBUG || WINDOWS
        _capture.Annotate($"terminal width confirmed: {confirmedWidth}");
#endif
    }
}
