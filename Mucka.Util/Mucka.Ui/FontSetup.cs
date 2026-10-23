namespace Mucka.Ui;

/// <summary>
/// The Text size page's starting values, and the Game Font's range. Pure, so the rule "err on the
/// side of oversized" can be tested. docs/font-setup-design.md governs.
/// </summary>
public static class FontSetup
{
    /// <summary>Global [settings] key written when the Text size page has been answered, either way.</summary>
    public const string DoneKey = "fontsetup";

    /// <summary>The Game Font's range, in pixels, and its default.</summary>
    public const int MinGameFontPx = 9;
    public const int MaxGameFontPx = 24;
    public const int DefaultGameFontPx = 15;

    /// <summary>The UI step that undoes the Mac's 77% iPad-idiom shrink.</summary>
    public const int MacBaselineStep = 4;

    /// <summary>A fresh install on the Mac starts one step above the baseline: oversized, not small.</summary>
    public const int FreshStepMac = 5;

    /// <summary>A fresh install on Windows starts one step above Normal.</summary>
    public const int FreshStepOther = 1;

    /// <summary>The Mac's fresh Game Font: the default divided by 0.77, rounded up.</summary>
    public const int FreshGameFontMac = 20;

    public static int ClampGameFont(int px) => Math.Clamp(px, MinGameFontPx, MaxGameFontPx);

    /// <summary>The Game Font as the player sees it named: "Default", or its offset from
    /// <see cref="DefaultGameFontPx"/> ("+5", "-2").</summary>
    public static string GameFontName(int px) => UiFontScale.OffsetName(ClampGameFont(px) - DefaultGameFontPx);

    /// <summary>What the app runs at, and the Text size page opens with.</summary>
    /// <param name="storedStep">The stored UI step, null when there is none.</param>
    /// <param name="storedGameFontPx">The stored Game Font, null (or not positive) when there is none.</param>
    /// <param name="answered">Whether the page has been answered before.</param>
    /// <param name="isMac">Running on Mac Catalyst.</param>
    public static FontSetupChoice Initial(int? storedStep, int? storedGameFontPx, bool answered, bool isMac)
    {
        var hasFont = storedGameFontPx is > 0;
        var gameFont = hasFont ? ClampGameFont(storedGameFontPx!.Value)
                     : isMac ? FreshGameFontMac : DefaultGameFontPx;

        if (answered)
            return new FontSetupChoice(UiFontScale.Clamp(storedStep ?? UiFontScale.Normal), gameFont);

        if (storedStep is null && !hasFont)
            return new FontSetupChoice(isMac ? FreshStepMac : FreshStepOther, gameFont);

        var step = UiFontScale.Clamp(storedStep ?? UiFontScale.Normal);
        if (isMac) step = Math.Max(step, MacBaselineStep);
        return new FontSetupChoice(step, gameFont);
    }

    /// <summary>Whether the Text size page should open by itself.</summary>
    public static bool ShouldPrompt(string? doneValue) => doneValue is null;
}

/// <summary>A UI font step and a Game Font size, chosen together.</summary>
public readonly record struct FontSetupChoice(int UiStep, int GameFontPx);
