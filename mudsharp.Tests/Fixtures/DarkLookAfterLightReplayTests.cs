using System.Globalization;
using System.Text;
using MudSharp.Combat;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// <c>dark-look-after-light.c1</c>: 48 seconds of the operator's wire corpus, verbatim. The player
/// stands at the lit "Ford across river.", moves into the unlit stable while attacking the zombie4
/// ("nw,dr cc,k z wi axe"): "It's too dark to see now." is printed and the items reply's room section
/// comes back "oo", a glow spell lights the room, and the `l` that follows prints the room short "Stable.". The fight with the zombie4 goes on
/// after it.
///
/// <para>What this adds over DarkSightWiringTests, which feeds the same signals by hand: the wire's
/// own interleaving of the items reply, the glow spell, the look and the blows around them, so the
/// rule (a room short after the dark is a baseline) is checked against the order the server actually
/// sends, not the order a test author would write.</para>
/// </summary>
public sealed class DarkLookAfterLightReplayTests
{
    /// <summary>The combat events in order, with a null entry wherever an encounter closed, so the
    /// encounter boundaries sit in the same sequence as the lines that caused them.</summary>
    private static List<CombatEvent?> Replay(out List<bool> inCombatChanges)
    {
        var capture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Data", "dark-look-after-light.c1");
        using var session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval = TimeSpan.FromSeconds(600),   // keep probe traffic out of the replay
        });
        var events = new List<CombatEvent?>();
        var changes = new List<bool>();
        long captureTs = 0;
        session.CombatClock = () => DateTimeOffset.FromUnixTimeMilliseconds(captureTs).UtcDateTime;
        session.CombatEventOccurred += events.Add;
        session.InCombatChanged += inCombat =>
        {
            changes.Add(inCombat);
            if (!inCombat)
                events.Add(null);
        };
        // A recording cannot answer this client's setup batch - see MudSession.SetupInjectEnabled.
        session.SetupInjectEnabled = false;
        foreach (var rawLine in File.ReadLines(capture, Encoding.Latin1))
        {
            var parts = rawLine.Split(' ', 3);
            if (parts.Length < 3 || parts[1] != "rx")
                continue;
            captureTs = long.Parse(parts[0], CultureInfo.InvariantCulture);
            session.Feed(CaptureBytes(parts[2]));
        }
        inCombatChanges = changes;
        return events;
    }

    /// <summary>Undoes the three escapes a <c>.c1</c> payload carries - see
    /// <see cref="WyvernPoisonDeathReplayTests"/>.</summary>
    private static byte[] CaptureBytes(string payload)
    {
        var bytes = new List<byte>(payload.Length);
        for (var i = 0; i < payload.Length; i++)
        {
            if (payload[i] == '\\' && i + 1 < payload.Length)
                bytes.Add(payload[++i] switch { 'r' => (byte)0x0D, 'n' => (byte)0x0A, _ => (byte)0x5C });
            else
                bytes.Add((byte)payload[i]);
        }
        return bytes.ToArray();
    }

    [Fact]
    public void TheLookAfterTheLightReturns_DoesNotEndTheFight()
    {
        var events = Replay(out var changes);

        // The fight opens in the dark, and the light comes back inside it.
        var start = events.FindIndex(e => e is { Kind: CombatEventKind.FightStart, NpcName: "zombie4" });
        var dark = events.FindIndex(e => e is { Kind: CombatEventKind.SightLost });
        var lastBlow = events.FindLastIndex(e => e is { Kind: CombatEventKind.Hit or CombatEventKind.Miss, NpcName: "zombie4" });
        var light = events.FindIndex(e => e is { Kind: CombatEventKind.SightRegained });
        Assert.InRange(dark, 0, start - 1);
        Assert.InRange(light, start + 1, lastBlow - 1);
        // One encounter from the zombie4's start to its last blow: nothing force-ends it and nothing
        // closes it in between.
        var between = events.Skip(start).Take(lastBlow - start).ToList();
        Assert.DoesNotContain(between, e => e is null);
        Assert.DoesNotContain(between, e => e is { Kind: CombatEventKind.EncounterForceEnded });
        Assert.Contains(changes, c => c);
    }
}
