using System.Text;
using MudSharp.Protocol;
using Mucka.Combat;
using Mucka.WireLog;

namespace Mucka.Util.Tests;

/// <summary>
/// <see cref="WireUnwrap"/> on the bytes the recordings hold - a break is CR NUL CR LF - and on the
/// places where two lines that look like one wrapped line are not: something sits between them.
/// </summary>
public class WireUnwrapBoundaryTests
{
    private const string Break = "\r\0\r\n";

    private static readonly byte[] GameModeEntry = [0x9D, 0x9C, 0xFF, 0xFF];
    private static readonly byte[] EntryAt90 = Latin1("\x1b-[\x1b\x06\x1b-N/T90\x1b-]");
    private static readonly byte[] PromptContainer = [0x9C, 0xFF, 0xFF, 0x9C, 0x9D, 0xFF, 0xFF, (byte)'*', 0xFF, 0xFF, 0xFF, 0xFF];

    // Two rows of one description, recorded at /T90: the first is 85 characters and "and" would have
    // made it 89, one past what a wrapped row holds.
    private const string First = "This cosy, Tudor period room is where all MUD adventures start. Its exposed oak beams";
    private const string Second = "and soft, velvet-covered furniture provide it with the ideal atmosphere in which to";

    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    private static WireTrim.Row Rx(long id, byte[] data) => new(id, id, (int)WireDirection.Rx, data);
    private static WireTrim.Row Tx(long id, byte[] data) => new(id, id, (int)WireDirection.Tx, data);

    private static byte[] Join(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    private static List<string> LinesOf(IEnumerable<byte[]> chunks)
    {
        var lines = new List<string>();
        var parser = new MudStreamParser();
        parser.LineReady += line => { if (!line.IsPartial) lines.Add(line.PlainText); };
        foreach (var chunk in chunks) parser.Feed(chunk);
        return lines;
    }

    private static List<byte[]> Apply(List<WireTrim.Row> rows, List<WireUnwrap.Changed> changes)
    {
        var byId = changes.ToDictionary(c => c.Id, c => c.Data);
        return rows.Where(r => r.Direction == (int)WireDirection.Rx)
            .Select(r => byId.TryGetValue(r.Id, out var data) ? data : r.Data).ToList();
    }

    private static List<WireTrim.Row> Run(params byte[][] rxRows)
    {
        var rows = new List<WireTrim.Row> { Tx(1, EntryAt90), Rx(2, GameModeEntry) };
        long id = 3;
        foreach (var data in rxRows) rows.Add(Rx(id++, data));
        return rows;
    }

    [Fact]
    public void ABreakOfCrNulCrLf_IsReplacedByOneSpace()
    {
        var rows = Run(Latin1(First + Break + Second + Break));

        var changes = WireUnwrap.Plan(rows);

        Assert.NotNull(changes);
        Assert.Equal(First + " " + Second + Break, Encoding.Latin1.GetString(Assert.Single(changes).Data));
    }

    [Fact]
    public void ABreakSplitBetweenTwoRows_LeavesNoStrayBytesInEither()
    {
        var rows = Run(Latin1(First + "\r\0"), Latin1("\r\n" + Second + Break));

        var changes = WireUnwrap.Plan(rows);

        Assert.NotNull(changes);
        Assert.Equal([First + " " + Second], LinesOf(Apply(rows, changes)));
        Assert.Equal(First + " ", Encoding.Latin1.GetString(changes.Single(c => c.Id == 3).Data));
        Assert.Equal(Second + Break, Encoding.Latin1.GetString(changes.Single(c => c.Id == 4).Data));
    }

    [Fact]
    public void ALineThatStartsAfterAPop_IsAnotherParagraph_AndIsNotJoined()
    {
        // A pop right after the break closes the colour scope the first line was in.
        var rows = Run(Join(Latin1(First + Break), [0xFF, 0xFF], Latin1(Second + Break)));

        Assert.Empty(WireUnwrap.Plan(rows)!);
    }

    [Fact]
    public void APromptBetweenTwoLines_IsATurnBoundary_AndIsNotJoined()
    {
        var rows = Run(Join(Latin1(First + Break), PromptContainer, Latin1(Second + Break)));

        Assert.Empty(WireUnwrap.Plan(rows)!);
    }

    [Fact]
    public void ABlankLineBetweenThem_IsNotJoined()
    {
        var rows = Run(Latin1(First + Break + Break + Second + Break));

        Assert.Empty(WireUnwrap.Plan(rows)!);
    }

    [Fact]
    public void AFirstLineEndingInASpace_IsNotJoined()
    {
        var rows = Run(Latin1(First + " " + Break + Second + Break));

        Assert.Empty(WireUnwrap.Plan(rows)!);
    }

    [Fact]
    public void ALineBeginningWithASpace_IsNotJoined()
    {
        var rows = Run(Latin1(First + Break + "        " + Second + Break));

        Assert.Empty(WireUnwrap.Plan(rows)!);
    }

    [Fact]
    public void AFirstLineLongerThanARowHolds_IsNotJoined()
    {
        // Past the 88 characters a wrapped row holds at /T90: not the server's wrap.
        var first = First + " and so on";
        Assert.True(first.Length > 88);
        var rows = Run(Latin1(first + Break + Second + Break));

        Assert.Empty(WireUnwrap.Plan(rows)!);
    }

    [Fact]
    public void WhenTheWordWouldHaveFitted_TheLineWasEndedOnPurpose()
    {
        var rows = Run(Latin1("You hit the wyvern (20-29)." + Break + "The wyvern looks injured." + Break));

        Assert.Empty(WireUnwrap.Plan(rows)!);
    }

    [Fact]
    public void ALineTypedWithASlashT_IsNotAWidth()
    {
        var rows = new List<WireTrim.Row>
        {
            Tx(1, Latin1("say try /T90\r\n")),
            Rx(2, GameModeEntry),
            Rx(3, Latin1(First + Break + Second + Break)),
        };

        Assert.Empty(WireUnwrap.Plan(rows)!);
    }

    [Theory]
    [InlineData("\x1b-[\x1b\x06\x1b-N/T90\x1b-]")]
    [InlineData("\x1b-[\x1b\x06\x1b-T\x1b-N/T90\x1b-]")]
    [InlineData("\x1b-[/T90\x1b-]")]
    [InlineData("/T90\r\n")]
    public void EveryFormOfTheWidthCommand_SetsTheWidth(string sent)
    {
        var rows = new List<WireTrim.Row>
        {
            Tx(1, Latin1(sent)), Rx(2, GameModeEntry), Rx(3, Latin1(First + Break + Second + Break)),
        };

        Assert.NotEmpty(WireUnwrap.Plan(rows)!);
    }
}
