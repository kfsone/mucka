using Mucka.Commands;

namespace Mucka.Util.Tests;

public class LogLineTests
{
    private static readonly DateTimeOffset s_when = new(2026, 10, 14, 9, 8, 7, 6, TimeSpan.Zero);

    [Fact]
    public void FormatsTimestampLevelCategoryMessage()
        => Assert.Equal("2026-10-14T09:08:07.006+00:00 WARN [Sound] missing asset x.wav",
            LogLine.Format(s_when, LogLevel.Warn, "Sound", "missing asset x.wav"));

    [Fact]
    public void MultiLineMessageStaysOnOneLine()
    {
        var line = LogLine.Format(s_when, LogLevel.Error, "Ctx", "System.Exception: boom\r\n   at A.B()\n   at C.D()\0");
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\0', line);
        Assert.EndsWith("ERROR [Ctx] System.Exception: boom |    at A.B() |    at C.D()", line);
    }
}

public class ConsoleLogSinkTests
{
    private sealed class ThrowingWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("closed");
    }

    [Fact]
    public async Task WritesQueuedLinesInOrder()
    {
        var target = new StringWriter();
        var sink = new ConsoleLogSink(() => target);
        sink.Enqueue("a");
        sink.Enqueue("b");
        await sink.CompleteAsync();
        Assert.Equal("a" + Environment.NewLine + "b" + Environment.NewLine, target.ToString());
    }

    [Fact]
    public async Task UnwritableTargetNeitherThrowsNorBlocks()
    {
        var sink = new ConsoleLogSink(() => new ThrowingWriter());
        sink.Enqueue("a");
        await sink.CompleteAsync();
        sink.Enqueue("b");
    }
}
