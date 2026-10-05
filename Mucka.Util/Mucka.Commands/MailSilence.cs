using System.Globalization;

namespace Mucka.Commands;

/// <summary>
/// "Ignore for today" for the login mail notice: the day it was ignored and how many items were
/// waiting then. The notice stays silent only while it is still that day AND the shell reports no
/// more items than were ignored, so mail that piles up on top of the ignored mail re-alerts. Mail
/// that replaces it (read, then a new item the same day) cannot be told apart by count and stays
/// silent until tomorrow.
/// </summary>
public sealed record MailSilence(DateOnly Day, int Items)
{
    public bool Silences(int items, DateOnly today) => Day == today && items <= Items;

    /// <summary>"2026-10-04:1", the form kept in mucka.ini.</summary>
    public string Serialize() => string.Create(CultureInfo.InvariantCulture, $"{Day:yyyy-MM-dd}:{Items}");

    /// <summary>Null for anything that is not <see cref="Serialize"/>'s output, so a hand-edited or
    /// stale value means "not silenced" rather than an error.</summary>
    public static MailSilence? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var parts = text.Split(':');
        if (parts.Length != 2
            || !DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var items))
            return null;
        return new MailSilence(day, items);
    }
}
