using System.Globalization;
using System.Text;

namespace Mucka.WireLog;

/// <summary>
/// A replay file (<c>.muckrep</c>): what the server sent in one session, and when, played back by the
/// client with no socket and no server. See <c>docs/replay.md</c>.
///
/// <para>One record per line - <c>{offset_ms} {rx|an} {payload}</c> - the same line shape as the
/// test fixtures' <c>.c1</c> captures: the payload is the record's own bytes read as Latin-1, with
/// only a backslash, a CR and an LF escaped, because those would split a record. Lines that open
/// with <c>#</c> are header notes. Offsets count from the first record, so a file carries no clock
/// time.</para>
///
/// <para>What a file never holds: anything the client sent. Tx records are dropped whole, so what
/// was typed - the password included - cannot reach a file. What the server echoed back is still
/// there, and <see cref="FromSession"/> replaces the account ID and password in it with
/// <see cref="PlaceholderAccountId"/> and <see cref="PlaceholderPassword"/>.</para>
/// </summary>
public static class Muckrep
{
    /// <summary>The file extension, dot included.</summary>
    public const string Extension = ".muckrep";

    /// <summary>The host prefix that plays a file instead of connecting: <c>replay:name</c>.</summary>
    public const string HostPrefix = "replay:";

    /// <summary>A secret shorter than this is not searched for in what the server sent: it would
    /// match ordinary text. The record that carried it is a Tx record and is dropped anyway.</summary>
    public const int MinScrubLength = 4;

    /// <summary>The account ID a replay shows in place of the real one: the stand-in for a valid
    /// account. Nine characters, as the captured one is, so an echo split across records stays split
    /// where it was.</summary>
    public const string PlaceholderAccountId = "M00010101";

    /// <summary>The password a replay shows in place of the real one: the stand-in for a password
    /// the server accepts.</summary>
    public const string PlaceholderPassword = "passWord";

    /// <summary>The go-to password the server refuses, for anything that wants a login to fail. Not
    /// the only wrong password - the one to reach for first.</summary>
    public const string PlaceholderWrongPassword = "PASSwORD";

    /// <summary>One edit: every case-blind <paramref name="From"/> becomes <paramref name="To"/>.</summary>
    public readonly record struct Substitution(string From, string To);

    /// <summary>True for a host string that names a replay rather than a server.</summary>
    public static bool IsReplayHost(string? host)
        => host is not null && host.Trim().StartsWith(HostPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The replay name in a <c>replay:</c> host, trimmed; empty when there is none.</summary>
    public static string ReplayName(string host)
        => IsReplayHost(host) ? host.Trim()[HostPrefix.Length..].Trim() : string.Empty;

    /// <summary>Every record in a file, in order. Header lines and blank lines are skipped; so is a
    /// line whose offset does not parse or whose direction is not <c>rx</c> or <c>an</c>.</summary>
    public static List<WireRecord> Read(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: false);
        var records = new List<WireRecord>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            // Two parts is a record with an empty payload whose trailing space an editor trimmed.
            var parts = line.Split(' ', 3);
            if (parts.Length < 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
                continue;
            WireDirection? direction = parts[1] switch { "rx" => WireDirection.Rx, "an" => WireDirection.Annotation, _ => null };
            if (direction is null)
                continue;
            records.Add(new WireRecord(offset, direction.Value, parts.Length > 2 ? Unescape(parts[2]) : []));
        }
        return records;
    }

    /// <summary>Writes <paramref name="header"/> as <c>#</c> lines, then the records. Tx records are
    /// skipped, whatever the caller passed.</summary>
    public static void Write(Stream stream, IEnumerable<string> header, IEnumerable<WireRecord> records)
    {
        using var writer = new StreamWriter(stream, Encoding.Latin1, leaveOpen: true) { NewLine = "\n" };
        foreach (var note in header)
            writer.WriteLine("# " + note.Replace('\r', ' ').Replace('\n', ' '));
        foreach (var record in records)
        {
            var tag = record.Direction switch
            {
                WireDirection.Rx => "rx",
                WireDirection.Annotation => "an",
                _ => null,
            };
            if (tag is null)
                continue;
            writer.Write(record.TimestampMs.ToString(CultureInfo.InvariantCulture));
            writer.Write(' ');
            writer.Write(tag);
            writer.Write(' ');
            writer.WriteLine(Escape(record.Payload));
        }
    }

    /// <summary>
    /// A replay of <paramref name="session"/>'s records <paramref name="first"/> to
    /// <paramref name="last"/> (indexes, inclusive - a session's <c>seq</c>): Tx dropped, offsets
    /// counted from the first record kept, the login's account ID and password replaced by
    /// <see cref="PlaceholderAccountId"/> and <see cref="PlaceholderPassword"/> wherever the server
    /// echoed them, and any <paramref name="edits"/> applied.
    ///
    /// <para>The login is found over the WHOLE session (<see cref="FindLoginSecrets"/>), so a slice
    /// that starts after it is still scrubbed; <paramref name="accountId"/> and
    /// <paramref name="password"/> add what the caller knows, such as the profile's own.</para>
    /// </summary>
    public static List<WireRecord> FromSession(IReadOnlyList<WireRecord> session, int first = 0, int last = int.MaxValue,
        string? accountId = null, string? password = null, IEnumerable<Substitution>? edits = null)
    {
        var secrets = FindLoginSecrets(session);
        if (!string.IsNullOrWhiteSpace(accountId))
            secrets.Add(new Substitution(accountId.Trim(), PlaceholderAccountId));
        if (!string.IsNullOrWhiteSpace(password))
            secrets.Add(new Substitution(password.Trim(), PlaceholderPassword));
        var all = secrets.Where(s => s.From.Length >= MinScrubLength).Concat(edits ?? []).ToList();

        last = Math.Min(last, session.Count - 1);
        var kept = new List<WireRecord>();
        for (var i = Math.Max(first, 0); i <= last; i++)
            if (session[i].Direction != WireDirection.Tx)
                kept.Add(session[i]);

        // The server echoes the account ID as it is typed, so one echo can be split across records;
        // and it is repeated in lower case. Each direction is searched as one stream, case-blind.
        foreach (var direction in new[] { WireDirection.Rx, WireDirection.Annotation })
        {
            var at = Enumerable.Range(0, kept.Count).Where(i => kept[i].Direction == direction).ToList();
            var edited = Substitute(at.Select(i => kept[i]).ToList(), all);
            for (var k = 0; k < at.Count; k++)
                kept[at[k]] = edited[k];
        }

        if (kept.Count == 0)
            return kept;
        var origin = kept[0].TimestampMs;
        return kept.Select(r => r with { TimestampMs = Math.Max(0, r.TimestampMs - origin) }).ToList();
    }

    /// <summary>
    /// What the client typed in answer to the server's login prompts, each paired with its
    /// placeholder: a Tx line sent while the server's latest output ended at <c>Account ID:</c> or
    /// <c>Password:</c>. Only a Tx record of printable text ending in CR or LF is a typed line; any
    /// other - a telnet negotiation reply, a window size - is passed over without ending the wait
    /// for the answer. Those are the prompts, as captured on a mudii.co.uk telnet session:
    /// <code>Type QUIT to abort login.\r\nAccount ID: </code> and <code>\r\nPassword: </code>
    /// </summary>
    public static List<Substitution> FindLoginSecrets(IEnumerable<WireRecord> session)
    {
        var secrets = new List<Substitution>();
        var tail = new StringBuilder();
        foreach (var record in session)
        {
            switch (record.Direction)
            {
                case WireDirection.Rx:
                    tail.Append(Encoding.Latin1.GetString(record.Payload));
                    if (tail.Length > 64)
                        tail.Remove(0, tail.Length - 64);
                    break;
                case WireDirection.Tx when IsTypedLine(record.Payload):
                    var prompt = tail.ToString().TrimEnd();
                    var typed = Encoding.Latin1.GetString(record.Payload).Trim();
                    if (typed.Length > 0 && prompt.EndsWith("Account ID:", StringComparison.OrdinalIgnoreCase))
                        secrets.Add(new Substitution(typed, PlaceholderAccountId));
                    else if (typed.Length > 0 && prompt.EndsWith("Password:", StringComparison.OrdinalIgnoreCase))
                        secrets.Add(new Substitution(typed, PlaceholderPassword));
                    tail.Clear();
                    break;
            }
        }
        return secrets;
    }

    private static bool IsTypedLine(byte[] payload)
        => payload.Length > 0 && payload[^1] is 0x0D or 0x0A
           && payload.All(b => b is >= 0x20 and < 0x7F or 0x0D or 0x0A);

    /// <summary>
    /// <paramref name="records"/> with every case-blind occurrence of each edit's
    /// <see cref="Substitution.From"/> replaced by its <see cref="Substitution.To"/>, searching the
    /// records' payloads as one stream so a match may span records. Matches are taken left to right,
    /// the longest at a position first, and never overlap. A match that differs in case from what was
    /// searched for and is all lower case - the server's lower-case repeat of an account ID - gets
    /// its replacement in lower case.
    ///
    /// <para>The replacement is laid over the match byte for byte, so when the two are the same
    /// length every record keeps its length and an echo split across records stays split the same
    /// way. A longer replacement puts its excess in the record holding the match's last byte; a
    /// shorter one leaves the match's trailing bytes out.</para>
    /// </summary>
    public static List<WireRecord> Substitute(IReadOnlyList<WireRecord> records, IReadOnlyList<Substitution> edits)
    {
        var usable = edits.Where(e => e.From.Length > 0)
            .Select(e => (From: Encoding.Latin1.GetBytes(e.From), To: Encoding.Latin1.GetBytes(e.To)))
            .OrderByDescending(e => e.From.Length)
            .ToList();
        var joined = records.SelectMany(r => r.Payload).ToArray();
        var owner = new int[joined.Length];
        for (int r = 0, at = 0; r < records.Count; r++)
            for (var k = 0; k < records[r].Payload.Length; k++)
                owner[at++] = r;

        var output = records.Select(_ => new List<byte>()).ToArray();
        for (var i = 0; i < joined.Length;)
        {
            var edit = usable.FirstOrDefault(e => Matches(joined, i, e.From));
            if (edit.From is null)
            {
                output[owner[i]].Add(joined[i]);
                i++;
                continue;
            }
            var to = edit.To;
            var match = joined.AsSpan(i, edit.From.Length);
            if (!match.SequenceEqual(edit.From) && !match.ContainsAnyInRange((byte)'A', (byte)'Z'))
                to = to.Select(Fold).ToArray();
            var last = edit.From.Length - 1;
            for (var k = 0; k <= last; k++)
            {
                var piece = k < last ? to.Skip(k).Take(1) : to.Skip(last);
                output[owner[i + k]].AddRange(piece);
            }
            i += edit.From.Length;
        }
        return records.Select((r, n) => r with { Payload = output[n].ToArray() }).ToList();
    }

    private static bool Matches(byte[] haystack, int at, byte[] needle)
    {
        if (at + needle.Length > haystack.Length)
            return false;
        for (var k = 0; k < needle.Length; k++)
            if (Fold(haystack[at + k]) != Fold(needle[k]))
                return false;
        return true;
    }

    private static byte Fold(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;

    private static string Escape(byte[] payload)
    {
        var sb = new StringBuilder(payload.Length + 8);
        foreach (var b in payload)
        {
            switch (b)
            {
                case 0x5C: sb.Append(@"\\"); break;
                case 0x0D: sb.Append(@"\r"); break;
                case 0x0A: sb.Append(@"\n"); break;
                default: sb.Append((char)b); break;
            }
        }
        return sb.ToString();
    }

    private static byte[] Unescape(string payload)
    {
        var bytes = new List<byte>(payload.Length);
        for (var i = 0; i < payload.Length; i++)
        {
            if (payload[i] == '\\' && i + 1 < payload.Length && payload[i + 1] is 'r' or 'n' or '\\')
                bytes.Add(payload[++i] switch { 'r' => (byte)0x0D, 'n' => (byte)0x0A, _ => (byte)0x5C });
            else
                bytes.Add((byte)payload[i]);
        }
        return bytes.ToArray();
    }
}
