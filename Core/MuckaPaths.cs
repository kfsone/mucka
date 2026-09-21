using Mucka.Store;

namespace Mucka.Core;

/// <summary>
/// Where the client's data lives. One directory, one database file inside it - see
/// <c>docs/persistence-design.md</c>.
///
/// <para>This is the only file in the persistence path that compiles against
/// <c>FileSystem.AppDataDirectory</c> (Microsoft.Maui.Storage), which a plain xunit project cannot
/// resolve.
/// Everything below it takes a path from its caller and stays MAUI-free, which is what lets the
/// stores be tested against a temp directory and read by an offline tool.</para>
/// </summary>
internal static class MuckaPaths
{
    /// <summary>The data directory: <c>~/.mucka</c> on desktop, and the platform's per-app data
    /// directory on mobile, which has no home-directory concept.
    ///
    /// <para>Not the cache directory, which the clogs and the wire log used to use. A cache
    /// directory is reclaimable: Android deletes it under storage pressure without asking and
    /// without telling the app, and "Clear cache" in the settings app empties it. That was tolerable
    /// for a hand-armed log and is not tolerable for the corpus. This is the same directory
    /// <c>mucka.ini</c> and the crash log already use.</para></summary>
    internal static string GetDataDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mucka");

        return FileSystem.AppDataDirectory;
    }

    /// <summary>The one database: <c>~/.mucka/mucka.db</c>.</summary>
    internal static string GetDatabasePath()
        => Path.Combine(GetDataDirectory(), MuckaDb.DefaultFileName);
}
