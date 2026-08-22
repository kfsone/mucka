using MudSharp.Combat;
using MudSharp.Models;
using MudSharp.Session;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace Mucka.Core;

/// <summary>
/// Mucka's TCP connection layer. Owns the socket and read loop, wraps MudSession,
/// and intercepts raw RX/TX bytes before they reach the parser for optional session capture.
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
    // Set by DisconnectAsync() before it cancels the read loop, so ReadLoopAsync's finally can
    // tell "we asked for this" apart from the server actually dropping us, and skip firing
    // Disconnected (which drives the "server closed the connection" alert) for the former.
    // Cleared at the start of ConnectAsync() so a later, genuine drop on the new connection still
    // fires normally.
    private volatile bool _deliberateDisconnect;

    private readonly SessionCapture _capture = new();
    private readonly ClogWriter _clog = new(ClogPaths.GetClogDirectory());
    // Both write into the SAME database file (see CombatDb), each owning its own table and its own
    // background write connection. One file, so the analysis view can join a swing to the fight it
    // belonged to; separate connections, so neither writer can ever be blocked by the other.
    private static readonly string CombatDbPath =
        Path.Combine(ClogPaths.GetCombatDirectory(), CombatDb.DefaultFileName);

    private readonly FightHistoryStore _fightHistory = new(CombatDbPath, CrashLog.Write);
    private readonly FightHistoryRecorder _fightRecorder;
    // Always on, like the fight history and unlike clogging - see SwingLedger's remarks.
    private readonly SwingLedger _swingLedger = new(CombatDbPath, CrashLog.Write);

    // ── Public events (forwarded from MudSession) ─────────────────────────────
    public event Action<StyledLine>? LineReady;
    public event Action<GameStatsSnapshot>? StatsUpdated;
    /// <summary>The server's C08+C13 ("Not updating persona.") signal: permadeath wiped the
    /// current persona. Fires alongside <see cref="StatsUpdated"/>'s zeroed snapshot. Fires on
    /// the read-loop thread — consumers marshal to their UI thread.</summary>
    public event Action? PersonaWiped;
    /// <summary>The server's C06 C04 auto-reset announcement ("you have 120 seconds to finish up"):
    /// an exact statement that a reset is under way, used to classify the drop to the Option menu
    /// that follows. Fires on the read-loop thread — consumers marshal to their UI thread.</summary>
    public event Action? AutoResetInitiated;
    public event Action<StatusEffectState>? StatusEffectsChanged;
    /// <summary>Fires whenever combat is entered/left (see MudSharp.Combat.CombatTracker).</summary>
    public event Action<bool>? InCombatChanged;
    /// <summary>Fires whenever <see cref="IsInCombatGracePeriod"/> flips (see
    /// <see cref="ClogWriter.TailOnlyChanged"/>).</summary>
    public event Action<bool>? CombatGracePeriodChanged;
    /// <summary>Fires for every classified combat line while (or just as) InCombat.</summary>
    public event Action<CombatEvent>? CombatEventOccurred;
    public event Action? BellReceived;
    public event Action? GameModeEntered;
    public event Action? GameModeExited;
    /// <summary>The character in this session was identified from the setup <c>score</c> reply.
    /// Payload is the character name. Fires on the Feed thread — consumers marshal to the UI.</summary>
    public event Action<string>? CharacterIdentified;
    public event Action<string?>? DreamwordChanged;
    public event Action<string>? SoundRequested;
    /// <summary>A tell arrived from a named sender. Payload is the sender's screen name; drives the
    /// ctrl-r reply hotkey.</summary>
    public event Action<string>? TellReceived;
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
    /// <summary>Fired when a queued "sniff" value-probe resolves. Payload: probed name + outcome.
    /// Fires on the read-loop thread — consumers marshal to their UI thread.</summary>
    public event Action<string, SniffOutcome>? SniffResult;
    /// <summary>Fired when the connection is lost UNEXPECTEDLY (read loop ended on server EOF or an
    /// exception). Null = the loop ended without an exception (still unexpected -- the server
    /// closed its end). Never fires for a locally-initiated <see cref="DisconnectAsync"/> (cancelling
    /// the persona picker, tearing down the page, etc.) -- see <see cref="_deliberateDisconnect"/>.
    /// Consumers use this to show "the server closed the connection" style UI, so a deliberate,
    /// player/UI-driven close must not trip it.</summary>
    public event Action<Exception?>? Disconnected;
#if WINDOWS
    /// <summary>Fires on the read-loop thread with each raw chunk received from the server.</summary>
    public event Action<byte[]>? RawBytesReceived;
    /// <summary>Fires on the writer-task thread with each raw chunk about to be written to the server.</summary>
    public event Action<byte[]>? RawBytesSent;
#endif

    public bool IsConnected => _client?.Connected ?? false;
    public bool InGameMode => _session.InGameMode;

    public bool IsCapturing => _capture.IsRecording;
    public string? CaptureFilePath => _capture.FilePath;
    /// <summary>Write a free-text annotation into the active capture log.</summary>
    public void Annotate(string message) => _capture.Annotate(message);

    public bool InCombat => _session.InCombat;
    /// <summary>See <see cref="ClogWriter.IsTailOnly"/> — a clog is still draining its tail
    /// (trailing prose captured up to the next prompt) even though no encounter is actively
    /// live any more.</summary>
    public bool IsInCombatGracePeriod => _clog.IsTailOnly;

    /// <summary>The current merged stats snapshot — see <see cref="MudSharp.Session.MudSession.CurrentStats"/>.
    /// Read this for an immediate "whatever we currently know" value; do not subscribe to
    /// <see cref="StatsUpdated"/> and wait for the next event when a synchronous read will do, since
    /// that races the FES heartbeat's own cadence.</summary>
    public GameStatsSnapshot CurrentStats => _session.CurrentStats;
    public string? ClogFilePath => _clog.FilePath;

    /// <summary>The accumulated per-fight history index, for contrasting the current fight against
    /// prior ones. Unlike clogging this always records — see FightHistoryRecorder's remarks.</summary>
    public FightHistoryStore FightHistory => _fightHistory;

    /// <summary>Loads the fight-history index. Fire-and-forget from startup; must not be awaited on
    /// the UI thread (Invariant #1). Safe to call before any fight has been recorded.</summary>
    public Task LoadFightHistoryAsync(CancellationToken cancellationToken = default)
        => _fightHistory.LoadAsync(cancellationToken);

    /// <summary>The accumulated per-creature incoming-damage record, for the rail's "how hard does
    /// this thing hit" column. Fed by the swing ledger, which sees every blow already.</summary>
    public MudSharp.Combat.SwingDamageIndex SwingDamage => _swingLedger.Damage;

    /// <summary>Warms the damage cache from the database's own aggregate views. Fire-and-forget from
    /// startup, alongside <see cref="LoadFightHistoryAsync"/> and under the same rule: never awaited
    /// on the UI thread (Invariant #1). Must run AFTER the fight-history load, which is what performs
    /// the one-time legacy import - warming first would aggregate a table the old swings had not been
    /// moved into yet.</summary>
    public Task LoadSwingDamageAsync(CancellationToken cancellationToken = default)
        => _swingLedger.WarmDamageIndexAsync(cancellationToken);

    private int _windowCols;

    public MuckaConnection(string? accountId = null, string? password = null, int maxCols = 80, string loginName = "mud")
    {
        _windowCols = Math.Clamp(maxCols, 20, 160);
        _fightRecorder = new FightHistoryRecorder(_fightHistory);
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
        _deliberateDisconnect = false;   // fresh connection: a later drop is unexpected again

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

    /// <summary>Disconnect cleanly. This is always a deliberate, locally-initiated close (the
    /// player quitting, the UI tearing down, a reconnect cleaning up the previous attempt) -- it
    /// never fires <see cref="Disconnected"/>, so callers that want the player told the connection
    /// ended must say so themselves (see the guided-login re-entry cancel path in GamePage).</summary>
    public async Task DisconnectAsync()
    {
        _deliberateDisconnect = true;
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
        var host = string.IsNullOrWhiteSpace(hostOverride) ? _host : hostOverride!.Trim();
        if (string.IsNullOrWhiteSpace(host)) host = "unknown";
        return _capture.TryStart(host, out error);
    }

    public void StopCapture() => _capture.Stop();

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

    /// <summary>Latest reset-time projection (target instant + uncertainty + phase). Read by the 1 Hz
    /// countdown tick; the projection and its precision burst are driven entirely in the session layer.</summary>
    public ResetEstimate ResetEstimate => _session.ResetEstimate;

    /// <summary>The reset-time projection changed — optional immediate UI-refresh hint (also polled).</summary>
    public event Action? ResetEstimateChanged;

    /// <summary>When true (and a capture is active), each folded reset reading is written to the capture
    /// log as an annotation. Gated by the per-profile "log reset diagnostics" toggle.</summary>
    public bool LogResetDiagnostics { get; set; }

    /// <summary>Queue a "sniff" (value &lt;name&gt;) probe to ride the next FES heartbeat
    /// (see MudSession.QueueValueProbe). Used to disambiguate a player who left the Online list.</summary>
    public void QueueValueProbe(string name) => _session.QueueValueProbe(name);

    /// <summary>Mapping window focus changed -- suppresses heartbeat FEI while retaining FES+FEW
    /// (see MudSession.SetMappingFocus).</summary>
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

    /// <summary>
    /// Re-advertise the terminal width to the MUD shell mid-session — e.g. after a device
    /// rotation changes the usable column count. The server word-wraps game text on the /T
    /// value (set at client-mode entry), NOT on the telnet NAWS subnegotiation, so a resize
    /// that only updates NAWS leaves freshly-sent text wrapped at the old width. Wraps
    /// /T{cols} in a command interrupt (ESC-[ … ESC-]) so no newline is injected. No-op
    /// unless we are in game mode — /T is only meaningful once the in-game shell is active.
    /// Call after <see cref="SetWindowSize"/> so _windowCols already holds the new value.
    /// May be called from any thread.
    /// </summary>
    public void SendTerminalWidth()
    {
        if (!_session.InGameMode)
            return;

        // ESC-[  = begin command interrupt · /T{n} = MUD shell width command · ESC-] = end
        byte[] prefix = { 0x1B, 0x2D, 0x5B };
        byte[] colCmd = Encoding.ASCII.GetBytes($"/T{_windowCols}");
        byte[] suffix = { 0x1B, 0x2D, 0x5D };

        var widthSeq = new byte[prefix.Length + colCmd.Length + suffix.Length];
        Buffer.BlockCopy(prefix, 0, widthSeq, 0, prefix.Length);
        Buffer.BlockCopy(colCmd, 0, widthSeq, prefix.Length, colCmd.Length);
        Buffer.BlockCopy(suffix, 0, widthSeq, prefix.Length + colCmd.Length, suffix.Length);
        _session.Send(widthSeq);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _loginHandler?.Detach();
        // Order matters: _session.Dispose() force-closes any open encounter (MudSession.Dispose ->
        // CombatTracker.ForceEnd), and that cascades through the events wired in WireSessionEvents
        // to _fightRecorder.OnCombatEvent/OnInCombatChanged, which calls _store.Append(...) for every
        // fight that was still open. _fightRecorder.Dispose() right after is a belt-and-braces flush
        // (idempotent, see its remarks) in case that cascade is ever bypassed. Only once both have
        // had the chance to enqueue their rows does _fightHistory.Dispose() drain the store's
        // background writer to disk - disposing it any earlier could lose exactly the rows this
        // whole ordering exists to save.
        _session.Dispose();
        _fightRecorder.Dispose();
        _fightHistory.Dispose();
        // After _session.Dispose() for the same reason: the ledger enqueues a row per swing as the
        // event arrives (nothing is held back to flush at fight end), so the only rows still at risk
        // here are the ones already in its queue - which is exactly what this drains.
        _swingLedger.Dispose();
        _capture.Dispose();
        _clog.Dispose();
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
                _capture.RecordRx(buf.AsSpan(0, read));
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
            if (!_deliberateDisconnect)
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
                _capture.RecordTx(bytes);
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

    /// <summary>
    /// The encounter currently open, as a unix-ms id, or null between encounters.
    ///
    /// <para>Stamped HERE and handed to every consumer, rather than each computing its own. It is the
    /// join key between the fights and swings tables, and two consumers each calling UtcNow would
    /// produce two values microseconds apart - a join that silently matched nothing, in a way no test
    /// of either side alone would catch. One clock reading, one id.</para>
    /// </summary>
    private long? _encounterId;

    /// <summary>
    /// Client-detected combat alerts: sounds we choose to play from a parsed line, as opposed to the
    /// ones the server requests by FE code.
    ///
    /// <para>Only one so far, and it earns its place. A FAILED flee
    /// (<see cref="CombatEventKind.YouFleeFailed"/>) is the worst-value outcome in the game: MUD2
    /// charges the points, can demote the persona a whole experience level, drops the weapon out of
    /// the player's hands, ends every fight they were in - and leaves them standing exactly where they
    /// were, in front of whatever they were running from. Owner, on the frame that prompted this:
    /// "If I'd waited a heartbeat longer to qq, i'd have died."</para>
    ///
    /// <para>The text alone is a poor carrier for it. "You have fled by trying to go out." differs from
    /// the success line by two words, arrives in a frame alongside the drop and the persona-save
    /// lines, and lands at exactly the moment the player is reading fast and about to act on the
    /// belief that they got away. A buzzer is unambiguous without being read.</para>
    ///
    /// <para><b>Deliberately not gated on the combat rail being visible</b>, unlike the metronome. The
    /// metronome's only switch is drawn on the rail, so clicking away while the rail is hidden would
    /// give the player a noise they cannot find or silence; this is an alert about something that just
    /// happened TO them, and hiding a panel is not a request to stop being warned. It is gated on
    /// master mute and on its own catalogue entry ("Client alerts"), which is where it can be turned
    /// down or off.</para>
    ///
    /// <para>Runs on the Feed thread. <c>SoundService.PlayServerSound</c> is fire-and-forget and
    /// explicitly safe from a background thread (it is already called from the TCP thread), so no hop
    /// is taken - and none is wanted, since a warning delayed by a UI-thread queue is a warning that
    /// arrives after the keystroke it was meant to stop.</para>
    /// </summary>
    private static void OnCombatEventSound(CombatEvent combatEvent)
    {
        if (combatEvent.Kind == CombatEventKind.YouFleeFailed)
            Mucka.Audio.SoundService.PlayServerSound("sounds/flee-failed.wav");
    }

    private void OnSessionInCombatChanged(bool inCombat)
    {
        // UtcNow rather than a combat event's own stamp: this fires synchronously from the same
        // Feed-thread call that flips CombatTracker.InCombat, ahead of the FightStart event for the
        // SAME line (Begin() raises InCombatChanged before Observe() calls Emit()), so the two are
        // effectively the same instant anyway.
        _encounterId = inCombat ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : _encounterId;

        _clog.OnInCombatChanged(inCombat);
        _fightRecorder.OnInCombatChanged(inCombat, _encounterId);
        _swingLedger.OnInCombatChanged(inCombat, _encounterId);
        InCombatChanged?.Invoke(inCombat);

        // Cleared only AFTER both recorders have flushed - each stamps its final rows for the
        // encounter that just closed, and clearing first would strip the id off exactly the rows that
        // most need it.
        if (!inCombat)
            _encounterId = null;
    }

    private void WireSessionEvents()
    {
        _session.PersonaWiped       += () => PersonaWiped?.Invoke();
        _session.AutoResetInitiated += () => AutoResetInitiated?.Invoke();
        _session.LineReady          += l => { _clog.OnLineReady(l); LineReady?.Invoke(l); };
        _session.StatsUpdated       += s => { _clog.OnStatsUpdated(s); _fightRecorder.OnStatsUpdated(s); _swingLedger.OnStatsUpdated(s); StatsUpdated?.Invoke(s); };
        _session.StatusEffectsChanged += s => { _clog.OnStatusEffectsChanged(s); _fightRecorder.OnStatusEffectsChanged(s); _swingLedger.OnStatusEffectsChanged(s); StatusEffectsChanged?.Invoke(s); };
        _session.InCombatChanged     += OnSessionInCombatChanged;
        _clog.TailOnlyChanged        += v => CombatGracePeriodChanged?.Invoke(v);
        _session.CombatEventOccurred += e =>
        {
            _clog.OnCombatEvent(e); _fightRecorder.OnCombatEvent(e); _swingLedger.OnCombatEvent(e);
            OnCombatEventSound(e);
            CombatEventOccurred?.Invoke(e);
        };
        _session.BellReceived       += () => BellReceived?.Invoke();
        _session.GameModeEntered    += () => GameModeEntered?.Invoke();
        _session.GameModeExited     += () => GameModeExited?.Invoke();
        _session.CharacterIdentified += n => { _fightRecorder.OnCharacterIdentified(n); _swingLedger.OnCharacterIdentified(n); CharacterIdentified?.Invoke(n); };
        _session.DreamwordChanged   += w =>
        {
            if (w != null)
                _capture.Annotate($"dreamword detected: {w}");
            else
                _capture.Annotate("dreamword cleared");
            DreamwordChanged?.Invoke(w);
        };
        _session.SoundRequested     += s => SoundRequested?.Invoke(s);
        _session.TellReceived       += name => TellReceived?.Invoke(name);
        _session.FewPlayerReady     += (n, c) => FewPlayerReady?.Invoke(n, c);
        _session.FewListStarting    += () => FewListStarting?.Invoke();
        _session.FewListComplete    += () => FewListComplete?.Invoke();
        _session.RoomEntered        += () => RoomEntered?.Invoke();
        _session.RoomShortReady     += name => { _clog.OnRoomShortReady(name); _fightRecorder.OnRoomShortReady(name); RoomShortReady?.Invoke(name); };
        _session.FeiListStarting    += () => FeiListStarting?.Invoke();
        _session.FeiItemReady       += item => FeiItemReady?.Invoke(item);
        _session.FeiListComplete    += () => FeiListComplete?.Invoke();
        _session.FexListStarting    += () => FexListStarting?.Invoke();
        _session.FexItemReady       += item => FexItemReady?.Invoke(item);
        _session.FexListComplete    += () => FexListComplete?.Invoke();
        _session.ExitLineReady      += (dir, dest) => ExitLineReady?.Invoke(dir, dest);
        _session.ProbeSent          += () => FesProbeSent?.Invoke();
        _session.SniffResult        += (name, outcome) => SniffResult?.Invoke(name, outcome);
        _session.TerminalWidthConfirmed += OnTerminalWidthConfirmed;
        _session.ResetEstimateChanged   += () => ResetEstimateChanged?.Invoke();
        _session.ResetObservationRecorded += OnResetObservation;
        _session.ResetDiagnostic        += OnResetDiagnostic;
    }

    // Reset-projection incident (unanswered sample, lock contradiction, auto-reset anchor) → capture
    // log, when the per-profile toggle is on. Annotate() no-ops unless a capture is active.
    private void OnResetDiagnostic(string note)
    {
        if (LogResetDiagnostics)
            _capture.Annotate($"reset! {note}");
    }

    // Reset-projection diagnostics: append each folded reading to the capture log when the per-profile
    // toggle is on. Annotate() is a no-op unless a capture is active, so the flag is the only extra gate.
    private void OnResetObservation(ResetObservation o)
    {
        if (!LogResetDiagnostics) return;
        _capture.Annotate(
            $"reset {o.Phase} v={o.Minutes}{(o.Sample ? " sample" : "")} rtt={o.RttMs:F0}ms " +
            $"win=[{o.WindowLoSecFromNow:F2},{o.WindowHiSecFromNow:F2})s ±{o.UncertaintySec:F2}s");
    }

    private void OnTerminalWidthConfirmed(int confirmedWidth)
    {
        if (confirmedWidth != _windowCols)
            System.Diagnostics.Debug.WriteLine(
                $"[MuckaConnection] Terminal width mismatch: requested {_windowCols}, confirmed {confirmedWidth}");
        _capture.Annotate($"terminal width confirmed: {confirmedWidth}");
    }
}
