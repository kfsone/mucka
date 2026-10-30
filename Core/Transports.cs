using System.Net.Sockets;

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
        if (!useSsh)
            return TelnetTransport.OpenAsync(host, port, ct);
#if DESKTOP
        return SshTransport.OpenAsync(host, port, loginName, cols, rows, ct);
#else
        throw new NotSupportedException("SSH is not available on this platform.");
#endif
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

#if DESKTOP
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
