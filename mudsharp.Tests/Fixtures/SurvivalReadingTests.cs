using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Coverage for the encounter table's one coloured cell.
///
/// <para>Worth pinning even though it is four lines of switch, because it is the readout that says
/// "run" on a permadeath character and because the version it replaced was wrong in a way no test
/// could have caught: a second ladder derived from the raw projections, which painted green inside
/// the band where <see cref="CombatOutlook"/> itself was reporting Even. These tests exist mostly to
/// hold the relationship between the two - the verdict decides the side, the margin decides the
/// distance, and neither is re-derived here.</para>
/// </summary>
public sealed class SurvivalReadingTests
{
    private const double Tick = 2000.0;

    private static CombatOutlook Outlook(OutlookVerdict verdict, double? killSeconds, double? dieSeconds)
        => new(verdict, killSeconds, dieSeconds);

    /// <summary>Seconds for a given number of combat ticks - the tests speak in ticks because the
    /// bands do.</summary>
    private static double Ticks(double count) => count * Tick / 1000.0;

    [Fact]
    public void NoProjection_ReadsAsNothing()
    {
        Assert.Equal(SurvivalReading.None, Survival.Read(CombatOutlook.Unknown, Tick));
    }

    [Fact]
    public void Unhurt_IsNotWinning()
    {
        // Nothing has landed on the player, so there is no incoming rate and no comparison. Drawing
        // this as a win would be the panel congratulating the player on a fight it cannot yet see.
        var outlook = Outlook(OutlookVerdict.Unhurt, killSeconds: Ticks(3), dieSeconds: null);
        Assert.Equal(SurvivalReading.None, Survival.Read(outlook, Tick));
    }

    [Fact]
    public void ImminentDeath_OverridesEvenAWinningVerdict()
    {
        // Two ticks from death while technically winning the race. The margin is an average and a
        // blow is not - this is the case the override exists for.
        var outlook = Outlook(OutlookVerdict.Winning, killSeconds: Ticks(1), dieSeconds: Ticks(2));
        Assert.Equal(SurvivalReading.Dire, Survival.Read(outlook, Tick));
    }

    [Fact]
    public void Losing_ByThreeTicksOrMore_IsDire()
    {
        var outlook = Outlook(OutlookVerdict.Losing, killSeconds: Ticks(9), dieSeconds: Ticks(6));
        Assert.Equal(SurvivalReading.Dire, Survival.Read(outlook, Tick));
    }

    [Fact]
    public void Losing_ByLessThanThreeTicks_IsMerelyLosing()
    {
        var outlook = Outlook(OutlookVerdict.Losing, killSeconds: Ticks(8), dieSeconds: Ticks(6));
        Assert.Equal(SurvivalReading.Losing, Survival.Read(outlook, Tick));
    }

    [Fact]
    public void Winning_NeedsAWholeExchangeOfMargin_BeforeItSaysSo()
    {
        // Ahead on the ratio but by less than two ticks: reported as Even rather than as a win worth
        // relaxing about. The asymmetry with the losing side is deliberate - danger is called as soon
        // as the margin is gone, safety only once there is several ticks of it.
        var close = Outlook(OutlookVerdict.Winning, killSeconds: Ticks(5), dieSeconds: Ticks(6));
        Assert.Equal(SurvivalReading.Even, Survival.Read(close, Tick));

        var clear = Outlook(OutlookVerdict.Winning, killSeconds: Ticks(4), dieSeconds: Ticks(6));
        Assert.Equal(SurvivalReading.Winning, Survival.Read(clear, Tick));
    }

    [Fact]
    public void Winning_ByFourTicksOrMore_IsCommanding()
    {
        var outlook = Outlook(OutlookVerdict.Winning, killSeconds: Ticks(2), dieSeconds: Ticks(6));
        Assert.Equal(SurvivalReading.Commanding, Survival.Read(outlook, Tick));
    }

    [Fact]
    public void TheParityBandIsHonoured_NotSecondGuessed()
    {
        // An Even verdict stays Even however the seconds happen to fall. This is the whole reason the
        // reading is built on the verdict rather than on the numbers: CombatOutlook's 0.75x-1.33x band
        // is it declining to resolve, and a margin computed beside it would overrule that refusal.
        var outlook = Outlook(OutlookVerdict.Even, killSeconds: Ticks(3), dieSeconds: Ticks(9));
        Assert.Equal(SurvivalReading.Even, Survival.Read(outlook, Tick));
    }

    [Fact]
    public void OneHalfMissing_StandsOnTheVerdictAlone()
    {
        // No kill projection (an unfought species has no pool estimate, so CombatOutlook suppresses
        // it) means no margin. The verdict still says which side of the line the fight is on.
        var losing = Outlook(OutlookVerdict.Losing, killSeconds: null, dieSeconds: Ticks(9));
        Assert.Equal(SurvivalReading.Losing, Survival.Read(losing, Tick));

        var winning = Outlook(OutlookVerdict.Winning, killSeconds: null, dieSeconds: Ticks(9));
        Assert.Equal(SurvivalReading.Winning, Survival.Read(winning, Tick));
    }

    [Fact]
    public void ANonsenseTickLength_ReadsAsNothing_RatherThanDividingByIt()
    {
        var outlook = Outlook(OutlookVerdict.Losing, killSeconds: Ticks(9), dieSeconds: Ticks(2));
        Assert.Equal(SurvivalReading.None, Survival.Read(outlook, 0));
    }
}
