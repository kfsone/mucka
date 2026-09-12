using Mucka.Combat;

namespace Mucka.Util.Tests;

/// <summary>
/// Coverage for the client's shared blink cycle. Small, but it pins the three properties the whole
/// doctrine rests on: the period is one combat tick, the phase is a pure function of the wall clock
/// so nothing has to co-ordinate two blinking elements, and antiphase really is opposite.
/// </summary>
public sealed class BlinkTests
{
    private static DateTime AtMs(long unixMs)
        => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;

    [Fact]
    public void ThePeriodIsOneCombatTick()
    {
        // Not a free parameter: a combat alarm beats with the thing that is hitting the player, and
        // the 1 Hz flush that repaints it samples this at exactly twice per cycle. Change this and
        // the blink either stutters or stops agreeing with the game.
        Assert.Equal(CombatTiming.TickMilliseconds, Blink.BlinkPeriodMilliseconds);
    }

    [Fact]
    public void ThePhaseHoldsForHalfACycleAndThenFlips()
    {
        var half = (long)(Blink.BlinkPeriodMilliseconds / 2);

        // Anchored on a boundary so the assertions are about the cycle, not about where "now" landed.
        var start = AtMs(0);
        var first = Blink.PhaseOn(start);

        Assert.Equal(first, Blink.PhaseOn(AtMs(half - 1)));
        Assert.NotEqual(first, Blink.PhaseOn(AtMs(half)));
        Assert.NotEqual(first, Blink.PhaseOn(AtMs((half * 2) - 1)));
        Assert.Equal(first, Blink.PhaseOn(AtMs(half * 2)));
    }

    [Fact]
    public void InvertedIsAlwaysTheOpposite()
    {
        // Two alarms in phase read as one flashing region; this is what keeps them two things.
        for (var ms = 0L; ms < (long)Blink.BlinkPeriodMilliseconds * 3; ms += 137)
        {
            var at = AtMs(ms);
            Assert.NotEqual(Blink.PhaseOn(at), Blink.PhaseOn(at, inverted: true));
        }
    }

    [Fact]
    public void ThePhaseIsAFunctionOfTheClockAlone()
    {
        // No internal counter, no start time, no state: the same instant must always give the same
        // answer, which is what lets two elements agree without either one owning the cycle - and
        // what lets a late repaint show the phase it is late INTO rather than resuming a sequence.
        var at = AtMs(1_234_567_890_123);
        var first = Blink.PhaseOn(at);
        for (var i = 0; i < 5; i++)
            Assert.Equal(first, Blink.PhaseOn(at));
    }

    [Fact]
    public void LocalAndUtcInstantsAgree()
    {
        // The phase is taken off Unix ms, so a DateTime carrying a local Kind must not land on the
        // other half of the cycle from the same instant expressed as UTC.
        var utc = AtMs(1_700_000_001_500);
        Assert.Equal(Blink.PhaseOn(utc), Blink.PhaseOn(utc.ToLocalTime()));
    }
}
