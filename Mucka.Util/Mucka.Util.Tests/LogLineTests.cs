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
