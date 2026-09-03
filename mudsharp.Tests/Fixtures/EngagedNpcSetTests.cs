using Mucka.ViewModels;
using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// The "which of these are we fighting" set behind the Here list's swords icons.
///
/// <para>The property worth pinning is not membership - that is a HashSet - but <b>when
/// <see cref="EngagedNpcSet.Update"/> reports a change</b>. Its caller re-walks the whole Here list
/// on true, and Update runs on every combat event, every FES heartbeat and every 1 Hz tick, so a
/// spurious true puts that walk on the typing path several times a second (Invariant #1).</para>
/// </summary>
public sealed class EngagedNpcSetTests
{
    private static FightSnapshot Fight(string npcName, bool resolved = false)
        => new(
            npcName, NpcGroups.Normalize(npcName), Weapon: "axe0", NpcWeapon: null,
            YouHits: 0, YouMisses: 0, TheyHits: 0, TheyMisses: 0,
            ApproxDamageDone: 0, ApproxDamageTaken: 0, Duration: TimeSpan.Zero,
            Outcome: resolved ? FightOutcome.Kill : FightOutcome.Unresolved,
            IsResolved: resolved,
            EndedUtc: resolved ? DateTime.UtcNow : null,
            RecentYourSwings: [], RecentTheirSwings: []);

    [Fact]
    public void Empty_ContainsNothing()
    {
        var set = new EngagedNpcSet();
        Assert.False(set.Contains("rat0"));
        Assert.False(set.Contains(null));
        Assert.False(set.Contains(""));
    }

    [Fact]
    public void FirstOpenFight_IsAChange()
    {
        var set = new EngagedNpcSet();
        Assert.True(set.Update([Fight("rat0")]));
        Assert.True(set.Contains("rat0"));
    }

    [Fact]
    public void RepeatingTheSameSnapshot_IsNotAChange()
    {
        // THE property. Most refreshes say exactly what the last one said.
        var set = new EngagedNpcSet();
        set.Update([Fight("rat0"), Fight("rat1")]);
        Assert.False(set.Update([Fight("rat0"), Fight("rat1")]));
        // Order is not membership: the aggregator is free to reorder its fight list.
        Assert.False(set.Update([Fight("rat1"), Fight("rat0")]));
    }

    [Fact]
    public void ResolvedFightsAreNotEngaged()
    {
        var set = new EngagedNpcSet();
        Assert.False(set.Update([Fight("rat0", resolved: true)]));
        Assert.False(set.Contains("rat0"));
    }

    [Fact]
    public void AFightResolving_IsAChangeAndDropsIt()
    {
        var set = new EngagedNpcSet();
        set.Update([Fight("rat0")]);
        Assert.True(set.Update([Fight("rat0", resolved: true)]));
        Assert.False(set.Contains("rat0"));
    }

    [Fact]
    public void OneJoiningWhileAnotherResolves_IsAChange()
    {
        // THE reason this type replaced a count-compare. The count stays at one across this refresh
        // while the membership changes completely, so a count-only check reports no change: rat1
        // keeps a grey icon while it is hitting the player, and the dead rat0 keeps a red one.
        var set = new EngagedNpcSet();
        set.Update([Fight("rat0")]);
        Assert.True(set.Update([Fight("rat0", resolved: true), Fight("rat1")]));
        Assert.False(set.Contains("rat0"));
        Assert.True(set.Contains("rat1"));
    }

    [Fact]
    public void EncounterEnding_ClearsEverything()
    {
        var set = new EngagedNpcSet();
        set.Update([Fight("rat0"), Fight("rat1")]);
        Assert.True(set.Update([]));
        Assert.Equal(0, set.Count);
        Assert.False(set.Update([]));
    }

    [Fact]
    public void BlankNamesAreIgnored()
    {
        var set = new EngagedNpcSet();
        Assert.False(set.Update([Fight("   ")]));
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void MembershipIsCaseInsensitive()
    {
        // FEI and the combat tracker both print the game's own spelling, but nothing guarantees the
        // two agree on case, and a miss here would leave a live opponent's icon grey.
        var set = new EngagedNpcSet();
        set.Update([Fight("Rat0")]);
        Assert.True(set.Contains("rat0"));
    }

    [Fact]
    public void DuplicateNamesInOneSnapshot_AreDegenerateInputAndStillSettle()
    {
        // Purely an input-robustness guard, and deliberately NOT evidence that this happens: both
        // CombatStatsAggregator and FightHistoryRecorder hold their fights in a
        // Dictionary<string, FightAccumulator> keyed by NPC name, so one snapshot cannot contain two
        // fights with the same name. (That keying has a real cost elsewhere - two coots in a room
        // share one name and therefore one fight record - but it is exactly what makes this state
        // unreachable here.) Kept only so a malformed list settles instead of reporting a change
        // forever.
        var set = new EngagedNpcSet();
        Assert.True(set.Update([Fight("rat0"), Fight("rat0")]));
        Assert.False(set.Update([Fight("rat0"), Fight("rat0")]));
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void ADescribedCreature_MatchesTheHereListsBareId()
    {
        // The bug this exists for, observed on screen 2026-09-02: fighting the large rat0 in a room of
        // four rats, the terminal and the rail both said "large rat0" while the Here list said "rat0",
        // so rat0 alone kept a grey swords icon while it was hitting the player.
        var set = new EngagedNpcSet();
        set.Update([Fight("large rat0")]);

        Assert.True(set.Contains("rat0"));
        // And the name the tracker actually saw still matches, since either spelling can reach here.
        Assert.True(set.Contains("large rat0"));
    }

    [Fact]
    public void AnUnnumberedMob_DoesNotMatchOnItsLastWord()
    {
        // The safety rule behind the digit test. A Here row reading "bat" must NOT light up because a
        // "giant cave bat" is engaged - an instance NUMBER is unique in a room and settles identity on
        // its own, but an ordinary last word settles nothing.
        var set = new EngagedNpcSet();
        set.Update([Fight("giant cave bat")]);

        Assert.False(set.Contains("bat"));
        Assert.False(set.Contains("cave bat"));
        Assert.True(set.Contains("giant cave bat"));
    }

    [Fact]
    public void ResolvingADescribedFight_ClearsTheBareIdToo()
    {
        // Both keys have to go together: leaving the id behind would keep a red swords icon on a
        // creature that is already dead, which is the same class of failure as the grey-on-live one.
        var set = new EngagedNpcSet();
        set.Update([Fight("large rat0")]);

        Assert.True(set.Update([Fight("large rat0", resolved: true)]));
        Assert.False(set.Contains("rat0"));
        Assert.False(set.Contains("large rat0"));
    }

    [Fact]
    public void ADescribedNameRepeated_StillReportsNoChange()
    {
        // The unchanged-refresh property this whole class is built around has to survive the second
        // key: two entries per fight must compare equal to two entries per fight, not report a change
        // on every heartbeat and put the Here-list walk back on the typing path (Invariant #1).
        var set = new EngagedNpcSet();
        Assert.True(set.Update([Fight("large rat0"), Fight("rat1")]));
        Assert.False(set.Update([Fight("large rat0"), Fight("rat1")]));
        Assert.Equal(3, set.Count);
    }
}
