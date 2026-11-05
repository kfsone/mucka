using MudSharp.Models;
using Mucka.Terminal;

namespace Mucka.Terminal.Tests;

/// <summary>Tests for <see cref="LineWrapper"/> - MUD2's word wrapping, with style preservation.</summary>
public class LineWrapperTests
{
    private static readonly TextStyle Red = new(Foreground: AnsiColor.Red);
    private static readonly TextStyle Blue = new(Foreground: AnsiColor.Blue);

    private static StyledLine Line(params StyledSpan[] spans) => new(spans, isPartial: false);
    private static StyledSpan Span(string text, TextStyle? style = null) => new(text, style ?? TextStyle.Default);
    private static string[] Rows(string text, int columns) =>
        LineWrapper.Wrap(Line(Span(text)), columns).Select(r => r.PlainText).ToArray();

    // -- No-op when already within the row capacity -------------------------------

    [Fact]
    public void ShortLine_ProducesSingleRow_Unchanged()
    {
        var rows = LineWrapper.Wrap(Line(Span("hello")), columns: 80);

        Assert.Single(rows);
        Assert.Equal("hello", rows[0].PlainText);
    }

    [Fact]
    public void RowCapacity_IsTheColumnCount()
    {
        Assert.Equal(90, LineWrapper.RowCapacity(90));
        Assert.Equal(1, LineWrapper.RowCapacity(1));
    }

    // -- The break rule ------------------------------------------------------------

    [Fact]
    public void ALineOfExactlyTheColumnCount_IsOneRow()
    {
        var text = new string('a', 44) + " " + new string('b', 45);
        Assert.Equal(90, text.Length);
        Assert.Equal([text], Rows(text, 90));
    }

    [Fact]
    public void OneCharacterOverTheColumnCount_BreaksAtTheLastSpace()
    {
        var text = new string('a', 44) + " " + new string('b', 46);
        Assert.Equal(91, text.Length);
        Assert.Equal([new string('a', 44) + " ", new string('b', 46)], Rows(text, 90));
    }

    [Fact]
    public void TheSpaceAfterAFullRow_HangsPastTheLastColumn()
    {
        // The b-word ends at the 90th character and a space follows it: that space is in the 91st
        // cell, past the pane, and closes the row, so the b-word is not sent down.
        var text = new string('a', 10) + " " + new string('b', 79) + " cc";
        Assert.Equal(93, text.Length);
        Assert.Equal([new string('a', 10) + " " + new string('b', 79) + " ", "cc"], Rows(text, 90));
    }

    [Fact]
    public void ASpaceTwoCellsPastTheLastColumn_IsNotABreak()
    {
        var text = new string('a', 10) + " " + new string('b', 80) + " cc";
        Assert.Equal(94, text.Length);
        Assert.Equal([new string('a', 10) + " ", new string('b', 80) + " cc"], Rows(text, 90));
    }

    [Fact]
    public void TheSpaceThatClosesARow_StaysAtItsEnd_SoTheRowsConcatenateToTheText()
    {
        var text = "the quick brown fox jumps over the lazy dog";
        var rows = Rows(text, 20);
        Assert.Equal(["the quick brown fox ", "jumps over the lazy ", "dog"], rows);
        Assert.Equal(text, string.Concat(rows));
    }

    [Fact]
    public void AWordLongerThanARow_IsCutAtTheColumnCount()
    {
        Assert.Equal(["abcde", "fghij"], Rows("abcdefghij", 5));
        Assert.Equal(["abcde", "fghij", "k"], Rows("abcdefghijk", 5));
    }

    [Fact]
    public void ALongWordAfterShortOnes_IsCutAfterTheSpaceBeforeItIsUsed()
    {
        // A chosen behaviour, not a recorded one. No space is available inside the row once "ab " is behind us, so the long word starts
        // a row of its own and is cut there.
        Assert.Equal(["ab ", "cdefgh", "ijkl"], Rows("ab cdefghijkl", 6));
    }

    [Fact]
    public void LeadingIndent_IsNotABreak()
    {
        // A chosen behaviour, not a recorded one: a row cannot close at a space that has only indent
        // before it.
        Assert.Equal(["    abc", "def"], Rows("    abcdef", 7));
        Assert.Equal(["      a", "bcdef"], Rows("      abcdef", 7));
    }

    [Fact]
    public void ALineOfOnlySpaces_LongerThanARow_IsCutAtTheColumnCount()
    {
        // A chosen behaviour, not a recorded one: no space is a break when nothing but indent precedes it.
        Assert.Equal(["     ", "     "], Rows(new string(' ', 10), 5));
        Assert.Equal(["     ", "     ", " "], Rows(new string(' ', 11), 5));
    }

    [Fact]
    public void ARunOfSpacesAtTheBreak_IsLeftAlone()
    {
        // A chosen behaviour, not a recorded one: the row closes at the last space that hangs, and the
        // spaces before it stay in the row they were in, so the next row can begin with a space.
        Assert.Equal(["aaaa ", " bbb", "b"], Rows("aaaa  bbbb", 4));
    }

    [Fact]
    public void AHangingSpace_InAStyledSpan_StaysInItsSpanAtTheEndOfTheRow()
    {
        var rows = LineWrapper.Wrap(Line(Span("abcd", Red), Span(" ", Blue), Span("efgh", Red)), columns: 4);

        Assert.Equal(["abcd ", "efgh"], rows.Select(r => r.PlainText));
        Assert.Equal(["abcd", " "], rows[0].Spans.Select(s => s.Text));
        Assert.Equal([Red, Blue], rows[0].Spans.Select(s => s.Style));
    }

    [Fact]
    public void ExactMultipleOfTheColumnCount_ProducesNoTrailingEmptyRow()
    {
        Assert.Equal(["abcd", "efgh"], Rows("abcdefgh", 4));
    }

    [Fact]
    public void ARowWithNoSpaceAtAll_SplitsIntoColumnSizedPieces()
    {
        // A chosen behaviour, not a recorded one.
        Assert.Equal([new string('x', 16), new string('x', 16), new string('x', 14)],
            Rows(new string('x', 46), 16));
    }

    // -- Splitting across a span boundary, preserving styles ------------------------

    [Fact]
    public void SplitAcrossSpans_PreservesStyles()
    {
        // "abc"(red) + "defghij"(blue) at 5 columns -> ["abcde", "fghij"]; row 0 mixes styles.
        var rows = LineWrapper.Wrap(Line(Span("abc", Red), Span("defghij", Blue)), columns: 5);

        Assert.Equal(new[] { "abcde", "fghij" }, rows.Select(r => r.PlainText));

        Assert.Equal(2, rows[0].Spans.Count);
        Assert.Equal(("abc", Red), (rows[0].Spans[0].Text, rows[0].Spans[0].Style));
        Assert.Equal(("de", Blue), (rows[0].Spans[1].Text, rows[0].Spans[1].Style));

        Assert.Single(rows[1].Spans);
        Assert.Equal(("fghij", Blue), (rows[1].Spans[0].Text, rows[1].Spans[0].Style));
    }

    [Fact]
    public void ABreakSpace_InTheMiddleOfAStyledRun_KeepsItsStyle()
    {
        var rows = LineWrapper.Wrap(Line(Span("red words ", Red), Span("then blue words", Blue)), columns: 16);

        Assert.Equal(["red words then ", "blue words"], rows.Select(r => r.PlainText));
        Assert.Equal(["red words ", "then "], rows[0].Spans.Select(s => s.Text));
        Assert.Equal([Red, Blue], rows[0].Spans.Select(s => s.Style));
    }

    // -- ClickInsertText survives the wrap (clickable names stay clickable) ----------

    [Fact]
    public void SplitSpan_PreservesClickInsertText_InEveryPiece()
    {
        // A clickable underlined name "Frederick" split across a wrap boundary must keep its
        // insert payload in both pieces, or TryActivateSpanInsert can't fire on the tail row.
        var name = new StyledSpan("Frederick", Red, ClickInsertText: "Frederick ");
        var rows = LineWrapper.Wrap(Line(name), columns: 5);

        Assert.Equal(new[] { "Frede", "rick" }, rows.Select(r => r.PlainText));
        Assert.All(rows, r => Assert.Equal("Frederick ", r.Spans[0].ClickInsertText));
    }

    // -- Blank lines -----------------------------------------------------------------

    [Fact]
    public void BlankLine_ProducesOneEmptyRow()
    {
        var rows = LineWrapper.Wrap(Line(), columns: 10);

        Assert.Single(rows);
        Assert.Empty(rows[0].Spans);
        Assert.Equal(string.Empty, rows[0].PlainText);
    }

    [Fact]
    public void ALineOfOneEmptySpan_ProducesOneEmptyRow()
    {
        var rows = LineWrapper.Wrap(Line(Span("")), columns: 10);

        Assert.Single(rows);
        Assert.Equal(string.Empty, rows[0].PlainText);
    }

    // -- WrapAll -----------------------------------------------------------------------

    [Fact]
    public void WrapAll_FlattensRowsInOrder()
    {
        var lines = new[]
        {
            Line(Span("abcdef")),   // 4 columns -> "abcd","ef"
            Line(Span("gh")),       // -> "gh"
        };

        var rows = LineWrapper.WrapAll(lines, columns: 4);

        Assert.Equal(new[] { "abcd", "ef", "gh" }, rows.Select(r => r.PlainText));
    }

    // -- Soft-break flags ----------------------------------------------------------------
    //
    // A row that CONTINUES the one above came from the pane being narrow, not from the text. The
    // copy path strips those breaks, so these flags are what keeps a wrapped path or URL one string.

    [Fact]
    public void WrappedLine_MarksEveryRowAfterTheFirstAsAContinuation()
    {
        var continues = new List<bool>();
        var rows = LineWrapper.WrapAll([Line(Span(new string('x', 100)))], columns: 41, continues);

        Assert.Equal(3, rows.Count);
        Assert.Equal([false, true, true], continues);
    }

    [Fact]
    public void SeparateLogicalLines_EachStartFresh()
    {
        var continues = new List<bool>();
        var rows = LineWrapper.WrapAll([Line(Span("abcdef")), Line(Span("gh"))], columns: 4, continues);

        Assert.Equal(3, rows.Count);
        Assert.Equal([false, true, false], continues);
    }

    [Fact]
    public void ALineExactlyOneRowWide_IsOneRowAndNoContinuation()
    {
        // The case that rules out inferring softness from "the row above was full": this line fills
        // the row exactly, and the break after it is still hard.
        var continues = new List<bool>();
        var rows = LineWrapper.WrapAll([Line(Span("abcd")), Line(Span("ef"))], columns: 4, continues);

        Assert.Equal(["abcd", "ef"], rows.Select(r => r.PlainText));
        Assert.Equal([false, false], continues);
    }

    [Fact]
    public void ABlankLine_IsOneRowAndNoContinuation()
    {
        var continues = new List<bool>();
        var rows = LineWrapper.WrapAll([Line(), Line(Span("x"))], columns: 4, continues);

        Assert.Equal(2, rows.Count);
        Assert.Equal([false, false], continues);
    }

    // -- The server's own wrapping ---------------------------------------------------------

    /// <summary>MUD2 wrapping at W holds a row to W-2 characters, trailing space aside, so a pane of
    /// W-2 columns breaks where it did. The server dropped the space it broke at, which ours keeps at
    /// the end of the row.</summary>
    private static void AssertBreaksWhereTheServerDid(List<string> serverRows, int serverColumns)
    {
        var paneColumns = serverColumns - 2;
        var ours = Rows(string.Join(" ", serverRows), paneColumns);
        var shared = serverRows.Count - 1;
        Assert.True(ours.Length >= serverRows.Count);
        for (var i = 0; i < shared; i++)
        {
            Assert.EndsWith(" ", ours[i]);
            Assert.False(ours[i].EndsWith("  ", StringComparison.Ordinal), "a row closes at exactly one space");
            Assert.Equal(serverRows[i], ours[i][..^1]);
        }
        var last = serverRows[^1];
        Assert.True(last.TrimEnd(' ').Length <= paneColumns, "the server's last row holds W-2 characters too");
        Assert.Equal([last], ours.Skip(shared));
    }

    /// <summary>Wrapping a paragraph at the width MUD2 wrapped it at, less its two spare cells, gives
    /// MUD2's rows back. The paragraphs in the data file were recorded at 80, 90 and 100 columns.</summary>
    [Fact]
    public void RecordedParagraphs_RewrapToTheRowsTheServerSent()
    {
        var paragraphs = ServerWrappedParagraphs.Load();
        Assert.True(paragraphs.Count >= 40, "the data file is expected to hold the recorded paragraphs");
        Assert.Contains(80, paragraphs.Select(p => p.Columns));
        Assert.Contains(90, paragraphs.Select(p => p.Columns));
        Assert.Contains(100, paragraphs.Select(p => p.Columns));

        foreach (var (columns, serverRows) in paragraphs)
            AssertBreaksWhereTheServerDid(serverRows, columns);
    }

    /// <summary>The same text was recorded at two widths, and the two recordings differ: a rule that
    /// reproduces both has not fitted one width.</summary>
    [Fact]
    public void ATextRecordedAtTwoWidths_ReproducesBothWrappings()
    {
        var byText = ServerWrappedParagraphs.Load()
            .GroupBy(p => string.Join(" ", p.Rows))
            .Where(g => g.Select(p => p.Columns).Distinct().Count() >= 2)
            .ToList();
        Assert.NotEmpty(byText);

        foreach (var group in byText)
        {
            var wrappings = group.Select(p => string.Join("|", p.Rows)).Distinct().ToList();
            Assert.True(wrappings.Count >= 2, "a text recorded at two widths wraps differently at each");
            foreach (var recorded in group)
                AssertBreaksWhereTheServerDid(recorded.Rows, recorded.Columns);
        }
    }

    /// <summary>At every width from the narrowest to a wide window, the rows of each recorded paragraph
    /// concatenate back to its text and none shows more than a row holds.</summary>
    [Fact]
    public void AtAnyWidth_TheRowsOfAParagraph_ConcatenateToItsText_AndFit()
    {
        foreach (var (_, serverRows) in ServerWrappedParagraphs.Load())
        {
            var text = string.Join(" ", serverRows);
            for (var columns = TerminalColumns.Min; columns <= 200; columns++)
            {
                var rows = Rows(text, columns);
                Assert.Equal(text, string.Concat(rows));
                Assert.All(rows, r => Assert.True(r.TrimEnd(' ').Length <= LineWrapper.RowCapacity(columns)));
                Assert.All(rows, r => Assert.True(r.Length <= LineWrapper.RowCapacity(columns) + 1));
                Assert.All(rows.Take(rows.Length - 1), r => Assert.NotEmpty(r.TrimEnd(' ')));
            }
        }
    }

    /// <summary>Copying a whole wrapped line gives the line back exactly: the soft breaks are not in
    /// the clipboard and the spaces they closed at are.</summary>
    [Fact]
    public void ASoftWrappedLine_CopiesBackAsItsExactText()
    {
        var text = "the quick brown fox jumps over the lazy dog and keeps going until it has wrapped twice";
        var continues = new List<bool>();
        var rows = LineWrapper.WrapAll([Line(Span(text))], columns: 30, continues);
        Assert.True(rows.Count >= 3);

        var copied = TerminalSelection.Extract(rows, (0, 0), (rows.Count - 1, rows[^1].PlainText.Length), continues);

        Assert.Equal(text, copied);
    }

    // -- Guard -----------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveColumns_Throws(int columns)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LineWrapper.Wrap(Line(Span("x")), columns));
    }
}

/// <summary>Reads <c>Data/server-wrapped-paragraphs.txt</c>: blocks of a <c>W &lt;columns&gt;</c> line
/// and the rows MUD2 sent, each ending in a bar that is not part of the row.</summary>
internal static class ServerWrappedParagraphs
{
    public static List<(int Columns, List<string> Rows)> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "server-wrapped-paragraphs.txt");
        var result = new List<(int, List<string>)>();
        int columns = 0;
        List<string>? rows = null;
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.StartsWith('#')) continue;
            if (line.Length == 0)
            {
                if (rows is { Count: > 0 }) result.Add((columns, rows));
                rows = null;
            }
            else if (line.StartsWith("W ", StringComparison.Ordinal))
            {
                columns = int.Parse(line.AsSpan(2), System.Globalization.CultureInfo.InvariantCulture);
                rows = new List<string>();
            }
            else
            {
                Assert.EndsWith("|", line);
                rows!.Add(line[..^1]);
            }
        }
        if (rows is { Count: > 0 }) result.Add((columns, rows));
        return result;
    }
}
