namespace MudSharp.Models;

/// <summary>
/// The text of one example line in the settings editor's chat colours preview, in the pane's
/// three-part shape: the speaker's framing (<see cref="Lead"/> and <see cref="Tail"/>) around the
/// quoted words (<see cref="Quoted"/>). <see cref="Words"/> is the row that colours the quoted
/// words; an act or emote is all lead and belongs to the Speaker row. <see cref="Own"/> is a line
/// of your own rather than another Creature's.
///
/// <para>A tell's lead carries the underlines the pane's tell decoration draws, found by the same
/// <see cref="TellLabel"/>. <see cref="LeadRuns"/> is the lead cut into exactly
/// <see cref="LeadRunCount"/> runs that alternate plain and underlined, plain first, so a fixed
/// row of spans can draw it.</para>
/// </summary>
public sealed class ChatExampleText
{
    public const int LeadRunCount = 5;

    public ChatExampleText(SpeechPart words, bool own, string lead, string quoted = "", string tail = "")
    {
        Words = words;
        Own = own;
        Lead = lead;
        Quoted = quoted;
        Tail = tail;
        LeadRuns = CutLead(lead, words == SpeechPart.Tell ? TellLabel.Parse(lead) : null);
    }

    public SpeechPart Words { get; }
    public bool Own { get; }
    public string Lead { get; }
    public string Quoted { get; }
    public string Tail { get; }

    /// <summary>The lead in <see cref="LeadRunCount"/> runs; the odd-indexed runs are underlined.</summary>
    public IReadOnlyList<string> LeadRuns { get; }

    // Other's lines are another Creature's; Yourname's and "You" are yours. The names and framing
    // of every line are the Speaker row's, so that row's own examples are the emotes.
    public static IReadOnlyList<ChatExampleText> Say { get; } =
    [
        new(SpeechPart.Say, own: false, "Other the hero says \"", "Fizz", "\"."),
        new(SpeechPart.Say, own: true,  "Yourname the hero says \"", "Buzz", "\"."),
    ];

    public static IReadOnlyList<ChatExampleText> Shout { get; } =
    [
        new(SpeechPart.Shout, own: false, "Other the hero shouts \"", "Fizz!", "\"."),
        new(SpeechPart.Shout, own: true,  "Yourname the hero shouts \"", "Buzz!", "\"."),
    ];

    public static IReadOnlyList<ChatExampleText> Tell { get; } =
    [
        new(SpeechPart.Tell, own: false, "Other the hero tells you \"", "Fizz", "\"."),
        new(SpeechPart.Tell, own: true,  "You tell your listeners \"", "Buzz", "\"."),
    ];

    public static IReadOnlyList<ChatExampleText> Speaker { get; } =
    [
        new(SpeechPart.Speaker, own: false, "Other the hero dances."),
        new(SpeechPart.Speaker, own: true,  "Yourname the hero dances."),
    ];

    private static string[] CutLead(string lead, TellLabel? tell)
    {
        var underlined = new List<(int Start, int End)>(2);
        if (tell is { } t)
        {
            if (t.SenderLength > 0) underlined.Add((0, t.SenderLength));
            underlined.Add((t.VerbStart, t.VerbStart + t.VerbLength));
        }
        underlined.Sort();

        var runs = new string[LeadRunCount];
        Array.Fill(runs, string.Empty);
        int at = 0, run = 0;
        foreach (var (start, end) in underlined)
        {
            if (run + 2 >= LeadRunCount)
                throw new InvalidOperationException($"More underlined runs than {LeadRunCount} can hold: {lead}");
            runs[run++] = lead[at..start];
            runs[run++] = lead[start..end];
            at = end;
        }
        runs[run] = lead[at..];
        return runs;
    }
}
