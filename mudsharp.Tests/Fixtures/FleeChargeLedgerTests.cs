using Mucka.ViewModels;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Pairing the player's flights with what MUD2 charged for them. The ordering is the whole test
/// surface: the charge arrives BEFORE the flee line in every capture on file, the opposite of how a
/// kill's award arrives, and a flee that finds nothing held must record nothing rather than wait.
/// </summary>
public sealed class FleeChargeLedgerTests
{
    private static readonly DateTime T0 = new(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);

    private static DateTime At(double ms) => T0.AddMilliseconds(ms);

    [Fact]
    public void Pairs_a_charge_that_arrived_before_the_flee_line()
    {
        //   (Persona saved on -438 = 1,656).
        //   Croquet mallet dropped.
        //   You have fled by going east.
        var ledger = new FleeChargeLedger();

        ledger.NoteScoreFall(438);
        Assert.True(ledger.NoteFled(encounterOrdinal: 3, At(40)));

        Assert.Equal(438, ledger.ChargeFor(3, At(40)));
    }

    [Fact]
    public void Only_the_first_fall_of_a_frame_is_the_flight_cost()
    {
        // Fleeing can trigger two score falls in one frame -- for example fleeing from a shark and
        // then drowning (one for flight, one for a non-combat death). Only the first is the flight cost.
        var ledger = new FleeChargeLedger();

        ledger.NoteScoreFall(371);
        ledger.NoteScoreFall(1200);   // the drowning
        ledger.NoteFled(encounterOrdinal: 0, At(45));

        Assert.Equal(371, ledger.ChargeFor(0, At(45)));
    }

    [Fact]
    public void A_fall_from_an_earlier_frame_is_not_attributed_to_a_later_flee()
    {
        // A death penalty, a failed task - falls that have nothing to do with leaving. The frame is
        // what separates them from a flight charge.
        var ledger = new FleeChargeLedger();

        ledger.NoteScoreFall(102);
        ledger.NoteFrameClosed();

        Assert.False(ledger.NoteFled(encounterOrdinal: 0, At(10)));
        Assert.Null(ledger.ChargeFor(0, At(10)));
    }

    [Fact]
    public void A_fall_and_the_flee_it_explains_pair_however_long_the_frame_takes_to_arrive()
    {
        // Pins the absence of any time window: same frame, an implausible delivery gap between the two
        // lines, and the prompt has not been seen yet.
        var ledger = new FleeChargeLedger();

        ledger.NoteScoreFall(438);
        Assert.True(ledger.NoteFled(encounterOrdinal: 3, At(30_000)));

        Assert.Equal(438, ledger.ChargeFor(3, At(30_000)));
    }

    [Fact]
    public void The_frames_own_fall_and_flee_still_pair_across_a_close()
    {
        // Both lines of the frame are processed before its close is, so a legitimate pairing is
        // already recorded by the time the close arrives.
        var ledger = new FleeChargeLedger();

        ledger.NoteScoreFall(573);
        Assert.True(ledger.NoteFled(encounterOrdinal: 8, At(5)));
        ledger.NoteFrameClosed();

        Assert.Equal(573, ledger.ChargeFor(8, At(5)));
    }

    [Fact]
    public void A_free_flee_records_nothing_and_does_not_wait_for_a_later_fall()
    {
        // MUD2 charges nothing below 6% of maximum stamina and prints nothing. The row draws no figure
        // - not a zero - and the flee must NOT lie in wait for the next fall in the frame, which is how
        // a free flight from a shark would have been priced at the drowning that followed it.
        var ledger = new FleeChargeLedger();

        Assert.False(ledger.NoteFled(encounterOrdinal: 2, At(0)));
        ledger.NoteScoreFall(1200);   // the drowning

        Assert.Null(ledger.ChargeFor(2, At(0)));
    }

    [Fact]
    public void Two_flights_keep_their_own_figures()
    {
        var ledger = new FleeChargeLedger();

        ledger.NoteScoreFall(438);
        ledger.NoteFled(1, At(10));

        ledger.NoteScoreFall(90);
        ledger.NoteFled(2, At(60_010));

        Assert.Equal(438, ledger.ChargeFor(1, At(10)));
        Assert.Equal(90, ledger.ChargeFor(2, At(60_010)));
    }

    [Fact]
    public void The_lookup_is_keyed_on_the_flees_own_instant()
    {
        var ledger = new FleeChargeLedger();
        ledger.NoteScoreFall(86);
        ledger.NoteFled(4, At(10));

        Assert.Null(ledger.ChargeFor(4, At(11)));    // a different flee
        Assert.Null(ledger.ChargeFor(5, At(10)));    // a different encounter
        Assert.Null(ledger.ChargeFor(4, null));
    }

    [Fact]
    public void Old_flights_are_evicted_rather_than_growing_without_bound()
    {
        var ledger = new FleeChargeLedger();

        for (var i = 0; i <= FleeChargeLedger.MaxRemembered; i++)
        {
            ledger.NoteScoreFall(i + 1);
            ledger.NoteFled(i, At((i * 10_000) + 10));
        }

        // The oldest is gone; the newest is intact. A dead-strip row that far back is behind the
        // "+N earlier" marker and cannot be seen anyway.
        Assert.Null(ledger.ChargeFor(0, At(10)));
        var last = FleeChargeLedger.MaxRemembered;
        Assert.Equal(last + 1, ledger.ChargeFor(last, At((last * 10_000) + 10)));
    }

    [Fact]
    public void A_rise_is_never_a_charge()
    {
        var ledger = new FleeChargeLedger();
        ledger.NoteScoreFall(0);
        ledger.NoteScoreFall(-50);
        Assert.False(ledger.NoteFled(0, At(10)));
        Assert.Null(ledger.ChargeFor(0, At(10)));
    }
}
