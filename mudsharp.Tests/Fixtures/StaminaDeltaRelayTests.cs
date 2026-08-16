using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Direct coverage of the stamina-delta relay extracted from SwingLedger/FightHistoryRecorder/
/// CombatStatsAggregator, which each hand-copied this arithmetic until 2026-08-16 - see
/// tools/combat/MECHANICS_NOTES.md's "Damage taken always showing 0.0" section for the bug this
/// exists to prevent. None of the three consumers' own test fixtures isolate this arithmetic; they
/// each prove it correct through their own class's event plumbing instead.
/// </summary>
public sealed class StaminaDeltaRelayTests
{
    [Fact]
    public void ResolveDelta_RelayFromObserve_AttributesOnlyThisHit()
    {
        var relay = new StaminaDeltaRelay();
        relay.Observe(100);           // pre-fight baseline
        relay.Observe(95);            // the generic stats scan fires first for the SAME line...
        var (delta, baseline) = relay.ResolveDelta(95);   // ...then the combat regex reaches this second

        Assert.Equal(5, delta);
        Assert.Equal(100, baseline);
    }

    [Fact]
    public void ResolveDelta_NoRelay_FallsBackToLastKnownDirectly()
    {
        var relay = new StaminaDeltaRelay();
        relay.Observe(50);
        // No Observe fired for this line (e.g. a hit to exactly 0, which the compact-stamina scan
        // skips) - ResolveDelta must diff directly against the last known value.
        var (delta, baseline) = relay.ResolveDelta(42);

        Assert.Equal(8, delta);
        Assert.Equal(50, baseline);
    }

    [Fact]
    public void ResolveDelta_StaleRelayNeverConsumed_DoesNotOutrankAFresherReading()
    {
        var relay = new StaminaDeltaRelay();
        relay.Observe(100);
        relay.Observe(90);   // relay now holds 100, pending against a combat event that never arrives
        relay.Observe(80);   // an unrelated later reading - relay is now 90, _lastKnown is 80

        var (delta, baseline) = relay.ResolveDelta(80);

        // _lastKnown (80) already equals currentStamina (80), so the equality guard is satisfied and
        // the (now stale, but still "the value immediately before this exact reading") relay of 90 is
        // used - not the much older 100. This is the guard's actual contract: it only ever protects
        // against a relay left over from BEFORE the last Observe, never a same-line coincidence.
        Assert.Equal(10, delta);
        Assert.Equal(90, baseline);
    }

    [Fact]
    public void ResolveDelta_NegativeDelta_ReportsNoDamageButStillReturnsTheBaseline()
    {
        var relay = new StaminaDeltaRelay();
        relay.Observe(100);
        relay.Observe(90);
        // Regen/heal landed in the same tick and outran the hit - stamina went UP across the blow.
        // Delta is null (no honest damage figure), but Baseline is the raw pre-hit value regardless -
        // it is each CONSUMER's job to null it alongside Delta if its own contract wants the pair to
        // agree (see SwingLedger.ResolveDamageTakenLocked's own wrapper), not this type's.
        var (delta, baseline) = relay.ResolveDelta(95);

        Assert.Null(delta);
        Assert.Equal(90, baseline);
    }

    [Fact]
    public void ResolveDelta_NullReading_ReturnsNullWithoutConsumingTheRelay()
    {
        var relay = new StaminaDeltaRelay();
        relay.Observe(100);
        relay.Observe(90);

        var (delta, baseline) = relay.ResolveDelta(null);
        Assert.Null(delta);
        Assert.Null(baseline);

        // The pending relay must still be intact for the next real reading.
        var (delta2, baseline2) = relay.ResolveDelta(90);
        Assert.Equal(10, delta2);
        Assert.Equal(100, baseline2);
    }

    [Fact]
    public void LastKnown_ReflectsTheMostRecentObserve()
    {
        var relay = new StaminaDeltaRelay();
        Assert.Null(relay.LastKnown);

        relay.Observe(77);
        Assert.Equal(77, relay.LastKnown);

        relay.Observe(null);   // a null reading is a no-op, not a reset
        Assert.Equal(77, relay.LastKnown);
    }

    [Fact]
    public void ResolveDelta_AlsoAdvancesLastKnown()
    {
        var relay = new StaminaDeltaRelay();
        relay.Observe(100);
        relay.ResolveDelta(95);

        Assert.Equal(95, relay.LastKnown);
    }
}
