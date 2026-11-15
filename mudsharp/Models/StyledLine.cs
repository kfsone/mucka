namespace MudSharp.Models;

/// <summary>
/// An ordered sequence of styled spans forming one line of MUD2 output.
/// IsPartial = true means no \n received yet (e.g. a login prompt) - rendered in-place, replaced when completed.
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
    /// one message - e.g. the chat colouring keeps its whose-message verdict across them - instead
    /// of guessing from the text.
    /// </summary>
    public bool ContinuesChat { get; }

    /// <summary>
    /// The C1 codes whose colour frames held this line's text, each once, in the order the text
    /// met them: for each run of text, the code of the innermost frame open when it arrived. Text
    /// in no frame but C00's (plain, un-coded output) adds none, so a line of it has none. Made by
    /// the parser; a line made anywhere else has none.
    /// </summary>
    public IReadOnlyList<C1Code> Codes { get; }

    private string? _plainText;

    public StyledLine(IReadOnlyList<StyledSpan> spans, bool isPartial = false, LineKind kind = LineKind.Normal,
        bool continuesChat = false, IReadOnlyList<C1Code>? codes = null)
    {
        Spans = spans;
        IsPartial = isPartial;
        Kind = kind;
        ContinuesChat = continuesChat;
        Codes = codes ?? [];
    }

    /// <summary>The same line with its spans replaced and every other fact kept - the one way to
    /// restyle a line without dropping what the parser knew about it.</summary>
    public StyledLine WithSpans(IReadOnlyList<StyledSpan> spans)
        => new(spans, IsPartial, Kind, ContinuesChat, Codes);

    // Cached: spans are immutable, and the '\n' hot path reads this several times per line
    // (line analyzer, sound triggers, room-short/too-dark checks) plus every consumer.
    public string PlainText => _plainText ??= string.Concat(Spans.Select(s => s.Text));
}
