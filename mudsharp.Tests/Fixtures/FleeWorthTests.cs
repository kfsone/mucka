using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>Bartle's flee-cost formula against the observations that confirm it - see
/// <see cref="FleeWorth"/> for the formula and where each piece of evidence comes from.</summary>
public sealed class FleeWorthTests
{
    /// <summary>
    /// EVERY player flight in <c>~/.mucka/combat/mucka.db</c> with a recorded score fall: <c>fights</c>
    /// (outcome UFled/UFledFail, stamina_at_end, max_stamina) joined to <c>score_events</c> (delta &lt; 0
    /// within a second of ended_at_ms; total - delta is the score before). Columns: observed cost,
    /// stamina, maximum, score before. A pack flee produces one <c>fights</c> row per creature but one
    /// charge, and appears here once. Not a chosen sample - add every new one; if a row ever fails, the
    /// formula is wrong.
    /// </summary>
    public static TheoryData<int, int, int, int> RecordedFlights => new()
    {
        { 226,  11, 100,  9_822 },
        { 285,  12, 100, 12_482 },
        {  18,   8,  75,     443 },
        {  47,  16,  75,     686 },
        {  42,   9,  85,   1_556 },
        {  26,   6,  82,     812 },
        {  39,   8,  82,   1_403 },
        {  64,   6,  92,   2_524 },
        { 371,  95,  95,   1_716 },
        { 438,  95,  95,   2_094 },
        {  90,  55,  95,   1_656 },
        {  86,  17,  85,   1_566 },
        { 102,  58,  58,     200 },
        {  38,  13,  68,     492 },
        { 171,  14, 100,   7_335 },
        { 574,  16, 100,  12_555 },
        { 573,  19, 100,  12_527 },
        { 851,  16, 100,  18_775 },
    };

    [Theory]
    [MemberData(nameof(RecordedFlights))]
    public void Matches_every_recorded_flight(int observed, int stamina, int maxStamina, int score)
        => Assert.Equal(observed, FleeWorth.Cost(score, stamina, maxStamina));

    /// <summary>The corpus's only flight in the 76..99% band predates <c>score_events</c>, so this is
    /// the fight's own score drop: fight 631, 92/115, 74,824 to 68,140.</summary>
    [Fact]
    public void The_76_to_99_band_matches_its_one_sample()
        => Assert.Equal(6_684, FleeWorth.Cost(points: 74_824, stamina: 92, maxStamina: 115));

    /// <summary>Both flights are at stamina 6 and differ only in the maximum: fight 2162 (6/82) was
    /// charged 26; fight 1126 (6/120, a failed flee) left the score unchanged.</summary>
    [Fact]
    public void The_threshold_is_a_percentage_not_a_stamina_value()
    {
        Assert.Equal(26, FleeWorth.Cost(points: 812, stamina: 6, maxStamina: 82));      // 7.3%
        Assert.Equal(0, FleeWorth.Cost(points: 93_823, stamina: 6, maxStamina: 120));   // 5.0%
    }

    /// <summary>Bartle: "if this percentage is &lt; 6, your fleeworth is 0" - so 6.0% itself pays.</summary>
    [Fact]
    public void Six_percent_exactly_is_charged()
    {
        Assert.Equal(0, FleeWorth.Cost(points: 10_000, stamina: 59, maxStamina: 1_000));  // 5.9%
        Assert.True(FleeWorth.Cost(points: 10_000, stamina: 60, maxStamina: 1_000) > 0);  // 6.0%
    }

    [Theory]
    [InlineData(100.0, 1)]
    [InlineData(99.9, 2)]
    [InlineData(76.0, 2)]
    [InlineData(75.9, 4)]
    [InlineData(16.0, 4)]
    [InlineData(15.9, 8)]
    [InlineData(6.0, 8)]
    public void Divisor_bands_are_as_stated(double percent, int expected)
        => Assert.Equal(expected, FleeWorth.Divisor(percent));

    /// <summary>Null is not zero, and the pill relies on the difference: zero is "MUD2 will let this go
    /// free", worth printing; null is "we do not know", which must print nothing.</summary>
    [Fact]
    public void Missing_inputs_give_null_rather_than_a_figure()
    {
        Assert.Null(FleeWorth.Cost(null, 50, 100));
        Assert.Null(FleeWorth.Cost(1_000, null, 100));
        Assert.Null(FleeWorth.Cost(1_000, 50, null));
        Assert.Null(FleeWorth.Cost(1_000, 50, 0));
        Assert.Equal(0, FleeWorth.Cost(1_000, 1, 100));   // free, and that IS known
    }
}
