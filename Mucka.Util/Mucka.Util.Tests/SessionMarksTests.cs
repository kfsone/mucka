using Mucka.Commands;

namespace Mucka.Util.Tests;

/// <summary>
/// <c>$MARK</c>, against the operator's own worked example. The id rules are the whole of the
/// feature - a mark whose id cannot be found again in the log is not a mark - so every case below
/// asserts the exact text that reaches the capture.
/// </summary>
public class SessionMarksTests
{
    private const long Session = 61;

    /// <summary>A generated id, fixed so the expected text can be written out. Production supplies
    /// <see cref="SessionMarks.NewId"/>.</summary>
    private static string Gen() => "genid";

    private static string Apply(SessionMarks marks, string argument)
        => marks.Apply(argument, Session, Gen).Text;

    // -- standalone ------------------------------------------------------------------------------

    [Fact]
    public void BareMark_GeneratesAnId_WithAHyphen()
        => Assert.Equal("//MARK: mm-61-genid", Apply(new SessionMarks(), string.Empty));

    [Theory]
    // A note becomes the id behind a COLON, so a chosen id and a generated one never look alike.
    [InlineData("\"brand\"", "//MARK: mm-61:brand")]
    [InlineData("\"picking up brand\"", "//MARK: mm-61:picking up brand")]
    // Promotion is not idempotent on purpose - see the class remarks.
    [InlineData("\"mm-61-picking up brand\"", "//MARK: mm-61:mm-61-picking up brand")]
    [InlineData("\"mm-61:my brand\"", "//MARK: mm-61:mm-61:my brand")]
    [InlineData("@1234", "//MARK: mm-61:1234")]
    public void AStandaloneMark_CarriesItsPromotedId(string argument, string expected)
        => Assert.Equal(expected, Apply(new SessionMarks(), argument));

    // -- the worked example, start to finish -----------------------------------------------------

    /// <summary>The operator's sequence verbatim, in one run, because the errors are only meaningful
    /// against the stack the lines before them built.</summary>
    [Fact]
    public void TheNestedSequence_MatchesTheWorkedExample()
    {
        var marks = new SessionMarks();

        Assert.Equal("//BEG: mm-61:my session", Apply(marks, "start \"my session\""));
        Assert.Equal("//BEG: mm-61:1234", Apply(marks, "start @1234"));
        Assert.Equal("//BEG: mm-61:2345", Apply(marks, "start @2345"));

        // No argument closes the innermost.
        Assert.Equal("//END: mm-61:2345", Apply(marks, "end"));

        // A note naming an already-closed span is promoted like any other note, so it matches
        // nothing - and the message names the mark that IS current, the way it was given.
        Assert.Equal("ERROR: mm-61:mm-61:2345 is not the current mark (@1234)",
            Apply(marks, "end \"mm-61:2345\""));
        Assert.Equal("ERROR: mm-61:mm-61:1234 is not the current mark (@1234)",
            Apply(marks, "end \"mm-61:1234\""));

        Assert.Equal("//END: mm-61:1234", Apply(marks, "end @1234"));

        Assert.Equal("ERROR: mm-61:mm-61:my session is not the current mark (\"my session\")",
            Apply(marks, "end \"mm-61:my session\""));

        // The login ends under the one still open.
        var closed = marks.CloseAll("quit");
        Assert.Equal(["//END: mm-61:my session (quit)"], closed.Select(c => c.Text));
        Assert.Equal(0, marks.Depth);
    }

    /// <summary>A generated span cannot be named, so the mismatch message says what to type
    /// instead - the operator has no way of knowing the id this class chose.</summary>
    [Fact]
    public void AGeneratedSpan_CanOnlyBeClosedWithoutAnArgument()
    {
        var marks = new SessionMarks();

        Assert.Equal("//BEG: mm-61-genid", Apply(marks, "start"));
        Assert.Equal(
            "ERROR: mm-61:mm-61-genid is not the current mark (mm-61-genid), use `$MARK end`.",
            Apply(marks, "end \"mm-61-genid\""));
        Assert.Equal("//END: mm-61-genid", Apply(marks, "end"));
        Assert.Equal("ERROR: No open marks to close.", Apply(marks, "end"));
    }

    // -- limits and refusals ---------------------------------------------------------------------

    [Fact]
    public void TheStackIsBounded()
    {
        var marks = new SessionMarks();
        for (var i = 0; i < SessionMarks.MaxDepth; i++)
            Assert.StartsWith("//BEG: ", Apply(marks, $"start @s{i}"));

        Assert.Equal("ERROR: Too many open marks (maximum 8).", Apply(marks, "start @overflow"));
        Assert.Equal(SessionMarks.MaxDepth, marks.Depth);
    }

    [Fact]
    public void EveryFormNeedsAPersonaSession()
    {
        var marks = new SessionMarks();
        const string expected = "ERROR: $MARK needs an open persona session - you are not in game mode.";

        // All three, because there is no id without a login and so no mark that could be found
        // again - not only "start", which is the one that also has a stack to protect.
        Assert.Equal(expected, marks.Apply(string.Empty, null, Gen).Text);
        Assert.Equal(expected, marks.Apply("start @a", null, Gen).Text);
        Assert.Equal(expected, marks.Apply("end", null, Gen).Text);
    }

    [Theory]
    [InlineData("@", "Invalid mark id")]
    [InlineData("@-leading", "Invalid mark id")]
    [InlineData("@has space", "Invalid mark id")]
    [InlineData("@has/slash", "Invalid mark id")]
    [InlineData("\"\"", "Mark note is empty.")]
    [InlineData("\"unterminated", "Unterminated mark note")]
    [InlineData("bare", "Expected @id or a \"quoted note\"")]
    public void AnUnusableIdIsRefused(string argument, string expected)
        => Assert.Contains(expected, Apply(new SessionMarks(), argument));

    [Fact]
    public void ANoteLongerThanTheLimitIsRefused()
    {
        var marks = new SessionMarks();
        var ok = new string('a', SessionMarks.MaxNoteLength);
        var tooLong = new string('a', SessionMarks.MaxNoteLength + 1);

        Assert.Equal($"//MARK: mm-61:{ok}", Apply(marks, $"\"{ok}\""));
        Assert.Equal("ERROR: Mark note is 65 characters, maximum is 64.", Apply(marks, $"\"{tooLong}\""));
    }

    /// <summary>
    /// A text id must be quoted. An unquoted word is a syntax error and never a note, so
    /// <c>$MARK start beer</c> does not open a span called beer - it opens nothing.
    /// </summary>
    [Theory]
    [InlineData("start beer")]
    [InlineData("end beer")]
    [InlineData("beer")]
    public void AnUnquotedWordIsNeverANote(string argument)
    {
        var marks = new SessionMarks();

        var result = marks.Apply(argument, Session, Gen);

        Assert.Equal(MarkKind.Error, result.Kind);
        // Named back, so the operator can see it was the word and not the verb that was rejected.
        Assert.Contains("beer", result.Text);
        Assert.Equal(0, marks.Depth);
    }

    /// <summary>The quote is what separates a verb from an id, so a note can open with either word
    /// and still be a note. Nothing about the whole-word match is load-bearing for that.</summary>
    [Fact]
    public void AQuotedNoteMayOpenWithAVerb()
    {
        var marks = new SessionMarks();

        Assert.Equal("//MARK: mm-61:starting the run", Apply(marks, "\"starting the run\""));
        Assert.Equal("//BEG: mm-61:end of the run", Apply(marks, "start \"end of the run\""));
        Assert.Equal(1, marks.Depth);
    }

    /// <summary>What the whole-word match IS for: the refusal names what was typed rather than the
    /// remainder a prefix match would leave behind.</summary>
    [Fact]
    public void AWordMerelyStartingWithAVerbIsNamedInFull()
    {
        var marks = new SessionMarks();

        Assert.Contains("startle", Apply(marks, "startle"));
        Assert.DoesNotContain("got: le", Apply(marks, "startle"));
    }

    [Fact]
    public void TrailingTextAfterANoteIsRefused()
    {
        var marks = new SessionMarks();

        Assert.Equal("ERROR: Trailing text after the mark note: and more",
            Apply(marks, "start \"beer\" and more"));
        Assert.Equal(0, marks.Depth);
    }

    /// <summary>Case is not part of the verb - the command itself is typed in capitals, so a habit
    /// of shouting it should not fail.</summary>
    [Fact]
    public void TheVerbIsCaseInsensitive()
    {
        var marks = new SessionMarks();

        Assert.Equal("//BEG: mm-61:a", Apply(marks, "START @a"));
        Assert.Equal("//END: mm-61:a", Apply(marks, "End @a"));
    }

    /// <summary>Innermost first, so the closing lines nest the way the opening ones did.</summary>
    [Fact]
    public void CloseAll_UnwindsInnermostFirst_AndNamesTheReason()
    {
        var marks = new SessionMarks();
        Apply(marks, "start @outer");
        Apply(marks, "start @inner");

        Assert.Equal(
            ["//END: mm-61:inner (died)", "//END: mm-61:outer (died)"],
            marks.CloseAll("died").Select(c => c.Text));
    }

    /// <summary>An unclassified drop still closes the spans. A login the client could not classify
    /// is exactly when a landmark is worth having.</summary>
    [Fact]
    public void CloseAll_WithNoReason_SaysSoRatherThanNothing()
    {
        var marks = new SessionMarks();
        Apply(marks, "start @a");

        Assert.Equal(["//END: mm-61:a (session ended)"], marks.CloseAll(null).Select(c => c.Text));
    }
}
