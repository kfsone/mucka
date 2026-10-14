using System.Globalization;
using System.Text.RegularExpressions;

namespace Mucka.Commands;

/// <summary>Severity shown on a log line.</summary>
public enum LogLevel
{
    Info,
    Warn,
    Error,
}

/// <summary>The text of one log entry as written to stderr: timestamp, level, category, message,
/// always on a single line so a terminal or a pipe reads one entry per line.</summary>
public static partial class LogLine
{
    [GeneratedRegex(@"[\r\n]+")]
    private static partial Regex LineBreaks();

    public static string Format(DateTimeOffset when, LogLevel level, string category, string message)
    {
        var text = LineBreaks().Replace(message ?? string.Empty, " | ").Replace('\0', ' ').Trim();
        var tag  = level switch { LogLevel.Error => "ERROR", LogLevel.Warn => "WARN", _ => "INFO" };
        return string.Create(CultureInfo.InvariantCulture, $"{when:yyyy-MM-ddTHH:mm:ss.fffzzz} {tag} [{category}] {text}");
    }
}
