namespace MudSharp.Combat;

/// <summary>Coarse read on who is going to run out of stamina first.</summary>
public enum OutlookVerdict
{
    /// <summary>Not enough observed to say anything. The DEFAULT, and the honest answer most of the
    /// time — see <see cref="CombatOutlook"/> for why an early guess is worse than silence.</summary>
    Unknown,
    /// <summary>Nothing has landed on the player yet, so there is no incoming rate to project.</summary>
    Unhurt,
    Winning,
    Even,
    Losing,
}

/// <summary>
/// "Do the numbers say I die before it does?" — projected from the current fight's observed rates
/// against the opponent's historical stamina pool.
///
/// <para>This is possible chiefly because of the fight-history index: MUD2 only reports an NPC's
/// stamina on demand via a `diagnose` probe, not continuously, so the median damage dealt across
/// prior kills is the available estimate of how much it takes to put one down whenever no probe
/// reading is on hand. Without that there is no denominator and no projection.</para>
///
/// <para><b>Deliberate conservatism.</b> Three things make an early projection actively misleading,
/// so it stays <see cref="OutlookVerdict.Unknown"/> until they are addressed:</para>
/// <list type="bullet">
/// <item>MUD2 has a third per-tick outcome besides hit and miss — a <i>pass</i>, which emits no text
/// at all. A fight can sit silent for a long stretch (a starfish went 90 seconds with nothing). So
/// rates are computed over WALL-CLOCK elapsed, never per observed swing, or a lucky opening burst
/// reads as a certain win.</item>
/// <item>Both sides regenerate stamina, and NPC regen is entirely unobservable, so a grindy fight can
/// be unwinnable while the raw rates look fine.</item>
/// <item>The pool figure is a median over a handful of past kills, not a measurement of the
/// individual in front of you.</item>
/// </list>
/// <para>Consequently the output is a three-state verdict plus the two projected times, never a
/// percentage — a number here would imply precision that does not exist.</para>
/// </summary>
public sealed record CombatOutlook(
    OutlookVerdict Verdict,
    /// Seconds until the player finishes the opponent, null when unprojectable.
    double? SecondsToKill,
    /// Seconds until the opponent finishes the player, null when unprojectable.
    double? SecondsToDie)
{
    public static readonly CombatOutlook Unknown = new(OutlookVerdict.Unknown, null, null);

    /// <summary>Minimum elapsed fight time before projecting. Below this the rate denominators are
    /// too small for the pass-tick problem above to have averaged out at all.</summary>
    public const double MinimumElapsedSeconds = 10.0;

    /// <summary>Minimum landed blows by the player before projecting an outgoing rate.</summary>
    public const int MinimumOwnHits = 2;

    /// <summary>Ratio band treated as "too close to call". Anything inside 0.75x-1.33x of parity is
    /// reported as Even rather than pretending the estimate resolves it.</summary>
    private const double EvenBandLow = 0.75;
    private const double EvenBandHigh = 1.0 / EvenBandLow;

    /// <summary>
    /// Projects the outcome of the fight in progress.
    /// </summary>
    /// <param name="elapsedSeconds">Wall-clock duration of this fight so far.</param>
    /// <param name="damageDealt">Cumulative damage the player has dealt this fight.</param>
    /// <param name="damageTaken">Cumulative damage the player has taken this fight.</param>
    /// <param name="ownHits">Blows the player has landed this fight.</param>
    /// <param name="opponentHits">Blows the opponent has landed this fight.</param>
    /// <param name="playerStamina">The player's current stamina.</param>
    /// <param name="estimatedPool">Historical median damage needed to kill this opponent.</param>
    public static CombatOutlook Project(
        double elapsedSeconds,
        double damageDealt,
        double damageTaken,
        int ownHits,
        int opponentHits,
        int? playerStamina,
        double? estimatedPool)
    {
        if (elapsedSeconds < MinimumElapsedSeconds
            || ownHits < MinimumOwnHits
            || estimatedPool is not double pool
            || pool <= 0
            || playerStamina is not int stamina
            || stamina <= 0)
            return Unknown;

        // Rates over wall-clock, deliberately — see the class remarks on the silent pass tick.
        var outgoingRate = damageDealt / elapsedSeconds;
        if (outgoingRate <= 0)
            return Unknown;

        var remainingPool = Math.Max(pool - damageDealt, 0);
        var secondsToKill = remainingPool / outgoingRate;

        // Nothing has hit the player yet: there is no incoming rate to extrapolate, and inventing one
        // (or reporting an infinite time-to-die as "winning") would overstate what is known. The pass
        // tick means an opponent that has done nothing may simply not have acted yet.
        if (opponentHits == 0 || damageTaken <= 0)
            return new CombatOutlook(OutlookVerdict.Unhurt, secondsToKill, null);

        var incomingRate = damageTaken / elapsedSeconds;
        var secondsToDie = stamina / incomingRate;

        var ratio = secondsToKill / secondsToDie;
        var verdict = ratio switch
        {
            <= EvenBandLow => OutlookVerdict.Winning,
            >= EvenBandHigh => OutlookVerdict.Losing,
            _ => OutlookVerdict.Even,
        };

        return new CombatOutlook(verdict, secondsToKill, secondsToDie);
    }
}
