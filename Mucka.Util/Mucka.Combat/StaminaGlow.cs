using MudSharp.Combat;

namespace Mucka.Combat;

/// <summary>
/// How loud the Combat Rail's stamina alarm is at a given stamina - the panel's trim and glow, and the
/// 1px rules around the terminal and input row. Operator rules:
///
/// <para><b>Amber from 30, red at 20.</b> The ten stamina above the red are planning-the-exit
/// territory, an amber ramp in <see cref="Steps"/> steps that grows in colour, intensity and speed;
/// at or below 20 the exit should already be typed and ready.</para>
///
/// <para><b>Big hitters move both bands up.</b> In a fight, red starts where one more of the largest
/// blow a live creature has landed could kill (one above that blow), and amber where two could (one
/// above twice it), whenever those are higher than 20 and 30. Something that has hit for 39 makes the
/// red due at 40 and the amber at 79. Only live creatures count, and only in a fight: out of combat
/// there are no hits to worry about and the bands are 30 and 20. Incoming damage does not scale with
/// the player's stats (dexterity moves accuracy and avoidance, not the size of a blow), so the bands
/// are absolute stamina, never a fraction of the maximum.</para>
///
/// <para><b>Trim, then the whole panel.</b> The ramp starts as a band of light around the inside edge
/// of the panel; from <see cref="WholePanelFromLevel"/>, mid-ramp, the whole panel glows as well.</para>
/// </summary>
public static class StaminaGlow
{
    /// <summary>The red band's floor: the survival threshold.</summary>
    public const int RedStamina = (int)CombatTierResolver.SurvivalStaminaThreshold;

    /// <summary>The amber band's floor.</summary>
    public const int AmberStamina = RedStamina + 10;

    /// <summary>Amber steps between no glow and red.</summary>
    public const int Steps = 5;

    /// <summary>The level of full red.</summary>
    public const int Red = Steps + 1;

    /// <summary>The first level at which the whole panel glows rather than only its trim: the middle
    /// of the ramp, 25-26 stamina with the bands at 30 and 20.</summary>
    public const int WholePanelFromLevel = 3;

    /// <summary>Where the bands start: red at or below <c>RedAt</c>, the amber ramp at or below
    /// <c>AmberFrom</c>.</summary>
    public readonly record struct Bands(int RedAt, int AmberFrom);

    /// <summary>The bands for a fight whose largest blow from a live creature is
    /// <paramref name="largestLiveBlow"/>; null - no measured blow, or not in a fight - gives 20 and
    /// 30.</summary>
    public static Bands BandsFor(double? largestLiveBlow)
    {
        if (largestLiveBlow is not double blow)
            return new Bands(RedStamina, AmberStamina);
        var oneHit = (int)Math.Ceiling(blow);
        return new Bands(Math.Max(RedStamina, oneHit + 1), Math.Max(AmberStamina, (2 * oneHit) + 1));
    }

    /// <summary>0 for no glow, 1 to <see cref="Steps"/> up the amber ramp (equal parts of the span
    /// between the bands), <see cref="Red"/> at or below the red band.</summary>
    public static int Level(int? stamina, Bands bands)
    {
        if (stamina is not int s)
            return 0;
        if (s <= bands.RedAt)
            return Red;
        var span = bands.AmberFrom - bands.RedAt;
        var above = s - bands.RedAt;
        if (above > span)
            return 0;
        return Steps - ((above - 1) * Steps / span);
    }

    /// <summary>How one level is drawn on the panel: colour, and the opacity its pulse swings between
    /// and the pulse's period - for the whole-panel glow, and for the trim.</summary>
    public readonly record struct Look(
        byte R, byte G, byte B, float Peak, float Trough, double PeriodMilliseconds, float TrimPeak, float TrimTrough);

    // The red, exactly as the whole-panel glow drew it before the ramp: the panel's dark red at half
    // amplitude (0.5 -> 0.125) on the 1200 ms pulse. Half, not full (1.0 -> 0.25): full amplitude
    // dominated the panel so completely that the flee pill - the one element with something
    // actionable on it - did not draw the eye at all, observed in a fight at 23 stamina against a
    // banshee. Both ends halved rather than the trough raised, which would shrink the swing while
    // brightening the panel. The trim is a thin band, so it runs at twice the panel's opacity.
    private static readonly Look RedLook = new(0xC5, 0x0F, 0x1F, 0.5f, 0.125f, Blink.PulsePeriodMilliseconds, 1.0f, 0.25f);

    /// <summary>Campbell's normal yellow: the combat rules while a fight is live, and the bottom of the
    /// ramp everywhere. NOT the bright yellow the chat-mode cue on the input frame uses - the two borders
    /// can be lit at once within a couple of pixels of each other, and identical colours would read as
    /// one thick rule rather than two separate facts.</summary>
    public static readonly (byte R, byte G, byte B) Amber = (0xC1, 0x9C, 0x00);

    /// <summary>Campbell's bright red: the combat rules at the top of the ramp. A 1px line needs the
    /// bright slot to register at all; the panel glow uses the dark red because it fills a whole
    /// panel.</summary>
    public static readonly (byte R, byte G, byte B) EdgeRed = (0xE7, 0x48, 0x56);

    // The bottom of the ramp: the amber, a third of the red's amplitude, at half its speed.
    private static readonly Look AmberLook = new(Amber.R, Amber.G, Amber.B, 0.15f, 0.05f, Blink.PulsePeriodMilliseconds * 2, 0.3f, 0.1f);

    /// <summary>The look for <paramref name="level"/>, from 1 (amber) to <see cref="Red"/>, each step
    /// an equal part of the way from the amber to the red. Null for level 0: nothing lit.</summary>
    public static Look? LookFor(int level)
    {
        if (level <= 0)
            return null;
        if (level >= Red)
            return RedLook;
        var t = Fraction(level);
        return new Look(
            Lerp(AmberLook.R, RedLook.R, t), Lerp(AmberLook.G, RedLook.G, t), Lerp(AmberLook.B, RedLook.B, t),
            Lerp(AmberLook.Peak, RedLook.Peak, t), Lerp(AmberLook.Trough, RedLook.Trough, t),
            AmberLook.PeriodMilliseconds + ((RedLook.PeriodMilliseconds - AmberLook.PeriodMilliseconds) * t),
            Lerp(AmberLook.TrimPeak, RedLook.TrimPeak, t), Lerp(AmberLook.TrimTrough, RedLook.TrimTrough, t));
    }

    /// <summary>The static colour of the 1px rules around the terminal and input row at
    /// <paramref name="level"/>: the same ramp, from <see cref="Amber"/> to <see cref="EdgeRed"/>. Null
    /// for level 0.</summary>
    public static (byte R, byte G, byte B)? EdgeColorFor(int level)
    {
        if (level <= 0)
            return null;
        var t = level >= Red ? 1.0 : Fraction(level);
        return (Lerp(Amber.R, EdgeRed.R, t), Lerp(Amber.G, EdgeRed.G, t), Lerp(Amber.B, EdgeRed.B, t));
    }

    private static double Fraction(int level) => (level - 1) / (double)(Red - 1);

    private static byte Lerp(byte from, byte to, double t) => (byte)Math.Round(from + ((to - from) * t));

    private static float Lerp(float from, float to, double t) => (float)(from + ((to - from) * t));
}
