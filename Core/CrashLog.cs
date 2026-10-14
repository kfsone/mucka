using Microsoft.Maui.Storage;
using Mucka.Commands;

namespace Mucka.Core;

/// <summary>
/// The app's log. Every entry goes to stderr as one line, so Mucka.app/Contents/MacOS/Mucka run from a
/// terminal shows it; exceptions passed to <see cref="Write"/> are also appended to mucka-crash.txt.
/// Wire-log annotations are mirrored to the same stderr sink. The stderr write happens on one
/// background task; <see cref="Write"/> and process exit wait for it for at most
/// <see cref="FlushWait"/>.
/// </summary>
internal static class CrashLog
{
    private static readonly TimeSpan FlushWait = TimeSpan.FromMilliseconds(250);

    private static readonly ConsoleLogSink s_console = new(() => Console.Error);

    static CrashLog()
    {
        try
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => s_console.Complete(FlushWait);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Emit(LogLevel.Error, "UnhandledException", e.ExceptionObject.ToString() ?? string.Empty);
                s_console.Complete(FlushWait);
            };
        }
        catch { }
    }

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
        s_console.Flush(FlushWait);
    }

    public static void Info(string category, string message) => Emit(LogLevel.Info, category, message);

    public static void Warn(string category, string message) => Emit(LogLevel.Warn, category, message);

    private static void Emit(LogLevel level, string category, string message)
    {
        try { s_console.Enqueue(LogLine.Format(DateTimeOffset.Now, level, category, message)); }
        catch { }
    }
}
