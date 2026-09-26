using System.Globalization;
using MudSharp.Combat;

namespace Mucka.Combat;

/// <summary>
/// The Combat Rail's pure readout rules: the word each fight outcome is reported with, the tick
/// figures on the encounter table, and the two bar lengths the canvas draws from a measurement.
///
/// <para>Here rather than on the canvas because none of it needs Skia, and because the dead strip's
/// vocabulary is the part of the panel a player actually reads. <c>RailSlotGeometry</c> is the same
/// arrangement for the slot plan.</para>
/// </summary>
public static class RailReadout
{
    /// <summary>What one landed blow's mark saturates at. Beyond this the creature is already
    /// hitting for more than any single tick of headroom the player is likely to have, and a taller
    /// mark would only be re-stating that in a way that squeezed every other mark shorter.</summary>
    public const double SparkDamageCap = 20.0;

    /// <summary>The shortest spark mark. A swing that happened but whose size is unknown draws at
    /// exactly this - the swing is a fact even when its damage is not.</summary>
    public const float SparkMinBar = 3f;

    /// <summary>The tallest spark mark, reached at <see cref="SparkDamageCap"/>.</summary>
    public const float SparkMaxBar = 14f;

    /// <summary>One measured blow's mark height, linear between <see cref="SparkMinBar"/> and
    /// <see cref="SparkMaxBar"/> and saturating at <see cref="SparkDamageCap"/>.</summary>
    public static float SparkBarHeight(double damage)
        => SparkMinBar + (float)(Math.Clamp(damage / SparkDamageCap, 0.0, 1.0) * (SparkMaxBar - SparkMinBar));

    /// <summary>Where a boundary at <paramref name="startDegrees"/> falls along a bar that fills from
    /// the left with what remains. The ring's lit arc runs from its start round to 360, so the
    /// fraction still standing is everything the arc covers.</summary>
    public static float RemainingWidth(float startDegrees, float width)
        => Math.Clamp((360f - startDegrees) / 360f, 0f, 1f) * width;

    /// <summary>Ticks, rounded and suffixed, or empty for "no answer" - which the caller draws as the
    /// column's own name rather than as a figure.</summary>
    public static string Ticks(double? ticks, CultureInfo culture)
        => ticks is double value && value >= 0 ? value.ToString("0", culture) + "t" : string.Empty;

    /// <summary>
    /// A creature's blows on its dead-strip row: the smallest and largest that landed and the average,
    /// "4-15 avg 8.7", or "each 6" when every measured blow was alike - labelled, because a bare figure
    /// beside the total reads as some other quantity. Empty when nothing was measured.
    ///
    /// <para>The creature's side only. Its blows are exact - MUD2 prints the player's stamina on every
    /// one - whereas the player's own are brackets whose extremes are upper bounds, which a range
    /// beside the total's range would read as something they are not (see
    /// <see cref="ExchangeLine"/>).</para>
    /// </summary>
    public static string BlowShape(ExchangeLine line, CultureInfo culture)
    {
        if (!line.HasSamples)
            return string.Empty;
        if (line.AllAlike)
            return "each " + line.Max.ToString("0.#", culture);
        return line.Min.ToString("0.#", culture) + "-" + line.Max.ToString("0.#", culture)
            + " avg " + line.Mean.ToString("0.#", culture);
    }

    /// <summary>
    /// The dead-strip row's hover card: the ending, then a small table of each side's blows over every
    /// engagement the row stands for.
    ///
    /// <code>
    /// water-snake1  killed  1 flight  +89  18t
    ///      hits   max    avg   total  rate
    /// you     6  &lt;=14  ~12.8   65-89  3.3t
    /// it      3    15    8.7      26  1.4t
    /// </code>
    ///
    /// <para>A table rather than a sentence per side, so every figure keeps its own column and a
    /// long total cannot push another off the card's edge. Padded with spaces, which aligns because
    /// the card's face is monospace.</para>
    ///
    /// <para>The two sides read differently because they are different measurements. The creature's
    /// blows are exact. The player's are brackets: the total is a range, the average is a midpoint
    /// (marked "~"), and the largest blow is an upper bound (marked "&lt;="). See
    /// <see cref="ExchangeLine"/>. "hits" counts the landed blows that carried numbers
    /// (<see cref="ExchangeLine.Samples"/>); the word is the operator's.</para>
    /// </summary>
    public static IReadOnlyList<string> EndingCard(CombatEnding ending, CultureInfo culture)
    {
        var head = ending.Name + "  " + OutcomeWord(ending.Outcome);
        if (ending.Retries > 0)
            head += ending.Retries == 1 ? "  1 flight" : "  " + ending.Retries.ToString(culture) + " flights";
        if (ending.ScoreAwarded is int award && award != 0)
            head += "  " + (award < 0 ? "-" : "+") + Math.Abs(award).ToString(culture);
        var ticks = CombatTiming.TicksElapsed(ending.Duration);
        if (ticks >= 1.0)
            head += "  " + ticks.ToString("0", culture) + "t";

        string[][] rows =
        [
            ["", "hits", "max", "avg", "total", "rate"],
            CardRow("you", ending.Dealt, byPlayer: true, culture),
            CardRow("it", ending.Taken, byPlayer: false, culture),
        ];

        var widths = new int[rows[0].Length];
        foreach (var row in rows)
        {
            for (var c = 0; c < row.Length; c++)
                widths[c] = Math.Max(widths[c], row[c].Length);
        }

        var lines = new string[rows.Length + 1];
        lines[0] = head;
        for (var r = 0; r < rows.Length; r++)
        {
            // The label column reads left to right; every figure lines up on its last digit.
            var text = rows[r][0].PadRight(widths[0]);
            for (var c = 1; c < rows[r].Length; c++)
                text += "  " + rows[r][c].PadLeft(widths[c]);
            lines[r + 1] = text;
        }
        return lines;
    }

    private static string[] CardRow(string who, ExchangeLine line, bool byPlayer, CultureInfo culture)
    {
        if (!line.HasSamples)
            return [who, "0", "-", "-", "-", "-"];

        var total = byPlayer && line.Total.High > line.Total.Low
            ? line.Total.Low.ToString("0.#", culture) + "-" + line.Total.High.ToString("0.#", culture)
            : line.Total.High.ToString("0.#", culture);
        var rate = line.PerTick > 0 ? line.PerTick.ToString("0.#", culture) + "t" : "-";
        var max = line.Max.ToString("0.#", culture);
        var avg = line.Mean.ToString("0.#", culture);
        return byPlayer
            ? [who, line.Samples.ToString(culture), "<=" + max, "~" + avg, total, rate]
            : [who, line.Samples.ToString(culture), max, avg, total, rate];
    }

    /// <summary>
    /// The word one ending is reported with on the dead strip.
    ///
    /// <para>Empty for <see cref="FightOutcome.Unresolved"/>, and that blank is a distinct statement:
    /// we never saw this fight end at all. Every outcome MUD2 did give a reason for has a word of its
    /// own, and no two share one.</para>
    /// </summary>
    public static string OutcomeWord(FightOutcome outcome) => outcome switch
    {
        FightOutcome.Kill => "killed",
        FightOutcome.Died => "KILLED YOU",
        FightOutcome.CFled => "fled",
        // "broke off", not "fled": the creature is still standing in the room. Calling this a flee on
        // the roster row would have the player chase something that never left - see
        // FightOutcome.CFledFail.
        FightOutcome.CFledFail => "broke off",
        FightOutcome.UFled => "you fled",
        // The player is also still in the room, and still has to deal with this thing.
        FightOutcome.UFledFail => "flee failed",
        FightOutcome.Withdraw => "withdrew",
        // The outcome's own word rather than the observed cause: NoMore is an open family (poison so
        // far, with more expected) and the label has to cover the ones not yet seen. Not
        // "killed" - nothing in these frames says the player's blow finished it - and not "died",
        // which on a roster row beside "KILLED YOU" invites exactly the wrong reading.
        FightOutcome.NoMore => "no more",
        // The game closed it and gave no reason; saying more than that on the roster row would be
        // inventing one. Distinct from a blank label, which means we never saw it end at all.
        FightOutcome.EndOther => "ended",
        // The client stopped it, not the game - a reset, a logout, a room change, app exit. "cut
        // short" rather than "interrupted" only because the word has to fit DeadNameWidth on the dead
        // strip's second line; which of the four reasons it was is in the clog's EncounterForceEnded
        // event, not on a roster row.
        FightOutcome.Interrupted => "cut short",
        // The word's row retired because the Creature behind it was named once sight returned - the
        // fight goes on under that name. Not a kill and not a loss.
        FightOutcome.Named => "named",
        _ => string.Empty,
    };
}
