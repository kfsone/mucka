namespace Mucka.Ui;

/// <summary>
/// How the profile page's square logo sits above its list, from the list pane's size. The logo shows
/// whole when the pane has room for it and the list; short of that the list overlaps up to the logo's
/// bottom half; short of that the logo is left out and the header carries a small one.
/// </summary>
public static class ProfileLogoFit
{
    /// <summary>What the list needs under the logo at the default step: the header row, two and a half
    /// profile cards (the half shows there is more to scroll to) and the score history button, at the
    /// authored dimensions.</summary>
    public const double ListNeedDp = 410.0;

    /// <summary>The same at the Windows dimensions, which are about 77% of the authored ones.</summary>
    public const double DenseListNeedDp = 350.0;

    /// <summary>The share of the logo's height the list may cover.</summary>
    public const double MaxOverlapShare = 1.0 / 2.0;

    /// <param name="Show">The large logo is drawn.</param>
    /// <param name="LogoHeight">The logo's height; it is square, so the pane's width.</param>
    /// <param name="Overlap">How much of the logo's bottom the list covers.</param>
    public readonly record struct Fit(bool Show, double LogoHeight, double Overlap)
    {
        /// <summary>The height the logo takes from the list: the rest is under it.</summary>
        public double SpaceTaken => Show ? LogoHeight - Overlap : 0;
    }

    /// <param name="listNeedDp">What the list needs at the default step: <see cref="ListNeedDp"/> or
    /// <see cref="DenseListNeedDp"/>.</param>
    public static Fit Compute(double paneWidth, double paneHeight, int uiStep, double listNeedDp)
    {
        if (paneWidth <= 0 || paneHeight <= 0)
            return new Fit(false, 0, 0);
        var spare = paneHeight - listNeedDp * UiFontScale.PanelScale(uiStep);
        var overlap = Math.Clamp(paneWidth - spare, 0, paneWidth * MaxOverlapShare);
        return paneWidth - overlap <= spare
            ? new Fit(true, paneWidth, overlap)
            : new Fit(false, 0, 0);
    }
}
