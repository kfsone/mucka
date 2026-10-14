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

    [Fact]
    public void EveryControlCharacterBecomesASpace()
    {
        var line = LogLine.Format(s_when, LogLevel.Info, "C\tat", "a\u001b[31mb\u0007c\u007fd");
        Assert.EndsWith("INFO [C at] a [31mb c d", line);
    }

    [Fact]
    public void NullCategoryAndMessageAreEmpty()
        => Assert.EndsWith("INFO [] ", LogLine.Format(s_when, LogLevel.Info, null, null));
}

public class ConsoleLogSinkTests
{
    private sealed class ThrowingWriter : StringWriter
    {
        public int Calls;
        public override void WriteLine(string? value) { Calls++; throw new IOException("closed"); }
    }

    private sealed class BlockingWriter : StringWriter
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new();
        private int _first = 1;
        public override void WriteLine(string? value)
        {
            if (Interlocked.Exchange(ref _first, 0) == 1) { Entered.Set(); Release.Wait(); }
            base.WriteLine(value);
        }
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
        var target = new ThrowingWriter();
        var sink = new ConsoleLogSink(() => target);
        sink.Enqueue("a");
        await sink.CompleteAsync();
        sink.Enqueue("b");
        Assert.Equal(1, target.Calls);
        Assert.False(sink.Flush(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void FlushWaitsForWrittenLinesAndKeepsAccepting()
    {
        var target = new StringWriter();
        var sink = new ConsoleLogSink(() => target);
        sink.Enqueue("a");
        Assert.True(sink.Flush(TimeSpan.FromSeconds(5)));
        Assert.Equal("a" + Environment.NewLine, target.ToString());
        sink.Enqueue("b");
        Assert.True(sink.Flush(TimeSpan.FromSeconds(5)));
        Assert.EndsWith("b" + Environment.NewLine, target.ToString());
    }

    [Fact]
    public async Task DroppedLinesAreCountedAndReported()
    {
        var target = new BlockingWriter();
        var sink = new ConsoleLogSink(() => target, capacity: 2);
        sink.Enqueue("a");
        Assert.True(target.Entered.Wait(TimeSpan.FromSeconds(5)));
        foreach (var l in new[] { "b", "c", "d", "e" })
            sink.Enqueue(l);
        target.Release.Set();
        await sink.CompleteAsync();
        var lines = target.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.Contains("2 log lines dropped", lines[1]);
        Assert.Equal(["a", "b", "c"], lines.Where(l => !l.Contains("dropped")));
    }
}
