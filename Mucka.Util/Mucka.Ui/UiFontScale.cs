using System.Globalization;

namespace Mucka.Ui;

/// <summary>
/// The "UI font size" setting: three steps that multiply every authored chrome font size.
/// Step 0 (Normal) multiplies by exactly 1, so a size comes back bit-for-bit as authored.
/// The terminal grid and the Combat Rail canvas have their own sizing and do not use this.
/// </summary>
public static class UiFontScale
{
    public const int Normal = 0;
    public const int Large = 1;
    public const int Larger = 2;

    /// <summary>The ini key, in the global [settings] section.</summary>
    public const string IniKey = "uifontstep";

    /// <summary>Scale for each step, indexed by step. An 11pt label becomes 12.5pt and 14.5pt, a
    /// 14pt one 16pt and 18pt: each step is one to four points up across the sizes the chrome
    /// uses (8-16pt), and the first stays inside the width of the fixed side panel for the
    /// authored 11pt rows.</summary>
    private static readonly double[] s_scales = [1.0, 1.15, 1.3];

    /// <summary>Names shown in the settings picker, indexed by step.</summary>
    public static IReadOnlyList<string> Names { get; } = ["Normal", "Large", "Larger"];

    /// <summary>The largest scale applied to the status bar and F-key toolbar, whose widths are
    /// tiered by terminal columns and whose rows have fixed heights.</summary>
    public const double TightMaxScale = 1.15;

    public static int Clamp(int step) => Math.Clamp(step, Normal, Larger);

    public static double ScaleFor(int step) => s_scales[Clamp(step)];

    /// <summary>Parses the ini value; anything unparseable is <see cref="Normal"/>.</summary>
    public static int Parse(string? text)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Clamp(v) : Normal;

    public static string Format(int step) => Clamp(step).ToString(CultureInfo.InvariantCulture);

    /// <summary>An authored size at a step. Scaled sizes are rounded to the nearest half point;
    /// a scale of exactly 1 returns the authored size untouched.</summary>
    public static double Apply(double size, int step, bool tight = false)
    {
        var scale = ScaleFor(step);
        if (tight && scale > TightMaxScale) scale = TightMaxScale;
        return scale == 1.0 ? size : Math.Round(size * scale * 2.0, MidpointRounding.AwayFromZero) / 2.0;
    }
}
