namespace MudSharp.Models;

/// <summary>
/// The decorated parts of a tell's label - the text of a 09 03 message before its told words:
/// <c>Lazlo the yeoman tells you "</c>, <c>Lazlo tells you "</c> or <c>Someone tells you "</c>
/// inbound, <c>You tell your listeners "</c> outbound (every tell you send echoes in that form,
/// directed or not). The verb is underlined in both directions; the
/// sender, inbound only, is underlined and click-inserts its name. The pane's tell decoration and
/// the settings editor's tell examples both read it.
/// </summary>
public readonly record struct TellLabel(int VerbStart, int VerbLength, int SenderLength, string? SenderName)
{
    private const string OutboundLead = "You tell ";
    private const string InboundMarker = " tells you";

    /// <summary>
    /// The parts of <paramref name="label"/>, or null when it is neither form. The sender is the
    /// label's first word, and is absent when that is "Someone".
    /// </summary>
    public static TellLabel? Parse(string label)
    {
        if (label.StartsWith(OutboundLead, StringComparison.Ordinal))
            return new TellLabel(VerbStart: "You ".Length, VerbLength: "tell".Length, SenderLength: 0, SenderName: null);

        int marker = label.IndexOf(InboundMarker, StringComparison.OrdinalIgnoreCase);
        if (marker <= 0) return null;

        int firstSpace = label.IndexOfAny([' ', '\t']);
        if (firstSpace < 1 || firstSpace > marker)
            firstSpace = marker;

        var senderToken = label[..firstSpace];
        bool named = !senderToken.Equals("Someone", StringComparison.OrdinalIgnoreCase);
        return new TellLabel(VerbStart: marker + 1, VerbLength: "tells".Length,
            SenderLength: named ? firstSpace : 0, SenderName: named ? senderToken : null);
    }
}
