using System.Threading.Channels;

namespace Mucka.Commands;

/// <summary>
/// The one writer of log lines to a text stream (stderr in the app). <see cref="Enqueue"/> is a
/// non-blocking <c>TryWrite</c> onto a bounded channel that drops when full; a single background task
/// does the writing. A stream that throws ends the writing and later lines are dropped.
/// </summary>
public sealed class ConsoleLogSink
{
    private readonly Channel<string> _lines = Channel.CreateBounded<string>(
        new BoundedChannelOptions(512) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Func<TextWriter> _target;
    private readonly Lazy<Task> _drain;

    public ConsoleLogSink(Func<TextWriter> target)
    {
        _target = target;
        _drain = new Lazy<Task>(() => Task.Run(DrainAsync));
    }

    public void Enqueue(string line)
    {
        try
        {
            _ = _drain.Value;
            _lines.Writer.TryWrite(line);
        }
        catch { }
    }

    /// <summary>Stops accepting lines and completes once everything queued has been written.</summary>
    public Task CompleteAsync()
    {
        _lines.Writer.TryComplete();
        return _drain.Value;
    }

    private async Task DrainAsync()
    {
        try
        {
            var writer = _target();
            await foreach (var line in _lines.Reader.ReadAllAsync().ConfigureAwait(false))
                writer.WriteLine(line);
        }
        catch
        {
            _lines.Writer.TryComplete();
        }
    }
}
