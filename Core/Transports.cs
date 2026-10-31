using System.Net.Sockets;
using System.Threading.Channels;
using Mucka.WireLog;

namespace Mucka.Core;

/// <summary>
/// The byte pipe <see cref="MuckaConnection"/> reads and writes: a telnet socket, or an SSH shell
/// channel. Bytes reach the parser unchanged either way; any telnet option negotiation in them is
/// the parser's business, not the transport's.
/// </summary>
internal interface IMudTransport : IDisposable
{
    /// <summary>The bytes. Anything written is flushed by the caller; an SSH shell stream holds
    /// writes in a buffer until it is.</summary>
    Stream Stream { get; }

    /// <summary>False once the link is gone or this transport is disposed. Never throws.</summary>
    bool IsConnected { get; }

    /// <summary>Tell the far end the window changed. Telnet does this in-band (NAWS), so its
    /// transport ignores the call. Returns at once; any thread.</summary>
    void Resize(int cols, int rows);
}

internal static class TransportFactory
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <param name="loginName">The SSH user. mud2.co.uk accepts the name with method "none" and then
    /// starts the game at the account-id prompt; telnet reaches the same prompt through a
    /// <c>login:</c> exchange instead.</param>
    public static Task<IMudTransport> OpenAsync(string host, int port, bool useSsh, string loginName,
        int cols, int rows, CancellationToken ct)
    {
        if (Muckrep.IsReplayHost(host))
            return ReplayTransport.OpenAsync(Muckrep.ReplayName(host), ct);
        if (!useSsh)
            return TelnetTransport.OpenAsync(host, port, ct);
#if SSH
        return SshTransport.OpenAsync(host, port, loginName, cols, rows, ct);
#else
        throw new NotSupportedException("SSH is not available on this platform.");
#endif
    }
}

/// <summary>
/// A replay file played as if it were a server (<c>replay:name</c> as the host; docs/replay.md): no
/// socket, so it works wherever the file can be found. The server's records arrive on the
/// <see cref="ReplayPlayer"/>'s schedule; whatever the client sends is accepted and dropped. When the
/// last record has played the link stays open, so the screen holds the end state until the player
/// quits.
/// </summary>
internal sealed class ReplayTransport : IMudTransport
{
    private readonly ReplayStream _stream = new();
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    private ReplayTransport(ReplayPlayer player)
    {
        Player = player;
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            try { await player.PlayAsync(_stream.EnqueueAsync, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { CrashLog.Write("ReplayPlay", ex); }
        }, CancellationToken.None);
    }

    /// <summary>The playback, whose <see cref="ReplayPlayer.Step"/> the replay bar's slider sets.</summary>
    public ReplayPlayer Player { get; }

    public Stream Stream => _stream;
    public bool IsConnected => Volatile.Read(ref _disposed) == 0;
    public void Resize(int cols, int rows) { }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _stream.Complete();
        _cts.Dispose();
    }

    public static async Task<IMudTransport> OpenAsync(string name, CancellationToken ct)
    {
        var records = await ReplayFiles.LoadAsync(name, ct).ConfigureAwait(false);
        return new ReplayTransport(new ReplayPlayer(records));
    }

    // The server's half of the link: chunks queued by the player, read by the connection's read
    // loop. Writes are the client's half, and go nowhere.
    private sealed class ReplayStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private byte[] _current = [];
        private int _offset;

        public ValueTask EnqueueAsync(byte[] chunk, CancellationToken ct) => _chunks.Writer.WriteAsync(chunk, ct);
        public void Complete() => _chunks.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (_offset >= _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    return 0;
                if (_chunks.Reader.TryRead(out var next))
                {
                    _current = next;
                    _offset = 0;
                }
            }
            var n = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, n).CopyTo(buffer);
            _offset += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => default;
        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>
/// Where a replay named in a <c>replay:</c> host is found: an absolute path as given; else
/// <see cref="MuckaPaths.GetReplayDirectory"/>, the player's own; on Android, else the app's external
/// files folder; else the replays shipped in the app package, which only the desktop builds carry.
/// The name may leave off <c>.muckrep</c>.
/// </summary>
internal static class ReplayFiles
{
    public static async Task<List<WireRecord>> LoadAsync(string name, CancellationToken ct)
    {
        if (name.Length == 0)
            throw new FileNotFoundException("A replay host needs a file name: replay:<name>.");
        var fileName = name.EndsWith(Muckrep.Extension, StringComparison.OrdinalIgnoreCase) ? name : name + Muckrep.Extension;

        if (Path.IsPathRooted(fileName))
            return await Task.Run(() => ReadFile(fileName), ct).ConfigureAwait(false);

        var searched = new List<string> { MuckaPaths.GetReplayDirectory() };
        var own = Path.Combine(MuckaPaths.GetReplayDirectory(), fileName);
        if (File.Exists(own))
            return await Task.Run(() => ReadFile(own), ct).ConfigureAwait(false);
#if ANDROID
        // The app's external files folder itself, not a folder under it: adb push reaches it on any
        // build, where the data directory needs a debuggable one. Observed on an API 36 emulator: a
        // folder made under it by `adb shell mkdir` is owned by the shell and the app is refused it.
        if (Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath is { } external)
        {
            searched.Add(external);
            var pushed = Path.Combine(external, fileName);
            if (File.Exists(pushed))
                return await Task.Run(() => ReadFile(pushed), ct).ConfigureAwait(false);
        }
#endif

        try
        {
            await using var packaged = await FileSystem.OpenAppPackageFileAsync("replays/" + fileName).ConfigureAwait(false);
            using var copy = new MemoryStream();
            await packaged.CopyToAsync(copy, ct).ConfigureAwait(false);
            copy.Position = 0;
            return Muckrep.Read(copy);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new FileNotFoundException($"No replay \"{name}\" in {string.Join(", ", searched)} or among the app's own.");
        }
    }

    private static List<WireRecord> ReadFile(string path)
    {
        using var file = File.OpenRead(path);
        return Muckrep.Read(file);
    }
}

internal sealed class TelnetTransport : IMudTransport
{
    private readonly TcpClient _client;

    private TelnetTransport(TcpClient client)
    {
        _client = client;
        Stream = client.GetStream();
    }

    public Stream Stream { get; }
    public bool IsConnected => _client.Connected;

    public void Resize(int cols, int rows) { }

    public void Dispose() => _client.Dispose();

    public static async Task<IMudTransport> OpenAsync(string host, int port, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            using var timeoutCts = new CancellationTokenSource(TransportFactory.ConnectTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await client.ConnectAsync(host, port, linkedCts.Token).ConfigureAwait(false);
            client.NoDelay = true;
            return new TelnetTransport(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

#if SSH
internal sealed class SshTransport : IMudTransport
{
    private readonly Renci.SshNet.SshClient _client;
    private readonly Renci.SshNet.ShellStream _shell;
    // Columns in the high half, rows in the low half. The newest wanted size wins, however many
    // Resize calls arrive while one is being sent; at most one task is ever queued for them.
    private long _wantedSize;
    private long _sentSize;
    private int _resizeScheduled;
    private int _disposed;

    private SshTransport(Renci.SshNet.SshClient client, Renci.SshNet.ShellStream shell, int cols, int rows)
    {
        _client = client;
        _shell = shell;
        // The server's C1 codes end in 0xFF, which telnetd doubles and sshd does not; the parser
        // is written against the doubled form.
        Stream = new MudSharp.Protocol.IacEscapingStream(shell);
        _wantedSize = _sentSize = Pack(cols, rows);
    }

    public Stream Stream { get; }

    // CanWrite is false once the shell stream is disposed, which it does itself when the server
    // closes the channel; SshClient.IsConnected throws once the client is disposed.
    public bool IsConnected
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0 || !_shell.CanWrite) return false;
            try { return _client.IsConnected; }
            catch (ObjectDisposedException) { return false; }
        }
    }

    public void Resize(int cols, int rows)
    {
        Volatile.Write(ref _wantedSize, Pack(cols, rows));
        if (Interlocked.Exchange(ref _resizeScheduled, 1) == 0)
            _ = Task.Run(SendWantedSizes);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _shell.Dispose(); }
        finally { _client.Dispose(); }
    }

    private static long Pack(int cols, int rows) => ((long)cols << 32) | (uint)rows;

    private void SendWantedSizes()
    {
        while (true)
        {
            var wanted = Volatile.Read(ref _wantedSize);
            if (wanted != _sentSize)
            {
                // Advanced even when nothing is sent, or a disposed transport would loop here.
                _sentSize = wanted;
                if (Volatile.Read(ref _disposed) == 0)
                {
                    try
                    {
                        _shell.ChangeWindowSize((uint)(wanted >> 32), (uint)(wanted & uint.MaxValue), 0, 0);
                    }
                    catch
                    {
                        // The link is going or gone; the read loop reports that.
                    }
                }
            }
            // A full fence, not a volatile write: the read of _wantedSize below must not move ahead
            // of the flag clearing, or a Resize landing between the two is never sent.
            Interlocked.Exchange(ref _resizeScheduled, 0);
            // A Resize that landed after the read above but before the flag cleared saw the flag set
            // and queued nothing, so look again.
            if (Volatile.Read(ref _wantedSize) == Volatile.Read(ref _sentSize)
                || Interlocked.Exchange(ref _resizeScheduled, 1) != 0)
                return;
        }
    }

    public static async Task<IMudTransport> OpenAsync(string host, int port, string loginName,
        int cols, int rows, CancellationToken ct)
    {
        var user = string.IsNullOrWhiteSpace(loginName) ? "mud" : loginName.Trim();
        // SSH.NET insists on at least one configured method. It offers "none" first on its own, and
        // that is the one mud2.co.uk accepts.
        var info = new Renci.SshNet.ConnectionInfo(host, port, user,
            new Renci.SshNet.PasswordAuthenticationMethod(user, string.Empty))
        {
            Timeout = TransportFactory.ConnectTimeout,
        };
        var client = new Renci.SshNet.SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        // Operator rule: any host key is accepted.
        client.HostKeyReceived += (_, e) => e.CanTrust = true;
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
            // With ECHO off the game still echoes a typed line, byte for byte as it does over telnet.
            var modes = new Dictionary<Renci.SshNet.Common.TerminalModes, uint>
            {
                [Renci.SshNet.Common.TerminalModes.ECHO] = 0,
                [Renci.SshNet.Common.TerminalModes.ICRNL] = 0,
            };
            var shell = client.CreateShellStream("ansi", (uint)cols, (uint)rows, 0, 0, 4096, modes);
            // Observed: when the socket drops, ErrorOccurred fires and IsConnected goes false, but a
            // read already pending on the shell stream stays blocked, neither returning nor throwing,
            // and cancelling its token does not release it either. Disposing the stream does (it
            // returns 0), which is what ends the read loop and reports the drop. The shell only: this
            // runs on the SSH library's own thread, which disposing the client would wait on.
            client.ErrorOccurred += (_, _) =>
            {
                try { shell.Dispose(); } catch { }
            };
            // The same for the shell stream's Closed event, which SSH.NET documents as "Occurs when
            // the channel was closed". Observed on an iPad over SSH: the server sent "Hangup" at the
            // persona name prompt, nothing followed, and the session was not reported as ended.
            shell.Closed += (_, _) =>
            {
                try { shell.Dispose(); } catch { }
            };
            return new SshTransport(client, shell, cols, rows);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
#endif
