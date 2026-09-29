namespace MudSharp.Models;

public sealed record TextStyle(
    AnsiColor Foreground = AnsiColor.Default,
    AnsiColor Background = AnsiColor.Default,
    bool Bold = false,
    bool Underline = false,
    bool Blink = false,
    bool Reverse = false,
    bool Italic = false,
    // The C09 message part this span belongs to. A span with a part is drawn in that part's chat
    // colour setting, looked up when it is drawn (CampbellPalette.ForegroundRgb).
    SpeechPart Speech = SpeechPart.None,
    // On a span with a Speech part: the message is the local player's (ChatColorizer.Apply's
    // verdict). Its row's own or other face is looked up when it is drawn.
    bool Own = false,
    // On a span with a Speech part: the channel of the message it is in - the part of its quoted
    // words (ChatColorizer.Apply's verdict). A shout's or tell's emote spans take that channel's
    // colour (ChatColorizer.ColoursWholeLine).
    SpeechPart Channel = SpeechPart.None
)
{
    public static readonly TextStyle Default = new();
}
