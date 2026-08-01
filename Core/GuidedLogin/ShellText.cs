using System.Text.RegularExpressions;

namespace Mucka.Core.GuidedLogin;

/// <summary>One entry from the MUD Shell's numbered "personae available to you" list
/// (shown after sending "p" at the Option menu, right before "By what name...?").</summary>
/// <param name="Slot">1-based slot number, in the order the shell lists them.</param>
/// <param name="Name">Persona name, or null when <see cref="IsUnused"/> is true.</param>
/// <param name="IsUnused">True when this slot is free ("**Unused**") and can hold a new persona.</param>
public sealed record PersonaSlot(int Slot, string? Name, bool IsUnused);

/// <summary>One persona's summary line from the EXAMINE ("e") sub-shell listing.</summary>
public sealed record ExaminePersona(string Name, string Sex, int Score, int Played);

/// <summary>
/// Pure text-matching helpers for driving the MUD Shell (Option menu -> EXAMINE -> persona
/// selection/creation -> tearoom). No MAUI dependency so this is unit-testable in isolation
/// (see mudsharp.Tests, which links this file the same way it links SessionCommandAliases.cs).
///
/// The MUD wraps all server text to the negotiated terminal width and pads wrapped lines with a
/// spurious "\r\0" before the real "\r\n", so every landmark check here is done against text that
/// has first been run through <see cref="NormalizeWhitespace"/> (all runs of whitespace/NUL
/// collapsed to a single space) rather than matched against raw line boundaries.
/// </summary>
public static class ShellText
{
    /// <summary>Collapses all whitespace (including the embedded NUL wrap-padding byte) to single
    /// spaces and trims the ends, so landmark/phrase matching is agnostic to how the server wrapped
    /// a given line for the negotiated terminal width.</summary>
    public static string NormalizeWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new System.Text.StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var c in text)
        {
            var isSpace = c is ' ' or '\r' or '\n' or '\t' or '\0';
            if (isSpace)
            {
                if (!lastWasSpace && sb.Length > 0)
                    sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }

        // Trim a single trailing space left by a run of whitespace at the very end.
        if (sb.Length > 0 && sb[^1] == ' ')
            sb.Length--;
        return sb.ToString();
    }

    private static bool ContainsPhrase(string normalized, string phrase)
        => normalized.Contains(phrase, StringComparison.OrdinalIgnoreCase);

    /// <summary>Any "(y/n)" confirmation prompt shown between login and the shell splash --
    /// e.g. "Skip the rest? (y/n)" (MOTD skip) or "...usurp...(y/n)" (kick an existing session).
    /// Guided login answers "y" to whichever of these actually appears.</summary>
    public static bool IsYesNoPrompt(string normalized) => ContainsPhrase(normalized, "(y/n)");

    /// <summary>The "&lt;account&gt; logged in on &lt;tty&gt;." line — the splash/banner proper starts
    /// immediately after this (skipping over any MOTD/notice text and y/n prompt in between).</summary>
    public static bool IsLoggedInLine(string normalized) => ContainsPhrase(normalized, "logged in on");

    /// <summary>"[Checking mail...]" — the splash/banner ends immediately before this.</summary>
    public static bool IsCheckingMailLine(string normalized) => ContainsPhrase(normalized, "checking mail");

    /// <summary>The MUD Shell's top-level "Option (H for help):" prompt.</summary>
    public static bool IsShellOptionPrompt(string normalized) => ContainsPhrase(normalized, "option (h for help)");

    /// <summary>The EXAMINE sub-shell's "EXAMINE>" prompt.</summary>
    public static bool IsExaminePrompt(string normalized) => ContainsPhrase(normalized, "examine>");

    /// <summary>The persona-selection prompt shown after sending "p".</summary>
    public static bool IsPersonaNamePrompt(string normalized)
        => ContainsPhrase(normalized, "by what name shall i call you");

    /// <summary>"Creating new persona." confirmation line.</summary>
    public static bool IsCreatingPersonaLine(string normalized) => ContainsPhrase(normalized, "creating new persona");

    /// <summary>The new-persona "What sex do you wish to be?" prompt (answer with 'm', 'f', or 'q').</summary>
    public static bool IsSexPrompt(string normalized) => ContainsPhrase(normalized, "what sex do you wish to be");

    /// <summary>
    /// Parses the numbered persona list out of the "personae available to you" block
    /// (normalized text, between "are:" and "by what name..."). Returns null if the landmark
    /// text isn't present yet (e.g. more output is still arriving).
    /// </summary>
    public static IReadOnlyList<PersonaSlot>? TryParsePersonaSlots(string normalized)
    {
        const string startMarker = "personae available to you are:";
        var startIdx = normalized.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
        if (startIdx < 0)
            return null;
        startIdx += startMarker.Length;

        var endIdx = normalized.IndexOf("by what name", startIdx, StringComparison.OrdinalIgnoreCase);
        if (endIdx < 0)
            return null;

        var body = normalized[startIdx..endIdx];
        // Slot numbers like "(1)" are always sequential in the order listed, so strip them
        // rather than parse them — simpler and immune to whitespace-mangled "( 1 )" variants.
        body = Regex.Replace(body, @"\(\s*\d+\s*\)", string.Empty);

        var slots = new List<PersonaSlot>();
        var slot = 0;
        foreach (var rawToken in body.Split(',', '.'))
        {
            var token = rawToken.Trim();
            if (token.Length == 0)
                continue;
            slot++;
            slots.Add(token.Equals("**Unused**", StringComparison.OrdinalIgnoreCase)
                ? new PersonaSlot(slot, null, true)
                : new PersonaSlot(slot, token, false));
        }

        return slots;
    }

    /// <summary>
    /// Extracts the real login splash/banner (ASCII logo etc) out of the raw line buffer captured
    /// from connection start through the first "Option (H for help):" prompt: everything from just
    /// after the "&lt;account&gt; logged in on..." line up to (not including) "[Checking mail...]",
    /// with any "(y/n)" prompt line and its bare "y"/"n" answer-echo stripped out. Returns null if
    /// there's nothing left (or the "logged in on" landmark was never seen).
    /// </summary>
    public static string? ExtractSplash(IReadOnlyList<string> lines)
    {
        var start = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (IsLoggedInLine(NormalizeWhitespace(lines[i])))
            {
                start = i + 1;
                break;
            }
        }

        var end = lines.Count;
        for (var i = start; i < lines.Count; i++)
        {
            if (IsCheckingMailLine(NormalizeWhitespace(lines[i])))
            {
                end = i;
                break;
            }
        }

        var kept = new List<string>();
        for (var i = start; i < end; i++)
        {
            var normalized = NormalizeWhitespace(lines[i]);
            if (normalized.Length == 0)
            {
                kept.Add(string.Empty);
                continue;
            }
            if (IsYesNoPrompt(normalized))
                continue;
            if (normalized is "y" or "n")   // leftover echo of our answer to the y/n prompt
                continue;
            kept.Add(lines[i]);
        }

        var splash = string.Join("\n", kept).Trim('\r', '\n', ' ');
        return splash.Length > 0 ? splash : null;
    }

    private static readonly Regex ExaminePersonaRegex = new(
        @"(?<name>[A-Za-z][A-Za-z']*)\s+Score:\s*(?<score>[\d,]+)\s+Played:\s*(?<played>\d+)\s+(?<sex>male|female)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses persona summary lines out of the EXAMINE ("e") sub-shell listing (normalized text).
    /// Safe to call incrementally on a growing buffer — returns whatever complete entries have
    /// arrived so far.
    /// </summary>
    public static IReadOnlyList<ExaminePersona> ParseExaminePersonae(string normalized)
    {
        var result = new List<ExaminePersona>();
        foreach (Match m in ExaminePersonaRegex.Matches(normalized))
        {
            var score = int.Parse(m.Groups["score"].Value.Replace(",", string.Empty));
            var played = int.Parse(m.Groups["played"].Value);
            result.Add(new ExaminePersona(m.Groups["name"].Value, m.Groups["sex"].Value.ToLowerInvariant(), score, played));
        }
        return result;
    }
}
