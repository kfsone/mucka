using MudSharp.Models;
using MudSharp.Protocol;

namespace MudSharp.Tests.Fixtures;

/// <summary>Each line carries the C1 codes of the colour frames that held its text
/// (<see cref="StyledLine.Codes"/>).</summary>
public class LineCodesTests
{
    private static readonly byte[] Pop = [0xFF, 0xFF];

    private static byte[] Code(params byte[] leadAndParameters) => [.. leadAndParameters, 0xFF, 0xFF];

    private static byte[] Text(string text) => System.Text.Encoding.Latin1.GetBytes(text);

    private static List<StyledLine> Lines(params byte[][] parts)
    {
        var parser = new MudStreamParser();
        var lines = new List<StyledLine>();
        parser.LineReady += lines.Add;
        parser.Feed(parts.SelectMany(p => p).ToArray());
        return lines;
    }

    [Fact]
    public void ALine_CarriesTheCodeOfTheFrameItsTextArrivedIn()
    {
        var lines = Lines(
            Code(0x9D, 0x9C), Text("Elizabethan tearoom."), Pop, Text("\n"),   // 02 01 room short description
            Code(0xA3, 0xA3), Text("You killed the rat."), Pop, Text("\n"),     // 08 08 you killed them
            Text("plain words\n"));

        Assert.Equal(new[] { new C1Code(2, 1) }, lines[0].Codes);
        Assert.Equal(new[] { new C1Code(8, 8) }, lines[1].Codes);
        Assert.Empty(lines[2].Codes);
    }

    [Fact]
    public void TextAfterAPop_IsTheOuterFramesAgain_AndEachCodeIsListedOnce()
    {
        // 08 11 round the line, an 08 01 inside it, then the 08 11 frame's text again.
        var lines = Lines(Code(0xA3, 0xA6), Text("The rat "), Code(0xA3, 0x9C), Text("hits"), Pop,
            Text(" and flees "), Text("away."), Pop, Text("\n"));

        Assert.Equal(new[] { new C1Code(8, 11), new C1Code(8, 1) }, lines.Single().Codes);
    }

    [Fact]
    public void ABareCode_ReadsAsItsZeroParameter()
    {
        var lines = Lines(Code(0xA3), Text("You attack the rat."), Pop, Text("\n"));
        Assert.Equal(new[] { new C1Code(8, 0) }, lines.Single().Codes);
        Assert.Equal("08 00", lines.Single().Codes[0].ToString());
    }

    [Fact]
    public void RestylingALine_KeepsItsCodes()
    {
        var line = Lines(Code(0x9D, 0x9C), Text("Hall."), Pop, Text("\n")).Single();
        Assert.Equal(new[] { new C1Code(2, 1) }, line.WithSpans([new StyledSpan("Hall.", TextStyle.Default)]).Codes);
    }
}
