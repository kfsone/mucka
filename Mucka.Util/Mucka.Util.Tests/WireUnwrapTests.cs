using System.Text;
using MudSharp.Protocol;
using Mucka.Combat;
using Mucka.WireLog;

namespace Mucka.Util.Tests;

/// <summary>
/// <see cref="WireUnwrap"/>: the lines a server wrapped at the width the client sent become one line,
/// and nothing else in the run changes.
/// </summary>
public class WireUnwrapTests
{
    private static readonly byte[] GameModeEntry = [0x9D, 0x9C, 0xFF, 0xFF];

    // The old client-mode entry, which carried the width.
    private static readonly byte[] EntryAt90 = Latin1("\x1b-[\x1b\x06\x1b-N/T90\x1b-]");

    // Recorded at /T90: three rows of one room description.
    private static readonly string[] Description =
    [
        "This cosy, Tudor period room is where all MUD adventures start. Its exposed oak beams",
        "and soft, velvet-covered furniture provide it with the ideal atmosphere in which to",
        "relax before venturing out into that strange, timeless realm. A sense of decency and",
        "decorum prevails. ",
    ];

    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    private static WireTrim.Row Rx(long id, byte[] data) => new(id, id, (int)WireDirection.Rx, data);
    private static WireTrim.Row Tx(long id, byte[] data) => new(id, id, (int)WireDirection.Tx, data);

    private static byte[] Rows(params string[] lines)
        => Latin1(string.Concat(lines.Select(l => l + "\r\r\n")));

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

    [Fact]
    public void AParagraphTheServerWrapped_BecomesOneLine()
    {
        var rows = new List<WireTrim.Row> { Tx(1, EntryAt90), Rx(2, GameModeEntry), Rx(3, Rows(Description)) };

        var changes = WireUnwrap.Plan(rows);

        Assert.NotNull(changes);
        Assert.Equal([string.Join(" ", Description)], LinesOf(Apply(rows, changes)));
    }

    [Fact]
    public void AParagraphSplitOverSeveralRows_IsJoinedAcrossThem()
    {
        // Each line of the description in a row of its own, and the break itself split between two.
        var rows = new List<WireTrim.Row> { Tx(1, EntryAt90), Rx(2, GameModeEntry) };
        long id = 3;
        foreach (var line in Description)
        {
            rows.Add(Rx(id++, Latin1(line + "\r\r")));
            rows.Add(Rx(id++, Latin1("\n")));
        }

        var changes = WireUnwrap.Plan(rows);

        Assert.NotNull(changes);
        Assert.Equal([string.Join(" ", Description)], LinesOf(Apply(rows, changes)));
    }

    [Fact]
    public void ALineTheServerEndedOnPurpose_IsLeftAlone()
    {
        // The first line is short: the next word would have fitted, so the server did not wrap it.
        string[] lines = ["You hit the wyvern (20-29).", "The wyvern looks superficially injured."];
        var rows = new List<WireTrim.Row> { Tx(1, EntryAt90), Rx(2, GameModeEntry), Rx(3, Rows(lines)) };

        Assert.Empty(WireUnwrap.Plan(rows)!);
    }

    [Fact]
    public void ARunWithNoWidthSent_IsLeftAlone()
    {
        var rows = new List<WireTrim.Row> { Rx(1, GameModeEntry), Rx(2, Rows(Description)) };

        Assert.Empty(WireUnwrap.Plan(rows)!);
    }

    [Fact]
    public void LinesBeforeTheGame_AreLeftAlone()
    {
        var rows = new List<WireTrim.Row> { Tx(1, EntryAt90), Rx(2, Rows(Description)) };

        Assert.Empty(WireUnwrap.Plan(rows)!);
    }

    [Fact]
    public void ATxBetweenTwoLines_IsABoundary()
    {
        var rows = new List<WireTrim.Row>
        {
            Tx(1, EntryAt90), Rx(2, GameModeEntry),
            Rx(3, Rows(Description[0])), Tx(4, Latin1("look\r\n")), Rx(5, Rows(Description[1])),
        };

        Assert.Empty(WireUnwrap.Plan(rows)!);
    }

    [Fact]
    public void WhatWasChanged_IsOnlyTheBreaks()
    {
        var rows = new List<WireTrim.Row> { Tx(1, EntryAt90), Rx(2, GameModeEntry), Rx(3, Rows(Description)) };

        var changes = WireUnwrap.Plan(rows)!;

        // Each break the server made is one space; the paragraph's own end is kept.
        var after = Encoding.Latin1.GetString(Assert.Single(changes).Data);
        Assert.Equal(string.Join(" ", Description) + "\r\r\n", after);
    }
}
