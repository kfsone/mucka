namespace MudSharp.Models;

public sealed record StyledSpan(string Text, TextStyle Style, string? ClickInsertText = null);
