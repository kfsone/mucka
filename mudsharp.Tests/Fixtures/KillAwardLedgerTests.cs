using Mucka.ViewModels;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Pairing each fight ending with the score MUD2 announced for it. The frame is the whole test
/// surface: an ending still unpaired when its frame closes was never scored, and if it is allowed to
/// wait it takes the next award that arrives and every row after it is wrong by one, permanently -
/// <see cref="Replays_the_20260910_drift"/> is that failure, from the session that found it.
/// </summary>
public sealed class KillAwardLedgerTests
{
    private static readonly DateTime T0 = new(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);

    private static DateTime At(double seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void Pairs_an_award_with_the_ending_that_precedes_it()
    {
        //   You have killed the billy goat.
        //   (Persona saved on +118 = 12,459).
        var ledger = new KillAwardLedger();

        ledger.NoteEnding(encounterOrdinal: 16, "billy goat", At(0));
        Assert.True(ledger.NoteScoreRise(118));

        Assert.Equal(118, ledger.AwardFor(16, "billy goat", At(0)));
    }

    [Fact]
    public void An_unscored_ending_cannot_steal_a_later_frames_award()
    {
        // The flight is not scored (MUD2 prints nothing), the kill in the NEXT frame is, and the award
        // must land on the kill.
        var ledger = new KillAwardLedger();

        ledger.NoteEnding(encounterOrdinal: 13, "thief", At(0));
        ledger.NoteFrameClosed();

        ledger.NoteEnding(encounterOrdinal: 14, "thief", At(2));
        Assert.True(ledger.NoteScoreRise(367));
        ledger.NoteFrameClosed();

        Assert.Equal(367, ledger.AwardFor(14, "thief", At(2)));
        Assert.Null(ledger.AwardFor(13, "thief", At(0)));
    }

    /// <summary>
    /// The 2026-09-10 session, from <c>fights</c> and <c>score_events</c> in mucka.db: five thief
    /// endings in five consecutive frames, two billy goat endings, a zombie7 kill. Four thief flights
    /// and the goat's flight scored nothing - the totals leave no room for an award between them
    /// (<c>11,974 + 367 = 12,341</c>, <c>12,341 + 118 = 12,459</c>). Before the frame scoping the panel
    /// drew +367 and +118 on thief FLED rows three and one endings early.
    /// </summary>
    [Fact]
    public void Replays_the_20260910_drift()
    {
        var ledger = new KillAwardLedger();

        // 08:03:23  The thief has fled by going northeast.   (Persona saved on +20 = 11,974).
        ledger.NoteEnding(9, "thief", At(203));
        Assert.True(ledger.NoteScoreRise(20));
        ledger.NoteFrameClosed();

        // 08:06:03 .. 08:06:09  four more thief flights, none of them scored.
        var unscored = new[] { (10, 363.0), (11, 365.0), (12, 367.0), (13, 369.0) };
        foreach (var (encounter, at) in unscored)
        {
            ledger.NoteEnding(encounter, "thief", At(at));
            ledger.NoteFrameClosed();
        }

        // 08:06:11  You have killed the thief.       (Persona saved on +367 = 12,341).
        ledger.NoteEnding(14, "thief", At(371));
        Assert.True(ledger.NoteScoreRise(367));
        ledger.NoteFrameClosed();

        // 08:08:51  The billy goat has fled by going west.   - not scored.
        ledger.NoteEnding(15, "billy goat", At(531));
        ledger.NoteFrameClosed();

        // 08:09:53  You have killed the billy goat.  (Persona saved on +118 = 12,459).
        ledger.NoteEnding(16, "billy goat", At(593));
        Assert.True(ledger.NoteScoreRise(118));
        ledger.NoteFrameClosed();

        // 08:12:59  You have killed the zombie7.     (Persona saved on +38 = 12,547).
        ledger.NoteEnding(17, "zombie7", At(779));
        Assert.True(ledger.NoteScoreRise(38));
        ledger.NoteFrameClosed();

        // Every award on its own row, and every unscored ending blank rather than borrowing one.
        Assert.Equal(20, ledger.AwardFor(9, "thief", At(203)));
        foreach (var (encounter, at) in unscored)
            Assert.Null(ledger.AwardFor(encounter, "thief", At(at)));
        Assert.Equal(367, ledger.AwardFor(14, "thief", At(371)));
        Assert.Null(ledger.AwardFor(15, "billy goat", At(531)));
        Assert.Equal(118, ledger.AwardFor(16, "billy goat", At(593)));
        Assert.Equal(38, ledger.AwardFor(17, "zombie7", At(779)));
    }

    [Fact]
    public void Swallows_a_task_payout_so_the_kill_keeps_its_own_award()
    {
        //   You have killed the water-snake4.
        //   You have completed a Task.
        //   (Persona saved on +100 = 7,058).     <- the task's flat payout, pays FIRST
        //   (Persona saved on +84 = 7,142).      <- what the creature was worth
        var ledger = new KillAwardLedger();

        ledger.NoteEnding(encounterOrdinal: 2, "water-snake4", At(0));
        ledger.NoteTaskCompleted();
        Assert.False(ledger.NoteScoreRise(100));
        Assert.True(ledger.NoteScoreRise(84));
        ledger.NoteFrameClosed();

        Assert.Equal(84, ledger.AwardFor(2, "water-snake4", At(0)));
    }

    [Fact]
    public void An_unspent_task_payout_cannot_swallow_a_later_frames_award()
    {
        var ledger = new KillAwardLedger();

        ledger.NoteTaskCompleted();
        ledger.NoteFrameClosed();

        ledger.NoteEnding(encounterOrdinal: 5, "rat18", At(10));
        Assert.True(ledger.NoteScoreRise(22));

        Assert.Equal(22, ledger.AwardFor(5, "rat18", At(10)));
    }

    [Fact]
    public void Two_deaths_in_one_frame_take_their_awards_in_order()
    {
        // A pack finishing together, or poison: several endings and several awards inside one frame,
        // which is the case the queue exists for at all.
        var ledger = new KillAwardLedger();

        ledger.NoteEnding(6, "rat16", At(0));
        ledger.NoteEnding(6, "rat21", At(0.1));
        Assert.True(ledger.NoteScoreRise(22));
        Assert.True(ledger.NoteScoreRise(30));
        ledger.NoteFrameClosed();

        Assert.Equal(22, ledger.AwardFor(6, "rat16", At(0)));
        Assert.Equal(30, ledger.AwardFor(6, "rat21", At(0.1)));
    }

    [Fact]
    public void A_creature_killed_twice_keeps_both_awards()
    {
        // Owner, 2026-09-07: kill rat8, take the +22, drink a vial, "The rat8 has been summoned.",
        // kill it again next combat tick for another +22 - same instance name, same encounter, two
        // awards. The kill timestamp is what separates them.
        var ledger = new KillAwardLedger();

        ledger.NoteEnding(4, "rat8", At(0));
        Assert.True(ledger.NoteScoreRise(22));
        ledger.NoteFrameClosed();

        ledger.NoteEnding(4, "rat8", At(2));
        Assert.True(ledger.NoteScoreRise(22));
        ledger.NoteFrameClosed();

        Assert.Equal(22, ledger.AwardFor(4, "rat8", At(0)));
        Assert.Equal(22, ledger.AwardFor(4, "rat8", At(2)));
    }
}
