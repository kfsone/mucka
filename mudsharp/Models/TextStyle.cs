namespace MudSharp.Models;

public sealed record TextStyle(
    AnsiColor Foreground = AnsiColor.Default,
    AnsiColor Background = AnsiColor.Default,
    bool Bold = false,
    bool Underline = false,
    bool Blink = false,
    bool Reverse = false
)
{
    public static readonly TextStyle Default = new();
}
