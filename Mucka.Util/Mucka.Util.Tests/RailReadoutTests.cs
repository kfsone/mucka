using System.Globalization;
using Mucka.Combat;
using MudSharp.Combat;

namespace Mucka.Util.Tests;

/// <summary>
/// The rail's readout rules. These lived on the <c>SKCanvasView</c> until they moved into
/// <see cref="RailReadout"/>, so the words the player reads on the dead strip had no coverage at
/// all - which is exactly the mapping a new outcome would be added to.
/// </summary>
public sealed class RailReadoutTests
{
    private static readonly FightOutcome[] AllOutcomes = Enum.GetValues<FightOutcome>();

    // ---- OutcomeWord ---------------------------------------------------------------------------

    /// <summary>
    /// Every outcome MUD2 gave a reason for reads as a word. A blank label is a DIFFERENT statement -
    /// "we never saw this fight end at all" - so an outcome falling through to the default arm does
    /// not merely look untidy, it reports something untrue.
    ///
    /// <para>Catches a new <see cref="FightOutcome"/> member added without a word, which is the
    /// single most likely edit to this mapping and the one with no other detector.</para>
    /// </summary>
    [Fact]
    public void OutcomeWord_EveryResolvedOutcomeHasAWord()
    {
        foreach (var outcome in AllOutcomes)
        {
            if (outcome == FightOutcome.Unresolved)
                continue;
            Assert.False(
                string.IsNullOrWhiteSpace(RailReadout.OutcomeWord(outcome)),
                $"{outcome} has no word, so its row would read as 'we never saw it end'.");
        }
    }

    /// <summary>No two outcomes share a word. Catches a copy-paste that maps a new member onto an
    /// existing label, which makes two different endings indistinguishable on the strip.</summary>
    [Fact]
    public void OutcomeWord_NoTwoOutcomesShareAWord()
    {
        var words = AllOutcomes
            .Where(o => o != FightOutcome.Unresolved)
            .Select(RailReadout.OutcomeWord)
            .ToArray();

        Assert.Equal(words.Length, words.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>The one blank, and what it means.</summary>
    [Fact]
    public void OutcomeWord_UnresolvedIsBlank()
        => Assert.Equal(string.Empty, RailReadout.OutcomeWord(FightOutcome.Unresolved));

    /// <summary>"broke off", not "fled": the creature is still standing in the room. Calling a failed
    /// creature-flight a flee would have the player chase something that never left.</summary>
    [Fact]
    public void OutcomeWord_AFailedCreatureFlightIsNotAFlee()
    {
        Assert.Equal("broke off", RailReadout.OutcomeWord(FightOutcome.CFledFail));
        Assert.Equal("fled", RailReadout.OutcomeWord(FightOutcome.CFled));
    }

    /// <summary>The player's own failed flight is likewise not a flight - they are still in the room
    /// and still have to deal with this thing.</summary>
    [Fact]
    public void OutcomeWord_AFailedPlayerFlightIsNotAFlight()
    {
        Assert.Equal("flee failed", RailReadout.OutcomeWord(FightOutcome.UFledFail));
        Assert.Equal("you fled", RailReadout.OutcomeWord(FightOutcome.UFled));
    }

    /// <summary>A row retiring because sight returned and the Creature got a name is neither a kill
    /// nor a loss. Catches it being folded onto "ended" or "killed".</summary>
    [Fact]
    public void OutcomeWord_NamedIsNeitherAKillNorALoss()
    {
        var named = RailReadout.OutcomeWord(FightOutcome.Named);

        Assert.Equal("named", named);
        Assert.NotEqual(RailReadout.OutcomeWord(FightOutcome.Kill), named);
        Assert.NotEqual(RailReadout.OutcomeWord(FightOutcome.Died), named);
    }

    // ---- Ticks ---------------------------------------------------------------------------------

    /// <summary>No answer is an empty string, which the caller draws as the column's own name rather
    /// than as a figure. Catches a null being formatted as "0t" - a confident zero on the death
    /// column at the exact moment the projection refused to make one.</summary>
    [Fact]
    public void Ticks_NoAnswerIsEmpty()
        => Assert.Equal(string.Empty, RailReadout.Ticks(null, CultureInfo.InvariantCulture));

    /// <summary>A negative figure is not an answer either.</summary>
    [Fact]
    public void Ticks_NegativeIsEmpty()
        => Assert.Equal(string.Empty, RailReadout.Ticks(-1.0, CultureInfo.InvariantCulture));

    /// <summary>Rounded and suffixed; zero is a real answer and prints.</summary>
    [Theory]
    [InlineData(0.0, "0t")]
    [InlineData(4.4, "4t")]
    [InlineData(4.6, "5t")]
    [InlineData(12.0, "12t")]
    public void Ticks_RoundsAndSuffixes(double value, string expected)
        => Assert.Equal(expected, RailReadout.Ticks(value, CultureInfo.InvariantCulture));

    // ---- RemainingWidth --------------------------------------------------------------------------

    /// <summary>The lit arc runs from its start round to 360, so the fraction still standing is
    /// everything the arc covers: a boundary at 0 degrees is a full bar, at 180 half of one.</summary>
    [Theory]
    [InlineData(0f, 100f)]
    [InlineData(180f, 50f)]
    [InlineData(360f, 0f)]
    public void RemainingWidth_IsTheFractionTheArcCovers(float startDegrees, float expected)
        => Assert.Equal(expected, RailReadout.RemainingWidth(startDegrees, 100f), 3);

    /// <summary>Clamped at both ends, so a boundary outside the circle cannot draw a bar longer than
    /// the track or shorter than nothing.</summary>
    [Fact]
    public void RemainingWidth_ClampsOutsideTheCircle()
    {
        Assert.Equal(100f, RailReadout.RemainingWidth(-90f, 100f), 3);
        Assert.Equal(0f, RailReadout.RemainingWidth(450f, 100f), 3);
    }

    // ---- SparkBarHeight --------------------------------------------------------------------------

    /// <summary>A measured blow of nothing still draws the minimum mark: the swing happened, and
    /// unknown-or-nothing must not look like an absent slot.</summary>
    [Fact]
    public void SparkBarHeight_StartsAtTheMinimum()
        => Assert.Equal(RailReadout.SparkMinBar, RailReadout.SparkBarHeight(0), 3);

    /// <summary>Saturates at the cap rather than growing without bound - beyond it a taller mark
    /// would only squeeze every other mark shorter.</summary>
    [Fact]
    public void SparkBarHeight_SaturatesAtTheCap()
    {
        Assert.Equal(RailReadout.SparkMaxBar, RailReadout.SparkBarHeight(RailReadout.SparkDamageCap), 3);
        Assert.Equal(RailReadout.SparkMaxBar, RailReadout.SparkBarHeight(RailReadout.SparkDamageCap * 10), 3);
    }

    /// <summary>Linear between the two, so half the cap is half the range.</summary>
    [Fact]
    public void SparkBarHeight_IsLinearBetweenTheEnds()
        => Assert.Equal(
            RailReadout.SparkMinBar + ((RailReadout.SparkMaxBar - RailReadout.SparkMinBar) / 2f),
            RailReadout.SparkBarHeight(RailReadout.SparkDamageCap / 2),
            3);
}
