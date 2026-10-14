using System.Threading.Channels;
using Microsoft.Maui.Storage;
using Mucka.Commands;

namespace Mucka.Core;

/// <summary>
/// The app's log. Every entry goes to stderr as one line, so Mucka.app/Contents/MacOS/Mucka run from a
/// terminal shows it; exceptions passed to <see cref="Write"/> are also appended to mucka-crash.txt.
/// The stderr write happens on one background task, never on the caller's thread.
/// </summary>
internal static class CrashLog
{
    private static readonly Channel<string> s_lines = Channel.CreateBounded<string>(
        new BoundedChannelOptions(512) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });

    private static int s_started;

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
            if (Interlocked.Exchange(ref s_started, 1) == 0)
                _ = Task.Run(DrainAsync);
            s_lines.Writer.TryWrite(line);
        }
        catch { }
    }

    private static async Task DrainAsync()
    {
        try
        {
            await foreach (var line in s_lines.Reader.ReadAllAsync().ConfigureAwait(false))
                Console.Error.WriteLine(line);
        }
        catch
        {
            // stderr closed or unwritable: stop writing, drop further lines.
            s_lines.Writer.TryComplete();
        }
    }
}
