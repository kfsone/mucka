#if DESKTOP
using Mucka.Store;

namespace Mucka.Core;

/// <summary>
/// <c>mucka -import &lt;file&gt;</c>: runs <see cref="DatabaseImport"/> during start-up, behind the
/// already-open window and before the profile page exists, so nothing has opened the database yet.
/// <see cref="StartupSequence"/> calls it off the UI thread; the profile page reports the outcome
/// in an alert. Mucka then starts as normal either way.
///
/// <para>The merge is MAUI-free and lives in <c>Mucka.Store</c>; this is only the start-up hook.</para>
/// </summary>
internal static class StartupImport
{
    /// <summary>The outcome, or null when no import was asked for.</summary>
    internal static DatabaseImportOutcome? RunIfRequested()
    {
        if (CommandLineArgs.Current.ImportPath is not { } path)
            return null;

        try
        {
            return DatabaseImport.Run(MuckaPaths.GetDatabasePath(), Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new DatabaseImportOutcome(false, $"Import failed, and your database was not changed: {ex.Message}");
        }
    }
}
#endif
