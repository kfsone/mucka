namespace MudSharp.Combat;

/// <summary>How the fight in progress is going, at the granularity a single coloured cell can carry.</summary>
public enum SurvivalReading
{
    /// <summary>Nothing worth colouring: no projection yet, or nothing has swung at the player. The
    /// DEFAULT and the honest answer for the opening of every fight - see
    /// <see cref="CombatOutlook"/> on why an early guess is worse than silence.</summary>
    None,

    /// <summary>Ahead, and by enough to say so.</summary>
    Winning,

    /// <summary>Ahead by so much the fight is not a question.</summary>
    Commanding,

    /// <summary>Too close to call. NOT a mild version of Losing - it is the projection declining to
    /// resolve, which on a permadeath character is information in its own right.</summary>
    Even,

    /// <summary>Behind. The creature is projected to finish first.</summary>
    Losing,

    /// <summary>Behind with no room left. The state a blink belongs to - see Mucka.Rendering.Blink on
    /// why the top of a scale escalates by blinking rather than by acquiring another colour.</summary>
    Dire,
}

/// <summary>
/// Turns <see cref="CombatOutlook"/> into the one reading a coloured cell shows.
///
/// <para><b>Built on the outlook's own verdict rather than on a second ladder over the same two
/// numbers.</b> That was the first attempt and it was a mistake of exactly the kind this codebase
/// keeps making: <see cref="CombatOutlook"/> already resolves "am I winning this", already refuses to
/// project before it has evidence, and already has a deliberate parity band (0.75x-1.33x) inside
/// which it reports Even rather than pretending the estimate resolves it. Re-deriving a colour from
/// the raw seconds threw all three away and produced a confident green where the outlook itself was
/// saying "too close to call". CombatComposition.ComputeOutlook was extracted precisely so two
/// consumers could not disagree about how close a fight is; this is a consumer of it.</para>
///
/// <para><b>What the margin adds.</b> The verdict says which side of the line the fight is on; the
/// margin in TICKS says how far, which is what separates "behind" from "run". Ticks rather than
/// seconds because a tick is the unit the player actually acts in - MUD2 resolves combat every 2000
/// ms, so a margin of one tick means one more exchange, not "two seconds".</para>
///
/// <para><b>The bands are asymmetric and that is deliberate.</b>
/// Danger is called as soon as the margin is gone, safety not until there are several ticks of it. In
/// a game where death is deletion, being told early that you are in trouble and late that you are
/// safe is the correct bias, and the reverse would be the one that gets a character killed.</para>
/// </summary>
public static class Survival
{
    /// <summary>Ticks of margin before the fight stops being a question.</summary>
    public const double CommandingTicks = 4.0;

    /// <summary>Ticks of margin before the panel will call the player ahead at all.</summary>
    public const double WinningTicks = 2.0;

    /// <summary>Ticks BEHIND at which "losing" becomes "run".</summary>
    public const double DireTicks = 3.0;

    /// <summary>Ticks to death below which the reading is Dire whatever the margin. A fight the player
    /// is technically winning is still Dire if the next exchange but one can kill them - the margin is
    /// an average and a blow is not.</summary>
    public const double ImminentDeathTicks = 2.0;

    /// <summary>
    /// The reading for <paramref name="outlook"/>, with its seconds converted at
    /// <paramref name="tickMilliseconds"/> (Mucka.Core.CombatTiming.TickMilliseconds - passed in
    /// rather than referenced, because this project must not depend on the app's).
    /// </summary>
    public static SurvivalReading Read(CombatOutlook outlook, double tickMilliseconds)
    {
        if (outlook is null || tickMilliseconds <= 0)
            return SurvivalReading.None;

        // Unhurt is not "winning": nothing has landed on the player, so there is no incoming rate and
        // no comparison to make. It draws as neutral for the same reason None does.
        if (outlook.Verdict is OutlookVerdict.Unknown or OutlookVerdict.Unhurt)
            return SurvivalReading.None;

        var toDie = outlook.SecondsToDie is double die ? die * 1000.0 / tickMilliseconds : (double?)null;
        var toKill = outlook.SecondsToKill is double kill ? kill * 1000.0 / tickMilliseconds : (double?)null;

        // Imminent death overrides everything, including a Winning verdict.
        if (toDie is double dieTicks && dieTicks <= ImminentDeathTicks)
            return SurvivalReading.Dire;

        // Without both halves there is no margin, so the verdict stands on its own at its mildest.
        if (toDie is not double d || toKill is not double k)
        {
            return outlook.Verdict switch
            {
                OutlookVerdict.Winning => SurvivalReading.Winning,
                OutlookVerdict.Losing => SurvivalReading.Losing,
                _ => SurvivalReading.Even,
            };
        }

        // Margin: how many more ticks the player lasts than the creature does. Positive is good.
        var margin = d - k;

        return outlook.Verdict switch
        {
            OutlookVerdict.Winning when margin >= CommandingTicks => SurvivalReading.Commanding,
            OutlookVerdict.Winning when margin >= WinningTicks => SurvivalReading.Winning,
            // Ahead on the ratio but not by a whole exchange. The parity band already covers most of
            // this; what is left reads as Even rather than as a win worth relaxing about.
            OutlookVerdict.Winning => SurvivalReading.Even,
            OutlookVerdict.Losing when margin <= -DireTicks => SurvivalReading.Dire,
            OutlookVerdict.Losing => SurvivalReading.Losing,
            _ => SurvivalReading.Even,
        };
    }
}
