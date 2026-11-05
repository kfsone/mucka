using System.Text;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// The client asks the shell to stop wrapping (<c>/F+</c>) once, at the Option menu, and the shell's
/// answer is swallowed. The terminal width is never sent: the pane wraps the text itself.
/// </summary>
public class LocalFormattingTests
{
    // Verbatim from a guest's Option menu session: the ANSI colour code that precedes every reply,
    // then the reply, then the prompt again.
    private const string Reply = "\x1B[0;37;40m[Local formatting is now on]\r\nOption (H for help): ";

    [Fact]
    public void EntryBytes_AreOneCommandInterrupt_ThatTurnsLocalFormattingOn()
    {
        var text = Encoding.Latin1.GetString(ClientModeEntry.Bytes);

        Assert.Equal("\x1B-[\x1B\x06\x1B-N/F+\x1B-]", text);
    }

    [Fact]
    public void EntryBytes_NeverCarryATerminalWidth()
    {
        Assert.DoesNotContain("/T", Encoding.Latin1.GetString(ClientModeEntry.Bytes));
    }

    [Fact]
    public void TheShellsAnswer_IsSwallowed_AndConfirmed()
    {
        var h = new ParserHarness();
        var confirmed = 0;
        h.Parser.LocalFormattingConfirmed += () => confirmed++;

        h.Feed(Reply);
        h.Parser.ShowPrompt();

        Assert.Equal(1, confirmed);
        Assert.DoesNotContain(h.Lines, l => l.PlainText.Contains("Local formatting"));
        Assert.Contains(h.Lines, l => l.PlainText.Contains("Option (H for help):"));
    }

    [Fact]
    public void TheShellsAnswer_IsSwallowed_WhenItArrivesInPieces()
    {
        var h = new ParserHarness();
        var confirmed = 0;
        h.Parser.LocalFormattingConfirmed += () => confirmed++;

        foreach (var chunk in new[] { "\x1B[0;37;40m[Local forma", "tting is now", " on]\r", "\n" })
            h.Feed(chunk);

        Assert.Equal(1, confirmed);
        Assert.Empty(h.Lines);
    }

    [Fact]
    public void TheShellsAnswerOff_IsShown_NotSwallowed()
    {
        // Off means the server is wrapping again: the player is told rather than left to wonder.
        var h = new ParserHarness();
        var confirmed = 0;
        h.Parser.LocalFormattingConfirmed += () => confirmed++;

        h.Feed("[Local formatting is now off]\r\n");

        Assert.Equal(0, confirmed);
        Assert.Equal("[Local formatting is now off]", Assert.Single(h.Lines).PlainText);
    }

    [Fact]
    public void TheSameLineInGameMode_IsNotSwallowed()
    {
        // Game output is never eaten on the strength of its wording: only the Option menu's own
        // reply is.
        var h = new ParserHarness();
        h.Feed("\x9D\x9C\xFF\xFF");
        h.ClearCounters();
        var confirmed = 0;
        h.Parser.LocalFormattingConfirmed += () => confirmed++;

        h.Feed("[Local formatting is now on]\r\n");

        Assert.Equal(0, confirmed);
        Assert.Single(h.Lines);
    }

    [Fact]
    public void AnOrdinaryLineThatMerelyStartsTheSameWay_IsShown()
    {
        var h = new ParserHarness();
        h.Feed("[Local formatting is now on] and more\r\n");

        Assert.Single(h.Lines);
    }

    /// <summary>No source file builds a <c>/T</c> command: the width the pane wraps at is never sent.
    /// Tests are exempt, because they feed the old reply to the parser.</summary>
    [Fact]
    public void NoSourceFileSendsATerminalWidth()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Mucka.csproj")))
            root = root.Parent;
        Assert.NotNull(root);

        var sends = new System.Text.RegularExpressions.Regex("[\"']/T[{0-9\"]",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var skipped = new[] { "bin", "obj", "tools", ".git", "Tests" };
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root.FullName, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root.FullName, file);
            if (relative.Split(Path.DirectorySeparatorChar, '/').Any(part =>
                    skipped.Any(s => part.Equals(s, StringComparison.OrdinalIgnoreCase)
                        || part.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase))))
                continue;
            if (sends.IsMatch(File.ReadAllText(file)))
                offenders.Add(relative);
        }

        Assert.Empty(offenders);
    }
}
