namespace MudSharp.Models;

/// <summary>
/// An ordered sequence of styled spans forming one line of MUD2 output.
/// IsPartial = true means no \n received yet (e.g. a login prompt) — rendered in-place, replaced when completed.
/// </summary>
public sealed class StyledLine
{
    public IReadOnlyList<StyledSpan> Spans { get; }
    public bool IsPartial { get; }

    /// <summary>Semantic class of this line, from the C1 code that introduced it. Drives the chat-view filter.</summary>
    public LineKind Kind { get; }

    private string? _plainText;

    public StyledLine(IReadOnlyList<StyledSpan> spans, bool isPartial = false, LineKind kind = LineKind.Normal)
    {
        Spans = spans;
        IsPartial = isPartial;
        Kind = kind;
    }

    // Cached: spans are immutable, and the '\n' hot path reads this several times per line
    // (line analyzer, sound triggers, room-short/too-dark checks) plus every consumer.
    public string PlainText => _plainText ??= string.Concat(Spans.Select(s => s.Text));
}
