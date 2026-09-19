using System.Text.RegularExpressions;

namespace Mucka.Commands;

/// <summary>Stores client commands whose lifetime is the current gameworld visit.</summary>
public sealed class SessionCommandAliases
{
    /// <summary>
    /// Doubling the prefix character escapes an interpolation: "$$VER" sends the literal text
    /// "$VER" rather than the version string, "^^1" sends "^1" rather than running slot 1. The
    /// "esc" branch is tried first and unconditionally - it does not care what follows, so an
    /// escaped pair in front of a name nobody defined ("$$nosuchalias") collapses the same way
    /// an escaped pair in front of a real one does. Reading left to right, an odd run collapses
    /// pairs and interpolates the leftover single: "$$$VER" is an escaped "$" followed by an
    /// interpolated "$VER" (the reading a shell's "$$" or SQL's "''" trained a user to expect),
    /// not a literal "$$VER".
    ///
    /// The match is NOT collapsed to a literal character here - it is turned into a private
    /// marker instead (see <see cref="EscapedDollarMarker"/>/<see cref="EscapedCaretMarker"/>)
    /// and only <see cref="CollapseEscapes"/> turns the marker back into "$"/"^". This matters
    /// because outgoing text goes through a SECOND interpolation pass after this one -
    /// GameViewModel.ExpandOutgoingCommand runs Watchword's $slotname expansion on this class's
    /// output. If "$$gold" collapsed to "$gold" right here, that second pass would see an
    /// ordinary "$gold" and expand it as a live watchword slot, defeating the escape. Deferring
    /// the collapse until after every pass has run is what keeps the escape honest.
    /// </summary>
    private const char EscapedDollarMarker = (char)1;
    private const char EscapedCaretMarker = (char)2;

    private static readonly Regex AliasRefRegex = new(
        @"(?<esc>\$\$|\^\^)|\$(?<name>\^[1-3]|[A-Za-z][A-Za-z0-9_]*|[?<])|(?<bare>\^[1-3])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Argument slots in a definition body - see <see cref="TryExpandPositional"/>. These
    /// deliberately do NOT appear in <see cref="AliasRefRegex"/>: an alias name must begin with a
    /// letter, so <c>$1</c> has always passed through that pattern untouched, and keeping it that
    /// way means a slot is inert everywhere except the one place that fills it. In particular
    /// <see cref="TryDefine"/> still stores the body with its slots intact rather than trying to
    /// resolve them at definition time, when there are no arguments yet.
    ///
    /// "$$1" escapes a slot the same way "$$VER" escapes the built-in - but that happens in
    /// <see cref="TryDefine"/>, at DEFINITION time, via the escape-aware <see cref="AliasRefRegex"/>
    /// running over the raw body before this regex ever sees it. By the time an alias is invoked
    /// there is no "$$" left in the stored body for this regex to find, so it needs no escape
    /// handling of its own - an escaped slot has already become a literal digit with no leading
    /// "$", and simply does not match <c>\$([1-9])</c> at all.</summary>
    private static readonly Regex PositionalRegex = new(
        @"\$([1-9])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _versionExpansion;
    private readonly Dictionary<string, string> _commands =
        new(StringComparer.OrdinalIgnoreCase);

    public SessionCommandAliases(string version)
        => _versionExpansion = $"Mucka v{version}";

    public bool TryDefine(
        string definition,
        out string name,
        out string command,
        out string? error)
    {
        name = string.Empty;
        command = string.Empty;
        error = null;

        var equals = definition.IndexOf('=');
        if (equals < 0)
            return false;

        name = definition[..equals].Trim();
        command = definition[(equals + 1)..].Trim();

        if (!IsValidName(name))
        {
            error = "name must start with a letter and contain only letters, digits, or underscores";
            return true;
        }

        if (IsReservedClientCommand(name))
        {
            error = $"cannot replace built-in ${name}";
            return true;
        }

        if (command.Length == 0)
        {
            error = "command cannot be empty";
            return true;
        }

        foreach (Match match in AliasRefRegex.Matches(command))
        {
            // An escape match ("$$"/"^^") carries no reference - nothing to validate.
            var reference = ReferenceOf(match);
            if (reference != null && IsReservedClientCommand(reference) && reference != "VER")
            {
                error = $"cannot use built-in ${reference} in a command definition";
                return true;
            }
        }

        command = ExpandReferences(command);

        _commands[name] = command;
        return true;
    }

    public bool TryGet(string name, out string command)
        => _commands.TryGetValue(name, out command!);

    public bool TryGetBuiltInExpansion(string name, out string expansion)
    {
        if (name == "VER")
        {
            expansion = _versionExpansion;
            return true;
        }

        expansion = string.Empty;
        return false;
    }

    public string Expand(string text)
    {
        if (TryExpandPositional(text, out var positional))
            return positional;

        return ExpandReferences(text);
    }

    /// <summary>Shared by <see cref="TryDefine"/> (which snapshots references into a stored body)
    /// and <see cref="Expand"/> (which resolves them in outgoing text): replace every reference
    /// <see cref="AliasRefRegex"/> finds, and turn every escape it finds into the matching
    /// private marker rather than a literal character - see the regex's own doc comment for why
    /// the collapse to a literal has to wait until <see cref="CollapseEscapes"/>.</summary>
    private string ExpandReferences(string text)
        => AliasRefRegex.Replace(text, match =>
        {
            if (match.Groups["esc"].Success)
                return (match.Value[0] == '$' ? EscapedDollarMarker : EscapedCaretMarker).ToString();

            var reference = ReferenceOf(match)!;
            if (reference == "VER")
                return _versionExpansion;

            return _commands.TryGetValue(reference, out var expansion)
                ? expansion
                : match.Value;
        });

    /// <summary>
    /// Turns a "$$"/"^^" escape marker left by <see cref="ExpandReferences"/> back into the
    /// literal "$"/"^" it stands for. Called exactly once, in GameViewModel.ExpandOutgoingCommand,
    /// after EVERY interpolation pass over the outgoing text has run (this class's, then
    /// Watchword's $slotname expansion) - see <see cref="AliasRefRegex"/>'s doc comment for why
    /// collapsing any earlier would let a later pass re-interpret an escaped token.
    /// </summary>
    public static string CollapseEscapes(string text)
        => text.IndexOf(EscapedDollarMarker) < 0 && text.IndexOf(EscapedCaretMarker) < 0
            ? text
            : text.Replace(EscapedDollarMarker, '$').Replace(EscapedCaretMarker, '^');

    /// <summary>
    /// A stored body as the player would type it back: every escape marker shown as the doubled
    /// sigil it came from, so "say $$1 wi $2" echoes as exactly that - the literal slot and the live
    /// one stay distinguishable, and the marker byte never reaches the terminal.
    /// </summary>
    public static string DisplayForm(string stored)
        => stored.IndexOf(EscapedDollarMarker) < 0 && stored.IndexOf(EscapedCaretMarker) < 0
            ? stored
            : stored.Replace(EscapedDollarMarker.ToString(), "$$").Replace(EscapedCaretMarker.ToString(), "^^");

    /// <summary>
    /// The positional form: <c>$k=ql $1,k $1 wi $2</c> then <c>$k rat0 axe</c> sends
    /// <c>ql rat0,k rat0 wi axe</c>.
    ///
    /// <para>Only fires when the line STARTS with the reference and the body actually mentions
    /// <c>$1</c>..<c>$9</c>. Anything else falls through to the ordinary inline replacement, which
    /// leaves the rest of the line alone and therefore keeps appending - so <c>$g=get</c> invoked as
    /// <c>$g sword</c> still sends <c>get sword</c>, as it always has. Two rules, and which one you
    /// get is decided by the body you wrote, not by how you call it.</para>
    ///
    /// <para>A referenced slot with no argument expands to nothing, the way a shell does. That can
    /// produce a malformed command, which is the honest outcome: the player sees what was sent and
    /// why, rather than the alias silently not firing. Arguments past the highest slot the body
    /// mentions are appended, so a body using only <c>$1</c> still behaves like the old form for
    /// anything extra. Slots are single-digit; <c>$10</c> is <c>$1</c> followed by a literal zero.</para>
    ///
    /// <para>Arguments are NOT themselves expanded - they go in verbatim. An argument containing a
    /// <c>$</c> is a creature or object name that happens to contain one, not a nested reference,
    /// and expanding it would make the result depend on what aliases happen to exist.</para>
    /// </summary>
    private bool TryExpandPositional(string text, out string expanded)
    {
        expanded = string.Empty;
        if (text.Length < 2 || text[0] != '$' || !IsAsciiLetter(text[1]))
            return false;

        var end = 1;
        while (end < text.Length && (IsAsciiLetter(text[end]) || char.IsAsciiDigit(text[end]) || text[end] == '_'))
            end++;

        var name = text[1..end];
        if (!_commands.TryGetValue(name, out var body))
            return false;

        var slots = PositionalRegex.Matches(body);
        if (slots.Count == 0)
            return false;

        var args = text[end..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var highest = 0;
        foreach (Match slot in slots)
            highest = Math.Max(highest, slot.Groups[1].Value[0] - '0');

        var filled = PositionalRegex.Replace(body, m =>
        {
            var index = m.Groups[1].Value[0] - '0';
            return index <= args.Length ? args[index - 1] : string.Empty;
        });

        expanded = args.Length > highest
            ? filled + " " + string.Join(' ', args[highest..])
            : filled;
        return true;
    }

    // "name" covers "$name"/"$^n" references; "bare" covers bare "^n" control-macro references.
    // Neither is set for an "esc" match ("$$"/"^^") - it is not a reference at all.
    private static string? ReferenceOf(Match match)
        => match.Groups["name"].Success ? match.Groups["name"].Value
         : match.Groups["bare"].Success ? match.Groups["bare"].Value
         : null;

    public void Clear() => _commands.Clear();

    private static bool IsValidName(string name)
    {
        if (name.Length == 2
            && name[0] == '^'
            && name[1] is >= '1' and <= '3')
            return true;

        if (name.Length == 0 || !IsAsciiLetter(name[0]))
            return false;

        for (var i = 1; i < name.Length; i++)
        {
            var c = name[i];
            if (!IsAsciiLetter(c) && !char.IsAsciiDigit(c) && c != '_')
                return false;
        }

        return true;
    }

    private static bool IsAsciiLetter(char c)
        => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsReservedClientCommand(string name)
    {
        if (name is "help" or "?" or "<" or "con" or "map" or "fkeys" or "VER")
            return true;

        return name.Length >= 2
            && name[0] is 'f' or 'F'
            && int.TryParse(name[1..], out _);
    }
}
