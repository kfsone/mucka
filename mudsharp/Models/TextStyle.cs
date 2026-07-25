namespace MudSharp.Models;

public sealed record TextStyle(
    AnsiColor Foreground = AnsiColor.Default,
    AnsiColor Background = AnsiColor.Default,
    bool Bold = false,
    bool Underline = false,
    bool Blink = false,
    bool Reverse = false,
    bool Italic = false,
    // A packed 0xRRGGBB foreground override. When set, it wins over the ANSI palette slot
    // (and the bold→bright promotion) — used for client-applied colours such as the "me"
    // self-chat highlight that have no place in the 16-colour palette.
    int? ForegroundRgb = null
)
{
    public static readonly TextStyle Default = new();
}
