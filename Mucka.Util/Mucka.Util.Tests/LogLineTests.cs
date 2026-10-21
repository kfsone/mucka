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
    // Flush returns as soon as the drain catches up, and the drain is a thread-pool task; this bound
    // only turns a drain that never runs into a failure instead of a hang.
    private static readonly TimeSpan SafetyNet = TimeSpan.FromMinutes(2);

    private sealed class ThrowingWriter : StringWriter
    {
        public int Calls;
        public override void WriteLine(string? value) { Calls++; throw new IOException("closed"); }
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
        Assert.True(sink.Flush(SafetyNet));
        Assert.Equal("a" + Environment.NewLine, target.ToString());
        sink.Enqueue("b");
        Assert.True(sink.Flush(SafetyNet));
        Assert.EndsWith("b" + Environment.NewLine, target.ToString());
    }

    /// <summary>The drain asks for its target before it reads a line, so holding the target back
    /// keeps every line in the channel whether or not the drain has started yet.</summary>
    [Fact]
    public async Task DroppedLinesAreCountedAndReported()
    {
        var target = new StringWriter();
        using var open = new ManualResetEventSlim();
        var sink = new ConsoleLogSink(() => { open.Wait(); return target; }, capacity: 2);
        foreach (var l in new[] { "a", "b", "c", "d", "e" })
            sink.Enqueue(l);
        open.Set();
        await sink.CompleteAsync().WaitAsync(SafetyNet);
        var lines = target.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Contains("3 log lines dropped", lines[0]);
        Assert.Equal(["a", "b"], lines[1..]);
    }
}
