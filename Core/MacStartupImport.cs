#if MACCATALYST
using Mucka.Store;

namespace Mucka.Core;

/// <summary>
/// <c>-import &lt;file&gt;</c> on a Mac: runs <see cref="DatabaseImport"/> before the app builds,
/// as <c>StartupImport</c> does on Windows, and holds the outcome until a page is on screen, since
/// there is no window yet to put it in. Reported once with <c>DisplayAlertAsync</c>.
/// </summary>
internal static class MacStartupImport
{
    private static DatabaseImportOutcome? _outcome;

    internal static void RunIfRequested()
    {
        if (CommandLineArgs.Current.ImportPath is not { } path)
            return;

        _outcome = DatabaseImport.Run(MuckaPaths.GetDatabasePath(), Path.GetFullPath(path));
    }

    /// <summary>Alerts with the import outcome the first time <paramref name="page"/> appears; does
    /// nothing when no import was requested.</summary>
    internal static void ShowResultWhenShown(Page page)
    {
        if (_outcome is null)
            return;

        void OnAppearing(object? sender, EventArgs e)
        {
            page.Appearing -= OnAppearing;
            if (Interlocked.Exchange(ref _outcome, null) is { } outcome)
                _ = page.DisplayAlertAsync("Mucka import", outcome.Message, "OK");
        }
        page.Appearing += OnAppearing;
    }
}
#endif
