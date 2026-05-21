namespace Mucka.Core;

/// <summary>A run of text with a single ANSI colour/style.</summary>
public sealed class StyledSpan
{
    public string Text { get; init; } = string.Empty;
    public byte Fg { get; init; } = 7;
    public byte Bg { get; init; } = 0;
    public bool Bold { get; init; }
    /// <summary>True when this span is the server's echo of the player's own input (rendered gray italic).</summary>
    public bool Echo { get; init; }
}

/// <summary>A single display line made of styled spans. May be partial (no newline received yet).</summary>
public sealed class StyledLine
{
    private readonly List<StyledSpan> _spans = new();
    public IReadOnlyList<StyledSpan> Spans => _spans;
    public string PlainText => string.Concat(_spans.Select(s => s.Text));
    /// <summary>True if this line has not yet been terminated by a newline (e.g. a login prompt).</summary>
    public bool IsPartial { get; set; }
    /// <summary>True when this line is a clear-screen sentinel (form feed) rather than display text.</summary>
    public bool IsClearScreen { get; init; }
    public void Add(StyledSpan span) => _spans.Add(span);
}
