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

    /// <summary>
    /// True when this line continues the chat message of the previous line: its C09 colour scope
    /// was already open when the line started (the server soft-wrapped one speaker message across
    /// several '\n' lines without re-sending the code). Lets consumers treat the wrapped rows as
    /// one message — e.g. the self-chat recolour keeps its per-message state across them — instead
    /// of guessing from the text.
    /// </summary>
    public bool ContinuesChat { get; }

    private string? _plainText;

    public StyledLine(IReadOnlyList<StyledSpan> spans, bool isPartial = false, LineKind kind = LineKind.Normal,
        bool continuesChat = false)
    {
        Spans = spans;
        IsPartial = isPartial;
        Kind = kind;
        ContinuesChat = continuesChat;
    }

    // Cached: spans are immutable, and the '\n' hot path reads this several times per line
    // (line analyzer, sound triggers, room-short/too-dark checks) plus every consumer.
    public string PlainText => _plainText ??= string.Concat(Spans.Select(s => s.Text));
}
