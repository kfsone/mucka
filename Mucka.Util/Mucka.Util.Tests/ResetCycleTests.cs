using Mucka.Combat;

namespace Mucka.Util.Tests;

/// <summary>
/// <see cref="ResetCycle"/>: the dead strip's reset ordinal moves at a reset and at nothing else. The
/// numbers follow the wire table's banners: 127229, then 127230 after a reset, repeated on every
/// login inside it.
/// </summary>
public sealed class ResetCycleTests
{
    [Fact]
    public void RelogsInsideOneReset_DoNotAdvanceIt()
    {
        var cycle = new ResetCycle();

        cycle.NoteResetNumber(127230);
        cycle.NoteResetNumber(127230);
        cycle.NoteResetNumber(127230);

        Assert.Equal(0, cycle.Ordinal);
    }

    [Fact]
    public void ALanding_AdvancesIt_AndTheBannerThatConfirmsItDoesNotAgain()
    {
        var cycle = new ResetCycle();
        cycle.NoteResetNumber(127229);

        cycle.NoteLanding();
        Assert.Equal(1, cycle.Ordinal);

        cycle.NoteResetNumber(127230);
        Assert.Equal(1, cycle.Ordinal);
    }

    [Fact]
    public void ALandingTheNextBannerContradicts_IsWithdrawn()
    {
        var cycle = new ResetCycle();
        cycle.NoteResetNumber(127229);

        cycle.NoteLanding();
        cycle.NoteResetNumber(127229);

        Assert.Equal(0, cycle.Ordinal);
    }

    [Fact]
    public void ANewNumberWithNoLanding_AdvancesIt()
    {
        var cycle = new ResetCycle();
        cycle.NoteResetNumber(127229);

        cycle.NoteResetNumber(127230);

        Assert.Equal(1, cycle.Ordinal);
    }

    [Fact]
    public void TwoResets_EachLandedAndConfirmed_CountTwice()
    {
        var cycle = new ResetCycle();
        cycle.NoteResetNumber(127229);

        cycle.NoteLanding();
        cycle.NoteResetNumber(127230);
        cycle.NoteLanding();
        cycle.NoteResetNumber(127231);

        Assert.Equal(2, cycle.Ordinal);
    }

    [Fact]
    public void AnUnconfirmedLanding_DoesNotSwallowTheNextUnlandedReset()
    {
        var cycle = new ResetCycle();
        cycle.NoteResetNumber(127229);

        cycle.NoteLanding();
        cycle.NoteResetNumber(127230);   // confirms the landing
        cycle.NoteResetNumber(127231);   // a reset with no landing seen

        Assert.Equal(2, cycle.Ordinal);
    }
}
