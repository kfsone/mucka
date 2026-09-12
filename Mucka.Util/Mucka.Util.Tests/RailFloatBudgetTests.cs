using Mucka.Combat;

namespace Mucka.Util.Tests;

/// <summary>
/// Coverage for the Combat Rail's motion budget - how many damage floats may be in the air at once
/// and what gets shed when more arrive than that.
///
/// <para>The rule being defended is the rail's own premise: it is a glance instrument, and the
/// player's eye belongs on the terminal text. At MUD2's 2000 ms tick with up to seven observed
/// simultaneous opponents, an unbudgeted feed is permanent motion in the corner of the eye. These
/// tests pin the two things that make the cap survive contact with a pack fight - that a landed
/// blow is never lost to make room for a miss, and that a single anchor cannot be smeared with a
/// stack of numbers.</para>
/// </summary>
public sealed class RailFloatBudgetTests
{
    private static readonly DateTime T0 = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
    private static DateTime At(int ms) => T0.AddMilliseconds(ms);

    private const int Player = RailFloat.PlayerAnchor;

    // -- the cap ------------------------------------------------------------------

    [Fact]
    public void FloatsGetDistinctPoolSlots_UpToTheCap()
    {
        var budget = new RailFloatBudget();
        var slots = new HashSet<int>();

        for (var i = 0; i < RailFloatBudget.MaxInFlight; i++)
        {
            // One per anchor, so the per-anchor cap is not what is being measured here.
            var grant = budget.Admit(RailFloatKind.OutgoingHit, anchor: i, At(i));
            Assert.NotNull(grant);
            Assert.True(slots.Add(grant!.Value.PoolSlot), "the same pool slot was handed out twice");
        }

        Assert.Equal(RailFloatBudget.MaxInFlight, budget.InFlight(At(10)));
    }

    [Fact]
    public void AMissIsDroppedOutright_WhenEverythingInTheAirIsALandedBlow()
    {
        var budget = new RailFloatBudget();
        for (var i = 0; i < RailFloatBudget.MaxInFlight; i++)
            budget.Admit(RailFloatKind.OutgoingHit, anchor: i, At(i));

        // The whole point of the shed order: the least consequential thing on the panel does not get
        // to displace the number the player's flee decision turns on.
        Assert.Null(budget.Admit(RailFloatKind.IncomingMiss, anchor: Player, At(50)));
        Assert.Equal(RailFloatBudget.MaxInFlight, budget.InFlight(At(50)));
    }

    [Fact]
    public void AHitShedsTheOldestMiss_NotTheOldestFloat()
    {
        var budget = new RailFloatBudget();
        var oldestHit = budget.Admit(RailFloatKind.OutgoingHit, anchor: 0, At(0))!.Value;
        var oldMiss = budget.Admit(RailFloatKind.OutgoingMiss, anchor: 1, At(10))!.Value;
        var newerMiss = budget.Admit(RailFloatKind.IncomingMiss, anchor: 2, At(20))!.Value;
        budget.Admit(RailFloatKind.IncomingHit, anchor: 3, At(30));

        var arriving = budget.Admit(RailFloatKind.IncomingHit, anchor: 4, At(40));

        Assert.NotNull(arriving);
        // Age decides only among misses. The oldest float in the air is a hit and survives.
        Assert.Equal(oldMiss.PoolSlot, arriving!.Value.PoolSlot);
        Assert.NotEqual(oldestHit.PoolSlot, arriving.Value.PoolSlot);
        Assert.NotEqual(newerMiss.PoolSlot, arriving.Value.PoolSlot);
    }

    [Fact]
    public void AHitShedsTheOldestHit_OnlyWhenThereIsNoMissToTake()
    {
        var budget = new RailFloatBudget();
        var oldest = budget.Admit(RailFloatKind.OutgoingHit, anchor: 0, At(0))!.Value;
        for (var i = 1; i < RailFloatBudget.MaxInFlight; i++)
            budget.Admit(RailFloatKind.IncomingHit, anchor: i, At(i * 10));

        var arriving = budget.Admit(RailFloatKind.OutgoingHit, anchor: 9, At(500));

        Assert.NotNull(arriving);
        Assert.Equal(oldest.PoolSlot, arriving!.Value.PoolSlot);
    }

    // -- lanes, and the one-anchor smear -----------------------------------------

    [Fact]
    public void TwoFloatsAtOneAnchor_GetDifferentLanes()
    {
        var budget = new RailFloatBudget();
        var first = budget.Admit(RailFloatKind.IncomingHit, Player, At(0))!.Value;
        var second = budget.Admit(RailFloatKind.IncomingHit, Player, At(5))!.Value;

        // Every incoming blow in a pack fight lands on the SAME anchor in the same tick, so without
        // lanes the stamina seal would carry two numbers on the same pixels.
        Assert.Equal(0, first.Lane);
        Assert.Equal(1, second.Lane);
        Assert.NotEqual(first.PoolSlot, second.PoolSlot);
    }

    [Fact]
    public void AThirdFloatAtOneAnchor_ReplacesTheOldestThere_AndKeepsItsLane()
    {
        var budget = new RailFloatBudget();
        var first = budget.Admit(RailFloatKind.IncomingHit, Player, At(0))!.Value;
        budget.Admit(RailFloatKind.IncomingHit, Player, At(5));

        var third = budget.Admit(RailFloatKind.IncomingHit, Player, At(10));

        Assert.NotNull(third);
        Assert.Equal(first.PoolSlot, third!.Value.PoolSlot);
        Assert.Equal(first.Lane, third.Value.Lane);
        // Still two over the seal, never three.
        Assert.Equal(2, budget.InFlight(At(10)));
    }

    [Fact]
    public void ThePerAnchorCapDoesNotStarveOtherAnchors()
    {
        var budget = new RailFloatBudget();
        budget.Admit(RailFloatKind.IncomingHit, Player, At(0));
        budget.Admit(RailFloatKind.IncomingHit, Player, At(5));

        // Two blows on the player must not stop a hit on an opponent's own slot being drawn.
        Assert.NotNull(budget.Admit(RailFloatKind.OutgoingHit, anchor: 0, At(10)));
        Assert.Equal(3, budget.InFlight(At(10)));
    }

    [Fact]
    public void AnArrivingMissAtAFullAnchor_IsDroppedRatherThanDisplacingAHitThere()
    {
        var budget = new RailFloatBudget();
        budget.Admit(RailFloatKind.IncomingHit, Player, At(0));
        budget.Admit(RailFloatKind.IncomingHit, Player, At(5));

        Assert.Null(budget.Admit(RailFloatKind.IncomingMiss, Player, At(10)));
    }

    // -- retiring, and the stale-completion hazard -------------------------------

    [Fact]
    public void RetiringAFloat_FreesItsSlotForReuse()
    {
        var budget = new RailFloatBudget();
        var grant = budget.Admit(RailFloatKind.OutgoingHit, anchor: 0, At(0))!.Value;
        Assert.Equal(1, budget.InFlight(At(1)));

        budget.Retire(grant.PoolSlot, grant.Token);

        Assert.Equal(0, budget.InFlight(At(1)));
    }

    [Fact]
    public void AStaleCompletion_CannotRetireTheFloatThatTookOverItsSlot()
    {
        // A CompositionScopedBatch cannot be cancelled once ended, so a shed float's Completed still
        // fires. Without the token it would free the slot the replacement is using and the panel
        // would carry a float the budget believes is gone - the exact hazard TickSweep's generation
        // counter exists for.
        var budget = new RailFloatBudget();
        var shed = budget.Admit(RailFloatKind.IncomingMiss, Player, At(0))!.Value;
        budget.Admit(RailFloatKind.IncomingMiss, Player, At(5));
        var replacement = budget.Admit(RailFloatKind.IncomingHit, Player, At(10))!.Value;
        Assert.Equal(shed.PoolSlot, replacement.PoolSlot);

        budget.Retire(shed.PoolSlot, shed.Token);

        Assert.Equal(2, budget.InFlight(At(10)));
    }

    [Fact]
    public void AFloatExpiresOnItsOwn_IfNoCompletionEverArrives()
    {
        // The host retires from the animation's completion callback; a torn-down compositor never
        // sends one, and a permanently-held slot would shrink the budget for the rest of the session.
        var budget = new RailFloatBudget();
        budget.Admit(RailFloatKind.OutgoingHit, anchor: 0, At(0));

        Assert.Equal(1, budget.InFlight(T0 + RailFloatBudget.Lifetime - TimeSpan.FromMilliseconds(1)));
        Assert.Equal(0, budget.InFlight(T0 + RailFloatBudget.Lifetime));
    }

    // -- resets ------------------------------------------------------------------

    [Fact]
    public void Clear_EmptiesTheAir()
    {
        var budget = new RailFloatBudget();
        budget.Admit(RailFloatKind.OutgoingHit, anchor: 0, At(0));
        budget.Admit(RailFloatKind.IncomingHit, Player, At(0));

        budget.Clear();

        Assert.Equal(0, budget.InFlight(At(1)));
    }

    [Fact]
    public void ClearOpponentAnchored_LeavesThePlayersOwnFloatsAlone()
    {
        // A reordered roster makes slot N a different creature, so opponent floats are now crediting
        // the wrong thing. The stamina seal has not moved and its numbers are still true.
        var budget = new RailFloatBudget();
        budget.Admit(RailFloatKind.OutgoingHit, anchor: 0, At(0));
        budget.Admit(RailFloatKind.OutgoingMiss, anchor: 1, At(0));
        budget.Admit(RailFloatKind.IncomingHit, Player, At(0));

        var freed = budget.ClearOpponentAnchored();

        Assert.Equal(1, budget.InFlight(At(1)));
        // The host resets exactly the elements named in the mask - a player float caught in a
        // blanket reset would have its completion callback made stale and its slot held until the
        // lifetime expiry.
        Assert.Equal(2, System.Numerics.BitOperations.PopCount((uint)freed));
    }
}

/// <summary>
/// Coverage for what a damage float is allowed to SAY - which is where this feature is most likely
/// to quietly start lying.
/// </summary>
public sealed class RailFloatTextTests
{
    [Fact]
    public void AnOutgoingHitPrintsTheGamesOwnBracket_NeverAMidpoint()
    {
        // "You hit the rat (5-9)." MUD2 does not tell the player how much they dealt - it tells them
        // a bucket. Averaging that to "7" would present a range as a measurement, and the observed
        // buckets are not even a fixed ladder (1-4, 5-9, 10-14, 15-19, 20-29, and two 30-39s on
        // record), so there is nothing to interpolate against either.
        Assert.Equal("5-9", RailFloatText.Outgoing(5, 9));
        Assert.Equal("20-29", RailFloatText.Outgoing(20, 29));
        Assert.Equal("30-39", RailFloatText.Outgoing(30, 39));
    }

    [Fact]
    public void AZeroWidthRangePrintsAsOneFigure_NotAsSixToSix()
    {
        // "You hit the banshee (6)." - MUD2 occasionally prints a single figure instead of a bracket,
        // and the tracker carries that as a range of width zero. WHY it does so is not known.
        // Deliberately named for the INPUT rather than a cause, so the expectation stays true if
        // the cause is ever found - and cannot re-seed a false one if it is not.
        Assert.Equal("6", RailFloatText.Outgoing(6, 6));
    }

    [Fact]
    public void AnOutgoingHitWithNoNumbers_PrintsNothingAtAll()
    {
        // No observed wording reaches here, but a float that has to guess is not drawn.
        Assert.Null(RailFloatText.Outgoing(null, null));
        Assert.Null(RailFloatText.Outgoing(null, 9));
    }

    [Fact]
    public void IncomingDamageAndDeducedGainAreSigned_BecauseBothAreRealNumbers()
    {
        // "The rat hits you (58/62)." is absolute stamina, so the delta against the previous reading
        // is exact - the sign is honest here in a way it would not be on the bracket above.
        Assert.Equal("-5", RailFloatText.Incoming(5));
        Assert.Equal("+14", RailFloatText.Gain(14));
    }
}
