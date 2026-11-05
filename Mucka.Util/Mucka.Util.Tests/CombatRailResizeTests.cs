using Mucka.Combat;

namespace Mucka.Util.Tests;

/// <summary>
/// Tests for <see cref="CombatRailResize"/> - the Combat Rail's window-resize arithmetic (T1/T3/T4),
/// extracted from GamePage.xaml.cs so it can be exercised here without a live WinUI window.
///
/// DPI is passed as a plain value everywhere (96 == 100%, 120 == 125%, 144 == 150%) - see
/// <see cref="CombatRailResize.DpToPxCeil"/>/<see cref="CombatRailResize.DpToPxRound"/> for the
/// dp-to-px formula this mirrors.
/// </summary>
public class CombatRailResizeTests
{
    private const double Dpi100 = 96.0;

    // The least window width the layout allows with the left panel expanded: gutter 4 + panel 228 +
    // chrome 16.
    private const int ExpandedFloorPx = 252;

    // The production methods take the rail's width as a REQUIRED parameter - no default - because a
    // defaulted one is how three call sites silently kept the wide width after the Stats toggle made
    // it variable. These wrappers put the wide value back for the cases below that are about
    // something else; the narrow-width cases at the end call the real methods directly.
    private static CombatRailResize.ToggleResult ComputeToggle(
        bool showing, int currentWidthPx, double dpi, bool panelExpanded, double appliedDeltaDp,
        double sidePanelScale = 1.0)
        => CombatRailResize.ComputeToggle(showing, currentWidthPx, dpi, panelExpanded, appliedDeltaDp,
            CombatRailResize.CombatPanelWidthDp, sidePanelScale);

    private static double SeedAppliedDeltaDp(int currentWidthPx, double dpi, bool panelExpanded)
        => CombatRailResize.SeedAppliedDeltaDp(currentWidthPx, dpi, panelExpanded,
            CombatRailResize.CombatPanelWidthDp);

    private static CombatRailResize.InitialSize ComputeInitialWidth(
        int minWidthPx, double dpi, double charWidthDp, bool panelExpanded, bool railShown)
        => CombatRailResize.ComputeInitialWidth(minWidthPx, dpi, charWidthDp,
            panelExpanded, railShown, CombatRailResize.CombatPanelWidthDp);

    private static int MinTrackWidthPx(int floorPx, double dpi, bool railShown)
        => CombatRailResize.MinTrackWidthPx(floorPx, dpi, railShown, CombatRailResize.CombatPanelWidthDp);

    private static CombatRailResize.RailReservation ReserveRailWidth(int targetWidthPxWithoutRail, double dpi)
        => CombatRailResize.ReserveRailWidth(targetWidthPxWithoutRail, dpi,
            CombatRailResize.CombatPanelWidthDp);

    // -- The floor ---------------------------------------------------------------------------------

    [Fact]
    public void Floor_IsGutterPanelAndChrome_WithNoColumnsReserved()
    {
        Assert.Equal(252.0, CombatRailResize.FloorWindowWidthDp(panelExpanded: true));
        Assert.Equal(24.0, CombatRailResize.FloorWindowWidthDp(panelExpanded: false));
    }

    [Fact]
    public void Floor_WidensBySidePanelScale_OnlyWhenThePanelIsShown()
    {
        Assert.Equal(252.0 + CombatRailResize.SidePanelWidthDp * 0.25,
            CombatRailResize.FloorWindowWidthDp(panelExpanded: true, sidePanelScale: 1.25), 6);
        Assert.Equal(24.0, CombatRailResize.FloorWindowWidthDp(panelExpanded: false, sidePanelScale: 1.25));
    }

    // -- Acceptance-criteria examples, at the real CombatPanelWidthDp (386, not the illustrative 336 -
    //    see CombatRailResize's own remarks on why the two differ by the Border's 2dp stroke inset) ---

    [Fact]
    public void Show_GrowsByFullRailWidth()
    {
        // "There's no slack" - the window always grows by the rail's full reservation, regardless of
        // its current width.
        var result = ComputeToggle(
            showing: true, currentWidthPx: 800, dpi: Dpi100, panelExpanded: true, appliedDeltaDp: 0.0);

        Assert.Equal(1186, result.TargetWidthPx);
        Assert.Equal(386.0, result.NewAppliedDeltaDp);
    }

    [Fact]
    public void Hide_AfterManualResize_PreservesTheManualAddition()
    {
        // Continuing from the show above (delta 386): the player manually widens the window by 100
        // (1186 -> 1286) while the rail is still up, THEN hides it. Hiding must remove only the
        // rail's own 386, not the player's +100.
        var result = ComputeToggle(
            showing: false, currentWidthPx: 1286, dpi: Dpi100, panelExpanded: true, appliedDeltaDp: 386.0);

        Assert.Equal(900, result.TargetWidthPx);
        Assert.Equal(0.0, result.NewAppliedDeltaDp);
    }

    [Fact]
    public void Hide_WithNoManualResize_RestoresExactPreShowWidth()
    {
        var result = ComputeToggle(
            showing: false, currentWidthPx: 1186, dpi: Dpi100, panelExpanded: true, appliedDeltaDp: 386.0);

        Assert.Equal(800, result.TargetWidthPx);
        Assert.Equal(0.0, result.NewAppliedDeltaDp);
    }

    [Fact]
    public void Seed_AfterRelog_AdoptsTheRailWidthTheWindowAlreadyCarries()
    {
        // Show the rail, quit to the persona picker, log back in, hide the rail - and the window
        // never shrinks. A relog builds a NEW GamePage while the OS window keeps the width the old
        // one gave it, so the incoming page's applied-delta starts at zero and the first hide
        // subtracts nothing.
        //
        // The window arrives at 1186, 938 above the floor: the seed claims the rail's 386 and
        // hiding gives it back.
        var seeded = SeedAppliedDeltaDp(currentWidthPx: 1186, dpi: Dpi100, panelExpanded: true);

        Assert.Equal(386.0, seeded);

        var hidden = ComputeToggle(
            showing: false, currentWidthPx: 1186, dpi: Dpi100, panelExpanded: true, appliedDeltaDp: seeded);

        Assert.Equal(800, hidden.TargetWidthPx);
        Assert.Equal(0.0, hidden.NewAppliedDeltaDp);
    }

    [Fact]
    public void Seed_OnAWindowAtTheFloor_ClaimsNothing()
    {
        // Nothing above the floor can be the rail's, so there is nothing to claim.
        var seeded = SeedAppliedDeltaDp(currentWidthPx: ExpandedFloorPx, dpi: Dpi100, panelExpanded: true);

        Assert.Equal(0.0, seeded);
    }

    [Fact]
    public void Seed_ClaimsOnlyTheSlackAboveTheFloor_WhenThatIsLessThanTheRail()
    {
        var seeded = SeedAppliedDeltaDp(currentWidthPx: ExpandedFloorPx + 100, dpi: Dpi100, panelExpanded: true);

        Assert.Equal(100.0, seeded);
    }

    [Fact]
    public void Seed_NeverClaimsMoreThanTheRailIsWorth()
    {
        // A window the player has dragged far wider than the rail could account for: the seed takes
        // the rail's own width and no more, so hiding gives their sizing back rather than eating it.
        var width = ExpandedFloorPx + 386 + 400;
        var seeded = SeedAppliedDeltaDp(currentWidthPx: width, dpi: Dpi100, panelExpanded: true);

        Assert.Equal(386.0, seeded);

        var hidden = ComputeToggle(
            showing: false, currentWidthPx: width, dpi: Dpi100, panelExpanded: true, appliedDeltaDp: seeded);

        Assert.Equal(ExpandedFloorPx + 400, hidden.TargetWidthPx);
    }

    // -- The floor clamp: the window may never end up narrower than the left-panel floor ----------------

    [Fact]
    public void Show_NeverEndsUpNarrowerThanTheFloor()
    {
        // A side panel scaled to twice its width puts the floor at 4 + 4 + 456 + 16 = 480, above the
        // rail's own 386: a near-zero starting width cannot end up below the floor even after adding
        // the rail.
        var result = ComputeToggle(
            showing: true, currentWidthPx: 0, dpi: Dpi100, panelExpanded: true, appliedDeltaDp: 0.0,
            sidePanelScale: 2.0);

        Assert.Equal(480, result.TargetWidthPx);
        // The remembered delta must match what was ACTUALLY applied (480 - 0), not the raw
        // pre-clamp 386 the rail costs.
        Assert.Equal(480.0, result.NewAppliedDeltaDp);
    }

    [Fact]
    public void Show_WhenFloorClampBites_DeltaMatchesWhatWasActuallyApplied_AndHideRoundTripsCorrectly()
    {
        // A window starting at 0 is narrower than even the un-railed floor - impossible via GamePage's
        // own UpdateWindowMinimumWidth in practice (it continuously enforces that floor), but
        // ComputeToggle is a pure function and has to stay correct regardless of caller discipline.
        var shown = ComputeToggle(
            showing: true, currentWidthPx: 0, dpi: Dpi100, panelExpanded: true, appliedDeltaDp: 0.0,
            sidePanelScale: 2.0);

        Assert.Equal(480, shown.TargetWidthPx);
        Assert.Equal(480.0, shown.NewAppliedDeltaDp);

        // Hiding immediately, using the resynced delta, must not overshoot below the floor either -
        // the window lands back at the floor, which is the honest answer: the pre-show 0 was never a
        // legal width to begin with. The delta driving this arithmetic has to be the real
        // (post-clamp) one, not the raw pre-clamp value.
        var hidden = ComputeToggle(
            showing: false, currentWidthPx: shown.TargetWidthPx, dpi: Dpi100, panelExpanded: true,
            appliedDeltaDp: shown.NewAppliedDeltaDp, sidePanelScale: 2.0);

        Assert.Equal(480, hidden.TargetWidthPx);
        Assert.Equal(0.0, hidden.NewAppliedDeltaDp);
    }

    [Fact]
    public void Hide_NeverEndsUpNarrowerThanTheFloor()
    {
        // Subtracting the full stored delta from a window already near the floor would go below it -
        // the clamp pins it at the floor instead, deliberately forgetting the width it could not
        // remove rather than trying to claw it back on a later resize.
        var result = ComputeToggle(
            showing: false, currentWidthPx: 300, dpi: Dpi100, panelExpanded: true, appliedDeltaDp: 386.0);

        Assert.Equal(ExpandedFloorPx, result.TargetWidthPx);
        Assert.Equal(0.0, result.NewAppliedDeltaDp);
    }

    // -- Non-100% DPI: the dp/px round-trip, and a DPI change made WHILE the rail is shown --

    [Fact]
    public void DpToPxRound_ScalesWithDpi_150Percent()
    {
        // 386dp at 150% (dpi 144): 386 * 144 / 96 = 579 exactly - no rounding ambiguity.
        Assert.Equal(579, CombatRailResize.DpToPxRound(CombatRailResize.CombatPanelWidthDp, 144.0));
    }

    [Fact]
    public void DpToPxRound_ScalesWithDpi_125Percent_BankersRounding()
    {
        // 386dp at 125% (dpi 120): 386 * 120 / 96 = 482.5 exactly - a genuine rounding midpoint.
        // Math.Round with no MidpointRounding argument defaults to "round half to even", so this
        // resolves to 482 (the nearest even integer), not 483. Asserted explicitly so a reader does
        // not have to guess which way DpToPxRound resolves a tie.
        Assert.Equal(482, CombatRailResize.DpToPxRound(CombatRailResize.CombatPanelWidthDp, 120.0));
    }

    [Fact]
    public void Show_At150Percent_ScalesTheFullRailWidth()
    {
        // At 150% DPI the rail's own 386dp becomes 579px, not 386px.
        var result = ComputeToggle(
            showing: true, currentWidthPx: 1200, dpi: 144.0, panelExpanded: true, appliedDeltaDp: 0.0);

        Assert.Equal(1200 + 579, result.TargetWidthPx);
        Assert.Equal(386.0, result.NewAppliedDeltaDp);
    }

    [Fact]
    public void Hide_AfterDpiChangedWhileShown_ReconvertsAtTheNewDpi_NotTheStaleOne()
    {
        // Show at 100% DPI (currentWidthPx 800): delta stored as 386dp, window grows to 1186px.
        var shown = ComputeToggle(
            showing: true, currentWidthPx: 800, dpi: Dpi100, panelExpanded: true, appliedDeltaDp: 0.0);
        Assert.Equal(1186, shown.TargetWidthPx);
        Assert.Equal(386.0, shown.NewAppliedDeltaDp);

        // The window is then dragged to a 150%-scaled monitor. WinUI rescales the whole window's
        // physical size with it (1186 * 1.5 = 1779) - this test does not re-derive that rescale, it
        // simply asserts what CombatRailResize does with the new state: hiding must reconvert the
        // STORED 386dp at the CURRENT 150% dpi (386 * 144 / 96 = 579), and not leave the window's
        // DPI-rescaled extra width behind as orphaned space.
        var hidden = ComputeToggle(
            showing: false, currentWidthPx: 1779, dpi: 144.0, panelExpanded: true,
            appliedDeltaDp: shown.NewAppliedDeltaDp);

        // 800dp's own equivalent at 150% is 1200px (800 * 1.5) - exactly what a correct reconversion
        // gives back: 1779 - 579 = 1200.
        Assert.Equal(1200, hidden.TargetWidthPx);
        Assert.Equal(0.0, hidden.NewAppliedDeltaDp);
    }

    // -- The enforced-minimum force-grow must not eat the rail's width out of the terminal, and the
    //    resynced delta must make a later hide exact ------------------------------------------------

    [Fact]
    public void ReserveRailWidth_AddsExactlyTheRailsFullReservation()
    {
        var reserved = ReserveRailWidth(targetWidthPxWithoutRail: 824, dpi: Dpi100);

        Assert.Equal(824 + 386, reserved.TargetWidthPx);
        Assert.Equal(386.0, reserved.AppliedDeltaDp);
    }

    // -- T0: the first sizing of a run, which happens before any toggle has run and must already
    //    account for a profile that starts with the rail shown --------------------------------

    [Fact]
    public void InitialWidth_UsesTheLaunchDefaultBasis()
    {
        // DefaultViewColumns (82): 82*8 + 4 gutter + 4 scrollbar + 228 panel + 16 chrome = 908.
        var initial = ComputeInitialWidth(
            minWidthPx: 0, dpi: Dpi100, charWidthDp: 8.0, panelExpanded: true, railShown: false);

        Assert.Equal(908, initial.TargetWidthPx);
        Assert.Equal(0.0, initial.AppliedDeltaDp);
    }

    [Fact]
    public void InitialWidth_RailShownFromTheProfile_ReservesItOnTopOfTheColumns()
    {
        // The reported defect: a profile with the rail already shown was sized to the terminal alone,
        // so the rail's 386 came out of the columns and the text was cut off. The rail must be added
        // on top, and the delta recorded in full - this width was computed with no slack, so a later
        // hide gives back exactly the rail.
        var initial = ComputeInitialWidth(
            minWidthPx: 0, dpi: Dpi100, charWidthDp: 8.0, panelExpanded: true, railShown: true);

        Assert.Equal(908 + 386, initial.TargetWidthPx);
        Assert.Equal(386.0, initial.AppliedDeltaDp);

        // The terminal keeps all 82 columns rather than paying for the rail out of them.
        var terminalColumnsWidth = initial.TargetWidthPx
            - CombatRailResize.DpToPxRound(CombatRailResize.CombatPanelWidthDp, Dpi100)
            - (int)CombatRailResize.SidePanelWidthDp
            - (int)CombatRailResize.TerminalGutterDp
            - (int)CombatRailResize.TerminalScrollbarDp
            - (int)CombatRailResize.WindowChromeDp;
        Assert.Equal((int)(CombatRailResize.DefaultViewColumns * 8.0), terminalColumnsWidth);
    }

    [Fact]
    public void InitialWidth_ThatFirstDelta_MakesTheFirstHideExact()
    {
        // The delta T0 records has to be the one T4 subtracts: showing from the profile and then
        // hiding must land back on the railless width, with nothing orphaned.
        var initial = ComputeInitialWidth(
            minWidthPx: 0, dpi: Dpi100, charWidthDp: 8.0, panelExpanded: true, railShown: true);

        var hidden = ComputeToggle(
            showing: false, currentWidthPx: initial.TargetWidthPx, dpi: Dpi100, panelExpanded: true,
            appliedDeltaDp: initial.AppliedDeltaDp);

        Assert.Equal(908, hidden.TargetWidthPx);
        Assert.Equal(0.0, hidden.NewAppliedDeltaDp);
    }

    [Fact]
    public void InitialWidth_NeverNarrowerThanTheEnforcedMinimum()
    {
        // The caller's floor still wins, and the rail is reserved on top of the floor - not taken
        // out of it - so a floor-pinned window still has room for the rail.
        var initial = ComputeInitialWidth(
            minWidthPx: 2000, dpi: Dpi100, charWidthDp: 8.0, panelExpanded: true, railShown: true);

        Assert.Equal(2000 + 386, initial.TargetWidthPx);
    }

    // -- The drag floor: while the rail is shown it includes the rail, so a drag cannot squeeze
    //    the terminal to pay for it --------------------------------------------------------------

    [Fact]
    public void MinTrack_WithoutTheRail_IsThePlainFloor()
        => Assert.Equal(600, MinTrackWidthPx(600, Dpi100, railShown: false));

    [Fact]
    public void MinTrack_WithTheRail_AddsTheRailsReservation()
        => Assert.Equal(986, MinTrackWidthPx(600, Dpi100, railShown: true));

    [Fact]
    public void MinTrack_RailShownFloorIsNotTheHideFloor()
    {
        // Hiding is allowed to shrink past the rail-shown floor - the plain floor is what it is held
        // to instead, which is what makes the width the rail was costing recoverable.
        var hidden = ComputeToggle(
            showing: false, currentWidthPx: 978, dpi: Dpi100, panelExpanded: true, appliedDeltaDp: 178.0);

        Assert.Equal(800, hidden.TargetWidthPx);
        Assert.Equal(600, MinTrackWidthPx(600, Dpi100, railShown: false));
    }

    // -- Stats off: the rail is narrower, and every width decision has to follow it ----------------
    //
    // The stat rows and the exchange spark can be switched off, and the panel narrows when they are.
    // The width is a parameter rather than a constant because a window that grew by the wide rail and
    // gave back the narrow one (or the reverse) leaves the terminal permanently the wrong size.

    [Fact]
    public void TheTwoWidths_DifferByExactlyTheStatsBlock()
    {
        Assert.Equal(384.0, CombatRailResize.CombatPanelContentWidthDp);
        Assert.Equal(296.0, CombatRailResize.CombatPanelNarrowContentWidthDp);
        Assert.Equal(88.0,
            CombatRailResize.CombatPanelContentWidthDp - CombatRailResize.CombatPanelNarrowContentWidthDp);
        // Both carry the Border's two strokes, so the panel costs two more than it draws.
        Assert.Equal(386.0, CombatRailResize.CombatPanelWidthDp);
        Assert.Equal(298.0, CombatRailResize.CombatPanelNarrowWidthDp);
    }

    [Fact]
    public void ContentWidth_AndPanelWidth_FollowTheStatsSetting()
    {
        Assert.Equal(384.0, CombatRailResize.ContentWidthDp(showStats: true));
        Assert.Equal(296.0, CombatRailResize.ContentWidthDp(showStats: false));
        Assert.Equal(386.0, CombatRailResize.PanelWidthDp(showStats: true));
        Assert.Equal(298.0, CombatRailResize.PanelWidthDp(showStats: false));
    }

    [Fact]
    public void Show_WithStatsOff_ReservesTheNarrowWidthNotTheWide()
    {
        // The rail costs its full reservation - which must be the narrow one. Reserving 386 here and
        // later giving back 298 would orphan 88dp of window.
        var result = CombatRailResize.ComputeToggle(
            showing: true,
            currentWidthPx: 1000,
            dpi: Dpi100,
            panelExpanded: true,
            appliedDeltaDp: 0.0,
            panelWidthDp: CombatRailResize.CombatPanelNarrowWidthDp);

        Assert.Equal(1000 + 298, result.TargetWidthPx);
        Assert.Equal(298.0, result.NewAppliedDeltaDp);
    }

    [Fact]
    public void InitialWindowWidth_WithStatsOff_ReservesTheNarrowRail()
    {
        // The launch path, and the one this whole feature is FOR: a saved "rail shown, stats off"
        // must size the window for the narrow rail. Reserving the wide one here hands the difference
        // to the terminal column and the window is 88dp too wide from the first frame, with no later
        // event to correct it - this went unnoticed because ComputeInitialWidth was the one function
        // that kept a defaulted width.
        var wide = CombatRailResize.ComputeInitialWidth(
            minWidthPx: 0, dpi: Dpi100, charWidthDp: 8.0, panelExpanded: true, railShown: true,
            panelWidthDp: CombatRailResize.CombatPanelWidthDp);
        var narrow = CombatRailResize.ComputeInitialWidth(
            minWidthPx: 0, dpi: Dpi100, charWidthDp: 8.0, panelExpanded: true, railShown: true,
            panelWidthDp: CombatRailResize.CombatPanelNarrowWidthDp);

        Assert.Equal(908 + 386, wide.TargetWidthPx);
        Assert.Equal(386.0, wide.AppliedDeltaDp);
        Assert.Equal(908 + 298, narrow.TargetWidthPx);
        Assert.Equal(298.0, narrow.AppliedDeltaDp);
    }

    [Fact]
    public void MinimumWidth_AndReservation_TakeTheNarrowRail()
    {
        Assert.Equal(600 + 298,
            CombatRailResize.MinTrackWidthPx(600, Dpi100, railShown: true,
                panelWidthDp: CombatRailResize.CombatPanelNarrowWidthDp));

        var reserved = CombatRailResize.ReserveRailWidth(800, Dpi100,
            CombatRailResize.CombatPanelNarrowWidthDp);
        Assert.Equal(800 + 298, reserved.TargetWidthPx);
        Assert.Equal(298.0, reserved.AppliedDeltaDp);
    }

    // -- The side panel widens with the UI font step (docs/font-setup-design.md) ---------------------

    [Fact]
    public void PreferredWidth_WidensBySidePanelScale_OnlyWhenThePanelIsShown()
    {
        var authored = CombatRailResize.PreferredWindowWidthDp(8.0, panelExpanded: true, 80.0);
        var scaled = CombatRailResize.PreferredWindowWidthDp(8.0, panelExpanded: true, 80.0, sidePanelScale: 1.25);
        Assert.Equal(CombatRailResize.SidePanelWidthDp * 0.25, scaled - authored, 6);
        Assert.Equal(
            CombatRailResize.PreferredWindowWidthDp(8.0, panelExpanded: false, 80.0),
            CombatRailResize.PreferredWindowWidthDp(8.0, panelExpanded: false, 80.0, sidePanelScale: 1.25));
    }

    [Fact]
    public void InitialWidth_CarriesTheSidePanelScale()
    {
        var authored = CombatRailResize.ComputeInitialWidth(
            minWidthPx: 0, dpi: Dpi100, charWidthDp: 8.0,
            panelExpanded: true, railShown: false, panelWidthDp: CombatRailResize.CombatPanelWidthDp);
        var scaled = CombatRailResize.ComputeInitialWidth(
            minWidthPx: 0, dpi: Dpi100, charWidthDp: 8.0,
            panelExpanded: true, railShown: false, panelWidthDp: CombatRailResize.CombatPanelWidthDp,
            sidePanelScale: 1.5);
        Assert.Equal(908, authored.TargetWidthPx);
        Assert.Equal(908 + 114, scaled.TargetWidthPx);
    }
}
