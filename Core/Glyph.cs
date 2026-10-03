namespace Mucka.Core;

/// <summary>
/// Every non-ASCII character the UI draws, named and defined by its code point. The repo is
/// ASCII (CLAUDE.md, Layout), so a glyph enters C# only through this table; XAML uses the
/// matching <c>&amp;#xXXXX;</c> entity.
/// </summary>
public static class Glyph
{
    /// <summary>Small right-pointing triangle: a collapsed sub-menu.</summary>
    public const string ChevronRight = "\u25B8";
    /// <summary>Small down-pointing triangle: an expanded sub-menu.</summary>
    public const string ChevronDown = "\u25BE";
    /// <summary>Right-pointing triangle: a collapsed group.</summary>
    public const string TriangleRight = "\u25B6";
    /// <summary>Down-pointing triangle: an expanded group.</summary>
    public const string TriangleDown = "\u25BC";
    /// <summary>Leftwards arrow with hook: the there-and-back (u-turn) button.</summary>
    public const string UTurn = "\u21A9";
    /// <summary>Clockwise open circle arrow: a reset-to-defaults button.</summary>
    public const string Reset = "\u21BB";
    /// <summary>Multiplication X: a close button.</summary>
    public const string Close = "\u2715";
    /// <summary>Leftwards arrow: bytes received from the server.</summary>
    public const string ArrowLeft = "\u2190";
    /// <summary>Rightwards arrow: bytes sent to the server.</summary>
    public const string ArrowRight = "\u2192";
    /// <summary>Ballot box: an unticked option.</summary>
    public const string BoxEmpty = "\u2610";
    /// <summary>Ballot box with check: a ticked option.</summary>
    public const string BoxChecked = "\u2611";
    /// <summary>White circle: an unchosen one-of-several option.</summary>
    public const string RadioOff = "\u25cb";
    /// <summary>Black circle: the chosen one-of-several option.</summary>
    public const string RadioOn = "\u25cf";
    /// <summary>Skull and crossbones: a persona wiped, on the score graph.</summary>
    public const string Skull = "\u2620";

    /// <summary>White square: a dock toggle's "float me" action, shown while the panel is docked.</summary>
    public const string SquareHollow = "\u25a1";
    /// <summary>Black square: a dock toggle's "dock me" action, shown while the panel floats.</summary>
    public const string SquareFilled = "\u25a0";
    /// <summary>Lock (emoji): a floating panel locked - content only, no title strip, no drag.</summary>
    public const string PadlockClosed = "\U0001F512";
    /// <summary>Open lock (emoji): a floating panel unlocked for dragging.</summary>
    public const string PadlockOpen = "\U0001F513";

    // Weather, from the FES weather character. Each carries U+FE0E, the text-presentation selector,
    // so it draws as a monochrome glyph that takes the status bar's colour rather than as emoji.
    /// <summary>Sun: fine weather.</summary>
    public const string WeatherFine = "\u2600\ufe0e";
    /// <summary>Sun behind cloud: cloudy.</summary>
    public const string WeatherCloudy = "\u26c5\ufe0e";
    /// <summary>Umbrella: raining.</summary>
    public const string WeatherRain = "\u2602\ufe0e";
    /// <summary>Snowflake: snowing.</summary>
    public const string WeatherSnow = "\u2744\ufe0e";
    /// <summary>Cloud: overcast.</summary>
    public const string WeatherOvercast = "\u2601\ufe0e";
    /// <summary>High voltage: stormy.</summary>
    public const string WeatherStorm = "\u26a1\ufe0e";
    /// <summary>Snowman: blizzard.</summary>
    public const string WeatherBlizzard = "\u2603\ufe0e";

    // Afflictions on the status bar.
    /// <summary>Ear (emoji): deaf.</summary>
    public const string Ear = "\U0001F442";
    /// <summary>Eye with U+FE0F, the emoji-presentation selector: blind.</summary>
    public const string Eye = "\U0001F441\ufe0f";
    /// <summary>Mouth (emoji): dumb.</summary>
    public const string Mouth = "\U0001F444";
    /// <summary>Wheelchair symbol: crippled.</summary>
    public const string Wheelchair = "\u267f";

    /// <summary>Clockwise right and left semicircle arrows: the swap mark on the Combat Rail's
    /// alternate-weapon line.</summary>
    public const string Swap = "\U0001F5D8";
}
