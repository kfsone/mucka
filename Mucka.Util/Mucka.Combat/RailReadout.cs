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
