using Microsoft.Maui.Storage;

namespace Mucka.Core;

internal static class CrashLog
{
    public static void Write(string context, Exception ex)
    {
        try
        {
            var path = Path.Combine(FileSystem.AppDataDirectory, "mucka-crash.txt");
            File.AppendAllText(path, $"{DateTimeOffset.Now:o}  [{context}]{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            System.Diagnostics.Trace.WriteLine($"[Mucka] crash log: {path}");
        }
        catch { }
    }
}
