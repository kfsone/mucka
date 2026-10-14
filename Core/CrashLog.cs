using Microsoft.Maui.Storage;
using Mucka.Commands;

namespace Mucka.Core;

/// <summary>
/// The app's log. Every entry goes to stderr as one line, so Mucka.app/Contents/MacOS/Mucka run from a
/// terminal shows it; exceptions passed to <see cref="Write"/> are also appended to mucka-crash.txt.
/// Wire-log annotations are mirrored to the same stderr sink. The stderr write happens on one
/// background task, never on the caller's thread.
/// </summary>
internal static class CrashLog
{
    private static readonly ConsoleLogSink s_console = new(() => Console.Error);

    public static void Write(string context, Exception ex)
    {
        try
        {
            var path = Path.Combine(FileSystem.AppDataDirectory, "mucka-crash.txt");
            File.AppendAllText(path, $"{DateTimeOffset.Now:o}  [{context}]{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            System.Diagnostics.Trace.WriteLine($"[Mucka] crash log: {path}");
        }
        catch { }
        Emit(LogLevel.Error, context, ex.ToString());
    }

    public static void Info(string category, string message) => Emit(LogLevel.Info, category, message);

    public static void Warn(string category, string message) => Emit(LogLevel.Warn, category, message);

    private static void Emit(LogLevel level, string category, string message)
    {
        try
        {
            var line = LogLine.Format(DateTimeOffset.Now, level, category, message);
            System.Diagnostics.Debug.WriteLine(line);
            s_console.Enqueue(line);
        }
        catch { }
    }
}
