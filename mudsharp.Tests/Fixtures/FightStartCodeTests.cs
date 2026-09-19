using System.Text;
using MudSharp.Models;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// The decoder tags an 08.00 line <see cref="LineKind.FightStart"/>. Pinned at the decoder because
/// nothing downstream can see it otherwise: the tracker's known wordings match before the kind is
/// consulted, and its unit tests build the kind by hand. Without this, removing the tag fails no
/// test at all.
/// </summary>
public sealed class FightStartCodeTests
{
    private static readonly byte[] GameModeEntry = [0x9D, 0x9C, 0xFF, 0xFF];

    /// <summary>A C08.nn bracket: <c>A3 [sub] FF FF</c> phrase <c>FF FF</c> CR NUL CR LF - the shape
    /// every combat line on the wire has.</summary>
    private static byte[] Combat(byte sub, string phrase)
        => [0xA3, sub, 0xFF, 0xFF, .. Encoding.Latin1.GetBytes(phrase), 0xFF, 0xFF, 0x0D, 0x00, 0x0D, 0x0A];

    [Fact]
    public void AnEightZeroLine_IsTaggedFightStart_WhateverItSays()
    {
        var h = new ParserHarness();
        h.Feed(GameModeEntry);
        h.Feed(Combat(0x9B, "The quazzle lunges at you with a shriek."));   // 08 00 = 9B

        var line = Assert.Single(h.Lines, l => l.PlainText == "The quazzle lunges at you with a shriek.");
        Assert.Equal(LineKind.FightStart, line.Kind);
    }

    [Fact]
    public void TheKnownOpening_CarriesTheTagToo()
    {
        var h = new ParserHarness();
        h.Feed(GameModeEntry);
        h.Feed(Combat(0x9B, "You attack the rat0."));

        var line = Assert.Single(h.Lines, l => l.PlainText == "You attack the rat0.");
        Assert.Equal(LineKind.FightStart, line.Kind);
    }

    [Fact]
    public void OtherEightCodes_AreNotStarts()
    {
        var h = new ParserHarness();
        h.Feed(GameModeEntry);
        h.Feed(Combat(0x9C, "You hit the rat0 (5-9)."));      // 08 01
        h.Feed(Combat(0x9E, "The rat0 hits you (70/80)."));   // 08 03

        // Both combat lines, and nothing shorter (the empty line each CR NUL CR LF terminator adds).
        var lines = h.Lines.Where(l => l.PlainText.Length > 1).ToList();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.NotEqual(LineKind.FightStart, l.Kind));
    }
}
