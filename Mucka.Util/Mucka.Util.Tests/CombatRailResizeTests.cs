using Mucka.Combat;

namespace Mucka.Util.Tests;

/// <summary>
/// Tests for <see cref="CombatRailResize"/> - the Combat Rail's window-resize arithmetic (T1/T3/T4
/// and the column-change desync fix), extracted from GamePage.xaml.cs so it can be exercised here
/// without a live WinUI window.
///
/// DPI is passed as a plain value everywhere (96 == 100%, 120 == 125%, 144 == 150%) - see
/// <see cref="CombatRailResize.DpToPxCeil"/>/<see cref="CombatRailResize.DpToPxRound"/> for the
/// dp-to-px formula this mirrors.
/// </summary>
public class CombatRailResizeTests
{
    private const double Dpi100 = 96.0;

    // -- Four acceptance-criteria examples, at the real CombatPanelWidthDp (378, not the
    //    illustrative 336 - see CombatRailResize's own remarks on why the two differ by the
    //    Border's 2dp stroke inset) --------------------------------------------------------------

    [Fact]
    public void Auto_Show_GrowsByFullRailWidth()
    {
        // Auto columns (MaxColumns == 0): "there's no slack" - the window always grows by the rail's
        // full reservation, regardless of its current width.
        var result = CombatRailResize.ComputeToggle(
            showing: true, currentWidthPx: 800, dpi: Dpi100, maxColumns: 0,
            charWidthDp: 8.0, panelExpanded: true, appliedDeltaDp: 0.0);

        Assert.Equal(1178, result.TargetWidthPx);
        Assert.Equal(378.0, result.NewAppliedDeltaDp);
    }

    [Fact]
    public void Auto_HideAfterManualResize_PreservesTheManualAddition()
    {
        // Continuing from the show above (delta 378): the player manually widens the window by 100
        // (1178 -> 1278) while the rail is still up, THEN hides it. Hiding must remove only the
        // rail's own 378, not the player's +100.
        var result = CombatRailResize.ComputeToggle(
            showing: false, currentWidthPx: 1278, dpi: Dpi100, maxColumns: 0,
            charWidthDp: 8.0, panelExpanded: true, appliedDeltaDp: 378.0);

        Assert.Equal(900, result.TargetWidthPx);
        Assert.Equal(0.0, result.NewAppliedDeltaDp);
    }

    // Fixed-columns setup shared by the next several tests: panelExpanded=true, maxColumns=42,
    // charWidthDp=8.0 makes PreferredWindowWidthDp(8, true, 44 /* maxColumns+2 */) come out to
    // exactly 600 (44*8 + 4 gutter + 228 panel + 16 chrome = 352 + 248 = 600) - a "fixed natural
    // 600" reached with clean round numbers rather than reverse-engineering charWidthDp to an
    // ugly fraction.
    private const int FixedMaxColumns = 42;
    private const double FixedCharWidthDp = 8.0;
    private const bool FixedPanelExpanded = true;

    [Fact]
    public void Fixed_Show_SeatsRailInExistingSlackFirst()
    {
        // Natural (without rail) is 600; the window is already at 650, i.e. 50 of genuine slack.
        // Only 378 - 50 = 328 needs adding, landing at 978 - not 650 + 378 = 1028.
        var result = CombatRailResize.ComputeToggle(
            showing: true, currentWidthPx: 650, dpi: Dpi100, maxColumns: FixedMaxColumns,
            charWidthDp: FixedCharWidthDp, panelExpanded: FixedPanelExpanded, appliedDeltaDp: 0.0);

        Assert.Equal(978, result.TargetWidthPx);
        Assert.Equal(328.0, result.NewAppliedDeltaDp);
    }

    [Fact]
    public void Seed_AfterRelog_AdoptsTheRailWidthTheWindowAlreadyCarries()
    {
        // Show the rail, quit to the persona picker, log back in, hide the rail - and the window
        // never shrinks. A relog builds a NEW GamePage while the OS window keeps the width the old
        // one gave it, so the incoming page's applied-delta starts at zero and the first hide
        // subtracts nothing.
        //
        // Natural here is 600 and the window arrives at 978: 650 of the player's own sizing plus the
        // 328 the rail was actually seated in. The seed must claim the 328 and leave the 50 of manual
        // slack alone.
        var seeded = CombatRailResize.SeedAppliedDeltaDp(
            currentWidthPx: 978, dpi: Dpi100, maxColumns: FixedMaxColumns,
            charWidthDp: FixedCharWidthDp, panelExpanded: FixedPanelExpanded);

        Assert.Equal(378.0, seeded);

        // The limit of the inference, asserted rather than glossed: the seed claims the rail's FULL
        // width where this particular show only ever seated 328 of it, because nothing on the window
        // records which part of the slack was whose. So hiding after a relog lands on 600 - the
        // natural width - rather than the 650 the same hide would have reached inside one session.
        //
        // That is the right way to be wrong. The window ends up at a legal, useful size instead of
        // stuck a rail-width too wide, and the only cost is 50 units of the player's own sizing in
        // the case where they had widened the window BEFORE showing the rail and then relogged.
        var hidden = CombatRailResize.ComputeToggle(
            showing: false, currentWidthPx: 978, dpi: Dpi100, maxColumns: FixedMaxColumns,
            charWidthDp: FixedCharWidthDp, panelExpanded: FixedPanelExpanded, appliedDeltaDp: seeded);

        Assert.Equal(600, hidden.TargetWidthPx);
        Assert.Equal(0.0, hidden.NewAppliedDeltaDp);
    }

    [Fact]
    public void Seed_OnAFreshRun_ClaimsNothing()
    {
        // The first page of a run adopts a window that owes the rail nothing - the seed must be a
        // no-op there, or every launch would hand the first hide a delta to subtract that was never
        // added. Natural is 600 and the window is at it.
        var seeded = CombatRailResize.SeedAppliedDeltaDp(
            currentWidthPx: 600, dpi: Dpi100, maxColumns: FixedMaxColumns,
            charWidthDp: FixedCharWidthDp, panelExpanded: FixedPanelExpanded);

        Assert.Equal(0.0, seeded);
    }

    [Fact]
    public void Seed_NeverClaimsMoreThanTheRailIsWorth()
    {
        // A window the player has dragged far wider than the rail could account for: the seed takes
        // the rail's own width and no more, so hiding gives their sizing back rather than eating it.
        var seeded = CombatRailResize.SeedAppliedDeltaDp(
            currentWidthPx: 600 + 378 + 400, dpi: Dpi100, maxColumns: FixedMaxColumns,
            charWidthDp: FixedCharWidthDp, panelExpanded: FixedPanelExpanded);

        Assert.Equal(378.0, seeded);

        var hidden = CombatRailResize.ComputeToggle(
            showing: false, currentWidthPx: 600 + 378 + 400, dpi: Dpi100, maxColumns: FixedMaxColumns,
            charWidthDp: FixedCharWidthDp, panelExpanded: FixedPanelExpanded, appliedDeltaDp: seeded);

        Assert.Equal(1000, hidden.TargetWidthPx);
    }

    [Fact]
    public void Fixed_Hide_WithNoManualResize_RestoresExactPreShowWidth()
    {
        var result = CombatRailResize.ComputeToggle(
            showing: false, currentWidthPx: 978, dpi: Dpi100, maxColumns: FixedMaxColumns,
            charWidthDp: FixedCharWidthDp, panelExpanded: FixedPanelExpanded, appliedDeltaDp: 328.0);

        Assert.Equal(650, result.TargetWidthPx);
        Assert.Equal(0.0, result.NewAppliedDeltaDp);
    }

    [Fact]
    public void Fixed_Hide_AfterManualResizeWhileShown_PreservesTheManualAddition()
    {
        // From 978 (shown), the player manually resizes to 1040 (+62) while the rail is still up.
        // Hiding removes only the stored 328, giving back 650 + 62 = 712, not 650.
        var result = CombatRailResize.ComputeToggle(
            showing: false, currentWidthPx: 1040, dpi: Dpi100, maxColumns: FixedMaxColumns,
            charWidthDp: FixedCharWidthDp, panelExpanded: FixedPanelExpanded, appliedDeltaDp: 328.0);

        Assert.Equal(712, result.TargetWidthPx);
        Assert.Equal(0.0, result.NewAppliedDeltaDp);
    }

    [Fact]
    public void Fixed_Show_WhenSlackAlreadyExceedsNeed_AddsNothing()
    {
        // Slack (388) already exceeds the rail's own width (378): the window must not resize at all.
        var result = CombatRailResize.ComputeToggle(
            showing: true, currentWidthPx: 988, dpi: Dpi100, maxColumns: FixedMaxColumns,
            charWidthDp: FixedCharWidthDp, panelExpanded: FixedPanelExpanded, appliedDeltaDp: 0.0);

        Assert.Equal(988, result.TargetWidthPx);
        Assert.Equal(0.0, result.NewAppliedDeltaDp);
    }

    // -- The floor clamp: the window may never end up narrower than the terminal+left-panel need --

    [Fact]
    public void Show_NeverEndsUpNarrowerThanTheFloor()
    {
        // Floor for 42 columns (no +2 margin - this is PreferredWindowWidthDp(charWidthDp,
        // panelExpanded, maxColumns), not maxColumns+2): 42*8 + 4 + 228 + 16 = 584.
        // A near-zero starting width still can't end up below that floor even after adding the rail.
        var result = CombatRailResize.ComputeToggle(
            showing: true, currentWidthPx: 0, dpi: Dpi100, maxColumns: FixedMaxColumns,
            charWidthDp: FixedCharWidthDp, panelExpanded: FixedPanelExpanded, appliedDeltaDp: 0.0);

        Assert.Equal(584, result.TargetWidthPx);
        // The remembered delta must match what was ACTUALLY applied (584 - 0 = 584), not the raw
        // pre-clamp deltaPx (378) computed before the floor pushed the target up further.
        Assert.Equal(584.0, result.NewAppliedDeltaDp);
    }

    [Fact]
    public void Show_WhenFloorClampBites_DeltaMatchesWhatWasActuallyApplied_AndHideRoundTripsCorrectly()
    {
        // A window starting at 0 is narrower than even the un-railed floor (584) - impossible via
        // GamePage's own UpdateWindowMinimumWidth in practice (it continuously enforces that floor),
        // but ComputeToggle is a pure function and has to stay correct regardless of caller
        // discipline. At such a low starting width there is no slack, so the raw computed delta is
        // the rail's full 378 - but 0 + 378 = 378 is still below the 584 floor, so the clamp bites and
        // the amount ACTUALLY applied is 584, not 378.
        var shown = CombatRailResize.ComputeToggle(
            showing: true, currentWidthPx: 0, dpi: Dpi100, maxColumns: FixedMaxColumns,
            charWidthDp: FixedCharWidthDp, panelExpanded: FixedPanelExpanded, appliedDeltaDp: 0.0);

        Assert.Equal(584, shown.TargetWidthPx);
        Assert.Equal(584.0, shown.NewAppliedDeltaDp);

        // Hiding immediately, using the resynced delta, must not overshoot below the floor either -
        // the window lands back at the floor (584), which is the honest answer: the pre-show 0 was
        // never a legal width to begin with. The delta driving this arithmetic has to be the real
        // (post-clamp) one, not the raw pre-clamp value.
        var hidden = CombatRailResize.ComputeToggle(
            showing: false, currentWidthPx: shown.TargetWidthPx, dpi: Dpi100, maxColumns: FixedMaxColumns,
            charWidthDp: FixedCharWidthDp, panelExpanded: FixedPanelExpanded,
            appliedDeltaDp: shown.NewAppliedDeltaDp);

        Assert.Equal(584, hidden.TargetWidthPx);
        Assert.Equal(0.0, hidden.NewAppliedDeltaDp);
    }

    [Fact]
    public void Hide_NeverEndsUpNarrowerThanTheFloor()
    {
        // Same floor (584). Subtracting the full stored delta from a window already AT natural width
        // would go to 262 - the clamp pins it at the floor instead, deliberately forgetting the width
        // it could not remove rather than trying to claw it back on a later resize.
        var result = CombatRailResize.ComputeToggle(
            showing: false, currentWidthPx: 600, dpi: Dpi100, maxColumns: FixedMaxColumns,
            charWidthDp: FixedCharWidthDp, panelExpanded: FixedPanelExpanded, appliedDeltaDp: 378.0);

        Assert.Equal(584, result.TargetWidthPx);
        Assert.Equal(0.0, result.NewAppliedDeltaDp);
    }

    // -- Non-100% DPI: the dp/px round-trip, and a DPI change made WHILE the rail is shown --

    [Fact]
    public void DpToPxRound_ScalesWithDpi_150Percent()
    {
        // 338dp at 150% (dpi 144): 378 * 144 / 96 = 567 exactly - no rounding ambiguity.
        Assert.Equal(567, CombatRailResize.DpToPxRound(CombatRailResize.CombatPanelWidthDp, 144.0));
    }

    [Fact]
    public void DpToPxRound_ScalesWithDpi_125Percent_BankersRounding()
    {
        // 338dp at 125% (dpi 120): 378 * 120 / 96 = 472.5 exactly - a genuine rounding midpoint.
        // Math.Round with no MidpointRounding argument defaults to "round half to even", so this
        // resolves to 472 (the nearest even integer), not 423. Asserted explicitly so a reader does
        // not have to guess which way DpToPxRound resolves a tie.
        Assert.Equal(472, CombatRailResize.DpToPxRound(CombatRailResize.CombatPanelWidthDp, 120.0));
    }

    [Fact]
    public void Show_At150Percent_ScalesTheFullRailWidth()
    {
        // Auto mode at 150% DPI: the rail's own 338dp becomes 507px, not 338px.
        var result = CombatRailResize.ComputeToggle(
            showing: true, currentWidthPx: 1200, dpi: 144.0, maxColumns: 0,
            charWidthDp: 12.0, panelExpanded: true, appliedDeltaDp: 0.0);

        Assert.Equal(1200 + 567, result.TargetWidthPx);
        Assert.Equal(378.0, result.NewAppliedDeltaDp);
    }

    [Fact]
    public void Hide_AfterDpiChangedWhileShown_ReconvertsAtTheNewDpi_NotTheStaleOne()
    {
        // Show at 100% DPI (auto mode, currentWidthPx 800): delta stored as 378dp,
        // window grows to 1178px.
        var shown = CombatRailResize.ComputeToggle(
            showing: true, currentWidthPx: 800, dpi: Dpi100, maxColumns: 0,
            charWidthDp: 8.0, panelExpanded: true, appliedDeltaDp: 0.0);
        Assert.Equal(1178, shown.TargetWidthPx);
        Assert.Equal(378.0, shown.NewAppliedDeltaDp);

        // The window is then dragged to a 150%-scaled monitor. WinUI rescales the whole window's
        // physical size with it (1178 * 1.5 = 1767) - this test does not re-derive that rescale, it
        // simply asserts what CombatRailResize does with the new state: hiding must reconvert the
        // STORED 338dp at the CURRENT 150% dpi (378 * 144 / 96 = 567), and not leave the window's
        // DPI-rescaled extra width behind as orphaned space.
        var hidden = CombatRailResize.ComputeToggle(
            showing: false, currentWidthPx: 1767, dpi: 144.0, maxColumns: 0,
            charWidthDp: 12.0, panelExpanded: true, appliedDeltaDp: shown.NewAppliedDeltaDp);

        // 800dp's own equivalent at 150% is 1200px (800 * 1.5) - exactly what a correct reconversion
        // gives back: 1767 - 567 = 1200.
        Assert.Equal(1200, hidden.TargetWidthPx);
        Assert.Equal(0.0, hidden.NewAppliedDeltaDp);
    }

    // -- A column-count change made while the rail is shown must not eat the rail's width out of
    //    the terminal, and the resynced delta must make a later hide exact -----------------------

    [Fact]
    public void ReserveRailWidth_AddsExactlyTheRailsFullReservation()
    {
        var reserved = CombatRailResize.ReserveRailWidth(targetWidthPxWithoutRail: 824, dpi: Dpi100);

        Assert.Equal(824 + 378, reserved.TargetWidthPx);
        Assert.Equal(378.0, reserved.AppliedDeltaDp);
    }

    [Fact]
    public void ColumnsChangeWhileRailShown_TerminalKeepsItsFullColumnCount()
    {
        // Fixed columns changed from 60 to 70 while the rail is shown. ResizeWindowToFitColumns
        // snaps to PreferredWindowWidthDp(charWidthDp, panelExpanded, 72 /* 70 + 2 breathing
        // columns */) = 72*8 + 4 + 228 + 16 = 824 - then, because the rail is shown, reserves its
        // width on top via ReserveRailWidth.
        const double charWidthDp = 8.0;
        const bool panelExpanded = true;
        var naturalWithoutRail = CombatRailResize.PreferredWindowWidthDp(charWidthDp, panelExpanded, 72.0);
        var naturalPx = CombatRailResize.DpToPxCeil(naturalWithoutRail, Dpi100);
        Assert.Equal(824, naturalPx);

        var reserved = CombatRailResize.ReserveRailWidth(naturalPx, Dpi100);

        Assert.Equal(824 + 378, reserved.TargetWidthPx);
        Assert.Equal(378.0, reserved.AppliedDeltaDp);

        // The terminal's own share of that final width - subtracting the rail, the left panel, the
        // gutter and the chrome - must equal exactly 72 columns' worth (576 = 72 * 8).
        var terminalColumnsWidth = reserved.TargetWidthPx
            - CombatRailResize.DpToPxRound(CombatRailResize.CombatPanelWidthDp, Dpi100)
            - (int)CombatRailResize.SidePanelWidthDp
            - (int)CombatRailResize.TerminalGutterDp
            - (int)CombatRailResize.WindowChromeDp;
        Assert.Equal((int)(72.0 * charWidthDp), terminalColumnsWidth);
    }

    [Fact]
    public void ColumnsChangeWhileRailShown_LaterHideReturnsExactlyToTheNewNaturalWidth()
    {
        // Continuing the scenario above: the resynced delta (378, zero slack) must make hiding land
        // exactly back on the new natural width (824), not clamped below the new floor.
        var result = CombatRailResize.ComputeToggle(
            showing: false, currentWidthPx: 824 + 378, dpi: Dpi100, maxColumns: 70,
            charWidthDp: 8.0, panelExpanded: true, appliedDeltaDp: 378.0);

        Assert.Equal(824, result.TargetWidthPx);
        Assert.Equal(0.0, result.NewAppliedDeltaDp);
    }
}
