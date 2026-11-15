using System.Text.RegularExpressions;
using MudSharp.Models;

namespace Mucka.Terminal;

/// <summary>What a filter rule picks out: its first part.</summary>
public enum RuleKind
{
    /// <summary>What the game's chat filter shows (<see cref="PaneHistory.IsChat"/>). Sub: the channel
    /// (<see cref="SpeechPart"/>). Pattern: in the speaker's part of the message.</summary>
    Chat,

    /// <summary>Text under the 02 family: room names (02 01) and long descriptions (02 02). Sub: which
    /// (<see cref="RoomPart"/>). Pattern: in the line.</summary>
    RoomText,

    /// <summary>Text under the 07 (isolated hits) and 08 (fight) families. Sub: which side. Pattern: a
    /// creature or player named in the line.</summary>
    Combat,

    /// <summary>08 08 "You killed them." and 08 09 "They killed you.". Sub: which. Pattern: a name.</summary>
    Kills,

    /// <summary>08 11 "Fight ends - flee.", a flee tried whether or not it got away. Pattern: a name.</summary>
    Flees,

    /// <summary>Text under the 14 family, the weather. No sub, no pattern.</summary>
    Weather,

    /// <summary>Any line. Pattern: in the line.</summary>
    Text,
}

/// <summary>A Room text rule's sub: which of a room's text.</summary>
public enum RoomPart
{
    /// <summary>"Short": a room's name line (02 00, 02 01), not inside a "Looking westward, you see ..."
    /// line.</summary>
    Names,

    /// <summary>"Short + Long": names and long descriptions, not the "Looking ..." lines.</summary>
    NamesAndDescriptions,

    /// <summary>"Long": long descriptions (02 02).</summary>
    Descriptions,

    /// <summary>Every line holding 02 text, the "Looking ..." lines (12 00 round a 02 01 name) included.</summary>
    All,
}

/// <summary>
/// One filter rule as the viewer's table shows it: a kind, then where the kind has them a sub
/// (<see cref="LogFilter.SubsOf"/>) and a pattern (<see cref="LogFilter.PatternOf"/>), and an eye that
/// says whether what it matches is shown or hidden.
/// </summary>
/// <param name="Sub">An index into <see cref="LogFilter.SubsOf"/>; 0 is the first choice.</param>
/// <param name="Pattern">Empty matches every line the kind and sub pick out.</param>
public sealed record FilterRule(RuleKind Kind, int Sub = 0, string Pattern = "", bool Show = true);

/// <summary>
/// The Session viewer's filters, over the lines the parser made (<c>SessionTextLog.Lines</c>), before
/// the pane's buffer merges a prompt onto the line that finishes it: a line's kind and codes are the
/// parser's, and a merged line does not carry them. So a rule judges each line as the game's chat
/// filter does (<see cref="PaneHistory"/>), and what is kept then becomes lines of screen
/// (<see cref="TerminalBuffer.ScreenOf"/>). A prompt matches no rule.
///
/// <para>The rules together, in any order: with no rule showing (its eye open), every line is shown;
/// with any, a line is shown when a showing rule matches it. A hiding rule then hides what it
/// matches, whatever a showing rule said.</para>
///
/// <para>A kind and its sub are the server's codes; a pattern only narrows them, matched in the
/// line's text ignoring case. A name - a speaker, a creature - matches as a word, a creature's number
/// after it allowed: "rat" matches "rat21", not "rattlesnake".</para>
/// </summary>
public static class LogFilter
{
    private static readonly string[] ChatSubs = ["any channel", "say", "shout", "tell"];
    private static readonly string[] RoomSubs = ["Short", "Short + Long", "Long", "All"];
    private static readonly string[] CombatSubs = ["any", "you attack", "attacking you"];
    private static readonly string[] KillSubs = ["any", "you killed", "killed you"];

    /// <summary>A kind's sub choices, the first the default; empty when it has none.</summary>
    public static IReadOnlyList<string> SubsOf(RuleKind kind) => kind switch
    {
        RuleKind.Chat => ChatSubs,
        RuleKind.RoomText => RoomSubs,
        RuleKind.Combat => CombatSubs,
        RuleKind.Kills => KillSubs,
        _ => [],
    };

    /// <summary>What a kind's pattern box asks for, or null when it has none.</summary>
    public static string? PatternOf(RuleKind kind) => kind switch
    {
        RuleKind.Chat => "from anyone",
        RuleKind.RoomText => "containing",
        RuleKind.Combat or RuleKind.Kills or RuleKind.Flees => "with anyone",
        RuleKind.Text => "containing",
        _ => null,
    };

    /// <summary>A Room text rule's default sub: All, every line of a room's text.</summary>
    public const int DefaultRoomSub = (int)RoomPart.All;

    /// <summary>Whether <paramref name="rule"/>'s kind, sub and pattern match <paramref name="line"/>,
    /// whatever its eye. <paramref name="head"/> is the line that began the chat message a wrapped row
    /// continues (<see cref="StyledLine.ContinuesChat"/>), which holds the speaker; the line itself
    /// otherwise.</summary>
    public static bool Matches(FilterRule rule, StyledLine line, StyledLine? head = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(line);
        if (line.IsPartial)
            return false;
        var pattern = rule.Pattern.Trim();
        switch (rule.Kind)
        {
            case RuleKind.Chat:
                if (!PaneHistory.IsChat(line))
                    return false;
                var channel = rule.Sub switch { 1 => SpeechPart.Say, 2 => SpeechPart.Shout, 3 => SpeechPart.Tell, _ => SpeechPart.None };
                if (channel != SpeechPart.None && !OnChannel(line, channel))
                    return false;
                return pattern.Length == 0 || IsNamed(SpeakerOf(head ?? line), pattern);
            case RuleKind.RoomText:
                if (!IsRoomPart(line, (RoomPart)Math.Clamp(rule.Sub, 0, 3)))
                    return false;
                return pattern.Length == 0 || line.PlainText.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            case RuleKind.Combat:
                var fight = rule.Sub switch
                {
                    1 => line.Codes.Any(c => c is { Family: 8, Sub: 1 or 2 }),
                    2 => line.Codes.Any(c => c.Family == 7 || c is { Family: 8, Sub: 3 or 4 }),
                    _ => line.Codes.Any(c => c.Family is 7 or 8),
                };
                return fight && (pattern.Length == 0 || IsNamed(line.PlainText, pattern));
            case RuleKind.Kills:
                var kill = rule.Sub switch
                {
                    1 => line.Codes.Contains(new C1Code(8, 8)),
                    2 => line.Codes.Contains(new C1Code(8, 9)),
                    _ => line.Codes.Any(c => c is { Family: 8, Sub: 8 or 9 }),
                };
                return kill && (pattern.Length == 0 || IsNamed(line.PlainText, pattern));
            case RuleKind.Flees:
                return line.Codes.Contains(new C1Code(8, 11)) && (pattern.Length == 0 || IsNamed(line.PlainText, pattern));
            case RuleKind.Weather:
                return line.Codes.Any(c => c.Family == 14);
            case RuleKind.Text:
                return pattern.Length == 0 || line.PlainText.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            default:
                return false;
        }
    }

    /// <summary>The lines <paramref name="rules"/> show, in order; all of them with no rules.</summary>
    public static List<StyledLine> Apply(IReadOnlyList<StyledLine> lines, IReadOnlyList<FilterRule> rules)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Count == 0)
            return [.. lines];
        var showing = rules.Any(r => r.Show);
        var kept = new List<StyledLine>(lines.Count);
        StyledLine? head = null;
        foreach (var line in lines)
        {
            head = HeadOf(line, head);
            var shown = !showing || rules.Any(r => r.Show && Matches(r, line, head));
            if (shown && !rules.Any(r => !r.Show && Matches(r, line, head)))
                kept.Add(line);
        }
        return kept;
    }

    /// <summary>How many of <paramref name="lines"/> <paramref name="rule"/> matches, whatever its eye.</summary>
    public static int Count(IReadOnlyList<StyledLine> lines, FilterRule rule)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var count = 0;
        StyledLine? head = null;
        foreach (var line in lines)
        {
            head = HeadOf(line, head);
            if (Matches(rule, line, head))
                count++;
        }
        return count;
    }

    // The line that began the chat message this line is part of: itself, or for a wrapped row the head
    // the row before it had.
    private static StyledLine? HeadOf(StyledLine line, StyledLine? previous)
        => line.IsPartial ? previous : line.ContinuesChat ? previous ?? line : line;

    // The words' own channel, or the one ChatColorizer.ApplyAll stamped on the message's every span:
    // a wrapped row that ends before the quoted words holds only the speaker.
    private static bool OnChannel(StyledLine line, SpeechPart part)
        => line.Spans.Any(s => s.Style.Speech == part || s.Style.Channel == part);

    private static string SpeakerOf(StyledLine line)
        => string.Concat(line.Spans.Where(s => s.Style.Speech == SpeechPart.Speaker).Select(s => s.Text));

    private static bool IsRoomPart(StyledLine line, RoomPart part)
    {
        var looking = line.Codes.Contains(new C1Code(12, 0));
        var name = line.Codes.Any(c => c is { Family: 2, Sub: 0 or 1 });
        var description = line.Codes.Contains(new C1Code(2, 2));
        return part switch
        {
            RoomPart.Names => name && !looking,
            RoomPart.NamesAndDescriptions => (name || description) && !looking,
            RoomPart.Descriptions => description,
            _ => line.Codes.Any(c => c.Family == 2),
        };
    }

    // A name as a word, a creature's number after it allowed, ignoring case. Each name's expression is
    // built once and kept: a rule is matched against every line of a Session, and counted again.
    private static bool IsNamed(string text, string name)
        => NameExpressions.GetOrAdd(name, n => new Regex(
            @"(?<![\p{L}\p{N}])" + Regex.Escape(n) + @"\d*(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).IsMatch(text);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> NameExpressions = new();
}
