using System.Text.RegularExpressions;

namespace MudSharp.Session;

/// <summary>
/// Whether a frame's text is the server's reply to the post-character-select setup batch, read on the
/// text with the server's wrapping taken out (<see cref="MudSharp.Models.ServerText.Collapse"/>) so
/// the answer does not depend on the terminal width.
/// </summary>
internal static class SetupReplies
{
    internal enum Verdict
    {
        /// <summary>A setup reply: swallow the frame.</summary>
        Ours,
        /// <summary>Not yet known: the text so far is the opening of a setup reply.</summary>
        Undecided,
        /// <summary>Not a setup reply: pass it on.</summary>
        NotOurs,
    }

    // The replies' opening words, as far as it takes to tell them from other prose. Verbatim, from the
    // wire table: identify and fightbrief answer "You'll now get ..." when the setting was off and
    // "You're already getting ..." when it was on (520 of 531 replies in the operator's wire log were
    // the "already" form, because the batch re-sends them every entry), and every auto fex reply
    // recorded reads "You will now get an automatic FEEXITS command performed every time you issue a
    // movement command. To cancel it, use UNAUTO FEEXITS." - except for a high-ranking persona, which
    // is refused (125 times in the operator's wire log): "At your level, you should be well past the
    // stage of needing to use AUTO FEEXITS commands... If you're really so much of a wimp that you
    // get lost without them, you'll just have to cope as best you can with the manual version,
    // FEEXITS."
    private static readonly string[] Openings =
    [
        "You'll now get object identification numbers",
        "You're already getting object identification numbers",
        "You'll now get brief descriptions of fights",
        "You're already getting brief descriptions of fights",
        "You will now get an automatic FEEXITS",
        "At your level, you should be well past the stage of needing to use AUTO FEEXITS",
    ];

    // The score sheet opens "name:" and the character's name, which at /T20 was pushed to the next row:
    // "name:   " / "        Kayfez".
    private const string NameLabel = "name:";
    private static readonly Regex NameLine = new(
        @"^name:\s+(\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Classifies a frame's text so far: its rows joined, collapsed, and any leading frame prompt
    /// stripped. <paramref name="characterName"/> is set when the text is the score sheet.
    /// </summary>
    /// <param name="commands">The batch as sent. The server echoes the batch as one frame, a command
    /// to a row, so the frame's first row alone is a command and the frame is the echo frame.</param>
    internal static Verdict Classify(string text, IReadOnlyList<string> commands, out string? characterName)
    {
        characterName = null;
        if (string.IsNullOrEmpty(text))
            return Verdict.Undecided;

        foreach (var cmd in commands)
            if (text.Equals(cmd, StringComparison.OrdinalIgnoreCase))
                return Verdict.Ours;

        var undecided = false;
        foreach (var opening in Openings)
        {
            if (text.StartsWith(opening, StringComparison.OrdinalIgnoreCase))
                return Verdict.Ours;
            if (opening.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                undecided = true;
        }

        var name = NameLine.Match(text);
        if (name.Success)
        {
            characterName = name.Groups[1].Value;
            return Verdict.Ours;
        }
        if (text.Equals(NameLabel, StringComparison.OrdinalIgnoreCase))
            undecided = true;

        return undecided ? Verdict.Undecided : Verdict.NotOurs;
    }
}
