using System.Globalization;
using System.Text;

namespace Mucka.Commands;

/// <summary>
/// Writes a password as an ini value and reads it back. The ini reader trims a value and cuts it
/// at a whitespace-then-';' or '#', and a line cannot hold a line break, so those characters are
/// written as %XX (UTF-8 bytes, upper-case hex) and '%' itself as %25. Every other character,
/// including a '#' or ';' that is not preceded by whitespace, is written as typed.
/// </summary>
public static class IniPasswordCodec
{
    public static string Encode(string password)
    {
        var sb = new StringBuilder(password.Length);
        for (var i = 0; i < password.Length; i++)
        {
            var c = password[i];
            var edge = i == 0 || i == password.Length - 1;
            var escape = c is '%' or ';' or '#' || char.IsControl(c) || (char.IsWhiteSpace(c) && (edge || c != ' '));
            if (!escape)
            {
                sb.Append(c);
                continue;
            }
            // A surrogate cannot be reached here: neither IsControl nor IsWhiteSpace is true for one.
            foreach (var b in Encoding.UTF8.GetBytes(c.ToString()))
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public static string Decode(string value)
    {
        if (value.IndexOf('%', StringComparison.Ordinal) < 0)
            return value;
        var bytes = new List<byte>(value.Length);
        var buf = new byte[4];
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length
                && byte.TryParse(value.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var b))
            {
                bytes.Add(b);
                i += 2;
                continue;
            }
            var len = char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]) ? 2 : 1;
            var n = Encoding.UTF8.GetBytes(value.AsSpan(i, len), buf);
            for (var k = 0; k < n; k++) bytes.Add(buf[k]);
            i += len - 1;
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}

/// <summary>
/// Moves saved passwords from one store to another. A password is written to the destination
/// before it is removed from the source, and a password that could not be read or written stays
/// where it was.
/// </summary>
public static class PasswordMigration
{
    public sealed record Result(int Moved, int Skipped, int Failed);

    public static async Task<Result> MoveAsync(
        IEnumerable<string> profileNames,
        Func<string, Task<string?>> read,
        Func<string, string, Task> write,
        Func<string, Task> removeFromSource)
    {
        int moved = 0, skipped = 0, failed = 0;
        foreach (var name in profileNames)
        {
            string? password;
            try { password = await read(name).ConfigureAwait(false); }
            catch { failed++; continue; }
            if (string.IsNullOrEmpty(password)) { skipped++; continue; }

            try { await write(name, password).ConfigureAwait(false); }
            catch { failed++; continue; }

            // The copy is safe in the destination; a source that refuses removal is left in place.
            try { await removeFromSource(name).ConfigureAwait(false); }
            catch { failed++; continue; }
            moved++;
        }
        return new Result(moved, skipped, failed);
    }
}
