using MudSharp.Models;

namespace Mucka.Terminal;

/// <summary>A kind of line the Session viewer's filters pick out.</summary>
public enum LineCategory
{
    /// <summary>What the game's chat filter shows (<see cref="PaneHistory.IsChat"/>).</summary>
    Chat,

    /// <summary>A line the server marks as a fight starting or ending (<see cref="LineKind.FightStart"/>,
    /// <see cref="LineKind.FightEnd"/>).</summary>
    FightStartsAndEnds,
}

/// <summary>What one filter does with the lines of its category.</summary>
public enum FilterAction
{
    /// <summary>Shows them, whatever else would hide them.</summary>
    Show,

    /// <summary>Hides them.</summary>
    Hide,

    /// <summary>Shows only them, and the lines of every other Only.</summary>
    Only,
}

/// <summary>One of the viewer's filters: a category, and what to do with it.</summary>
public readonly record struct FilterRule(LineCategory Category, FilterAction Action);

/// <summary>
/// The Session viewer's filters, over the lines the parser made (<c>SessionTextLog.Lines</c>), before
/// the pane's buffer merges a prompt onto the line that finishes it: a line's kind is the parser's,
/// and a merged line does not carry it. So a filter judges each line as the game's chat filter does
/// (<see cref="PaneHistory"/>), and what it keeps then becomes lines of screen
/// (<see cref="TerminalBuffer.ScreenOf"/>). A prompt is in no category.
///
/// <para>The filters together, in any order: with an Only among them, a line is shown when it is in
/// an Only's category; with none, every line is. A Hide then takes its category away, and a Show
/// puts its category back whatever an Only or a Hide said.</para>
/// </summary>
public static class LogFilter
{
    /// <summary>Whether <paramref name="line"/> is in <paramref name="category"/>.</summary>
    public static bool Is(LineCategory category, StyledLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return category switch
        {
            LineCategory.Chat => PaneHistory.IsChat(line),
            LineCategory.FightStartsAndEnds => !line.IsPartial && line.Kind is LineKind.FightStart or LineKind.FightEnd,
            _ => false,
        };
    }

    /// <summary>Whether <paramref name="rules"/> show <paramref name="line"/>.</summary>
    public static bool Shows(StyledLine line, IReadOnlyList<FilterRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        bool anyOnly = false, inOnly = false, hidden = false;
        foreach (var rule in rules)
        {
            var match = Is(rule.Category, line);
            switch (rule.Action)
            {
                case FilterAction.Show when match:
                    return true;
                case FilterAction.Hide when match:
                    hidden = true;
                    break;
                case FilterAction.Only:
                    anyOnly = true;
                    inOnly |= match;
                    break;
            }
        }
        return (!anyOnly || inOnly) && !hidden;
    }

    /// <summary>The lines <paramref name="rules"/> show, in order; all of them with no rules.</summary>
    public static List<StyledLine> Apply(IReadOnlyList<StyledLine> lines, IReadOnlyList<FilterRule> rules)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(rules);
        return rules.Count == 0 ? [.. lines] : lines.Where(line => Shows(line, rules)).ToList();
    }
}
