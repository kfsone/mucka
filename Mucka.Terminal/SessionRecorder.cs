using System.Globalization;
using System.Text;
using System.Threading.Channels;
using MudSharp.Models;

namespace Mucka.Terminal;

/// <summary>
/// The session transcript: what was on screen, as plain text, in
/// <c>session-rec.{host}.{yyyyMMdd-HHmmss}.txt</c>. Hand-armed - see docs/session-rec-design.md.
///
/// <para><b>The screen, not the socket.</b> The byte-exact record is the wire log, which is always
/// on and stores raw bytes. This is the readable half: MUD2's markup resolved away, one line per
/// line of screen. Where both record the same server line and they disagree, the transcript is the
/// one that is wrong. The transcript also carries lines <c>wire</c> has no reason to hold:
/// <see cref="Inject"/> writes client-side annotations by design, so a line present here and absent
/// there is not by itself a phantom.</para>
///
/// <para><b>Input is in here because MUD2 echoes it.</b> The server sends a typed command back
/// (verified on the wire: session 14 record 119965 is Tx <c>wave</c>, 119966 is Rx <c>wave</c>), so
/// the command is already a line of screen, already merged onto the prompt row, and already carries
/// whatever the alias and F-key expansion turned it into.</para>
///
/// <para>The login exchange is echoed the same way - the account ID comes back, the password does
/// not (session 14: 119941 echoes <c>Z00012305</c>, 119942's password draws no echo at all). So a
/// transcript armed before login carries the account ID in clear and never the password. The
/// operator's ruling is that it is not masked.</para>
///
/// <para><b>What one line is.</b> Delegated entirely to <see cref="TerminalBuffer"/>, which owns the
/// partial-replaces-partial, echo-merges-into-prompt and form-feed-clears rules. Writing a line per
/// <see cref="StyledLine"/> instead would put a copy of every prompt in the file, since a prompt is
/// a partial line that is replaced until its echo finishes it.</para>
///
/// <para><b>Threading.</b> <see cref="Append"/> is called from the TCP read loop and from the UI
/// thread (a client-side system line takes the same path); <see cref="Inject"/> from the UI thread.
/// The lock covers the buffer's state machine and a <c>TryWrite</c> onto an unbounded channel - no
/// I/O. One background task owns the writing, which is <c>AutoFlush</c>: the file is meant to survive
/// a crash mid-session, which is most of why it exists. The opening line is written in the
/// constructor, before that task starts.</para>
///
/// <para><b>Known skew.</b> Lines are recorded as they arrive off the socket, but reach the pane when
/// the UI thread next drains them; <see cref="Inject"/> is called from the UI thread directly. An
/// annotation raised in that gap lands in the file on the other side of the line it followed on
/// screen. The window is one dispatcher tick.</para>
/// </summary>
public sealed class SessionRecorder : IAsyncDisposable
{
    private readonly object _lock = new();
    // Cap 1: this buffer is a state machine, not scrollback. Committed lines go to the file and are
    // never read back.
    private readonly TerminalBuffer _screen = new(cap: 1);
    private readonly Channel<string> _queue =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TextWriter _writer;
    private readonly Task _pump;
    private readonly string _host;
    private readonly Action<string>? _failed;
    private bool _stopped;

    /// <summary>
    /// Creates the file and writes the opening line. Throws if it cannot be created - the caller
    /// surfaces the message rather than letting it die on a background thread.
    /// </summary>
    /// <param name="failed">Raised if a write fails after this returns. Without it the file would
    /// stop and the chip would stay lit: the operator would go on believing he was recording. Fires
    /// on the writer task - marshal before touching the UI.</param>
    public SessionRecorder(string filePath, string host, DateTime startedLocal,
        Action<string>? failed = null)
        : this(filePath, host, startedLocal, failed, OpenFile(filePath))
    {
    }

    /// <summary>Takes the writer instead of opening one, so a test can supply a writer that fails on
    /// demand. That path is the whole reason <see cref="Failure"/> and the <c>failed</c> callback
    /// exist, and it cannot be reached through a real file.</summary>
    internal SessionRecorder(string filePath, string host, DateTime startedLocal,
        Action<string>? failed, TextWriter writer)
    {
        Location = filePath;
        _failed = failed;
        _host = string.IsNullOrWhiteSpace(host) ? "unknown" : host;
        _writer = writer;
        try
        {
            _writer.WriteLine($"Recording by Mucka from {_host} starting on {Stamp(startedLocal)}.");
            _writer.WriteLine();
        }
        catch
        {
            // The file is already open. Without this the handle survives until finalization and the
            // name stays locked, so the retry a second later fails on a file this process holds.
            _writer.Dispose();
            throw;
        }
        _screen.LineCommitted += OnLineCommitted;
        _pump = Task.Run(PumpAsync);
    }

    private static StreamWriter OpenFile(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        return new StreamWriter(filePath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
    }

    /// <summary>Where the transcript is being written - shown to the player when it is armed.</summary>
    public string Location { get; }

    /// <summary>Completes when the queue has drained and the file is closed. Never faults: a write
    /// failure is reported through the constructor's <c>failed</c> callback and ends the recording,
    /// so awaiting this cannot throw into a caller's teardown path.</summary>
    public Task Completion => _pump;

    /// <summary>The write failure that ended this recording, or null. Held as well as raised so a
    /// failure is still answerable after the fact.</summary>
    public string? Failure { get; private set; }

    /// <summary>The conventional file name: <c>session-rec.{host}.{yyyyMMdd-HHmmss}.txt</c>, with
    /// anything illegal in the host replaced.</summary>
    public static string BuildFileName(string host, DateTime localTimestamp)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        var safeHost = new string((string.IsNullOrWhiteSpace(host) ? "unknown" : host)
            .Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return $"session-rec.{safeHost}.{localTimestamp:yyyyMMdd-HHmmss}.txt";
    }

    /// <summary>One line off the stream, on its way to the screen. Sanitised and tab-expanded the way
    /// the renderer does it (see TerminalView.AppendLines) so the file and the pane agree.</summary>
    public void Append(StyledLine line)
    {
        lock (_lock)
        {
            if (_stopped) return;
            _screen.Append(TerminalText.ExpandTabs(TerminalText.Sanitize(line)));
        }
    }

    /// <summary>A client-side note shown above the live prompt rather than merged into it - the
    /// <c>$f&lt;n&gt;</c> F-key annotations, which reach the pane through a path of their own.</summary>
    public void Inject(StyledLine line)
    {
        lock (_lock)
        {
            if (_stopped) return;
            _screen.InjectAbovePartial(TerminalText.ExpandTabs(TerminalText.Sanitize(line)));
        }
    }

    /// <summary>Writes the closing line and stops accepting input. Idempotent, and safe to call while
    /// another thread is inside <see cref="Append"/>: the flag is set under the same lock the
    /// committing path holds, so nothing can queue a line after the footer. A line whose Append is
    /// waiting on the lock at that moment is dropped.
    ///
    /// <para>Returns without waiting for the file to close - every line already reached disk as it
    /// was written. Use <see cref="DisposeAsync"/> when the handle must be released first.</para></summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_stopped) return;
            _stopped = true;
            // A live prompt is on screen but was never committed. It belongs in the transcript -
            // the player was looking at it when they stopped recording. There is nothing to write
            // when a form feed arrived last: TerminalBuffer.Append clears the partial on one, along
            // with any text that shared its line.
            if (_screen.Partial is { } partial && partial.PlainText.Length > 0)
                _queue.Writer.TryWrite(partial.PlainText);
            // The footer and the completion are queued under the SAME lock the committing path holds,
            // and that is what puts the footer last. With them outside it, an Append waiting on the
            // lock would acquire it after the flag was set but before the queue closed, and its line
            // would land after the line that says the recording ended. All of this is a TryWrite onto
            // an unbounded channel - no I/O inside the lock.
            _queue.Writer.TryWrite(string.Empty);
            _queue.Writer.TryWrite(
                $"Recording by Mucka from {_host} finished on {Stamp(DateTime.Now)}.");
            _queue.Writer.TryComplete();
        }
    }

    /// <summary>Ends the recording and waits for the file to close. <see cref="Stop"/> on its own is
    /// enough to make the transcript complete on disk; this is for callers that need the handle
    /// released before they return.</summary>
    public async ValueTask DisposeAsync()
    {
        Stop();
        await _pump.ConfigureAwait(false);
    }

    private void OnLineCommitted(StyledLine line) => _queue.Writer.TryWrite(line.PlainText);

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var line in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                await _writer.WriteLineAsync(line).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A write that fails (full disk, a network path that went away) must end the recording
            // rather than fault this task: nothing awaits it in the running client, so the failure
            // would be silent, the chip would stay lit, and every later line would pile up in a
            // channel with no reader. Stop() below closes the queue, so Append stops accepting.
            Failure = ex.Message;
            _failed?.Invoke(ex.Message);
        }
        finally
        {
            // Everything here is caught, without exception. Teardown awaits this task after closing
            // the store, so a throw escaping it would take the wire log's close down with it - and
            // "Completion never faults" has to be a property of the code, not of the current
            // contents of Stop().
            try
            {
                Stop();
                // Drain whatever Stop queued (or whatever was left when the write failed) so the
                // reader is complete and nothing stays referenced.
                while (_queue.Reader.TryRead(out _)) { }
            }
            catch (Exception ex) { Failure ??= ex.Message; }
            try { await _writer.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Failure ??= ex.Message; }
        }
    }

    // The transcript is a record format, not a readout: the stamp reads the same on every machine
    // that opens the file, so the culture is fixed rather than the reader's.
    private static string Stamp(DateTime local) =>
        local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
