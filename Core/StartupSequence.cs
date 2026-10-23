using Mucka.Store;
using Mucka.Ui;

namespace Mucka.Core;

/// <summary>
/// What runs between the window opening and the profile page appearing - docs/font-setup-design.md,
/// "The window". Off the UI thread: the <c>-import</c> merge when one was asked for, then the stored
/// font state, which decides the UI step the app runs at and whether the Text size page opens.
/// </summary>
internal static class StartupSequence
{
    /// <summary>The start-up's findings, for the profile page.</summary>
    /// <param name="Import">The import's outcome, null when none was asked for.</param>
    /// <param name="Font">The UI step and Game Font to run at, and to open the Text size page with.</param>
    /// <param name="PromptFontSetup">The Text size page has never been answered.</param>
    internal sealed record Result(DatabaseImportOutcome? Import, FontSetupChoice Font, bool PromptFontSetup);

    internal static Task<Result> RunAsync() => Task.Run(async () =>
    {
        DatabaseImportOutcome? import = null;
#if DESKTOP
        // Its own try, so a failed import still leaves the font state to be read.
        try
        {
            import = StartupImport.RunIfRequested();
        }
        catch (Exception ex)
        {
            CrashLog.Write("StartupImport", ex);
            import = new DatabaseImportOutcome(false, $"Import failed: {ex.Message}");
        }
#endif
        var values = await SettingsStore.GetGlobalValuesAsync(
            UiFontScale.IniKey, "fontsize", FontSetup.DoneKey).ConfigureAwait(false);
        int? step = UiFontScale.TryParse(values[0], out var s) ? s : null;
        int? fontPx = int.TryParse(values[1], System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : null;
#if DESKTOP
        var prompt = FontSetup.ShouldPrompt(values[2]);
#else
        // The page's side-by-side examples are laid out for a desktop window.
        var prompt = false;
#endif
        var font = FontSetup.Initial(step, fontPx, answered: !prompt, isMac: OperatingSystem.IsMacCatalyst());
        return new Result(import, font, prompt);
    });
}
