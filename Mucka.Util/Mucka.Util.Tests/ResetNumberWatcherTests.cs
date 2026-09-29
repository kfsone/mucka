using Mucka.Commands;

namespace Mucka.Util.Tests;

/// <summary>
/// <see cref="ResetNumberWatcher"/> and the <see cref="WrappedTail"/> it reads through. The split
/// banner is verbatim from a phone login at /T20 (mud2.co.uk, 2026-09-28, wire rows as the server
/// sent them): "This reset is" / "number 127276.".
/// </summary>
public sealed class ResetNumberWatcherTests
{
    private static List<long> Watch(params string[] lines)
    {
        var watcher = new ResetNumberWatcher();
        var seen = new List<long>();
        foreach (var line in lines)
            if (watcher.TryNote(line, out var reset))
                seen.Add(reset);
        return seen;
    }

    [Fact]
    public void OneRow_IsRead()
        => Assert.Equal([127149L], Watch("This reset is number 127149."));

    [Fact]
    public void TheBannerAsAPhoneReceivedIt_IsRead()
        => Assert.Equal([127276L], Watch("MUD last reset on", "This reset is\r\0", "number 127276.\r\0", "The personae"));

    [Fact]
    public void ThreeRows_AreRead()
        => Assert.Equal([127276L], Watch("This reset is\r\0", "number\r\0", "127276.\r\0"));

    /// <summary>Once read, the sentence is forgotten, so a later row ending in digits does not read
    /// the same number a second time.</summary>
    [Fact]
    public void ANumberIsReadOnce()
        => Assert.Equal([127276L], Watch("This reset is\r\0", "number 127276.\r\0", "Level 12."));

    [Fact]
    public void ARowEndingASentence_IsNotTheFrontOfTheNext()
    {
        var tail = new WrappedTail(3);
        tail.Add("It is number one.");
        tail.Add("This is");
        tail.Add("number 2.");
        Assert.Equal(["number 2.", "This is number 2."], tail.Candidates());
    }

    [Fact]
    public void TheTail_ForgetsItsOldestRow()
    {
        var tail = new WrappedTail(2);
        tail.Add("a");
        tail.Add("b");
        tail.Add("c");
        Assert.Equal(["c", "b c"], tail.Candidates());
    }
}
