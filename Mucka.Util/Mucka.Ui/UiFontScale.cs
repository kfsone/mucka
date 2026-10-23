using System.Globalization;

namespace Mucka.Ui;

/// <summary>
/// The "UI font size" setting: seventeen steps, <see cref="Min"/> to <see cref="Max"/>, that multiply
/// every authored chrome font size. Step 0 (Normal) multiplies by exactly 1, so a size comes back
/// bit-for-bit as authored. The terminal grid and the Combat Rail canvas have their own sizing and
/// do not use this. docs/font-setup-design.md governs.
/// </summary>
public static class UiFontScale
{
    public const int Min = -8;
    public const int Normal = 0;
    public const int Max = 8;

    /// <summary>Each step's scale is this times the one below. +2 is about the old Large (1.15), +4
    /// about the old Larger (1.30) and about 1/0.77, the Mac's iPad-idiom shrink.</summary>
    public const double StepRatio = 1.07;

    /// <summary>The ini key, in the global [settings] section.</summary>
    public const string IniKey = "uifontscale";

    /// <summary>The three-step key this one replaced: 0 Normal, 1 Large, 2 Larger.</summary>
    public const string LegacyIniKey = "uifontstep";

    /// <summary>The largest scale applied to the status bar and F-key toolbar, whose widths are
    /// tiered by terminal columns and whose rows have fixed heights: the +4 scale.</summary>
    public static readonly double TightMaxScale = Math.Pow(StepRatio, 4);

    public static int Clamp(int step) => Math.Clamp(step, Min, Max);

    public static double ScaleFor(int step)
    {
        step = Clamp(step);
        return step == Normal ? 1.0 : Math.Pow(StepRatio, step);
    }

    /// <summary>The name shown to the player: "Default", or the signed step ("+3", "-2").</summary>
    public static string Name(int step) => OffsetName(Clamp(step));

    /// <summary>"Default" for 0, otherwise the signed offset: how both Text size rows name a size.</summary>
    public static string OffsetName(int offset)
        => offset == 0 ? "Default" : offset.ToString("+0;-0", CultureInfo.InvariantCulture);

    /// <summary>Parses the ini value; false when it is absent or not a number.</summary>
    public static bool TryParse(string? text, out int step)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
        {
            step = Clamp(v);
            return true;
        }
        step = Normal;
        return false;
    }

    public static string Format(int step) => Clamp(step).ToString(CultureInfo.InvariantCulture);

    /// <summary>The step an old three-step value stands for.</summary>
    public static int FromLegacyStep(int legacy) => legacy switch
    {
        <= 0 => Normal,
        1 => 2,
        _ => 4,
    };

    /// <summary>Moves an old <see cref="LegacyIniKey"/> value in the global section to
    /// <see cref="IniKey"/> and removes the old key. A value already under the new key wins. Returns
    /// whether anything changed.</summary>
    public static bool MigrateLegacyKey(Func<string, string, string?> get, Action<string, string, string> set,
        Action<string, string> remove, string section = "settings")
    {
        if (get(section, LegacyIniKey) is not { } legacy)
            return false;
        if (get(section, IniKey) is null)
        {
            var old = int.TryParse(legacy, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
            set(section, IniKey, Format(FromLegacyStep(old)));
        }
        remove(section, LegacyIniKey);
        return true;
    }

    /// <summary>An authored size at a step. Scaled sizes are rounded to the nearest half point;
    /// a scale of exactly 1 returns the authored size untouched.</summary>
    public static double Apply(double size, int step, bool tight = false)
    {
        var scale = ScaleFor(step);
        if (tight && scale > TightMaxScale) scale = TightMaxScale;
        return scale == 1.0 ? size : Math.Round(size * scale * 2.0, MidpointRounding.AwayFromZero) / 2.0;
    }

    /// <summary>How much wider than authored the side panel is at a step: the step's scale above
    /// Normal, and 1 at and below it, so the panel grows with its text and never narrows.</summary>
    public static double PanelScale(int step) => Math.Max(1.0, ScaleFor(step));
}
