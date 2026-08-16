using System.Globalization;

namespace MudSharp.Combat;

/// <summary>
/// The plain-language "why is this going badly" line: DESIGN_FINAL.md section 3.8's
/// deterministic, priority-ordered rule table. Surfaces CAUSES, never coefficients - this is the
/// literal implementation of the owner's "surface causes, not coefficients" instruction, for a
/// player who is explicitly not a maths person.
///
/// <para>Only the single highest-priority active condition renders, one line, max; silent when
/// nothing qualifies. Pure and primitive-typed (no snapshot/history record types), so it is
/// directly unit-testable via mudsharp.Tests' existing ProjectReference with no test-project
/// wiring, and so it never has to know about the MAUI-side record shapes that carry this data
/// around in the view model.</para>
///
/// <para>Note on section 3.5's wireframe: that sketch shows priority 1's and priority 2's
/// sentences concatenated onto two lines ("low dmg: fighting bare handed, and 7 items cost you 11
/// str now"). Section 3.8's rule table is the authoritative spec here ("only the single highest-
/// priority active condition renders... one line max"), so this implementation renders exactly one
/// sentence - priority 2's text drops the wireframe's continuation "and" prefix when it renders on
/// its own rather than after priority 1's sentence.</para>
/// </summary>
public static class CombatWhyLine
{
    public readonly record struct Result(int Priority, string Text);

    /// <summary>Minimum landed hits against this weapon/npc_group before priority 3's per-hit
    /// comparison is trusted enough to speak - matches the design's own "(n &gt;= 3)" condition.</summary>
    public const int MinSampleForPerHitComparison = 3;

    /// <summary>Window (seconds) after an NpcWeaponEquip event during which priority 5 still
    /// applies (3.8: "fired in the last 20s for the primary target").</summary>
    public const double NpcWeaponEquipWindowSeconds = 20.0;

    public static Result? Resolve(
        bool hasWeapon,
        int? strengthDelta,
        // Nullable, and null really does mean "not known yet" rather than "nothing carried". The
        // count is only live once the inventory probe has reported; defaulting an unknown to 0 put
        // the flat lie "0 items cost you 12 str right now" on screen. Both sentences below fall
        // back to naming the load without counting it.
        int? itemsCarried,
        double? livePerHit,
        double? historicalMedianPerHit,
        int historicalSampleSize,
        string? weaponDisplayName,
        int? dexterityDelta,
        double? liveHitRate,
        double? historicalHitRate,
        double? secondsSinceNpcWeaponEquip,
        string? npcName,
        string? npcWeaponDisplayName)
    {
        // Priority 1: current weapon is null.
        if (!hasWeapon)
            return new Result(1, "low dmg: fighting bare handed");

        // Priority 2: strength delta <= -10.
        if (strengthDelta is int strength && strength <= -10)
        {
            return new Result(2, itemsCarried is int carried
                ? $"{carried} items cost you {-strength} str right now"
                : $"what you're carrying costs you {-strength} str right now");
        }

        // Priority 3: live per-hit < 70% of this weapon's own historical median for this npc_group
        // (n >= 3).
        if (livePerHit is double now
            && historicalMedianPerHit is double median
            && median > 0
            && historicalSampleSize >= MinSampleForPerHitComparison
            && now < median * 0.70
            && !string.IsNullOrWhiteSpace(weaponDisplayName))
        {
            return new Result(3,
                $"{weaponDisplayName} is hitting for less than usual ({Num(now)} vs your usual {Num(median)})");
        }

        // Priority 4: dexterity delta <= -15 and live hit-rate < historical hit-rate for this weapon.
        if (dexterityDelta is int dexterity && dexterity <= -15
            && liveHitRate is double lhr && historicalHitRate is double hhr && lhr < hhr)
        {
            return new Result(4, itemsCarried is int dexCarried
                ? $"carrying {dexCarried} items is costing you dex, and it shows in your hit rate"
                : "what you're carrying is costing you dex, and it shows in your hit rate");
        }

        // Priority 5: an NpcWeaponEquip fired in the last 20s for the primary target.
        if (secondsSinceNpcWeaponEquip is double secs && secs >= 0 && secs <= NpcWeaponEquipWindowSeconds
            && !string.IsNullOrWhiteSpace(npcName) && !string.IsNullOrWhiteSpace(npcWeaponDisplayName))
        {
            return new Result(5, $"they're hitting harder: {npcName} picked up a {npcWeaponDisplayName} partway through this");
        }

        return null;
    }

    private static string Num(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
