using System.Threading.Channels;

namespace Mucka.Commands;

/// <summary>
/// The one writer of log lines to a text stream (stderr in the app). <see cref="Enqueue"/> is a
/// non-blocking <c>TryWrite</c> onto a bounded channel that drops when full; a single background task
/// does the writing, and the next line it writes after a drop is preceded by a line giving the count.
/// A stream that throws ends the writing and later lines are dropped.
/// </summary>
public sealed class ConsoleLogSink
{
    private readonly Channel<string> _lines;
    private readonly Func<TextWriter> _target;
    private readonly Lazy<Task> _drain;
    private long _accepted;
    private long _written;
    private long _dropped;
    private volatile bool _broken;

    public ConsoleLogSink(Func<TextWriter> target, int capacity = 512)
    {
        _target = target;
        _lines = Channel.CreateBounded<string>(
            new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _drain = new Lazy<Task>(() => Task.Run(DrainAsync));
    }

    public void Enqueue(string line)
    {
        try
        {
            _ = _drain.Value;
            if (_lines.Writer.TryWrite(line))
                Interlocked.Increment(ref _accepted);
            else
                Interlocked.Increment(ref _dropped);
        }
        catch { }
    }

    /// <summary>Waits until every line accepted so far has been written, or <paramref name="timeout"/>
    /// passes, or the stream has failed. The sink keeps accepting lines afterwards.</summary>
    public bool Flush(TimeSpan timeout)
    {
        try
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!_broken && Interlocked.Read(ref _written) < Interlocked.Read(ref _accepted))
            {
                if (DateTime.UtcNow >= deadline) return false;
                Thread.Sleep(1);
            }
            return !_broken;
        }
        catch { return false; }
    }

    /// <summary>Stops accepting lines and completes once everything queued has been written.</summary>
    public Task CompleteAsync()
    {
        _lines.Writer.TryComplete();
        return _drain.Value;
    }

    /// <summary>Stops accepting lines and waits up to <paramref name="timeout"/> for the queue to empty.</summary>
    public void Complete(TimeSpan timeout)
    {
        try { CompleteAsync().Wait(timeout); }
        catch { }
    }

    private async Task DrainAsync()
    {
        try
        {
            var writer = _target();
            await foreach (var line in _lines.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0)
                    writer.WriteLine(LogLine.Format(DateTimeOffset.Now, LogLevel.Warn, "log", $"{dropped} log lines dropped"));
                writer.WriteLine(line);
                Interlocked.Increment(ref _written);
            }
        }
        catch
        {
            _broken = true;
            _lines.Writer.TryComplete();
        }
    }
}
