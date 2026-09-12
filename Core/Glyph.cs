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
    /// <summary>Multiplication X: a close button.</summary>
    public const string Close = "\u2715";
    /// <summary>Leftwards arrow: bytes received from the server.</summary>
    public const string ArrowLeft = "\u2190";
    /// <summary>Rightwards arrow: bytes sent to the server.</summary>
    public const string ArrowRight = "\u2192";
}
