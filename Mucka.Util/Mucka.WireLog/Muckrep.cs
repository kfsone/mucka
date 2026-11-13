using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

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
/// <para>What a file never holds: a Tx record. Those are dropped whole, so what was typed - the
/// password included - cannot reach a file. Annotations are kept; they are the client's own notes,
/// and a few name a command it sent (a dreamword spoken, an alternate weapon wielded). Where the
/// server printed the account ID back, <see cref="FromSession"/> masks it by its context
/// (<see cref="MaskLogin"/>).</para>
/// </summary>
public static class Muckrep
{
    /// <summary>The file extension, dot included.</summary>
    public const string Extension = ".muckrep";

    /// <summary>The host prefix that plays a file instead of connecting: <c>replay:name</c>.</summary>
    public const string HostPrefix = "replay:";

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

    /// <summary>The header every replay the client makes carries.</summary>
    public static readonly IReadOnlyList<string> ScrubbedHeader =
        ["muckrep", "account ID masked; no record of what the client sent"];

    /// <summary>Writes <paramref name="records"/> to a new file at <paramref name="path"/> under
    /// <see cref="ScrubbedHeader"/>. An existing file is never overwritten (<see cref="IOException"/>),
    /// and a file left half-written by a failure is deleted, so a retry is not refused as existing.
    /// </summary>
    public static void WriteNewFile(string path, IEnumerable<WireRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var created = false;
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew);
            created = true;
            Write(file, ScrubbedHeader, records);
        }
        catch when (created)
        {
            try { File.Delete(path); } catch (IOException) { }
            throw;
        }
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
    /// <paramref name="last"/> (indexes, inclusive - a session's <c>seq</c>): the login masked
    /// (<see cref="MaskLogin"/>), Tx dropped, any <paramref name="edits"/> applied, and offsets counted
    /// from the first record kept.
    ///
    /// <para>The login is masked over the WHOLE session before the slice is cut: a context can span
    /// records (the account ID is echoed one keystroke at a time), and a cut inside one would leave it
    /// whole.</para>
    /// </summary>
    public static List<WireRecord> FromSession(IReadOnlyList<WireRecord> session, int first = 0, int last = int.MaxValue,
        IEnumerable<Substitution>? edits = null)
    {
        var masked = MaskLogin(session);
        var substitutions = (edits ?? []).ToList();

        last = Math.Min(last, masked.Count - 1);
        var kept = new List<WireRecord>();
        for (var i = Math.Max(first, 0); i <= last; i++)
            if (masked[i].Direction != WireDirection.Tx)
                kept.Add(masked[i]);

        // Each direction is searched as one stream, case-blind, so an edit may span records.
        if (substitutions.Count > 0)
        {
            foreach (var direction in new[] { WireDirection.Rx, WireDirection.Annotation })
            {
                var at = Enumerable.Range(0, kept.Count).Where(i => kept[i].Direction == direction).ToList();
                var edited = Substitute(at.Select(i => kept[i]).ToList(), substitutions);
                for (var k = 0; k < at.Count; k++)
                    kept[at[k]] = edited[k];
            }
        }

        if (kept.Count == 0)
            return kept;
        var origin = kept[0].TimestampMs;
        return kept.Select(r => r with { TimestampMs = Math.Max(0, r.TimestampMs - origin) }).ToList();
    }

    /// <summary>Where the server prints the player's account ID, found by what surrounds it and never
    /// by its value; group 1 is the ID. The FE frames are from <c>mud2_FE4.txt</c>: code 95's block
    /// carries it in its fourth line, and 95 02 (account change) and 95 03 (account logout) bracket
    /// it. The prose is as captured on mudii.co.uk and mud2.com (<c>docs/replay.md</c>).</summary>
    private static readonly Regex[] AccountContexts =
    [
        // What the player typed at the account prompt, as the server echoed it, to the end of the
        // line - whatever it is, a password typed there by mistake included.
        // Matched anywhere and in any case, as the login handler and AccountBackfill find the prompt:
        // a prompt that does not start a line (after a screen clear, say) is still the prompt.
        new(@"Account ID:(?> *)([^\r\n]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
        // A line in these blocks may end CR NUL LF, as the decoder allows (Mud2C1Decoder.OnC95Data);
        // the bare block may be spelt 95 00.
        new(@"\xFA\x9B?\xFF{1,2}(?:[^\r\n\xFF]*\r?\0?\n){3}([^\r\n\xFF\0]+)\r?\0?\n", RegexOptions.CultureInvariant),
        new(@"\xFA[\x9D\x9E]\xFF{1,2}([^\r\n\xFF\0]+)\r?\0?\n", RegexOptions.CultureInvariant),
        // In prose the ID is held to its shape, a letter then digits, because what comes before it
        // need not end a word: the banner follows the screen-clear ESC [ 2 J with no space.
        new(@"([A-Za-z][0-9]+) logged in on ", RegexOptions.CultureInvariant),
        new(@"([A-Za-z][0-9]+) logged out\.", RegexOptions.CultureInvariant),
        new(@"Account ([A-Za-z][0-9]+) is already logged in", RegexOptions.CultureInvariant),
        new(@"Account: ([A-Za-z][0-9]+)", RegexOptions.CultureInvariant),
        new(@"Last games for ([A-Za-z][0-9]+):", RegexOptions.CultureInvariant),
    ];

    /// <summary>The rest of the line after the password prompt. The server is not seen to echo a
    /// password there; anything it did send is masked.</summary>
    private static readonly Regex PasswordLine =
        new(@"Password:(?> *)([^\r\n]+)", RegexOptions.CultureInvariant);

    /// <summary>
    /// <paramref name="session"/> with the login masked in what the server sent: each account ID found
    /// by its context (<see cref="AccountContexts"/>) becomes <see cref="PlaceholderAccountId"/> - in
    /// lower case where the server printed it in lower case - and anything after the password prompt
    /// on its line becomes asterisks. Rx is searched as one stream, so a context may span records.
    /// Tx and annotations pass through unchanged.
    ///
    /// <para>Masking is by context and never by value. Replacing a known password or ID wherever it
    /// occurs would mark every place it occurs - a password that is also a word the player said shows
    /// itself by being replaced - and a saved password says nothing about a session played before it
    /// was changed.</para>
    /// </summary>
    public static List<WireRecord> MaskLogin(IReadOnlyList<WireRecord> session)
    {
        var rx = Enumerable.Range(0, session.Count).Where(i => session[i].Direction == WireDirection.Rx).ToList();
        var records = rx.Select(i => session[i]).ToList();
        var text = Encoding.Latin1.GetString(records.SelectMany(r => r.Payload).ToArray());

        var spans = new List<(int Start, int Length, byte[] To)>();
        foreach (var context in AccountContexts)
        {
            foreach (Match m in context.Matches(text))
            {
                var id = m.Groups[1];
                var placeholder = id.Value.Any(char.IsAsciiLetterUpper) || !id.Value.Any(char.IsAsciiLetter)
                    ? PlaceholderAccountId
                    : PlaceholderAccountId.ToLowerInvariant();
                spans.Add((id.Index, id.Length, Encoding.Latin1.GetBytes(placeholder)));
            }
        }
        foreach (Match m in PasswordLine.Matches(text))
            spans.Add((m.Groups[1].Index, m.Groups[1].Length, Encoding.Latin1.GetBytes(new string('*', m.Groups[1].Length))));

        var edited = Overlay(records, spans);
        var result = session.ToList();
        for (var k = 0; k < rx.Count; k++)
            result[rx[k]] = edited[k];
        return result;
    }

    /// <summary>
    /// <paramref name="records"/> with every case-blind occurrence of each edit's
    /// <see cref="Substitution.From"/> replaced by its <see cref="Substitution.To"/>, searching the
    /// records' payloads as one stream so a match may span records. Matches are taken left to right,
    /// the longest at a position first, and never overlap. A match that differs in case from what was
    /// searched for and is all lower case gets its replacement in lower case. The replacement is laid
    /// over the match as <see cref="Overlay"/> lays it. These are edits a person chose (<c>--sub</c>);
    /// the login is masked by context instead (<see cref="MaskLogin"/>).
    /// </summary>
    public static List<WireRecord> Substitute(IReadOnlyList<WireRecord> records, IReadOnlyList<Substitution> edits)
    {
        var usable = edits.Where(e => e.From.Length > 0)
            .Select(e => (From: Encoding.Latin1.GetBytes(e.From), To: Encoding.Latin1.GetBytes(e.To)))
            .OrderByDescending(e => e.From.Length)
            .ToList();
        var joined = records.SelectMany(r => r.Payload).ToArray();

        var spans = new List<(int Start, int Length, byte[] To)>();
        for (var i = 0; i < joined.Length;)
        {
            var edit = usable.FirstOrDefault(e => Matches(joined, i, e.From));
            if (edit.From is null)
            {
                i++;
                continue;
            }
            var to = edit.To;
            var match = joined.AsSpan(i, edit.From.Length);
            if (!match.SequenceEqual(edit.From) && !match.ContainsAnyInRange((byte)'A', (byte)'Z'))
                to = to.Select(Fold).ToArray();
            spans.Add((i, edit.From.Length, to));
            i += edit.From.Length;
        }
        return Overlay(records, spans);
    }

    /// <summary>
    /// <paramref name="records"/> with each span of their joined payloads replaced by its bytes, laid
    /// over byte for byte: when a replacement is the length of what it replaces, every record keeps
    /// its length, so an echo split across records stays split the same way. A longer replacement puts
    /// its excess in the record holding the span's last byte; a shorter one leaves the span's trailing
    /// bytes out. Where a span overlaps an earlier one, the earlier one's replacement stands and the
    /// later span's bytes beyond it are left out, so no part of either is kept.
    /// </summary>
    private static List<WireRecord> Overlay(IReadOnlyList<WireRecord> records,
        IEnumerable<(int Start, int Length, byte[] To)> spans)
    {
        var joined = records.SelectMany(r => r.Payload).ToArray();
        var owner = new int[joined.Length];
        for (int r = 0, at = 0; r < records.Count; r++)
            for (var k = 0; k < records[r].Payload.Length; k++)
                owner[at++] = r;

        var output = records.Select(_ => new List<byte>()).ToArray();
        var i = 0;
        foreach (var (start, length, to) in spans.Where(s => s.Length > 0).OrderBy(s => s.Start))
        {
            if (start < i)
            {
                i = Math.Max(i, start + length);
                continue;
            }
            for (; i < start; i++)
                output[owner[i]].Add(joined[i]);
            var last = length - 1;
            for (var k = 0; k <= last; k++)
                output[owner[start + k]].AddRange(k < last ? to.Skip(k).Take(1) : to.Skip(last));
            i = start + length;
        }
        for (; i < joined.Length; i++)
            output[owner[i]].Add(joined[i]);
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
