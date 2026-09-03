namespace Mucka.Terminal;

/// <summary>
/// Pure arithmetic for the WinUI window-resize decisions the Combat Rail panel drives, extracted
/// out of <c>GamePage.xaml.cs</c> (the <c>Mucka</c> project, Windows-only) so it can be exercised by
/// a plain <c>net10.0</c> test project - see <c>Mucka.Terminal.Tests/CombatRailResizeTests.cs</c>.
/// Nothing here touches <c>_hwnd</c>, <c>GetDpiForWindow</c>, or a live <c>AppWindow</c>; every input
/// a caller would otherwise have read from Win32/WinUI is a parameter instead, and every output is a
/// value the caller applies with its own <c>appWindow.Resize(...)</c> call. <c>GamePage.xaml.cs</c>'s
/// own methods (<c>ResizeWindowForCombatPanel</c>, <c>UpdateWindowMinimumWidth</c>,
/// <c>ResizeWindowToFitColumns</c>) are thin callers of this class plus that one side effect.
///
/// <para><b>Units.</b> "Dp" (device-independent pixels, MAUI's unit) round-trips to "Px" (physical
/// pixels, what <c>AppWindow.Size</c> and Win32 actually use) via the DPI passed in - see
/// <see cref="DpToPxCeil"/>/<see cref="DpToPxRound"/>. Storing the applied rail delta in dp rather
/// than px, and reconverting at the CURRENT dpi on every call, is what keeps a toggle correct across
/// a DPI change made while the rail is shown (dragging the window to a different-scaling monitor) -
/// a px amount captured at one DPI is the wrong amount to remove at another.</para>
/// </summary>
public static class CombatRailResize
{
    // -- Layout constants, moved here from GamePage.xaml.cs verbatim ----------------------------
    // Must match SidePanelBorder's WidthRequest in GamePage.xaml - the LEFT panel (Online/Items/
    // Map), unrelated to the combat rail. Deliberately untouched by the combat-rail work: that panel
    // keeps the width the player already plays with.
    public const double SidePanelWidthDp = 228.0;
    // Left gutter the terminal renderer pads text with - must match TerminalView.LeftPadDip.
    public const double TerminalGutterDp = 4.0;
    // Horizontal window chrome (resize borders) not part of the client area; small fudge so the
    // client area still fits DefaultViewColumns after WinUI subtracts the frame.
    public const double WindowChromeDp = 16.0;
    // Default terminal-view width (in characters) used to size the window on first appearance. Two
    // columns wider than the 80-column wrap so the rightmost text isn't flush against the panel.
    public const double DefaultViewColumns = 82.0;

    // Border stroke thickness on CombatPanelBorder (GamePage.xaml) - inset per side (confirmed by
    // decompiling Microsoft.Maui.Controls.dll's Border.CrossPlatformMeasure/Arrange, not assumed),
    // so the canvas/Composition-sibling content area inside is narrower than the panel's own
    // WidthRequest by twice this.
    public const double CombatPanelBorderStrokeDp = 1.0;
    // Content width of the combat rail panel once the Border's stroke has inset it. MUST match
    // Mucka.Rendering.CombatRailView.RailWidth (336f) - that type lives in the Mucka project, which
    // depends on THIS project, so the dependency cannot point the other way and this value is
    // necessarily a hand-kept duplicate. Same risk class as GamePage.xaml's WidthRequest="338"
    // literal duplicating CombatPanelWidthDp below, or SidePanelWidthDp duplicating
    // SidePanelBorder's own WidthRequest - this codebase's established, accepted way of pairing a
    // XAML/cross-project literal with a comment rather than a compile-time link.
    public const double CombatPanelContentWidthDp = 336.0;
    // The outer Border's own WidthRequest (must match GamePage.xaml literally) - the amount of
    // window width the docked panel actually costs when reserved in the Grid.
    public const double CombatPanelWidthDp = CombatPanelContentWidthDp + (CombatPanelBorderStrokeDp * 2.0);

    /// <summary>
    /// Window width (in DIPs) that fits <paramref name="viewColumns"/> terminal columns plus the
    /// renderer's left gutter, the side panel when expanded, and the window frame. Deliberately never
    /// includes the combat rail - see <see cref="CombatPanelWidthDp"/>'s own remarks - so this
    /// function's callers each decide separately whether to reserve the rail on top.
    /// </summary>
    public static double PreferredWindowWidthDp(
        double charWidthDp, bool panelExpanded, double viewColumns = DefaultViewColumns)
        => viewColumns * charWidthDp + TerminalGutterDp
         + (panelExpanded ? SidePanelWidthDp : 0.0) + WindowChromeDp;

    /// <summary>Converts a dp measurement to physical pixels, rounding up - used for floors/minimums,
    /// where under-reserving by a fraction of a pixel is the wrong direction to round.</summary>
    public static int DpToPxCeil(double dp, double dpi) => (int)Math.Ceiling(dp * dpi / 96.0);

    /// <summary>Converts a dp measurement to physical pixels, rounding to nearest - used for the
    /// rail's own reservation, where either direction of rounding error is equally harmless.</summary>
    public static int DpToPxRound(double dp, double dpi) => (int)Math.Round(dp * dpi / 96.0);

    /// <summary>Result of <see cref="ComputeToggle"/>: the window width to resize to, and the rail
    /// delta (in dp) the caller must remember for the next toggle.</summary>
    public readonly record struct ToggleResult(int TargetWidthPx, double NewAppliedDeltaDp);

    /// <summary>
    /// T3/T4: the window resize driven by showing or hiding the combat rail itself
    /// (<c>GamePage.ResizeWindowForCombatPanel</c>).
    ///
    /// <para><b>T3, on show - two regimes, per the user's own ruling.</b> With
    /// <paramref name="maxColumns"/> == 0 ("auto", the default profile value): "In auto columns,
    /// there's no slack. Ergo the window is always the size it needs to be" - every pixel the window
    /// has is already spoken for by columns, so the rail always costs its full reservation
    /// (<see cref="CombatPanelWidthDp"/>), with no slack computation at all. With a FIXED
    /// <paramref name="maxColumns"/>: the baseline the window would occupy WITHOUT the rail is what
    /// <c>maxColumns + 2.0</c> columns need - the same "+2 breathing columns" margin
    /// <c>ResizeWindowToFitColumns</c> and the default-viewColumns caller of
    /// <see cref="PreferredWindowWidthDp"/> already treat as this app's own notion of "the width we'd
    /// pick automatically" for a fixed column count (bare <paramref name="maxColumns"/>, with no
    /// margin, would count that margin as slack and let the rail steal it). Whatever width the window
    /// already carries above that baseline is slack the player created themselves, and the rail is
    /// seated there first: only <c>max(0, neededPx - slackPx)</c> is actually added.</para>
    ///
    /// <para><b>T4, on hide.</b> Subtracts exactly <paramref name="appliedDeltaDp"/> - what the
    /// matching show actually added, reconverted to px at the CURRENT <paramref name="dpi"/> - never
    /// the rail's full width and never a remembered pre-show snapshot, so a manual resize made while
    /// the rail was shown (which lands on top of the applied delta) is preserved exactly.</para>
    ///
    /// <para>Either direction is clamped to the terminal+left-panel floor
    /// (<c>PreferredWindowWidthDp(charWidthDp, panelExpanded, maxColumns)</c>), so the window can
    /// never end up narrower than the currently-configured columns require. The clamp deliberately
    /// forgets whatever width it could not remove (e.g. hiding on an already-floor-pinned window)
    /// rather than trying to claw it back on a later resize.</para>
    /// </summary>
    public static ToggleResult ComputeToggle(
        bool showing,
        int currentWidthPx,
        double dpi,
        int maxColumns,
        double charWidthDp,
        bool panelExpanded,
        double appliedDeltaDp)
    {
        var floorPx = DpToPxCeil(PreferredWindowWidthDp(charWidthDp, panelExpanded, maxColumns), dpi);

        int targetWidth;
        if (showing)
        {
            var neededPx = DpToPxRound(CombatPanelWidthDp, dpi);
            int deltaPx;
            if (maxColumns <= 0)
            {
                deltaPx = neededPx;
            }
            else
            {
                var naturalPx = DpToPxCeil(
                    PreferredWindowWidthDp(charWidthDp, panelExpanded, maxColumns + 2.0), dpi);
                var slackPx = Math.Max(0, currentWidthPx - naturalPx);
                deltaPx = Math.Max(0, neededPx - slackPx);
            }
            targetWidth = currentWidthPx + deltaPx;
        }
        else
        {
            var deltaPx = DpToPxRound(appliedDeltaDp, dpi);
            targetWidth = currentWidthPx - deltaPx;
        }

        if (targetWidth < floorPx)
            targetWidth = floorPx;

        // The remembered delta must match what was ACTUALLY applied to the window, not the raw
        // pre-clamp deltaPx above - if the floor clamp just bit (currentWidthPx was close enough to
        // the floor that adding the raw delta still landed below it), the real amount added is
        // targetWidth - currentWidthPx, which can be less than that raw value. Deriving the delta
        // from the POST-clamp targetWidth here, rather than from deltaPx before it was computed, is
        // what keeps a later hide subtracting exactly what show actually did instead of disagreeing
        // with it. (On hide the remembered delta is always 0 regardless of the clamp - nothing is
        // "applied" by hiding.)
        var newDelta = showing ? (targetWidth - currentWidthPx) * 96.0 / dpi : 0.0;
        return new ToggleResult(targetWidth, newDelta);
    }

    /// <summary>Result of <see cref="ReserveRailWidth"/>.</summary>
    public readonly record struct RailReservation(int TargetWidthPx, double AppliedDeltaDp);

    /// <summary>
    /// Adds the rail's reservation on top of a width some OTHER sizing decision computed without it
    /// (a column-count snap, or the enforced-minimum force-grow) - callers use this only when the
    /// rail is currently shown. <see cref="PreferredWindowWidthDp"/> deliberately never includes the
    /// rail, so a caller that skipped this step would eat the rail's <see cref="CombatPanelWidthDp"/>
    /// straight out of the terminal instead of growing the window for it.
    ///
    /// <para>Also returns the delta the caller must resync <c>_railDeltaAppliedDp</c> to: unlike
    /// <see cref="ComputeToggle"/>, these callers are snap/floor operations, not slack-aware, so once
    /// one of them repositions the window the rail's currently-applied width really is exactly its
    /// full reservation with zero slack - not whatever partial amount a PRIOR toggle's slack
    /// absorption last computed. Leaving that delta stale would make a later hide subtract the wrong
    /// quantity and leave orphaned or missing width behind.</para>
    /// </summary>
    public static RailReservation ReserveRailWidth(int targetWidthPxWithoutRail, double dpi)
        => new(
            targetWidthPxWithoutRail + DpToPxRound(CombatPanelWidthDp, dpi),
            CombatPanelWidthDp);
}
