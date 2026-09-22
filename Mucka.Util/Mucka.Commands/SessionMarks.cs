using System.Text.RegularExpressions;

namespace Mucka.Commands;

/// <summary>Which kind of line a <see cref="SessionMarks"/> call produced. Errors are coloured as
/// errors; the rest are annotations that reach the capture as well as the terminal.</summary>
public enum MarkKind
{
    /// <summary>A standalone point in time - "//MARK: ".</summary>
    Mark,
    /// <summary>The opening of a span - "//BEG: ".</summary>
    Begin,
    /// <summary>The closing of a span - "//END: ".</summary>
    End,
    /// <summary>Nothing was marked; the text says why.</summary>
    Error,
}

/// <summary>One line to print and record, already formatted.</summary>
public sealed record MarkResult(MarkKind Kind, string Text);

/// <summary>
/// <c>$MARK</c> - the operator's own landmarks in the session log, so a question asked later ("what
/// was I doing when the rail went wrong?") can be answered by looking rather than by remembering.
///
/// <para><b>Three forms.</b> Bare <c>$MARK</c> is a point in time. <c>$MARK start</c> opens a span
/// and <c>$MARK end</c> closes it, and spans nest - the stack is why <c>end</c> needs no argument in
/// the ordinary case.</para>
///
/// <para><b>Every id carries the persona session it was made in</b>, because that is the only
/// identifier that makes a mark findable afterwards: the rows a mark is about are attributed to
/// <c>persona_sessions.id</c>, and a bare word the operator typed twice in two logins is not an
/// identifier at all. A supplied id is promoted to <c>mm-{persona session}:{id}</c> and a generated
/// one to <c>mm-{persona session}-{generated}</c> - a COLON for what the operator chose, a HYPHEN
/// for what this class chose, so the two are distinguishable on sight in the log.</para>
///
/// <para><b>Promotion is not idempotent and deliberately so.</b> <c>$MARK "mm-61:my brand"</c>
/// becomes <c>mm-61:mm-61:my brand</c>. Stripping a prefix that looks like one already would make
/// the id depend on guessing whether the operator meant it as text, and a mark whose id changes
/// meaning between two readings is worse than an ugly one.</para>
///
/// <para><b>A persona session is required for all three forms</b>, not just for <c>start</c>. There
/// is no id without one, so a mark made at the shell could not be found again - which is the whole
/// point of making it.</para>
/// </summary>
public sealed class SessionMarks
{
    /// <summary>How deep spans may nest. Eight is past any nesting a person keeps track of by
    /// memory; beyond it the stack has stopped describing what the operator is doing and is
    /// recording that they forgot to close something.</summary>
    public const int MaxDepth = 8;

    /// <summary>Longest quoted note accepted as an id. A note is a label, not a sentence; past this
    /// it stops being readable in a log line.</summary>
    public const int MaxNoteLength = 64;

    /// <summary>A supplied <c>@id</c>: opens on a letter or digit, then anything that survives being
    /// pasted into a filename or a query.</summary>
    private static readonly Regex AtId = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.Compiled);

    /// <param name="Id">The promoted id, as it is printed.</param>
    /// <param name="Display">How the operator referred to it, for the mismatch message - "@1234",
    /// a quoted note, or the id itself when nothing was supplied.</param>
    /// <param name="Generated">True when this class chose the id, so no argument the operator can
    /// type will match it and the mismatch message says so.</param>
    private sealed record OpenMark(string Id, string Display, bool Generated);

    private readonly List<OpenMark> _open = [];

    /// <summary>How many spans are open.</summary>
    public int Depth => _open.Count;

    /// <summary>A generated id: short, opaque, and unique enough within one login.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Run one <c>$MARK</c> invocation.
    /// </summary>
    /// <param name="argument">Everything after <c>$MARK</c>.</param>
    /// <param name="personaSessionId">The open login, or null at the shell.</param>
    /// <param name="newId">Supplies a generated id - injected so a test can name one.</param>
    public MarkResult Apply(string argument, long? personaSessionId, Func<string> newId)
    {
        var rest = (argument ?? string.Empty).Trim();
        var verb = TakeVerb(ref rest);

        if (personaSessionId is not long session)
            return Error("$MARK needs an open persona session - you are not in game mode.");

        if (!TryTakeId(rest, session, out var id, out var display, out var idError))
            return Error(idError);

        var supplied = id is not null;
        id ??= $"mm-{session}-{newId()}";
        display ??= id;

        switch (verb)
        {
            case Verb.None:
                return new MarkResult(MarkKind.Mark, $"//MARK: {id}");

            case Verb.Start:
                if (_open.Count >= MaxDepth)
                    return Error($"Too many open marks (maximum {MaxDepth}).");
                _open.Add(new OpenMark(id, display, !supplied));
                return new MarkResult(MarkKind.Begin, $"//BEG: {id}");

            default:
                if (_open.Count == 0)
                    return Error("No open marks to close.");

                var top = _open[^1];
                // No argument closes the innermost span - the ordinary case, and the only way to
                // close one this class named.
                if (!supplied)
                {
                    _open.RemoveAt(_open.Count - 1);
                    return new MarkResult(MarkKind.End, $"//END: {top.Id}");
                }

                if (!string.Equals(id, top.Id, StringComparison.Ordinal))
                    return Error($"{id} is not the current mark ({top.Display})"
                        + (top.Generated ? ", use `$MARK end`." : string.Empty));

                _open.RemoveAt(_open.Count - 1);
                return new MarkResult(MarkKind.End, $"//END: {top.Id}");
        }
    }

    /// <summary>
    /// Close every open span, innermost first, because the login ended under them.
    ///
    /// <para>Ended rather than discarded: a span with no end is indistinguishable in the log from
    /// one still running, and the reason the login finished is the most useful thing that could be
    /// said about why it never closed properly.</para>
    /// </summary>
    /// <param name="reason">The login's end note, or null when nothing classified it.</param>
    public IReadOnlyList<MarkResult> CloseAll(string? reason)
    {
        var why = string.IsNullOrWhiteSpace(reason) ? "session ended" : reason;
        var closed = new List<MarkResult>(_open.Count);
        for (var i = _open.Count - 1; i >= 0; i--)
            closed.Add(new MarkResult(MarkKind.End, $"//END: {_open[i].Id} ({why})"));
        _open.Clear();
        return closed;
    }

    private enum Verb { None, Start, End }

    /// <summary>Strip a leading <c>start</c>/<c>end</c> off <paramref name="rest"/>. Matched as a
    /// whole word so a note beginning "start" is not mistaken for the verb.</summary>
    private static Verb TakeVerb(ref string rest)
    {
        if (TakeWord(ref rest, "start")) return Verb.Start;
        if (TakeWord(ref rest, "end")) return Verb.End;
        return Verb.None;
    }

    private static bool TakeWord(ref string rest, string word)
    {
        if (!rest.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            return false;
        if (rest.Length != word.Length && !char.IsWhiteSpace(rest[word.Length]))
            return false;
        rest = rest[word.Length..].Trim();
        return true;
    }

    /// <summary>
    /// Read the optional id. Returns false only when what was typed cannot be an id at all; an
    /// absent one is success with <paramref name="id"/> null, which the caller reads as "generate".
    /// </summary>
    private static bool TryTakeId(
        string rest, long session, out string? id, out string? display, out string error)
    {
        id = null;
        display = null;
        error = string.Empty;

        if (rest.Length == 0)
            return true;

        if (rest[0] == '@')
        {
            var raw = rest[1..];
            if (!AtId.IsMatch(raw))
            {
                error = $"Invalid mark id \"{raw}\" - start with a letter or digit, "
                    + "then letters, digits, period, underscore or hyphen.";
                return false;
            }
            id = $"mm-{session}:{raw}";
            display = "@" + raw;
            return true;
        }

        if (rest[0] == '"')
        {
            if (rest.Length < 2 || rest[^1] != '"')
            {
                error = "Unterminated mark note - close it with a quote.";
                return false;
            }
            var note = rest[1..^1];
            if (note.Length == 0)
            {
                error = "Mark note is empty.";
                return false;
            }
            if (note.Length > MaxNoteLength)
            {
                error = $"Mark note is {note.Length} characters, maximum is {MaxNoteLength}.";
                return false;
            }
            id = $"mm-{session}:{note}";
            display = "\"" + note + "\"";
            return true;
        }

        error = $"Expected @id or a \"quoted note\", got: {rest}";
        return false;
    }

    private static MarkResult Error(string text) => new(MarkKind.Error, "ERROR: " + text);
}
