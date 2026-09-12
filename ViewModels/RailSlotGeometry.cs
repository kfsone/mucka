namespace Mucka.ViewModels;

/// <summary>
/// A rectangle on the Combat Rail in device-independent units, measured from the TOP-LEFT of the
/// panel's content box - the same origin a sibling element laid out at the panel Grid's top-left
/// sees, so a Composition <c>Translation</c> can be set straight from <see cref="Left"/>/
/// <see cref="Top"/> with no further arithmetic at the call site.
///
/// <para>Top-left rather than the bottom-anchored tuples <c>CombatRailView.TickTrackDp</c> and
/// <c>FleePillDp</c> return. Those two describe elements pinned to the panel's bottom edge by MAUI
/// layout (<c>VerticalOptions="End"</c> plus a bottom margin), so a bottom offset is what their
/// consumer actually needs. A float is placed by the compositor instead of by layout, and the
/// compositor's own coordinate space starts at the element's arranged top-left.</para>
/// </summary>
public readonly record struct RailRect(double Left, double Top, double Width, double Height)
{
    public double CenterX => Left + (Width / 2.0);
    public double CenterY => Top + (Height / 2.0);
}

/// <summary>
/// The dead strip's layout for one frame - see <see cref="RailSlotGeometry.PlanDeadStrip"/>.
/// </summary>
/// <param name="ShownStart">Index of the first (oldest of the shown) ending to draw; everything
/// before this index in the caller's chronological list is hidden.</param>
/// <param name="ShowMarker">Whether a "+N earlier" row is drawn above the shown endings.</param>
/// <param name="HiddenCount">How many endings are not drawn - the marker's own count when
/// <see cref="ShowMarker"/> is true, and otherwise informational only (nothing on screen reports it;
/// see <see cref="RailSlotGeometry.PlanDeadStrip"/>'s capacity-1 case).</param>
public readonly record struct DeadStripPlan(int ShownStart, bool ShowMarker, int HiddenCount)
{
    public static readonly DeadStripPlan Empty = new(0, false, 0);
}

/// <summary>
/// What kind of grouping boundary, if any, sits between two chronologically adjacent
/// <see cref="CombatEnding"/>s in the dead strip - see <see cref="RailSlotGeometry.SeparatorBetween"/>.
/// The one place this is decided, shared by the height arithmetic (<see cref="RailSlotGeometry.PlanDeadStrip"/>)
/// and the actual drawing (<c>CombatRailView.DrawDeadStrip</c>), so the two can never disagree about
/// where a line falls.
/// </summary>
public enum DeadStripSeparatorKind
{
    /// <summary>Same encounter, same reset cycle - no line.</summary>
    None,
    /// <summary>Different <see cref="CombatEnding.EncounterOrdinal"/>, same
    /// <see cref="CombatEnding.ResetOrdinal"/> - the 1px yellow dotted line.</summary>
    Encounter,
    /// <summary>Different <see cref="CombatEnding.ResetOrdinal"/> - the 2px white solid line.
    /// Takes priority over <see cref="Encounter"/> even when the encounter ordinal also differs (it
    /// always does - a reset ends the encounter too): a reset boundary draws ONE line, never both
    /// stacked.</summary>
    Reset,
}

/// <summary>
/// The subset of <c>CombatRailView</c>'s layout constants the opponent-slot and stamina-seal
/// geometry is derived from, in the canvas's own fixed logical space.
///
/// <para>Passed in rather than restated here because the canvas owns those numbers and there must
/// be exactly one copy of them - the same reasoning that put <c>RailWidth</c> itself in
/// <c>CombatRailResize</c> instead of in two places. This record is what lets the arithmetic live
/// in a MAUI-free file (so mudsharp.Tests can link it) without the constants having to move out of
/// the class that draws with them.</para>
/// </summary>
public readonly record struct RailSlotMetrics(
    double RailWidth,
    double Pad,
    double SlotHeight,
    double SlotGap,
    // The gap the canvas leaves between the bottom row's top edge and the lowest opponent slot.
    double SlotsGap,
    // The whole bottom block: the player's tile PLUS the encounter table under it. This is what the
    // opponent stack is measured against, so it is the figure that decides how many slots fit.
    double BottomRowHeight,
    double TickRowHeight,
    // Just the player's tile, which is shorter than the block above. Only PlayerTileDp needs it -
    // a float anchored to the player belongs on the tile, not over the table beneath it.
    double PlayerTileHeight,
    int MaxSlots);

/// <summary>
/// Where the rail's opponent slots and its stamina seal actually land, in dp.
///
/// <para><b>This is a mirror of the paint path's own arithmetic, and it has to stay one.</b>
/// <c>CombatRailView.OnPaintSurface</c> lays the panel out from the BOTTOM edge upward in a fixed
/// 376-unit logical space and scales that space to whatever width the panel is given; a consumer
/// working in real dp therefore needs the same scale factor applied, exactly as
/// <c>TickTrackDp</c>/<c>FleePillDp</c> already do for the two Composition siblings. The
/// difference here is that the slot layout also has a CAPACITY rule (how many slots the available
/// height fits, and the one slot surrendered to the overflow row when the roster is longer than
/// that), so the canvas calls <see cref="ShownSlots"/> for its own draw loop rather than keeping a
/// second copy - a float placed over "slot 5" when the canvas drew only four would be pointing at
/// blank panel.</para>
///
/// <para>Pure, allocation-free and MAUI-independent, so it is linked into mudsharp.Tests
/// (RailSlotGeometryTests) the same way ClogRenderGate and CombatComposition are.</para>
/// </summary>
public static class RailSlotGeometry
{
    /// <summary>The logical y the lowest opponent slot's BOTTOM edge sits on, worked up from the
    /// panel's bottom edge through the tick row and the bottom row - the same chain
    /// <c>OnPaintSurface</c> walks.</summary>
    public static double SlotsBottom(in RailSlotMetrics m, double logicalHeight)
        => logicalHeight - m.Pad - m.TickRowHeight - m.BottomRowHeight - m.SlotsGap;

    /// <summary>
    /// How many LIVE opponent slots the height actually available can hold, clamped to the rail's own
    /// slot cap. At least one, even in a window too short for it, matching the canvas.
    ///
    /// <para>Live only. Resolved creatures left the vertical slots entirely and live in a top-anchored
    /// strip of their own (see <see cref="DeadStripBottom"/>). The two regions grow toward the gap
    /// between them and neither displaces the other: capacity here is computed against the WHOLE
    /// available height and never against what the strip has left over, so a death can never move a
    /// live slot.</para></summary>
    public static int Capacity(in RailSlotMetrics m, double logicalHeight)
    {
        var available = SlotsBottom(m, logicalHeight) - m.Pad;
        // N slots cost N*SlotHeight + (N-1)*SlotGap - there is no trailing gap below the topmost
        // slot, since nothing is drawn above it. Solving that inequality for N gives
        // (available + SlotGap) / (SlotHeight + SlotGap), not available / (SlotHeight + SlotGap) -
        // the latter charges a gap the top slot never draws and undercounts by one right at each
        // slot-height boundary.
        return Math.Clamp((int)Math.Floor((available + m.SlotGap) / (m.SlotHeight + m.SlotGap)), 1, m.MaxSlots);
    }

    /// <summary>
    /// How many opponents get a slot of their own. One slot is surrendered to the overflow row as
    /// soon as the opposition is longer than the capacity, so the answer is not simply
    /// min(count, capacity) - and the ones past this count are reported in the overflow row, which is
    /// not a pane anything can float over.
    ///
    /// <para><paramref name="liveCount"/> is the count of LIVE opponents - everything still fighting,
    /// including any past the roster's own row cap, and NOT the resolved ones. Resolved creatures are
    /// drawn in the dead strip and take no slot. Counting the published row list instead would hide
    /// the tail whenever the panel is tall enough to fit the whole capped list - true of every
    /// ordinary window.</para></summary>
    public static int ShownSlots(in RailSlotMetrics m, double logicalHeight, int liveCount)
    {
        if (liveCount <= 0)
            return 0;
        var capacity = Capacity(m, logicalHeight);
        return liveCount > capacity ? capacity - 1 : Math.Min(liveCount, capacity);
    }

    /// <summary>
    /// The logical y the top-anchored dead strip may run down to before it would collide with the
    /// live stack, given how many live slots are actually drawn.
    ///
    /// <para><b>Which region yields, and why it is this one.</b> The live stack is bottom-anchored and
    /// sized from the full height; the dead strip is top-anchored and gets whatever is left above it.
    /// So a creature dying never moves a live slot, and a pack big enough to squeeze the two together
    /// squeezes the DEAD, which is the region where movement is acceptable.</para>
    /// </summary>
    public static double DeadStripBottom(in RailSlotMetrics m, double logicalHeight, double liveStackHeight)
        => SlotsBottom(m, logicalHeight) - liveStackHeight - m.SlotGap;

    /// <summary>
    /// Which grouping line, if any, separates two chronologically adjacent endings - the single
    /// source of truth both <see cref="PlanDeadStrip"/> (which needs the line's HEIGHT to plan
    /// truncation) and <c>CombatRailView.DrawDeadStrip</c> (which needs to know WHAT to draw) read,
    /// so the two can never disagree about where a line falls.
    ///
    /// <para>Reset checked first and unconditionally wins: a reset always ends the encounter too
    /// (<see cref="CombatEnding.EncounterOrdinal"/> differs whenever <see cref="CombatEnding.ResetOrdinal"/>
    /// does), and a reset boundary draws ONE line, never both stacked.</para>
    /// </summary>
    public static DeadStripSeparatorKind SeparatorBetween(in CombatEnding older, in CombatEnding newer)
    {
        if (newer.ResetOrdinal != older.ResetOrdinal)
            return DeadStripSeparatorKind.Reset;
        if (newer.EncounterOrdinal != older.EncounterOrdinal)
            return DeadStripSeparatorKind.Encounter;
        return DeadStripSeparatorKind.None;
    }

    /// <summary>
    /// How many of <paramref name="history"/>'s session-history rows are drawn this frame, and where
    /// the truncation marker (if any) goes - lifted out here so mudsharp.Tests can pin it directly:
    /// <c>CombatRailView</c> is an <c>SKCanvasView</c> and unreachable from a unit test.
    ///
    /// <para><b>Per-row heights, not a uniform pitch.</b> The strip has two grouping separators
    /// (1px dotted yellow between encounters, 2px solid white between resets), which makes
    /// row pitch non-uniform - a boundary row costs its own text line PLUS the separator's own vertical
    /// allowance. The separator between ending <c>i</c> and the next, more recent, ending
    /// <c>i + 1</c> is costed as part of row <c>i</c> - the OLDER of the pair - rather than row
    /// <c>i + 1</c>. That attribution is what lets a plain backward walk from the newest ending get
    /// "never draw a separator above the oldest SHOWN row" for free: the separator above whichever row
    /// ends up topmost is the one between it and a row that is NOT shown, so it is simply never added
    /// to the running total - no special-casing needed for whichever row truncation happens to land
    /// on.</para>
    /// </summary>
    /// <param name="floor">The lowest logical y a row's baseline may sit on - <see cref="DeadStripBottom"/>
    /// for the live stack's own bound.</param>
    /// <param name="startY">The first (topmost) row's baseline.</param>
    /// <param name="lineHeight">One row's own text height - <c>CombatRailView.DeadLineHeight</c>.</param>
    /// <param name="encounterSeparatorHeight">The 1px dotted line's own vertical allowance -
    /// <c>CombatRailView.DeadStripEncounterSeparatorHeight</c>.</param>
    /// <param name="resetSeparatorHeight">The 2px solid line's own vertical allowance -
    /// <c>CombatRailView.DeadStripResetSeparatorHeight</c>.</param>
    /// <param name="history">The endings on file to show, chronological (oldest first).</param>
    public static DeadStripPlan PlanDeadStrip(
        double floor, double startY, double lineHeight,
        double encounterSeparatorHeight, double resetSeparatorHeight,
        IReadOnlyList<CombatEnding> history)
    {
        var n = history.Count;
        if (n <= 0 || lineHeight <= 0)
            return DeadStripPlan.Empty;

        // The row's own text plus, when it is not the newest, the separator between it and the NEXT
        // (more recent) row - see this method's own remarks on why that attribution (rather than the
        // separator ABOVE each row) is what makes the backward walk below correct with no
        // special-casing.
        double RowCost(int i)
        {
            if (i == n - 1)
                return lineHeight;
            var kind = SeparatorBetween(history[i], history[i + 1]);
            return lineHeight + kind switch
            {
                DeadStripSeparatorKind.Reset => resetSeparatorHeight,
                DeadStripSeparatorKind.Encounter => encounterSeparatorHeight,
                _ => 0,
            };
        }

        // budget == the total vertical space from the strip's top pad down to the floor
        // (startY is always Pad + lineHeight - see CombatRailView.DrawDeadStrip).
        var budget = floor - startY + lineHeight;
        if (budget < lineHeight)
            // Not even the newest ending fits above the live stack this frame - draw nothing rather
            // than crowd it, the same "the DEAD gives" rule DeadStripBottom is built on.
            return new DeadStripPlan(n, false, n);

        var total = 0.0;
        for (var i = 0; i < n; i++)
            total += RowCost(i);
        if (total <= budget)
            // Everything fits, separators included - no marker, nothing hidden.
            return new DeadStripPlan(0, false, 0);

        // Truncation: reserve one row's worth for the "+N earlier" marker (a flat text line - no
        // separator of its own either side of it), then walk backward from the newest ending,
        // including rows while the remaining budget allows.
        var markerBudget = budget - lineHeight;
        var shownStart = n;
        var cost = 0.0;
        for (var i = n - 1; i >= 0; i--)
        {
            var rowCost = RowCost(i);
            if (cost + rowCost > markerBudget)
                break;
            cost += rowCost;
            shownStart = i;
        }

        if (shownStart == n)
            // Nothing fit even after giving up the marker's own row - the capacity-1 rule: a marker
            // with zero endings under it is a NET LOSS of information versus the newest ending on its
            // own (RowCost(n-1) == lineHeight <= budget, guaranteed by the guard above), so the row
            // goes to the newest ending instead of the marker.
            return new DeadStripPlan(n - 1, false, n - 1);

        return new DeadStripPlan(shownStart, true, shownStart);
    }

    /// <summary>How tall the bottom-anchored live stack actually is, including a compact overflow row
    /// when one is drawn. The dead strip is given whatever is above it.</summary>
    public static double LiveStackHeight(
        in RailSlotMetrics m, int shownLiveSlots, double overflowRowHeight)
    {
        var height = shownLiveSlots * m.SlotHeight;
        var gaps = Math.Max(0, shownLiveSlots - 1);
        if (overflowRowHeight > 0)
        {
            height += overflowRowHeight;
            gaps = shownLiveSlots;
        }
        return height + (gaps * m.SlotGap);
    }

    /// <summary>The logical y of slot <paramref name="index"/>'s top edge. Index 0 is the BOTTOM
    /// slot - the rail fills upward from the bottom edge because that is where the player's gaze
    /// rests.</summary>
    public static double SlotTop(in RailSlotMetrics m, double logicalHeight, int index)
        => SlotsBottom(m, logicalHeight) - m.SlotHeight - (index * (m.SlotHeight + m.SlotGap));

    /// <summary>
    /// The rectangle roster row <paramref name="rosterIndex"/> is drawn in, or null when that row
    /// has no slot of its own - either the panel has not been measured yet, or the row is in the
    /// overflow tail. Null is a real answer and callers must honour it: floating a hit over a
    /// creature the panel is not drawing would put the number on top of an unrelated slot.
    /// </summary>
    public static RailRect? OpponentSlotDp(
        in RailSlotMetrics m, double panelWidthDp, double panelHeightDp,
        int rosterIndex, int liveCount)
    {
        if (rosterIndex < 0 || panelWidthDp <= 0 || panelHeightDp <= 0 || m.RailWidth <= 0)
            return null;

        var k = panelWidthDp / m.RailWidth;
        var logicalHeight = panelHeightDp / k;
        if (rosterIndex >= ShownSlots(m, logicalHeight, liveCount))
            return null;

        var top = SlotTop(m, logicalHeight, rosterIndex);
        if (top < 0)
            return null;

        return new RailRect(
            m.Pad * k,
            top * k,
            (m.RailWidth - (m.Pad * 2.0)) * k,
            m.SlotHeight * k);
    }

    /// <summary>
    /// The PLAYER'S TILE - what a player-anchored damage float centres itself on.
    ///
    /// <para>Centres on the whole tile, not a sub-region within it: the float represents "this
    /// happened to you", not "this happened to that widget".</para>
    ///
    /// <para>The tile sits at the very BOTTOM of the panel - the tick gauge and the encounter table
    /// are above it, between the player and the creatures - so it is one pad up from the bottom edge
    /// and nothing else enters the chain. <c>m.BottomRowHeight</c> still describes the whole block for
    /// the opponent-capacity arithmetic and is deliberately NOT used here.</para>
    /// </summary>
    public static RailRect PlayerTileDp(in RailSlotMetrics m, double panelWidthDp, double panelHeightDp)
    {
        if (panelWidthDp <= 0 || m.RailWidth <= 0)
            return default;

        var k = panelWidthDp / m.RailWidth;
        var logicalHeight = panelHeightDp / k;
        var top = logicalHeight - m.Pad - m.PlayerTileHeight;
        return new RailRect(
            m.Pad * k, top * k, (m.RailWidth - (m.Pad * 2.0)) * k, m.PlayerTileHeight * k);
    }
}
