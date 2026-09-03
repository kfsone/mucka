using Mucka.Terminal;

namespace Mucka.Terminal.Tests;

/// <summary>
/// Tests for <see cref="CombatRailResize"/> - the Combat Rail's window-resize arithmetic (T1/T3/T4
/// and the round-four column-change desync fix), extracted from GamePage.xaml.cs specifically so it
/// could be exercised here without a live WinUI window. Every scenario below was independently
/// verified only by hand-tracing the code before this file existed; per CLAUDE.md ("anything that
/// never reaches the user's hands has no gate unless you give it one"), these are that gate.
///
/// DPI is passed as a plain value everywhere (96 == 100%, 120 == 125%, 144 == 150%) - see
/// <see cref="CombatRailResize.DpToPxCeil"/>/<see cref="CombatRailResize.DpToPxRound"/> for the
/// dp-to-px formula this mirrors.
/// </summary>
public class CombatRailResizeTests
{
    private const double Dpi100 = 96.0;

    // -- The user's own four acceptance-criteria examples, at the real CombatPanelWidthDp (338, not
    //    the illustrative 336 - see CombatRailResize's own remarks on why the two differ by the
    //    Border's 2dp stroke inset) --------------------------------------------------------------

    [Fact]
    public void Auto_Show_GrowsByFullRailWidth()
    {
        // Auto columns (MaxColumns == 0): "there's no slack" - the window always grows by the rail's
        // full reservation, regardless of its current width.
        var result = CombatRailResize.ComputeToggle(
            showing: true, currentWidthPx: 800, dpi: Dpi100, maxColumns: 0,
            charWidthDp: 8.0, panelExpanded: true, appliedDeltaDp: 0.0);

        Assert.Equal(1138, result.TargetWidthPx);
        Assert.Equal(338.0, result.NewAppliedDeltaDp);
    }

    [Fact]
    public void Auto_HideAfterManualResize_PreservesTheManualAddition()
    {
        // Continuing from the show above (delta 338): the player manually widens the window by 100
        // (1138 -> 1238) while the rail is still up, THEN hides it. Hiding must remove only the
        // rail's own 338, not the player's +100.
        var result = CombatRailResize.ComputeToggle(
            showing: false, currentWidthPx: 1238, dpi: Dpi100, maxColumns: 0,
            charWidthDp: 8.0, panelExpanded: true, appliedDeltaDp: 338.0);

        Assert.Equal(900, result.TargetWidthPx);
        Assert.Equal(0.0, result.NewAppliedDeltaDp);
    }

    // Fixed-columns setup shared by the next several tests: panelExpanded=true, maxColumns=42,
    // charWidthDp=8.0 makes PreferredWindowWidthDp(8, true, 44 /* maxColumns+2 */) come out to
    // exactly 600 (44*8 + 4 gutter + 228 panel + 16 chrome = 352 + 248 = 600) - the "fixed natural
    // 600" the user's own examples use, reached with clean round numbers rather than reverse-
    // engineering charWidthDp to an ugly fraction.
    private const int FixedMaxColumns = 42;
    private const double FixedCharWidthDp = 8.0;
    private const bool FixedPanelExpanded = true;

    [Fact]
    public void Fixed_Show_SeatsRailInExistingSlackFirst()
    {
        // Natural (without rail) is 600; the window is already at 650, i.e. 50 of genuine slack.
        // Only 338 - 50 = 288 needs adding, landing at 938 - not 650 + 338 = 986.
        var result = CombatRailResize.ComputeToggle(
            showing: true, currentWidthPx: 650, dpi: Dpi100, maxColumns: FixedMaxColumns,
            charWidthDp: FixedCharWidthDp, panelExpanded: FixedPanelExpanded, appliedDeltaDp: 0.0);

        Assert.Equal(938, result.TargetWidthPx);
        Assert.Equal(288.0, result.NewAppliedDeltaDp);
    }

    [Fact]
    public void Fixed_Hide_WithNoManualResize_RestoresExactPreShowWidth()
    {
        var result = CombatRailResize.ComputeToggle(
            showing: false, currentWidthPx: 938, dpi: Dpi100, maxColumns: FixedMaxColumns,
            charWidthDp: FixedCharWidthDp, panelExpanded: FixedPanelExpanded, appliedDeltaDp: 288.0);

        Assert.Equal(650, result.TargetWidthPx);
        Assert.Equal(0.0, result.NewAppliedDeltaDp);
    }

    [Fact]
    public void Fixed_Hide_AfterManualResizeWhileShown_PreservesTheManualAddition()
    {
        // From 938 (shown), the player manually resizes to 1000 (+62) while the rail is still up.
        // Hiding removes only the stored 288, giving back 650 + 62 = 712, not 650.
        var result = CombatRailResize.ComputeToggle(
            showing: false, currentWidthPx: 1000, dpi: Dpi100, maxColumns: FixedMaxColumns,
            charWidthDp: FixedCharWidthDp, panelExpanded: FixedPanelExpanded, appliedDeltaDp: 288.0);

        Assert.Equal(712, result.TargetWidthPx);
        Assert.Equal(0.0, result.NewAppliedDeltaDp);
    }

    [Fact]
    public void Fixed_Show_WhenSlackAlreadyExceedsNeed_AddsNothing()
    {
        // Slack (388) already exceeds the rail's own width (338): the window must not resize at all.
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
        // pre-clamp deltaPx (338) computed before the floor pushed the target up further. A version
        // that derived the delta from the pre-clamp value instead of the post-clamp TargetWidthPx
        // would report 338.0 here even though the window actually moved by 584 - see
        // Show_WhenFloorClampBites_DeltaMatchesWhatWasActuallyApplied_AndHideRoundTripsCorrectly
        // below for why that disagreement matters.
        Assert.Equal(584.0, result.NewAppliedDeltaDp);
    }

    [Fact]
    public void Show_WhenFloorClampBites_DeltaMatchesWhatWasActuallyApplied_AndHideRoundTripsCorrectly()
    {
        // A window starting at 0 is narrower than even the un-railed floor (584) - impossible via
        // GamePage's own UpdateWindowMinimumWidth in practice (it continuously enforces that floor),
        // but ComputeToggle is a pure function and has to stay correct regardless of caller
        // discipline. At such a low starting width there is no slack, so the raw computed delta is
        // the rail's full 338 - but 0 + 338 = 338 is still below the 584 floor, so the clamp bites and
        // the amount ACTUALLY applied is 584, not 338. This is the defect this test exists to catch:
        // an earlier version derived the remembered delta from the pre-clamp 338 instead of the
        // post-clamp TargetWidthPx, so it disagreed with what the window actually did.
        var shown = CombatRailResize.ComputeToggle(
            showing: true, currentWidthPx: 0, dpi: Dpi100, maxColumns: FixedMaxColumns,
            charWidthDp: FixedCharWidthDp, panelExpanded: FixedPanelExpanded, appliedDeltaDp: 0.0);

        Assert.Equal(584, shown.TargetWidthPx);
        Assert.Equal(584.0, shown.NewAppliedDeltaDp);

        // Hiding immediately, using the resynced delta, must not overshoot below the floor either -
        // the window lands back at the floor (584), which is the honest answer: the pre-show 0 was
        // never a legal width to begin with, and a stale (pre-clamp) delta of 338 would have produced
        // a wrong, different result here (0 + 584 - 338 = 246, still below the floor and clamped up
        // to 584 anyway by coincidence at THIS floor - the point is the delta driving that arithmetic
        // has to be the real one, not an assumption that happens to still land in the right place).
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
            charWidthDp: FixedCharWidthDp, panelExpanded: FixedPanelExpanded, appliedDeltaDp: 338.0);

        Assert.Equal(584, result.TargetWidthPx);
        Assert.Equal(0.0, result.NewAppliedDeltaDp);
    }

    // -- Non-100% DPI: the dp/px round-trip, and a DPI change made WHILE the rail is shown (finding 5) --

    [Fact]
    public void DpToPxRound_ScalesWithDpi_150Percent()
    {
        // 338dp at 150% (dpi 144): 338 * 144 / 96 = 507 exactly - no rounding ambiguity.
        Assert.Equal(507, CombatRailResize.DpToPxRound(CombatRailResize.CombatPanelWidthDp, 144.0));
    }

    [Fact]
    public void DpToPxRound_ScalesWithDpi_125Percent_BankersRounding()
    {
        // 338dp at 125% (dpi 120): 338 * 120 / 96 = 422.5 exactly - a genuine rounding midpoint.
        // Math.Round with no MidpointRounding argument defaults to "round half to even", so this
        // resolves to 422 (the nearest even integer), not 423. Asserted explicitly so a reader does
        // not have to guess which way DpToPxRound resolves a tie.
        Assert.Equal(422, CombatRailResize.DpToPxRound(CombatRailResize.CombatPanelWidthDp, 120.0));
    }

    [Fact]
    public void Show_At150Percent_ScalesTheFullRailWidth()
    {
        // Auto mode at 150% DPI: the rail's own 338dp becomes 507px, not 338px.
        var result = CombatRailResize.ComputeToggle(
            showing: true, currentWidthPx: 1200, dpi: 144.0, maxColumns: 0,
            charWidthDp: 12.0, panelExpanded: true, appliedDeltaDp: 0.0);

        Assert.Equal(1200 + 507, result.TargetWidthPx);
        Assert.Equal(338.0, result.NewAppliedDeltaDp);
    }

    [Fact]
    public void Hide_AfterDpiChangedWhileShown_ReconvertsAtTheNewDpi_NotTheStaleOne()
    {
        // Finding 5. Show at 100% DPI (auto mode, currentWidthPx 800): delta stored as 338dp,
        // window grows to 1138px.
        var shown = CombatRailResize.ComputeToggle(
            showing: true, currentWidthPx: 800, dpi: Dpi100, maxColumns: 0,
            charWidthDp: 8.0, panelExpanded: true, appliedDeltaDp: 0.0);
        Assert.Equal(1138, shown.TargetWidthPx);
        Assert.Equal(338.0, shown.NewAppliedDeltaDp);

        // The window is then dragged to a 150%-scaled monitor. WinUI rescales the whole window's
        // physical size with it (1138 * 1.5 = 1707) - this test does not re-derive that rescale, it
        // simply asserts what CombatRailResize does with the new state: hiding must reconvert the
        // STORED 338dp at the CURRENT 150% dpi (338 * 144 / 96 = 507), not subtract the stale 338px
        // an earlier (buggy) version would have cached, and not leave the window's DPI-rescaled
        // extra width behind as orphaned space.
        var hidden = CombatRailResize.ComputeToggle(
            showing: false, currentWidthPx: 1707, dpi: 144.0, maxColumns: 0,
            charWidthDp: 12.0, panelExpanded: true, appliedDeltaDp: shown.NewAppliedDeltaDp);

        // 800dp's own equivalent at 150% is 1200px (800 * 1.5) - exactly what a correct reconversion
        // gives back: 1707 - 507 = 1200.
        Assert.Equal(1200, hidden.TargetWidthPx);
        Assert.Equal(0.0, hidden.NewAppliedDeltaDp);
    }

    // -- Round-four defect: a column-count change made while the rail is shown must not eat the
    //    rail's width out of the terminal, and the resynced delta must make a later hide exact -----

    [Fact]
    public void ReserveRailWidth_AddsExactlyTheRailsFullReservation()
    {
        var reserved = CombatRailResize.ReserveRailWidth(targetWidthPxWithoutRail: 824, dpi: Dpi100);

        Assert.Equal(824 + 338, reserved.TargetWidthPx);
        Assert.Equal(338.0, reserved.AppliedDeltaDp);
    }

    [Fact]
    public void ColumnsChangeWhileRailShown_TerminalKeepsItsFullColumnCount()
    {
        // The reviewer's own concrete failure: fixed columns changed from 60 to 70 while the rail is
        // shown. ResizeWindowToFitColumns snaps to PreferredWindowWidthDp(charWidthDp, panelExpanded,
        // 72 /* 70 + 2 breathing columns */) = 72*8 + 4 + 228 + 16 = 824 - then, because the rail is
        // shown, reserves its width on top via ReserveRailWidth.
        const double charWidthDp = 8.0;
        const bool panelExpanded = true;
        var naturalWithoutRail = CombatRailResize.PreferredWindowWidthDp(charWidthDp, panelExpanded, 72.0);
        var naturalPx = CombatRailResize.DpToPxCeil(naturalWithoutRail, Dpi100);
        Assert.Equal(824, naturalPx);

        var reserved = CombatRailResize.ReserveRailWidth(naturalPx, Dpi100);

        Assert.Equal(824 + 338, reserved.TargetWidthPx);
        Assert.Equal(338.0, reserved.AppliedDeltaDp);

        // The terminal's own share of that final width - subtracting the rail, the left panel, the
        // gutter and the chrome - must equal exactly 72 columns' worth (576 = 72 * 8), not the ~30
        // columns the pre-fix snap left it with (824 - 16 - 228 - 338 = 242 =~ 30 columns).
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
        // Continuing the scenario above: the resynced delta (338, zero slack - not whatever partial
        // amount an earlier toggle's slack absorption had computed) must make hiding land exactly
        // back on the new natural width (824), not on stale pre-change arithmetic and not clamped
        // below the new floor.
        var result = CombatRailResize.ComputeToggle(
            showing: false, currentWidthPx: 824 + 338, dpi: Dpi100, maxColumns: 70,
            charWidthDp: 8.0, panelExpanded: true, appliedDeltaDp: 338.0);

        Assert.Equal(824, result.TargetWidthPx);
        Assert.Equal(0.0, result.NewAppliedDeltaDp);
    }
}
