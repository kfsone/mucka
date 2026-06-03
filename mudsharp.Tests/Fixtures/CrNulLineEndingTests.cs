using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Regression tests for the telnet CR-NUL line ending (RFC 854): MUD2 terminates lines
/// with "\r\0\r\n" and the NUL must not leak into emitted line text. A leaked NUL is
/// invisible in the terminal but breaks exact-match consumers — most visibly watchword
/// triggers whose text spans a server-side wrap point (the "south tomb" bug, June 2026).
/// </summary>
public sealed class CrNulLineEndingTests(ITestOutputHelper output)
{
    // Session capture of the repro: `l` in the Mausoleum, where "Written on the south\r\0\r\n
    // tomb is: ..." wraps mid-trigger. Captures are private — point this env var at a local
    // session-rec .jsonl to enable the replay; the test no-ops otherwise.
    private static readonly string? SessionFile =
        Environment.GetEnvironmentVariable("MUCKA_SESSION_CAPTURE");

    [Fact]
    public void CrNulLineEnding_DoesNotLeakNulIntoLineText()
    {
        var h = new ParserHarness();
        h.Feed("first line\r\0\r\nsecond line\r\0\r\n");

        Assert.Equal(2, h.Lines.Count);
        Assert.Equal("first line", h.Lines[0].PlainText);
        Assert.Equal("second line", h.Lines[1].PlainText);
    }

    [Fact]
    public void SouthTombReplay_TriggerTextSurvivesWrapJoin()
    {
        if (SessionFile is null || !File.Exists(SessionFile))
        {
            output.WriteLine("SKIPPED: set MUCKA_SESSION_CAPTURE to a session-rec .jsonl to run the replay");
            return;
        }

        var h = new ParserHarness();
        foreach (var rawLine in File.ReadLines(SessionFile))
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            using var doc = JsonDocument.Parse(rawLine);
            var arr = doc.RootElement;
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 3) continue;
            if (arr[1].GetString() != "rx") continue;
            h.Feed(Encoding.Latin1.GetBytes(arr[2].GetString() ?? string.Empty));
        }

        Assert.DoesNotContain(h.Lines, l => l.PlainText.Contains('\0'));

        // Mirror GameViewModel.ScanHistory: join non-partial, non-blank lines with one
        // space, then collapse whitespace runs the way WatchwordStore.ScanAll does.
        var sb = new StringBuilder();
        foreach (var l in h.Lines)
        {
            if (l.IsPartial) continue;
            var plain = l.PlainText.TrimEnd();
            if (plain.Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(plain);
        }
        var joined = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ");

        // The trigger prefix spans the server's wrap point ("...the south" / "tomb is:...") —
        // finding it in the joined text proves no NUL survived to break the join.
        const string pre = "the south tomb is: \"";
        int start = joined.IndexOf(pre, StringComparison.Ordinal);
        Assert.True(start >= 0, "watchword trigger prefix not found in joined history");

        start += pre.Length;
        int end = joined.IndexOf('"', start);
        Assert.True(end > start, "capture suffix quote not found");
        Assert.False(string.IsNullOrWhiteSpace(joined[start..end]), "captured text is empty");
    }
}
