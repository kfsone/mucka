using MudSharp.Combat;
using Mucka.ViewModels;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Coverage for where the Combat Rail's opponent slots and stamina seal actually land - the
/// geometry the damage-float overlay places against (GamePage.OnCombatFloatRaised).
///
/// <para>The numbers below are the live panel's own: CombatRailView passes its layout constants in
/// as <see cref="RailSlotMetrics"/>, and this fixture restates them so a change to the canvas that
/// silently moves a slot shows up as a failing expectation here rather than as a float printed over
/// blank panel. Kept as a local copy on purpose - importing the real one would need the MAUI
/// assembly, which this project does not reference.</para>
/// </summary>
public sealed class RailSlotGeometryTests
{
    // Mirrors CombatRailView.SlotMetrics.
    private static readonly RailSlotMetrics M = new(
        RailWidth: 336f, Pad: 10f, SlotHeight: 76f, SlotGap: 5f, SlotsGap: 6f,
        BottomRowHeight: 96f, TickRowHeight: 30f, SealSize: 92f, MaxSlots: 8);

    /// <summary>A panel at the design width, so dp and the canvas's logical units are 1:1 - which
    /// is the condition GamePage.xaml's 338dp Border exists to guarantee (338 minus its own 1dp
    /// stroke per side).</summary>
    private const double W = 336.0;

    // -- the bottom-up chain -----------------------------------------------------

    [Fact]
    public void SlotsBottom_IsMeasuredUpFromThePanelsBottomEdge()
    {
        // Pad + tick row + bottom row + the 6 gap = 142 above the bottom edge, whatever the height.
        Assert.Equal(600.0 - 142.0, RailSlotGeometry.SlotsBottom(M, 600.0), 3);
        Assert.Equal(400.0 - 142.0, RailSlotGeometry.SlotsBottom(M, 400.0), 3);
    }

    [Fact]
    public void SlotZero_IsTheBottomSlot_AndHigherIndexesStackUpward()
    {
        var bottom = RailSlotGeometry.OpponentSlotDp(M, W, 600, rosterIndex: 0, liveCount: 3);
        var above = RailSlotGeometry.OpponentSlotDp(M, W, 600, rosterIndex: 1, liveCount: 3);

        Assert.NotNull(bottom);
        Assert.NotNull(above);
        // The rail is bottom-focused: index 0 sits lowest, and each further row is one slot plus one
        // gap higher up the panel.
        Assert.True(above!.Value.Top < bottom!.Value.Top);
        Assert.Equal(76.0 + 5.0, bottom.Value.Top - above.Value.Top, 3);
        Assert.Equal(76.0, bottom.Value.Height, 3);
        Assert.Equal(10.0, bottom.Value.Left, 3);
        Assert.Equal(336.0 - 20.0, bottom.Value.Width, 3);
    }

    // -- capacity and the overflow row -------------------------------------------

    [Fact]
    public void Capacity_IsBoundedByTheRailsOwnSlotCap_HoweverTallTheWindow()
    {
        Assert.Equal(8, RailSlotGeometry.Capacity(M, 5000.0));
    }

    [Fact]
    public void Capacity_IsAtLeastOne_EvenInAWindowTooShortForASlot()
    {
        // Matches the canvas's own Math.Clamp(..., 1, MaxSlots): a rail with nowhere to put a slot
        // still reports one, rather than a negative count that would make the overflow arithmetic
        // nonsense.
        Assert.Equal(1, RailSlotGeometry.Capacity(M, 150.0));
    }

    [Fact]
    public void OneSlotIsSurrenderedToTheOverflowRow_AsSoonAsTheRosterOutgrowsTheCapacity()
    {
        // 142 of chrome plus 10 of top pad leaves 248 for slots at 400dp tall: floor(248/81) = 3.
        var height = 400.0;
        Assert.Equal(3, RailSlotGeometry.Capacity(M, height));

        Assert.Equal(3, RailSlotGeometry.ShownSlots(M, height, liveCount: 3));
        // A fourth opponent does not squeeze in - the third slot becomes the "+N" overflow line.
        Assert.Equal(2, RailSlotGeometry.ShownSlots(M, height, liveCount: 4));
        Assert.Equal(2, RailSlotGeometry.ShownSlots(M, height, liveCount: 14));
    }

    [Fact]
    public void ARowInTheOverflowTail_HasNoRectangle()
    {
        var height = 400.0;
        // Index 2 has a slot when three opponents fit exactly, and loses it the moment a fourth joins
        // and the overflow row claims that slot. A float must not be drawn for it either way round.
        Assert.NotNull(RailSlotGeometry.OpponentSlotDp(M, W, height, rosterIndex: 2, liveCount: 3));
        Assert.Null(RailSlotGeometry.OpponentSlotDp(M, W, height, rosterIndex: 2, liveCount: 4));
        Assert.Null(RailSlotGeometry.OpponentSlotDp(M, W, height, rosterIndex: 7, liveCount: 14));
    }

    [Fact]
    public void ATallPanel_StillSurrendersASlot_WhenTheOppositionOutGrowsTheRowCap()
    {
        // The regression this parameter was renamed for: a caller passing the published ROW count
        // could never report an overflow once the panel was tall enough to fit the whole capped list.
        // Counted on LIVE opponents now - the resolved ones take no slot at all, they are in the
        // top-anchored dead strip.
        Assert.Equal(8, RailSlotGeometry.Capacity(M, 800.0));
        Assert.Equal(8, RailSlotGeometry.ShownSlots(M, 800.0, liveCount: 8));
        Assert.Equal(7, RailSlotGeometry.ShownSlots(M, 800.0, liveCount: 14));
        Assert.Null(RailSlotGeometry.OpponentSlotDp(M, W, 800.0, rosterIndex: 7, liveCount: 14));
    }

    [Fact]
    public void AnUnmeasuredPanel_HasNoGeometry()
    {
        Assert.Null(RailSlotGeometry.OpponentSlotDp(M, 0, 0, rosterIndex: 0, liveCount: 1));
        Assert.Null(RailSlotGeometry.OpponentSlotDp(M, W, 600, rosterIndex: -1, liveCount: 1));
        Assert.Equal(0, RailSlotGeometry.ShownSlots(M, 600, liveCount: 0));
    }

    // -- the stamina seal --------------------------------------------------------

    [Fact]
    public void StaminaSeal_SitsAtTheBottomRowsTopEdge_InTheLeftColumn()
    {
        var seal = RailSlotGeometry.StaminaSealDp(M, W, 600);

        // Bottom row top = height - pad - tick row - bottom row.
        Assert.Equal(600.0 - 10.0 - 30.0 - 96.0, seal.Top, 3);
        Assert.Equal(10.0, seal.Left, 3);
        Assert.Equal(92.0, seal.Width, 3);
        Assert.Equal(92.0, seal.Height, 3);
    }

    [Fact]
    public void TheSealNeverOverlapsTheLowestSlot()
    {
        // Six dp between them, which is the SlotsGap the canvas leaves. A float anchored on the seal
        // and one anchored on slot 0 must not start life on the same pixels.
        var seal = RailSlotGeometry.StaminaSealDp(M, W, 600);
        var slot0 = RailSlotGeometry.OpponentSlotDp(M, W, 600, rosterIndex: 0, liveCount: 1);

        Assert.NotNull(slot0);
        Assert.Equal(6.0, seal.Top - (slot0!.Value.Top + slot0.Value.Height), 3);
    }

    // -- the scale factor --------------------------------------------------------

    [Fact]
    public void EverythingScalesWithThePanelWidth_TheSameWayTheCanvasDoes()
    {
        // The canvas maps its fixed 336-unit space onto whatever width it is given, so a sibling
        // positioned in dp needs the same factor. Double the panel width at double the height and
        // every dp figure should double - this is the invariant TickTrackDp/FleePillDp already hold
        // and the one a float overlay silently violates if it hardcodes the design width.
        var single = RailSlotGeometry.OpponentSlotDp(M, W, 600, 0, 2)!.Value;
        var doubled = RailSlotGeometry.OpponentSlotDp(M, W * 2, 1200, 0, 2)!.Value;

        Assert.Equal(single.Left * 2, doubled.Left, 3);
        Assert.Equal(single.Top * 2, doubled.Top, 3);
        Assert.Equal(single.Width * 2, doubled.Width, 3);
        Assert.Equal(single.Height * 2, doubled.Height, 3);
    }

    // -- the live/dead split ------------------------------------------------------

    [Fact]
    public void FiveLiveSlotsStillFitTheShortestRealisticRail()
    {
        // What the badge size was bought against. Live concurrency in the clog corpus peaks at 5 or
        // fewer in 1268 of 1275 encounters (99.45%; tools/combat/concurrency.py against the whole clog
        // corpus, 2026-09-02 - see tools/combat/README.md's own stored result), so five slots is the
        // case worth sizing for - and a 76-unit slot is the largest that still fits five at a 560-unit
        // rail (408 available, 5 x 81 = 405). The one encounter that reached thirteen is the overflow
        // row's business.
        Assert.Equal(5, RailSlotGeometry.Capacity(M, 560.0));
        Assert.Equal(5, RailSlotGeometry.ShownSlots(M, 560.0, liveCount: 5));
        Assert.Equal(8, RailSlotGeometry.Capacity(M, 800.0));
    }

    [Fact]
    public void TheDeadStripGetsWhatIsLeftAboveTheLiveStack_NeverTheOtherWayRound()
    {
        // The containment for the movement-on-death rule. The live stack is bottom-anchored and sized
        // from the FULL height, so a death cannot move it; the dead strip is top-anchored and takes
        // whatever is above. When the two meet it is the dead that gives.
        const double h = 800.0;
        var oneLive = RailSlotGeometry.DeadStripBottom(
            M, h, RailSlotGeometry.LiveStackHeight(M, 1, 0));
        var fiveLive = RailSlotGeometry.DeadStripBottom(
            M, h, RailSlotGeometry.LiveStackHeight(M, 5, 0));

        Assert.True(fiveLive < oneLive);
        // And the live slots themselves sit in exactly the same place either way.
        Assert.Equal(
            RailSlotGeometry.SlotTop(M, h, 0),
            RailSlotGeometry.SlotTop(M, h, 0), 3);
        Assert.Equal(RailSlotGeometry.SlotsBottom(M, h) - 76.0, RailSlotGeometry.SlotTop(M, h, 0), 3);
    }

    [Fact]
    public void TheOverflowRowIsPartOfTheLiveStack_AndCostsOnlyItsOwnHeight()
    {
        // It is a tail of the LIVE roster, so it sits with the live region rather than up with the
        // dead - and it takes a line's worth rather than a whole slot, the same accounting that moved
        // the corpses out.
        var without = RailSlotGeometry.LiveStackHeight(M, 3, 0);
        var with = RailSlotGeometry.LiveStackHeight(M, 3, 22.0);

        Assert.Equal((3 * 76.0) + (2 * 5.0), without, 3);
        Assert.Equal(without + 22.0 + 5.0, with, 3);
    }

    // -- PlanDeadStrip -------------------------------------------------------------

    /// <summary>N endings, all in encounter 0 / reset 0 - i.e. no separators anywhere, the case the
    /// pre-2026-09-02 signature covered exclusively. Used to migrate the uniform-pitch tests onto the
    /// generalised signature without changing what they mean.</summary>
    private static CombatEnding[] Uniform(int count)
    {
        var list = new CombatEnding[count];
        for (var i = 0; i < count; i++)
            list[i] = new CombatEnding("n" + i, FightOutcome.Kill, null, EncounterOrdinal: 0, ResetOrdinal: 0);
        return list;
    }

    [Fact]
    public void PlanDeadStrip_EverythingFits_NoMarkerNothingHidden()
    {
        // startY=0, lineHeight=1, floor=9 -> ten rows fit (indices 0..9); zero separator allowance
        // reduces this to the pre-2026-09-02 uniform-pitch case.
        var plan = RailSlotGeometry.PlanDeadStrip(floor: 9, startY: 0, lineHeight: 1, 0, 0, Uniform(6));

        Assert.Equal(0, plan.ShownStart);
        Assert.False(plan.ShowMarker);
        Assert.Equal(0, plan.HiddenCount);
    }

    [Fact]
    public void PlanDeadStrip_ExactlyAtCapacity_NoMarker()
    {
        var plan = RailSlotGeometry.PlanDeadStrip(floor: 9, startY: 0, lineHeight: 1, 0, 0, Uniform(10));

        Assert.Equal(0, plan.ShownStart);
        Assert.False(plan.ShowMarker);
        Assert.Equal(0, plan.HiddenCount);
    }

    [Fact]
    public void PlanDeadStrip_Truncated_HiddenCountIsExactlyWhatTheMarkerDoesNotShow()
    {
        // The reviewer's regression case: capacity 4 (floor=3, startY=0, lineHeight=1 -> rows 0..3),
        // 5 endings on file. One row goes to the marker, three to the newest endings (indices 2,3,4),
        // so TWO are actually hidden (indices 0,1) - not one. The old inline arithmetic printed
        // hiddenOlder BEFORE reserving the marker's own row, so it always undercounted by exactly the
        // row it had just taken; this pins the corrected count directly.
        var plan = RailSlotGeometry.PlanDeadStrip(floor: 3, startY: 0, lineHeight: 1, 0, 0, Uniform(5));

        Assert.True(plan.ShowMarker);
        Assert.Equal(2, plan.HiddenCount);
        Assert.Equal(2, plan.ShownStart);
        // Three ending rows (indices 2,3,4) plus the marker row = 4 = the capacity.
        Assert.Equal(3, 5 - plan.ShownStart);
    }

    [Fact]
    public void PlanDeadStrip_CapacityOne_ShowsTheNewestEnding_NotAMarkerWithNothingUnderIt()
    {
        // Exactly one row available (floor=0, startY=0, lineHeight=1) and more than one ending on
        // file: a "+N earlier" marker with zero endings below it would be a NET LOSS of information
        // versus just showing the one ending that fits. The old code reserved this row for the marker
        // unconditionally and drew nothing else. Preserved unchanged across the per-row-height
        // signature change (2026-09-02).
        var plan = RailSlotGeometry.PlanDeadStrip(floor: 0, startY: 0, lineHeight: 1, 0, 0, Uniform(2));

        Assert.False(plan.ShowMarker);
        Assert.Equal(1, plan.ShownStart);   // the newest (last) ending, index 1
    }

    [Fact]
    public void PlanDeadStrip_NoRoomAtAll_DrawsNothing()
    {
        // floor below startY: not even one row fits above the live stack this frame.
        var plan = RailSlotGeometry.PlanDeadStrip(floor: -5, startY: 0, lineHeight: 1, 0, 0, Uniform(3));

        Assert.False(plan.ShowMarker);
        Assert.Equal(3, plan.ShownStart);   // empty range [3, 3) - nothing drawn
    }

    [Fact]
    public void PlanDeadStrip_NoEndings_IsEmpty()
    {
        Assert.Equal(
            DeadStripPlan.Empty,
            RailSlotGeometry.PlanDeadStrip(9, 0, 1, 0, 0, Array.Empty<CombatEnding>()));
    }

    [Fact]
    public void PlanDeadStrip_AtTheLivePanelsOwnLineHeight()
    {
        // The rail's real numbers (CombatRailView.Pad=10, DeadLineHeight=13): startY=23. A 200-unit
        // floor gives room for floor((200-23)/13)+1 = 14 rows, comfortably more than a realistic
        // session's endings, so nothing is truncated.
        var plan = RailSlotGeometry.PlanDeadStrip(floor: 200, startY: 23, lineHeight: 13, 0, 0, Uniform(12));

        Assert.False(plan.ShowMarker);
        Assert.Equal(0, plan.ShownStart);
    }

    // -- PlanDeadStrip: separators (2026-09-02) --------------------------------------
    // Coverage for the strip's two grouping lines (owner: "put a 1px yellow dotted separator
    // between encounters... put a 2px white solid line between resets"). Every test in this section
    // is built to FAIL against the old uniform-height arithmetic (PlanDeadStrip(floor, startY,
    // lineHeight, totalCount)) - a test that would pass either way proves nothing, and one of this
    // method's last two bugs was exactly that kind of gap.

    [Fact]
    public void PlanDeadStrip_ASeparator_ChangesWhichRowsFit_NewestStillWins()
    {
        // Six endings, lineHeight=1, a reset boundary between indices 2 and 3 costing 5 extra. Row
        // text alone (6 x 1 = 6) would fit a budget of 9 with room to spare and show everything - the
        // uniform-arithmetic answer. With the boundary's cost counted, it does not: the two oldest
        // rows must give way, and the newest three still win.
        var endings = new[]
        {
            new CombatEnding("e0", FightOutcome.Kill, null, EncounterOrdinal: 0, ResetOrdinal: 0),
            new CombatEnding("e1", FightOutcome.Kill, null, EncounterOrdinal: 0, ResetOrdinal: 0),
            new CombatEnding("e2", FightOutcome.Kill, null, EncounterOrdinal: 0, ResetOrdinal: 0),
            // reset boundary between e2 and e3
            new CombatEnding("e3", FightOutcome.Kill, null, EncounterOrdinal: 1, ResetOrdinal: 1),
            new CombatEnding("e4", FightOutcome.Kill, null, EncounterOrdinal: 1, ResetOrdinal: 1),
            new CombatEnding("e5", FightOutcome.Kill, null, EncounterOrdinal: 1, ResetOrdinal: 1),
        };

        // budget = floor - startY + lineHeight = 8 - 0 + 1 = 9.
        var plan = RailSlotGeometry.PlanDeadStrip(
            floor: 8, startY: 0, lineHeight: 1,
            encounterSeparatorHeight: 5, resetSeparatorHeight: 5, endings);

        Assert.True(plan.ShowMarker);
        Assert.Equal(3, plan.ShownStart);     // e0,e1,e2 hidden behind the marker
        Assert.Equal(3, plan.HiddenCount);
        Assert.Equal(3, endings.Length - plan.ShownStart);   // e3,e4,e5 - the newest three - shown
    }

    [Fact]
    public void PlanDeadStrip_ASeparator_IsExactlyWhatPushesTheOldestShownRowOut()
    {
        // Three endings with an encounter boundary between index 0 and 1. Row text alone sums to 3;
        // at budget 4 that fits with room for a 1-unit separator and nothing is hidden. Shrink the
        // separator's own allowance to 0 (i.e. no boundary at all) and the identical geometry shows
        // everything; put it back to 2 and index 0 alone is pushed out from under a marker - the
        // separator's own height is the entire difference between those two outcomes.
        var endings = new[]
        {
            new CombatEnding("e0", FightOutcome.Kill, null, EncounterOrdinal: 0, ResetOrdinal: 0),
            // encounter boundary between e0 and e1
            new CombatEnding("e1", FightOutcome.Kill, null, EncounterOrdinal: 1, ResetOrdinal: 0),
            new CombatEnding("e2", FightOutcome.Kill, null, EncounterOrdinal: 1, ResetOrdinal: 0),
        };

        // budget = floor - startY + lineHeight = 3 - 0 + 1 = 4.
        var withSeparator = RailSlotGeometry.PlanDeadStrip(
            floor: 3, startY: 0, lineHeight: 1, encounterSeparatorHeight: 2, resetSeparatorHeight: 2, endings);
        var withoutSeparator = RailSlotGeometry.PlanDeadStrip(
            floor: 3, startY: 0, lineHeight: 1, encounterSeparatorHeight: 0, resetSeparatorHeight: 0, endings);

        Assert.False(withoutSeparator.ShowMarker);
        Assert.Equal(0, withoutSeparator.ShownStart);   // everything fits with no boundary cost

        Assert.True(withSeparator.ShowMarker);
        Assert.Equal(1, withSeparator.ShownStart);      // e0 pushed behind the marker
        Assert.Equal(1, withSeparator.HiddenCount);
    }

    [Fact]
    public void SeparatorBetween_AResetBoundary_ReportsResetOnly_NeverEncounterToo()
    {
        // A reset always ends the encounter too (both ordinals differ here), and the owner's ask
        // draws ONE line at a reset boundary, never both stacked.
        var older = new CombatEnding("a", FightOutcome.Kill, null, EncounterOrdinal: 4, ResetOrdinal: 1);
        var newer = new CombatEnding("b", FightOutcome.Kill, null, EncounterOrdinal: 5, ResetOrdinal: 2);

        Assert.Equal(DeadStripSeparatorKind.Reset, RailSlotGeometry.SeparatorBetween(older, newer));
    }

    [Fact]
    public void PlanDeadStrip_AResetBoundary_ChargesOnlyTheResetAllowance_NeverBothStacked()
    {
        // Two endings whose encounter AND reset ordinals both differ. If the arithmetic mistakenly
        // stacked both allowances at this boundary, the budget below would be blown and truncation
        // would kick in; charging only the (much smaller) reset allowance keeps it at exactly
        // capacity. The encounter allowance is deliberately huge so a regression that sums both
        // fails loudly rather than by a hard-to-notice single unit.
        var endings = new[]
        {
            new CombatEnding("a", FightOutcome.Kill, null, EncounterOrdinal: 0, ResetOrdinal: 0),
            new CombatEnding("b", FightOutcome.Kill, null, EncounterOrdinal: 1, ResetOrdinal: 1),
        };

        // budget = floor - startY + lineHeight = 5 - 0 + 1 = 6 = 2 rows (2x1) + the reset allowance (4).
        var plan = RailSlotGeometry.PlanDeadStrip(
            floor: 5, startY: 0, lineHeight: 1,
            encounterSeparatorHeight: 100, resetSeparatorHeight: 4, endings);

        Assert.False(plan.ShowMarker);
        Assert.Equal(0, plan.ShownStart);
    }
}
