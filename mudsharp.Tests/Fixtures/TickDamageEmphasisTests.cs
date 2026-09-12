using Mucka.Core;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// The badge-name emphasis rule: bold for the tick a blow landed on, and never for the 50 ms that a
/// frame arriving just before a boundary would otherwise get.
///
/// <para>The carry-forward window is fixed at roughly the last 300ms before a tick boundary; its
/// size is a requirement rather than a tuning choice.</para>
/// </summary>
public sealed class TickDamageEmphasisTests
{
    private const double Tick = 2000.0;
    private static readonly DateTime Anchor = new(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);

    private static DateTime At(double ms) => Anchor.AddMilliseconds(ms);

    [Fact]
    public void Never_emphasises_a_subject_that_has_taken_nothing()
        => Assert.False(TickDamageEmphasis.IsOn(null, At(500), Anchor));

    [Fact]
    public void Emphasis_lasts_to_the_end_of_the_tick_the_blow_landed_in()
    {
        // A blow 200 ms into a tick holds for the remaining 1800 ms and stops there - not a rolling
        // window from the arrival, which would run 200 ms into the next tick and leave the panel's
        // badges each on their own phase.
        var blow = At(200);
        Assert.True(TickDamageEmphasis.IsOn(blow, At(200), Anchor));
        Assert.True(TickDamageEmphasis.IsOn(blow, At(1999), Anchor));
        Assert.False(TickDamageEmphasis.IsOn(blow, At(2000), Anchor));
    }

    [Fact]
    public void A_blow_in_the_last_sixth_of_a_tick_is_carried_into_the_next_one()
    {
        // 1900 ms in: 100 ms of its own tick left, too brief a flash to show on its own. It takes the
        // following tick instead, so the emphasis runs 100 + 2000 ms.
        var blow = At(1900);
        Assert.True(TickDamageEmphasis.IsOn(blow, At(2100), Anchor));   // past the boundary
        Assert.True(TickDamageEmphasis.IsOn(blow, At(3999), Anchor));
        Assert.False(TickDamageEmphasis.IsOn(blow, At(4000), Anchor));
    }

    [Fact]
    public void The_carry_boundary_is_a_sixth_of_a_tick()
    {
        Assert.Equal(Tick / 6.0, TickDamageEmphasis.CarryForwardMs, 6);

        // Exactly on the boundary does NOT carry: 333.33 ms is already long enough to see, and making
        // the comparison inclusive would carry every blow that landed on a lattice point exactly.
        //
        // Three decimal places, not six: DateTime holds 100 ns ticks, so At(1666.6666...) lands on
        // 1666.6667 and the remainder comes back 333.3334. That is the clock's resolution showing
        // through, not slack in the rule.
        var onBoundary = At(Tick - TickDamageEmphasis.CarryForwardMs);
        Assert.Equal(TickDamageEmphasis.CarryForwardMs, TickDamageEmphasis.HoldMs(onBoundary, Anchor), 3);

        var justInside = At(Tick - TickDamageEmphasis.CarryForwardMs + 1);
        Assert.True(TickDamageEmphasis.HoldMs(justInside, Anchor) > Tick);
    }

    [Fact]
    public void No_blow_is_ever_emphasised_for_less_than_the_carry_window()
    {
        // The whole point of the rule, asserted as the invariant rather than at one arrival: sweep a
        // blow across a full tick and the hold never dips below a sixth of one.
        for (var offset = 0.0; offset < Tick; offset += 7.0)
        {
            var hold = TickDamageEmphasis.HoldMs(At(offset), Anchor);
            Assert.True(hold >= TickDamageEmphasis.CarryForwardMs,
                $"a blow {offset} ms into the tick would be emphasised for only {hold} ms");
        }
    }

    [Fact]
    public void Without_a_lattice_it_falls_back_to_a_rolling_full_tick()
    {
        // The first couple of swings of a session, before TickPhase will offer an anchor. A full tick
        // from the arrival cannot under-bold, which is the direction of error that is allowed.
        var blow = At(0);
        Assert.Equal(Tick, TickDamageEmphasis.HoldMs(blow, tickAnchorUtc: null));
        Assert.True(TickDamageEmphasis.IsOn(blow, At(1999), tickAnchorUtc: null));
        Assert.False(TickDamageEmphasis.IsOn(blow, At(2000), tickAnchorUtc: null));
    }

    [Fact]
    public void A_blow_timestamped_marginally_ahead_of_now_is_still_emphasised()
    {
        // Clock artefact, not evidence that the blow has not happened. Clamped rather than allowed to
        // go negative - the same clamp SidePanelViewModel applies to health ages.
        Assert.True(TickDamageEmphasis.IsOn(At(510), At(500), Anchor));
    }

    [Fact]
    public void The_lattice_is_read_modulo_the_tick_so_an_old_anchor_still_works()
    {
        // TickPhase's anchor is a session-wide reference and can be many minutes behind. The rule must
        // depend only on the phase, not on how far back the reference sits.
        var oldAnchor = Anchor.AddMilliseconds(-Tick * 900);
        Assert.Equal(
            TickDamageEmphasis.HoldMs(At(200), Anchor),
            TickDamageEmphasis.HoldMs(At(200), oldAnchor), 6);
    }
}
