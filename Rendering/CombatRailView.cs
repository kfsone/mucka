using MudSharp.Combat;
using Mucka.Terminal;
using Mucka.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;
using Mucka.Combat;

namespace Mucka.Rendering;

/// <summary>
/// The Combat Rail's render surface. Built to docs/combat-rail-spec.md.
///
/// <para><b>Bottom-focused.</b> The player's gaze rests at the bottom-center of the window, on
/// the input box and the newest game text. So live content is laid out from the BOTTOM edge
/// upward and empty space collects at the top. The top of a full-height right-edge panel is
/// about the longest saccade available in the window; nothing that matters mid-fight belongs
/// there.</para>
///
/// <para><b>Nothing moves.</b> Slot geometry is computed once per size change and held. An
/// indicator changes state in place - lit, unlit, colour - the way a warning lamp does. Live
/// elements never resize or reflow, because a critical readout that shifts under the eye has
/// to be re-found on every glance.</para>
///
/// <para><b>Invariant #1 - this class never animates.</b> It repaints only when
/// <see cref="Live"/> becomes different state. There is no timer here and there must never be
/// one: on WinUI <c>SKXamlCanvas</c> paints ON the UI thread, so a repeating repaint competes
/// directly with typing. Continuous motion (tick sweep, pulse, glow) belongs to the
/// Composition layer behind this canvas - see <c>PulseLayer</c>.</para>
///
/// <para><b>Invariant #0 - this surface cannot take focus.</b> No gesture recognizers, mounted
/// <c>InputTransparent</c>. There is nothing to click, by construction.</para>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The paints and fonts live as long as the view does, and nothing disposes a MAUI view; a Dispose here would never be called.")]
public sealed class CombatRailView : SKCanvasView
{
    // ---- Geometry (logical units; the canvas is scaled to these at paint time) -------------
    // Internal (not private): GamePage.xaml.cs sizes the host Border's WidthRequest off this value so
    // the rail's design space maps 1:1 onto the Border's dp content area (WidthRequest minus
    // its own stroke inset) at every DPI - a panel narrower than this by more than the Border's own
    // stroke inset draws every glyph at the wrong size regardless of DPI. That is distinct from
    // OnPaintSurface's own `scale` below, which still carries the DPI factor on top of this:
    // e.Info.Width is the SKCanvasView surface in PHYSICAL pixels (IgnorePixelScaling is not set
    // anywhere in this repo), so scale == 1.0 only at 100% OS scaling - at 150% it is 1.5, etc.
    //
    // Derived from CombatRailResize.CombatPanelContentWidthDp (Mucka.Combat), not the other way
    // round: that project is plain net10.0 with no dependency on this one, so it cannot reference
    // this constant - this project (Mucka, Windows-only) DOES already depend on Mucka.Combat, so
    // the dependency has to run this direction for there to be exactly one source of truth instead of
    // two hand-kept copies. GamePage.xaml.cs's window-resize arithmetic reads the SAME
    // CombatRailResize constant, so the two sides cannot drift apart.
    /// <summary>The design width for a given stats setting. Not a constant any more: the stat rows
    /// and the spark can be switched off and the panel narrows when they are, so every width below is
    /// derived from this rather than baked in. <see cref="CombatRailResize"/> owns both numbers, so
    /// the renderer, the Border's WidthRequest and the window arithmetic cannot disagree.</summary>
    internal static float RailWidthFor(bool showStats) => (float)CombatRailResize.ContentWidthDp(showStats);
    private const float Pad = 10f;

    /// <summary>Whether the two stat rows and the exchange spark are drawn. False also narrows the
    /// panel - the host re-sizes the Border and the window off the same setting.</summary>
    public bool ShowStats
    {
        get => _showStats;
        set
        {
            if (_showStats == value) return;
            _showStats = value;
            InvalidateSurface();
        }
    }
    private bool _showStats = true;

    private float RailWidth => RailWidthFor(_showStats);
    private float Content => RailWidth - (Pad * 2);

    /// <summary>
    /// A LIVE opponent's slot, sized so five live slots still fit the shortest realistic rail.
    /// <c>Mucka.Combat.RailSlotGeometry.Capacity</c> owns that arithmetic - it is the one place the
    /// available height is divided by the slot pitch, and it counts no trailing gap below the top
    /// slot. Five covers 99.45% of encounters in the clog corpus (1268 of 1275), so the sizing is
    /// bought against the case that actually happens rather than against the one that happened once.
    /// </summary>
    private const float SlotHeight = 76f;
    private const float SlotGap = 5f;

    // ---- the tile ----
    //
    // The tile draws a full-width bar rather than a left-side ring seal, so all four text lines get
    // the tile's whole width. The tile height stays 76: four lines and a bar land inside it.
    //
    // Every offset below is from the tile's own top. They are constants rather than a measured flow
    // because a line that moves when its neighbour's text grows is the re-find-it-on-every-glance
    // failure the whole panel is laid out to avoid.
    private const float TileNameBaseline = 14f;
    private const float TileHealthBaseline = 29f;
    private const float TileBarTop = 34f;
    private const float TileBarFillHeight = 6f;
    private const float TileBarLaneTop = 41f;
    private const float TileBarLaneHeight = 2f;
    /// <summary>The tile's two stat rows, named by POSITION rather than by content - deliberately.
    /// Which direction each row carries depends on whose badge it is (see DrawStatRow: the upper row
    /// is always what the subject is TAKING), so a name like "DealtBaseline" would be true on an
    /// opponent's tile and a lie on the player's.</summary>
    private const float TileUpperBaseline = 56f;
    private const float TileLowerBaseline = 71f;

    /// <summary>
    /// The spark's own baseline, and the whole reason it is a constant rather than an offset from a
    /// text baseline: it sits below the bar's band (34-43) with the full bar height clear above it,
    /// and the tile's own bottom edge clear below. There is no clip anywhere on this canvas; the
    /// geometry has to be right rather than contained.
    /// </summary>
    private const float TileSparkCentre = 59f;

    /// <summary>The same, on the player's tile, which additionally has the magic strip at 45-47 to
    /// clear.</summary>
    private const float PlayerSparkCentre = 64f;

    /// <summary>The magic line's own strip, drawn on the player's tile only, immediately under the
    /// stamina bar. Two units high with notches at the quarters - it answers "roughly how much is
    /// left" and nothing more, because a caster who needs the exact figure has it in the status strip
    /// at the top of the window.</summary>
    private const float PlayerMagTop = 45f;
    private const float PlayerMagHeight = 2f;
    private const float PlayerUpperBaseline = 61f;
    private const float PlayerLowerBaseline = 76f;
    private const float PlayerTileHeight = 81f;

    // The stat row's four fixed columns and the spark that follows them. Fixed centres, so the two
    // rows of a tile line up with each other and with every other tile's.
    private const float StatMarkWidth = 14f;
    private const float StatTotalWidth = 88f;
    private const float StatShapeWidth = 96f;
    private const float StatDptWidth = 52f;
    private const float StatMarkLeft = Pad;
    private const float StatTotalLeft = StatMarkLeft + StatMarkWidth;
    private const float StatShapeLeft = StatTotalLeft + StatTotalWidth;
    private const float StatDptLeft = StatShapeLeft + StatShapeWidth;
    private const float SparkLeft = StatDptLeft + StatDptWidth + 4f;
    private float SparkWidth => Pad + Content - SparkLeft;

    // Inside the blow-shape group: three slots and two separators, all fixed. The outer two figures
    // are drawn a size down, which is exactly why the slots have to be positioned rather than laid
    // out by measuring - mixed sizes would otherwise shift the group's centre between the two rows.
    private const float ShapeLowWidth = 30f;
    private const float ShapeSepWidth = 6f;
    private const float ShapeHighWidth = 24f;
    private const float ShapeLowLeft = StatShapeLeft;
    private const float ShapeSep1Left = ShapeLowLeft + ShapeLowWidth;
    private const float ShapeHighLeft = ShapeSep1Left + ShapeSepWidth;
    private const float ShapeSep2Left = ShapeHighLeft + ShapeHighWidth;
    private const float ShapeMeanLeft = ShapeSep2Left + ShapeSepWidth;

    /// <summary>One swing's slot in the spark, and the width of the mark in it. How TALL a mark is
    /// belongs to <see cref="RailReadout.SparkBarHeight"/>, with the cap it saturates at.</summary>
    private const float SparkPitch = 5.5f;
    private const float SparkBarWidth = 3f;

    /// <summary>
    /// One ending's row in the top-anchored dead strip. The strip is top-anchored and grows downward:
    /// new rows append at the bottom, one row per ending, ordered chronologically by
    /// <c>EndedUtc</c> (see <see cref="Mucka.Combat.CombatEndingOrder"/>) so two rows never swap
    /// position after the fact. The live stack stays bottom-anchored and never moves.
    ///
    /// <para>Each row carries the exchange summary and the kill award as well as the name and the
    /// outcome, across two lines. Named "line" because <c>RailSlotGeometry.PlanDeadStrip</c> budgets in
    /// these units and does not care what is drawn inside one - it is the strip's row PITCH, and the
    /// two sub-lines are <see cref="DeadSubLineHeight"/> apart within it.</para>
    ///
    /// <para>See <see cref="DrawDeadStrip"/> and <c>SidePanelViewModel.BuildDeadStripHistory</c>.</para>
    /// </summary>
    private const float DeadLineHeight = 24f;

    /// <summary>
    /// The dead strip's two grouping separators' own vertical allowance. A 1px yellow dotted separator
    /// marks a boundary between encounters; a 2px white solid line marks a boundary between resets.
    /// This is the EXTRA space reserved between two rows for the line and its clearance, not the stroke
    /// width itself: <see cref="DeadLineHeight"/>'s
    /// own ~3f of slack around a 10f font is not enough room for even a 1px stroke to sit in without
    /// touching the glyphs on either side of it, so a separator gets its own budget rather than
    /// borrowing that gap. Equal for both kinds on purpose - the allowance's job is clearance, not
    /// visual weight, which the stroke width and dash/solid style already carry.
    ///
    /// <para>Read by <see cref="Mucka.Combat.RailSlotGeometry.PlanDeadStrip"/> too, as the two
    /// height parameters passed in from here - the single source of truth for what counts as a
    /// boundary is <see cref="Mucka.Combat.RailSlotGeometry.SeparatorBetween"/>, shared by both
    /// this drawing method and that arithmetic so they can never disagree about where a line
    /// falls.</para>
    /// </summary>
    private const float DeadStripEncounterSeparatorHeight = 6f;
    private const float DeadStripResetSeparatorHeight = 6f;

    /// <summary>The encounter table above the tick gauge: a heading row that is RESERVED always and
    /// painted only on hover, and the value row under it. Reserved rather than inserted: headings that
    /// appeared on hover would push the whole rail down every time the pointer crossed it.</summary>
    private const float EncounterHeadingHeight = 13f;
    private const float EncounterValueHeight = 15f;
    private const float EncounterRowHeight = EncounterHeadingHeight + EncounterValueHeight;
    private const float EncounterRowGap = 4f;

    /// <summary>The whole bottom block: the player's own tile plus the encounter table under it. Kept
    /// as one figure because RailSlotMetrics measures the opponent stack against it, and GamePage's
    /// float overlay measures against the same metrics - splitting it into two constants here would
    /// leave that arithmetic reading only half the block.</summary>
    private const float BottomRowHeight = PlayerTileHeight + EncounterRowGap + EncounterRowHeight;
    private const float TickRowHeight = 30f;

    // ---- the bottom-up chain ----
    //
    // Order from the panel's bottom edge upward: the PLAYER'S TILE, then the tick gauge, then the
    // encounter table, then the opponent stack.
    //
    // Everything below is an offset of a row's BOTTOM edge above the panel's, so it can be read in the
    // same direction the layout is built. The total is Pad + tick + BottomRowHeight = 153.
    //
    // The flee pill rides the tick gauge and carries the stamina reading and what leaving costs at it,
    // so it sits directly against the player's own stamina bar - the numbers the leaving decision turns
    // on, next to the gauge they came off.
    private const float PlayerTileBottomOffset = Pad;
    private const float TickRowBottomOffset = PlayerTileBottomOffset + PlayerTileHeight;
    private const float EncounterBottomOffset = TickRowBottomOffset + TickRowHeight + EncounterRowGap;
    private const float TickTrackHeight = 5f;

    /// <summary>Width reserved at the RIGHT end of the tick row for the metronome toggle. Taken out
    /// of the track rather than laid beside it, so the row's overall geometry is unchanged and the
    /// toggle occupies space that is reserved whether or not it is switched on (spec rule 3).</summary>
    private const float MetronomeReserve = 26f;

    private float TickTrackWidth => Content - MetronomeReserve;

    /// <summary>The tempo border's inset inside an opponent slot and around the player's device, and
    /// its stroke. Both fixed: the border is a frame on a rectangle whose bounds never change, so
    /// nothing here can reflow when a fight's rate does (only the DASH changes).</summary>
    private const float FrameStroke = 1.2f;

    /// <summary>The flee pill, at the top of the tick row above the player's tile. Reserved whether or
    /// not the pill is drawn (rule 3); nothing else in the row moves when it lights up.</summary>
    private const float PillHeight = 20f;
    /// <summary>4, not 5: the pill sits inside the tick row and the row's contents are dropped
    /// <see cref="TickRowDrop"/>, so 5 + 6 + 20 would put its lower edge one unit past the row, into
    /// the panel's bottom padding.</summary>
    private const float PillTopInset = 4f;

    /// <summary>The pill's fixed width: the chip carries a stamina figure and a price as well as the
    /// word and the key, which is what rules out anything much narrower.
    ///
    /// <para>It is the FLOOR on how narrow the panel can be. Centred in <see cref="Content"/>, which
    /// at the stats-off width is 276 (296 less the two <see cref="Pad"/>s), leaving the pill 28dp a
    /// side. No other SINGLE fixed width in the rail is this large - the stat row's columns come to
    /// more, but they are only drawn when stats are on, which is never when the panel is narrow.
    /// Narrow it further and this is what runs out first.</para>
    ///
    /// <para>Centred over the tick gauge. It covers the gauge only while it is showing, which is the
    /// point: between fights the tick bar is unobstructed, and when there is a reason to leave the
    /// alarm sits on top of the one instrument the eye is already returning to every two seconds.</para>
    /// </summary>
    private const float PillWidth = 220f;
    private float PillLeft => Pad + ((Content - PillWidth) / 2f);

    /// <summary>The pill is the floating dreamword chip's treatment in reds - a FILLED chip with a
    /// bright 2dp border and white bold text, not the thin dim outline the rest of this panel favours.
    /// Geometry matched to that chip: corner radius 10, 2dp stroke, the same roughly-10dp horizontal
    /// breathing room its Padding gives.
    ///
    /// <para>Note these are the panel's only colours NOT derived from <c>TerminalTheme.Palette</c>
    /// (section 11) - <c>#FF0000</c> is a pure red the Campbell palette does not contain (its bright red
    /// is <c>#E74856</c>). Recorded as an explicit override rather than left looking like drift.</para></summary>
    private static readonly SKColor PillFill = new(0x3E, 0x18, 0x1C);
    private static readonly SKColor PillEdge = new(0xFF, 0x00, 0x00);
    private static readonly SKColor PillText = new(0xFF, 0xFF, 0xFF);
    private const float PillStroke = 2f;
    private const float PillRadius = 10f;

    /// <summary>How far the whole chip is knocked back in the quiet state. Rule 3 lists intensity as a
    /// legitimate way for an indicator to change state in place, which is what lets the chip keep one
    /// shape across all three visible states: the escalation is brightness and motion, never geometry.
    /// Without it a full-strength red chip would be shouting from the moment it appears, and an alarm
    /// that is loudest before anything has happened is the one the spec warns gets ignored later.</summary>
    private const float PillQuietWash = 0.55f;

    /// <summary>Upper bound on opponent slots. The measured maximum simultaneously-engaged opponents
    /// is 13, one encounter out of 1275 in the clog corpus.
    ///
    /// <para>The cap deliberately does NOT chase that measured peak: live opponents already exceed
    /// MaxSlots by a comfortable margin in the worst observed cases, and the overflow row
    /// (<see cref="DrawOverflowRow"/>) exists precisely to carry whatever that excess is, live or dead
    /// alike - see its own remarks for the pack-of-fourteen case that forced it to count properly.
    /// Raising MaxSlots to match whatever the corpus says today would only have to be redone the next
    /// time it grows again; the overflow row is what makes that unnecessary.</para></summary>
    private const int MaxSlots = 8;

    // ---- Palette: Campbell by index, so the rail and the terminal never drift -------------
    private static readonly SKColor Ink = TerminalTheme.Palette[7];
    private static readonly SKColor InkBright = TerminalTheme.Palette[15];
    private static readonly SKColor InkDim = TerminalTheme.Palette[8];
    private static readonly SKColor Hostile = TerminalTheme.Palette[9];

    /// <summary>What a `diagnose` figure dims to once it is older than
    /// <see cref="RosterRow.StaminaReadStaleAfterSeconds"/>: 0.7 of full. Alpha rather than a step down
    /// the palette, because the figure is still the brightest thing on its row when fresh and a palette
    /// step would land it on the tone the wound phrase beside it already uses.</summary>
    private const byte StaleReadAlpha = 0xB2;
    private static readonly SKColor Caution = TerminalTheme.Palette[11];
    /// <summary>Score gained on a dead-strip row where the gain is the whole story - a creature that
    /// broke off and paid for the privilege. The SAME green <see cref="SurvivalTone"/> gives a
    /// Commanding fight, so the panel keeps one "this went your way" hue rather than learning a
    /// second.</summary>
    private static readonly SKColor Reward = TerminalTheme.Palette[10];
    /// <summary>How far up its ladder an opponent still is. ONE hue for every creature and every rung:
    /// the fill's length carries the position and the notches carry the steps, and colouring the fill by
    /// the rung would say the same thing twice in a channel the player would then have to learn. The
    /// player's own bar IS ratio-coloured, because it agrees with the status strip at the top of the
    /// window about a number the game prints them; nothing prints an opponent's.</summary>
    private static readonly SKColor Vitality = TerminalTheme.Palette[6];   // #3A96DD
    private static readonly SKColor VitalityStale = new(0x3d, 0x55, 0x59);

    /// <summary>The bar's unfilled track - the whole ladder, unclimbed. Same value the player's own
    /// bar uses for its track, so the two read as the same instrument.</summary>
    private static readonly SKColor SealTrack = Dim(TerminalTheme.Palette[8], 0.30f);

    // The spent slice's fade cannot be a compile-time constant, so it is computed at paint time in
    // DrawPlayerBar from Mucka.Combat.TickStaminaLoss.FadeFactor - see that method's own remarks for the
    // peak (down to 0.18) and the one-tick linear fade.

    /// <summary>The track drawn for a species nothing is known about. Deliberately bright enough to
    /// read (3.8:1 on the panel's #101618) rather than the near-invisible greys the rest of the
    /// panel's chrome uses: this is the MOST dangerous state on the slot and it may not be the
    /// quietest thing in it.</summary>
    private static readonly SKColor SealUnknown = Dim(TerminalTheme.Palette[7], 0.55f);

    /// <summary>The panel's own ground, for the notches that cut the bar into MUD2's seven rung steps.
    /// Matches GamePage.xaml's Border background - the canvas clears to transparent and composites over
    /// it, so a notch in this colour reads as a gap in the bar.</summary>
    private static readonly SKColor PanelGround = new(0x10, 0x16, 0x18);

    /// <summary>
    /// The two rDPT prediction bands: the next landed blow, and the one after it.
    ///
    /// <para><b>The colour clash, and the rule that resolves it.</b> Red already means "the enemy" on
    /// this panel - the creature's weapon and its damage figures are drawn in it. Here red marks the
    /// player's own progress, which is good news. The rule that makes both readable is POSITIONAL, not
    /// chromatic: everything in the lane beyond the fill is where the NEXT BLOW lands, whoever throws
    /// it - drawn in the same two-lane grammar on every bar the panel has, opponent and player alike.
    /// Every other red on the panel is text inside a slot.</para>
    /// </summary>
    private static readonly SKColor PredictNext = TerminalTheme.Palette[9];
    private static readonly SKColor PredictAfter = TerminalTheme.Palette[3];

    /// <summary>The tempo frame around an engaged widget. Deliberately neutral: the frame's MESSAGE is
    /// its dash density, and giving it a hue as well would be two encodings on one element. Its
    /// brighter sibling, <see cref="FrameAccent"/>, is the exception - see that field's own remarks.
    /// </summary>
    private static readonly SKColor FrameTone = Dim(TerminalTheme.Palette[7], 0.42f);

    /// <summary>
    /// The same frame, brighter. Worn by exactly two widgets at once: the player's own device, and the
    /// single live opponent <see cref="MudSharp.Combat.ReachAggregate.GreatestThreatForAccent"/> names.
    ///
    /// <para><b>What it means.</b> <c>ReachAggregate.GreatestThreat</c>
    /// picks the ONE live opponent the player's own prediction lanes (<c>CombatLiveView.YourNextBlow</c>/
    /// <c>YourBlowAfter</c>) project their damage bracket from - that selection runs on every refresh
    /// whether or not anything marks it. This accent is the ONLY on-screen indication that it is
    /// happening at all: without it, if the selection ever picked the wrong creature, nothing would
    /// show it - the prediction lanes would just be quietly wrong about which opponent they describe,
    /// with no way to notice from the screen. <b>Do not delete this as decoration without moving that
    /// visibility somewhere else first.</b></para>
    ///
    /// <para><b>Brightness, not hue.</b> Cyan already means "the creature I am hitting" (the target bar)
    /// and that is a genuinely different question from "the creature whose damage profile is feeding my
    /// prediction lanes" - a group fight is exactly where those two come apart, so reusing it would
    /// collapse the distinction this exists to draw. Red is the rDPT lane's. The same grey, more
    /// present, adds no vocabulary.</para>
    ///
    /// <para><b>It never appears alone.</b> Below two live opponents there is nothing to single out (see
    /// <c>GreatestThreatForAccent</c>), and out of combat or through the grace window there is no
    /// selection happening to mark at all - in every such case both frames fall back to
    /// <see cref="FrameTone"/>, so an accent always has its partner on screen.</para>
    /// </summary>
    private static readonly SKColor FrameAccent = Dim(TerminalTheme.Palette[7], 0.75f);
    private static readonly SKColor Magic = new(0x8F, 0x84, 0xEE);        // purple, shaded blue
    private static readonly SKColor Rule = Dim(TerminalTheme.Palette[8], 0.40f);

    /// <summary>
    /// The novelty marks, on names only (a creature's, and the player's weapon).
    ///
    /// <para><b>Orange is off-palette on purpose.</b> Campbell has no orange, and every near miss in it
    /// is already spoken for on this panel: #C19C00 is the second prediction band, #F9F1A5 is Caution,
    /// #E74856 is Hostile. A mark that shares a hue with a band or a damage figure would be read as
    /// belonging to it. This value sits between the yellows and the reds and belongs to nothing else
    /// here.</para>
    ///
    /// <para>Red is <see cref="Hostile"/> itself, which is the one reuse that is not a collision:
    /// everything on this panel drawn in it is a fact about the enemy, and "you have fought this and
    /// never finished it" is exactly that. What separates it from the NPC-weapon text on the same row is
    /// position (name column vs weapon column) and the halo.</para>
    /// </summary>
    internal static readonly SKColor NoveltyUnfought = new(0xF0, 0x88, 0x3E);
    private static readonly SKColor NoveltyUndefeated = TerminalTheme.Palette[9];

    /// <summary>
    /// The glow itself: the same text drawn once underneath in a blurred pass, so a marked name reads
    /// as lit rather than merely tinted.
    ///
    /// <para><b>Static, and it has to be.</b> Invariant #1 - this canvas never animates. A glow that
    /// breathes would belong in the Composition layer behind the canvas (where the panel pulse and the
    /// flee pill's alarm live); this one is a property of the frame state and repaints only when that
    /// changes, so it can be a paint setting.</para>
    ///
    /// <para>Deliberately NOT the client's other "glow": that word already means the C11 buff/debuff
    /// overlay in the status strip, which reports a MUD2 spell on the player. Reusing that mechanism
    /// here would put a claim about the fight corpus into the row of icons that report what the game
    /// has cast on you. Different subject, different surface, no shared code.</para>
    /// </summary>
    private static readonly SKMaskFilter NoveltyHalo = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2.6f);

    private static SKColor? NoveltyColor(NoveltyMark mark) => mark switch
    {
        NoveltyMark.Unfought => NoveltyUnfought,
        NoveltyMark.Undefeated => NoveltyUndefeated,
        _ => null,
    };

    /// <summary>Draws <paramref name="text"/> in its novelty colour with a halo behind it, or in
    /// <paramref name="plain"/> with no halo when there is no mark. One extra DrawText per marked
    /// string, at most nine per repaint (eight slots and the weapon).</summary>
    private void DrawMarkedText(
        SKCanvas canvas, string text, float x, float baseline, SKTextAlign align, SKFont font,
        SKColor plain, NoveltyMark mark)
    {
        if (NoveltyColor(mark) is not SKColor lit)
        {
            _text.Color = plain;
            canvas.DrawText(text, x, baseline, align, font, _text);
            return;
        }

        _text.Color = lit.WithAlpha(0xB0);
        _text.MaskFilter = NoveltyHalo;
        canvas.DrawText(text, x, baseline, align, font, _text);
        _text.MaskFilter = null;

        _text.Color = lit;
        canvas.DrawText(text, x, baseline, align, font, _text);
    }

    private static SKColor Dim(SKColor c, float k)
        => new((byte)(c.Red * k), (byte)(c.Green * k), (byte)(c.Blue * k), c.Alpha);

    /// <summary>Pushes a colour <paramref name="k"/> of the way toward another, keeping its alpha. For
    /// tinting rather than replacing - see <see cref="DrawPlayerBar"/>'s just-lost slice.</summary>
    private static SKColor Tint(SKColor c, SKColor toward, float k)
        => new(
            (byte)(c.Red + ((toward.Red - c.Red) * k)),
            (byte)(c.Green + ((toward.Green - c.Green) * k)),
            (byte)(c.Blue + ((toward.Blue - c.Blue) * k)),
            c.Alpha);

    /// <summary>
    /// Mixes a colour toward another and then rescales it back to the ORIGINAL's brightness, so only
    /// the hue changes - the dead strip's outcome tint.
    ///
    /// <para><b>Why brightness is held and only hue moves.</b> Pushing a grey toward yellow under a
    /// plain mix brightens it, and brightness is the channel that actually pulls the eye. Holding
    /// luminance constant and moving only the hue gives a difference the eye can read once it looks at
    /// the strip, without giving the strip a reason to be looked at. The rail is glance instrumentation
    /// and the live stack below it is what the eye is trained on; a settled ending must never outrank a
    /// live opponent.</para>
    ///
    /// <para>Rec.601 luma, so this panel has one brightness model rather than a second one alongside
    /// <see cref="Dim"/>.</para>
    /// </summary>
    private static SKColor HueOnly(SKColor c, SKColor toward, float k)
    {
        var r = c.Red + ((toward.Red - c.Red) * k);
        var g = c.Green + ((toward.Green - c.Green) * k);
        var b = c.Blue + ((toward.Blue - c.Blue) * k);

        var mixed = Luma(r, g, b);
        var scale = mixed > 0f ? Luma(c.Red, c.Green, c.Blue) / mixed : 1f;

        return new SKColor(
            (byte)Math.Clamp(r * scale, 0f, 255f),
            (byte)Math.Clamp(g * scale, 0f, 255f),
            (byte)Math.Clamp(b * scale, 0f, 255f),
            c.Alpha);
    }

    private static float Luma(float r, float g, float b) => (0.299f * r) + (0.587f * g) + (0.114f * b);

    /// <summary>How far <see cref="OutcomeTint"/> mixes toward the palette hue before
    /// <see cref="HueOnly"/> gives the brightness back. High enough that the hue survives being diluted
    /// by the grey it starts from - at 10f a washed-out mix reads as grey again, which is the failure
    /// this number exists to avoid - and it costs no extra attention, because the brightness the mix
    /// would have bought is handed straight back.</summary>
    private const float OutcomeTintMix = 0.65f;

    /// <summary>How far a live colour is knocked back once the fight is over - enough to read as
    /// "recorded" at a glance without losing the value's own hue, which is what makes the record
    /// worth keeping on screen at all.</summary>
    private const float SpentWash = 0.45f;

    /// <summary>
    /// Clio's colorcode() ladder, ported so the stamina bar agrees with the status strip at
    /// the top of the window - two readouts of one number must never disagree about its
    /// colour. Deliberately NOT the player's own flee doctrine: the 40/20 thresholds they act
    /// on drive the ALARM, never the readout's colour. A readout reports; an alarm interprets.
    /// </summary>
    private static SKColor RatioColor(int value, int max)
    {
        if (value <= 0 || max <= 0)
            return TerminalTheme.Palette[10];
        var ratio = value * 100 / max;
        if (ratio >= 100) return TerminalTheme.Palette[10];
        if (ratio >= 76) return TerminalTheme.Palette[2];
        if (ratio >= 36) return TerminalTheme.Palette[11];
        if (ratio >= 16) return TerminalTheme.Palette[3];
        if (ratio >= 6) return TerminalTheme.Palette[1];
        return TerminalTheme.Palette[9];
    }

    // ---- Paints, built once. The paint handler allocates nothing. -------------------------
    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
    private readonly SKPaint _text = new() { IsAntialias = true };
    /// <summary>Scratch path for <see cref="DrawDirectionMark"/> - built once and <c>Reset()</c>
    /// before every mark rather than <c>new SKPath()</c>'d per call: it runs twice per tile, so ~12
    /// times per paint. Never held past the <c>DrawPath</c> call that consumes it, so reusing it across
    /// draws is safe - Skia has already copied whatever it needs onto the canvas by the time the next
    /// caller resets it.</summary>
    private readonly SKPath _arcPath = new();
    /// <summary>Scratch path for <see cref="DrawMetronomeToggle"/> - same reasoning as
    /// <see cref="_arcPath"/>, and safe for the same reason: consumed by <c>DrawPath</c>/fill before
    /// the next paint ever touches it again.</summary>
    private readonly SKPath _metronomeBodyPath = new();
    private readonly SKFont _nameFont = new(SKTypeface.Default, 13.5f);
    /// <summary>A live creature's name, bold. Falls back to the regular cut where
    /// the platform has no bold face, exactly as <see cref="_pillFont"/> does - the name loses its
    /// emphasis rather than going missing. A RESOLVED row keeps <see cref="_nameFont"/>: the dead
    /// strip is a record, not a thing to be watched.</summary>
    private readonly SKFont _nameBoldFont = new(
        SKTypeface.FromFamilyName(SKTypeface.Default.FamilyName, SKFontStyle.Bold) ?? SKTypeface.Default, 13.5f);
    /// <summary>The wound phrase and the stat rows. Monospace and tabular, which is what actually
    /// holds the two stat rows in register: the fixed column origins place the groups, and equal
    /// digit advances line the figures up inside them.</summary>
    private readonly SKFont _rungFont = new(SKTypeface.FromFamilyName("Cascadia Mono") ?? SKTypeface.Default, 12f);
    /// <summary>The blow-shape group's outer two figures, and the ghost labels. Same face as the stat
    /// row so the digits still align, two points down so the eye is told which figure is the one that
    /// matters without the colour having to carry it alone.</summary>
    private readonly SKFont _statSmallFont = new(
        SKTypeface.FromFamilyName("Cascadia Mono") ?? SKTypeface.Default, 10f);

    /// <summary>The swap mark on the alternate-weapon line, U+1F5D8. Written as an escape because this
    /// codebase rejects non-ASCII characters in source; the escape is the same character either
    /// way.</summary>
    private const string SwapGlyph = "\U0001F5D8";

    /// <summary>
    /// A face that actually HAS <see cref="SwapGlyph"/>, or null.
    ///
    /// <para>Segoe UI - what <see cref="SKTypeface.Default"/> resolves to on Windows - does not carry
    /// U+1F5D8, so drawing it in any of the fonts above would produce a tofu box rather than a mark.
    /// <see cref="SKFontManager.MatchCharacter(int)"/> asks the platform which installed face does
    /// carry it, which is the only reliable way to ask. Null when nothing on the machine has it, and
    /// the alternate-weapon line then simply draws its name and its key without a mark - a missing
    /// decoration, never a missing affordance.</para>
    /// </summary>
    private readonly SKFont? _swapFont = MakeSwapFont();

    private static SKFont? MakeSwapFont()
    {
        var typeface = SKFontManager.Default.MatchCharacter(0x1F5D8);
        return typeface is null ? null : new SKFont(typeface, 12f);
    }
    private readonly SKFont _weaponFont = new(SKTypeface.Default, 13f);
    private readonly SKFont _smallFont = new(SKTypeface.Default, 10f);
    /// <summary>The dead strip's recent-ending cut: same size as <see cref="_smallFont"/>, bold face.
    /// Falls back to the regular cut where the platform has no bold face, exactly as
    /// <see cref="_pillFont"/> already does - a recent ending simply loses its emphasis rather than
    /// going missing.</summary>
    private readonly SKFont _smallFontBold = new(
        SKTypeface.FromFamilyName(SKTypeface.Default.FamilyName, SKFontStyle.Bold) ?? SKTypeface.Default, 10f);
    /// <summary>The flee pill's text. Bold like the dreamword chip's word, and 12f rather than the
    /// weapon line's 13f because the chip carries four things now - word, stamina, price, key - and the
    /// worst realistic string ("Flee 105 (-2.1k)" plus "^F") has to fit 152dp with padding. Falls back
    /// to the regular cut where the platform has no bold face, in which case the text is simply not
    /// emphasised rather than missing.</summary>
    private readonly SKFont _pillFont = new(
        SKTypeface.FromFamilyName(SKTypeface.Default.FamilyName, SKFontStyle.Bold) ?? SKTypeface.Default, 12f);

    // ---- Dash effects, built on first use and reused. Invariant #1. -----------------------
    //
    // A dash effect is a native Skia object behind a finalizable wrapper. Creating one with
    // SKPathEffect.CreateDash() INSIDE the paint handler on every paint - five sites, two of them
    // inside the per-opponent loop - is exactly the allocation storm Invariant #1 forbids, and
    // dropping the reference without Dispose leaves the native side to the finalizer queue.
    //
    // Built lazily rather than in the field initialiser, and nulled rather than merely disposed in
    // ReleaseDashEffects, so a view whose handler is torn down and later rebuilt simply recreates
    // them. A readonly field disposed at teardown would be a use-after-free on the next paint -
    // this file already carries a live crash precedent of that exact shape (see OnHandlerChanged).
    //
    // Assigning one to SKPaint.PathEffect does not transfer ownership: the paint takes its own
    // reference and setting the property back to null releases only that. These wrappers stay valid
    // across any number of paints, which is the whole point.
    private SKPathEffect? _dashResetRule;   // 1 on, 3 off - the dead strip's reset separator
    private SKPathEffect? _dashOverflow;    // 3 on, 3 off - the overflow row's frame
    private SKPathEffect? _dashUnseen;      // 3 on, 5 off - the unknown badge's still frame
    private SKPathEffect? _dashHalfLink;    // 1 on, 2 off - the half-link underline (see HalfLinkInk)

    private SKPathEffect DashResetRule => _dashResetRule ??= SKPathEffect.CreateDash([1f, 3f], 0f);
    private SKPathEffect DashOverflow  => _dashOverflow  ??= SKPathEffect.CreateDash([3f, 3f], 0f);
    private SKPathEffect DashUnseen    => _dashUnseen    ??= SKPathEffect.CreateDash([3f, 5f], 0f);
    private SKPathEffect DashHalfLink  => _dashHalfLink  ??= SKPathEffect.CreateDash([1f, 2f], 0f);

    /// <summary>Cap on the tempo-dash cache before it is emptied wholesale. The two tempo sites take
    /// their dash lengths from a continuous function of an observed hit rate
    /// (<c>DamagePrediction.Tempo</c>), so unlike the three fixed patterns above they cannot be
    /// hoisted into named fields - the set of distinct patterns is small at any instant (at most one
    /// per opponent slot, and the rail draws at most 8) but changes as a fight progresses. A plain
    /// dictionary would therefore grow without bound over a long session.</summary>
    private const int MaxTempoDashes = 32;

    private readonly Dictionary<(float Dot, float Gap), SKPathEffect> _tempoDashes = new();

    /// <summary>The dash for one swing tempo, reused whenever that exact pattern comes round again.
    /// On overflow the whole cache is disposed and dropped rather than evicting one entry - the
    /// patterns in play move together as a fight progresses, so the old set is stale as a set, and
    /// this keeps the paint path free of eviction bookkeeping.</summary>
    private SKPathEffect TempoDash(float dot, float gap)
    {
        var key = (dot, gap);
        if (_tempoDashes.TryGetValue(key, out var cached))
            return cached;
        if (_tempoDashes.Count >= MaxTempoDashes)
        {
            foreach (var stale in _tempoDashes.Values)
                stale.Dispose();
            _tempoDashes.Clear();
        }
        var effect = SKPathEffect.CreateDash([dot, gap], 0f);
        _tempoDashes[key] = effect;
        return effect;
    }

    /// <summary>Hand every cached dash back to Skia and forget it. Called from
    /// <see cref="OnHandlerChanged"/> when the handler goes away - the one teardown hook this view
    /// has. Safe to call more than once, and safe to call on a view that later paints again: the
    /// accessors above rebuild on demand.</summary>
    private void ReleaseDashEffects()
    {
        // Detach first: the paint may still be holding a reference to one of these.
        _stroke.PathEffect = null;
        _dashResetRule?.Dispose();
        _dashResetRule = null;
        _dashOverflow?.Dispose();
        _dashOverflow = null;
        _dashUnseen?.Dispose();
        _dashUnseen = null;
        _dashHalfLink?.Dispose();
        _dashHalfLink = null;
        foreach (var effect in _tempoDashes.Values)
            effect.Dispose();
        _tempoDashes.Clear();
    }

    private CombatLiveView _live = CombatLiveView.Idle;

    public CombatRailView()
    {
        InputTransparent = true;
    }

    /// <summary>The frame state to draw. Repaints only when <paramref name="value"/> is
    /// structurally different from the previous frame, so the per-event and 1 Hz refresh paths do
    /// not invalidate for identical content.
    ///
    /// <para><b>Structural, not reference, equality.</b> <c>ReferenceEquals</c> alone can never
    /// match here: <c>Mucka.Combat.CombatFrameComposer.Compose</c> allocates a fresh
    /// <see cref="CombatLiveView"/> on every refresh (a <c>with</c>-expression in the idle branch,
    /// <c>new</c> in the two in-combat branches), including the 1 Hz anti-idle tick that runs
    /// whether or not anything actually changed - so a reference check alone forced a repaint every
    /// second for as long as the summary stayed on screen, contradicting Invariant #1.
    /// <c>ReferenceEquals</c> is kept ahead of the value compare purely as a fast path, since the
    /// idle branch's <c>with</c>-expression reuses the same instance often enough for that to pay
    /// for itself.</para>
    ///
    /// <para><b>Member-wise equality is NOT automatically structural.</b> <see cref="CombatLiveView"/>
    /// is a record and <c>RosterPlan</c> a record struct, so both get synthesized member-wise equality
    /// - but <c>RosterPlan.Rows</c> is an <c>IReadOnlyList&lt;RosterRow&gt;</c>, and the synthesized
    /// compare for a reference-typed member of that shape is REFERENCE equality: a freshly allocated
    /// list of otherwise-identical rows compares unequal. <c>RosterPlan</c> declares its own
    /// element-wise <c>Equals</c> for this reason; see that method's remarks. If a collection-typed
    /// member is ever added to <see cref="CombatLiveView"/> itself, it needs the same treatment, or the
    /// rail will repaint every refresh again with no compiler warning to say so. (<c>DeadStripHistory</c>
    /// is safe today only because the view model publishes a CACHED instance and reallocates it only
    /// when the archive actually grows.)</para>
    /// </summary>
    public CombatLiveView Live
    {
        get => _live;
        set
        {
            if (ReferenceEquals(_live, value) || _live == value)
                return;
            _live = value;
            InvalidateSurface();
        }
    }

    public static readonly BindableProperty SidePanelProperty = BindableProperty.Create(
        nameof(SidePanel), typeof(SidePanelViewModel), typeof(CombatRailView), null,
        propertyChanged: OnSidePanelChanged);

    public SidePanelViewModel? SidePanel
    {
        get => (SidePanelViewModel?)GetValue(SidePanelProperty);
        set => SetValue(SidePanelProperty, value);
    }

    private static void OnSidePanelChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var view = (CombatRailView)bindable;
        if (oldValue is SidePanelViewModel old)
            old.PropertyChanged -= view.OnSidePanelPropertyChanged;
        if (newValue is SidePanelViewModel fresh)
        {
            fresh.PropertyChanged += view.OnSidePanelPropertyChanged;
            view.Live = fresh.Live;
        }
    }

    private void OnSidePanelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SidePanelViewModel.Live) or null && sender is SidePanelViewModel vm)
            Live = vm.Live;
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        // Mandatory: this codebase has a live crash precedent (RO_E_CLOSED) from a surface that
        // stayed subscribed after its host was destroyed, so the next combat line drew into
        // already-torn-down objects.
        if (Handler is null)
        {
            if (SidePanel is { } vm)
                vm.PropertyChanged -= OnSidePanelPropertyChanged;
            // The native dashes go back with it. See ReleaseDashEffects for why they are nulled
            // rather than treated as readonly-for-the-lifetime fields like the paints and fonts
            // above: a handler can come back, and the accessors rebuild on demand.
            ReleaseDashEffects();
        }
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var scale = e.Info.Width / RailWidth;
        canvas.Save();
        canvas.Scale(scale);

        var height = e.Info.Height / scale;
        var live = _live;

        // Everything is placed relative to the BOTTOM edge and worked upward - see the offsets'
        // own remarks for the order and why the block's total height is unchanged.
        DrawPlayerTile(canvas, height - PlayerTileBottomOffset - PlayerTileHeight, live);
        DrawTickRow(canvas, height - TickRowBottomOffset - TickRowHeight, live);
        DrawEncounterTable(canvas, height - EncounterBottomOffset - EncounterRowHeight, live);
        DrawOpponents(canvas, height, live);

        canvas.Restore();
    }

    /// <summary>
    /// Opponent slots, filled from the bottom upward. The slot count comes from the height
    /// actually available rather than a fixed number, so a taller window simply shows more of
    /// the fight; it is recomputed only on resize, never on a combat event, so nothing shifts
    /// mid-fight.
    /// </summary>
    private void DrawOpponents(SKCanvas canvas, float height, CombatLiveView live)
    {
        if (!live.HasEncounter)
            return;

        var rows = live.Roster.Rows;

        // LIVE opponents only, bottom-anchored. Capacity, the overflow rule and every row's y come from
        // RailSlotGeometry rather than being computed here: GamePage's float overlay has to place a
        // number over the same rectangle this loop draws, and a second copy of the arithmetic would
        // disagree the first time either side's constants moved.
        //
        // Counted on LIVE opponents, not on the published row list. The roster caps its rows at
        // ParticipantRoster.MaxRows, so deciding the overflow on rows.Count made the tail invisible at
        // any height that fits the whole capped list - which is every ordinary window - and a
        // fourteen-rat fight drew eight rats and said nothing about the other six. One opponent is one
        // slot: an unknown badge takes as many as the Unseen opponents it stands for
        // (RosterRow.SlotSpan) and RosterPlan.LiveCount counts them that way.
        var liveSlots = live.Roster.LiveCount;
        var overflow = liveSlots > RailSlotGeometry.Capacity(SlotMetrics, height);

        var threatRow = GreatestThreatRow(live);
        var hoveredValueTop = float.NaN;
        var usedSlots = 0;
        var shownRows = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            if (RailSlotGeometry.RowPlacement(SlotMetrics, height, rows, i, liveSlots)
                is not (double top, int span))
                break;
            // Every live row gets its own prediction, not just the current target: the bands come from
            // per-creature brackets and per-creature pool estimates, so each one is about the creature
            // it is drawn on.
            DrawOpponentSlot(canvas, (float)top, rows[i], i == threatRow, span);
            if (i == NpcValueHoverRow && rows[i].Value is not null)
                hoveredValueTop = (float)top;
            usedSlots += span;
            shownRows++;
        }
        var y = (float)RailSlotGeometry.SlotTop(SlotMetrics, height, usedSlots);

        // The overflow row is a tail of the LIVE roster, so it belongs with the live region - directly
        // above the last live slot, not up with the dead. Compact for the same reason the dead strip is:
        // one line of text does not need a whole slot's height.
        if (overflow)
        {
            y += SlotHeight - OverflowRowHeight;
            DrawOverflowRow(canvas, y, rows, shownRows, live.Roster);
        }

        var stack = RailSlotGeometry.LiveStackHeight(
            SlotMetrics, usedSlots, overflow ? OverflowRowHeight : 0);
        DrawDeadStrip(
            canvas, (float)RailSlotGeometry.DeadStripBottom(SlotMetrics, height, stack),
            live.DeadStripHistory);

        // LAST, over every tile and the strip alike - a readout the pointer is deliberately holding
        // open must not be overdrawn by the row below it.
        //
        // It covers its OWN tile's two damage rows, which is the acceptable half of the trade: that
        // tile is the one being interrogated, and the sentence explaining what "205pts" means is
        // worth more for those few seconds than the numbers it hides. Same reasoning as the encounter
        // tip covering the tick gauge.
        if (!float.IsNaN(hoveredValueTop))
        {
            var row = rows[NpcValueHoverRow];
            DrawTipChip(
                canvas,
                $"{row.Name} is worth {row.Value} points if killed",
                hoveredValueTop + TileHealthBaseline + 6f,
                Pad, Content);
        }
    }

    /// <summary>
    /// The settled dead: every combat ENDING this session, one row each, in a compact top-anchored
    /// strip.
    ///
    /// <para><b>Why the dead move and the living do not.</b> The strip is top-anchored: when an NPC
    /// dies, the adjustment carries information worth showing, unlike the live stack, which is under a
    /// standing block on shuffling.</para>
    ///
    /// <para>The rule exists so the eye never has to re-find something that has NOT changed; a death HAS
    /// changed something, and the strip growing a line is the panel saying so. The live stack below
    /// stays bottom-anchored and is sized from the full rail height, so a death can never move it. The
    /// two regions grow toward the gap between them, and when they meet it is the DEAD that gives -
    /// which is the region where movement is acceptable.</para>
    ///
    /// <para><b>One row per ending, session-scoped, not grouped and not per-encounter.</b>
    /// <paramref name="history"/> already IS that history, chronological oldest-first -
    /// <see cref="SidePanelViewModel.BuildDeadStripHistory"/> owns stitching the session archive to the
    /// current encounter's own endings, so this method only lays rows out and truncates from the front
    /// when there are more than fit.</para>
    ///
    /// <para><b>Combat ENDINGS, not just kills</b> - so a row can tell a flee from a death. Every
    /// <see cref="FightOutcome"/> a fight can resolve to gets its own row and its own word
    /// (<see cref="RailReadout.OutcomeWord"/>); a slight tint on the word marks the off-nominal endings
    /// (<see cref="OutcomeTint"/>) without ever replacing the word itself.</para>
    ///
    /// <para><b>Truncation drops the OLDEST, newest always wins.</b> When more endings exist than the
    /// available height can show, the top row becomes a dimmed "+N earlier" marker and the newest
    /// entries fill the rest.</para>
    /// </summary>
    private void DrawDeadStrip(SKCanvas canvas, float floor, IReadOnlyList<CombatEnding> history)
    {
        if (history.Count == 0)
            return;

        var startY = Pad + DeadLineHeight;
        // Row-count/truncation arithmetic lives in RailSlotGeometry so Mucka.Util.Tests can pin it
        // directly - this class is an SKCanvasView and unreachable from a unit test. See
        // RailSlotGeometry.PlanDeadStrip for the capacity-1 rule (show the newest ending rather than a
        // marker with nothing under it). The two separator heights are the SAME constants this method
        // draws with, below - PlanDeadStrip needs them to plan around, and passing anything else would
        // let the plan and the paint disagree about how much room a boundary takes.
        var plan = RailSlotGeometry.PlanDeadStrip(
            floor, startY, DeadLineHeight,
            DeadStripEncounterSeparatorHeight, DeadStripResetSeparatorHeight, history);

        var y = startY;
        if (plan.ShowMarker)
        {
            _text.Color = InkDim;
            canvas.DrawText(
                "+" + plan.HiddenCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " earlier",
                Pad, y, SKTextAlign.Left, _smallFont, _text);
            y += DeadLineHeight;
        }

        var nowUtc = DateTime.UtcNow;
        for (var i = plan.ShownStart; i < history.Count; i++)
        {
            var ending = history[i];
            // "Recent" is a window off the game's own tick, not a guess - see DeadStripRecentWindowMs.
            // A null EndedUtc (should not happen for a resolved fight - see FightAccumulator.Resolve -
            // but the field is nullable and CLAUDE.md forbids inventing a timestamp) reads as not
            // recent rather than as always-bold.
            var age = ending.EndedUtc is DateTime endedUtc ? (nowUtc - endedUtc).TotalMilliseconds : double.MaxValue;
            var isRecent = age >= 0 && age < DeadStripRecentWindowMs;
            var font = isRecent ? _smallFontBold : _smallFont;

            // Two lines per ending: the creature and what it cost above, how it ended and what it cost
            // the player below. The exchange summary is the only place a finished fight's figures
            // survive - its tile is gone.
            var nameBaseline = y - DeadSubLineHeight;

            _text.Color = Ink;
            canvas.DrawText(
                Ellipsize(ending.Name, DeadNameWidth, font), Pad, nameBaseline, SKTextAlign.Left,
                font, _text);

            _text.Color = ending.Outcome == FightOutcome.Died ? Hostile : OutcomeTint(InkDim, ending.Outcome);
            canvas.DrawText(
                Ellipsize(RailReadout.OutcomeWord(ending.Outcome), DeadNameWidth, font), Pad, y,
                SKTextAlign.Left, font, _text);

            // What MUD2 announced against this ending, when anything was paired to it. Never a zero -
            // see CombatEnding.ScoreAwarded for why the pairing is an inference and null means "no
            // announcement", not "worth nothing".
            //
            // Three tones, because the figure means three different things:
            //   a kill      +205   amber, the panel's existing award colour
            //   it fled     +158   green - it got away AND paid you, which is the only outcome on this
            //                      strip that is unambiguously good news
            //   you fled    -438   red, dimmed. Slight rather than full: it is a price already paid on
            //                      a row the player is reading after the fact, not an alarm. Full
            //                      Hostile on this strip belongs to "KILLED YOU".
            // The sign is carried in the value itself, so the label and the tone cannot disagree about
            // which way the score went.
            if (ending.ScoreAwarded is int award && award != 0)
            {
                _text.Color = award < 0
                    ? HostileDim
                    : ending.Outcome == FightOutcome.CFled ? Reward : Caution;
                var culture = System.Globalization.CultureInfo.InvariantCulture;
                canvas.DrawText(
                    (award < 0 ? "-" : "+") + Math.Abs(award).ToString(culture),
                    Pad + DeadNameWidth + DeadScoreWidth, nameBaseline, SKTextAlign.Right,
                    _statSmallFont, _text);
            }

            DrawDeadExchange(canvas, nameBaseline, ending.Dealt, inbound: true, byPlayer: true);
            DrawDeadExchange(canvas, y, ending.Taken, inbound: false, byPlayer: false);

            // The grouping separator, if any, belongs BETWEEN this row and the next SHOWN one - never
            // above the topmost shown row (there is nothing above it to separate from) and never below
            // the newest (nothing follows it either). RailSlotGeometry.SeparatorBetween is the same
            // call PlanDeadStrip's own budgeting made for this exact pair, so the two can never
            // disagree about whether a line falls here.
            if (i < history.Count - 1)
            {
                var kind = RailSlotGeometry.SeparatorBetween(ending, history[i + 1]);
                if (kind != DeadStripSeparatorKind.None)
                {
                    var allowance = kind == DeadStripSeparatorKind.Reset
                        ? DeadStripResetSeparatorHeight
                        : DeadStripEncounterSeparatorHeight;
                    DrawDeadStripSeparator(canvas, y, allowance, kind);
                    y += DeadLineHeight + allowance;
                    continue;
                }
            }
            y += DeadLineHeight;
        }
    }

    /// <summary>
    /// One grouping line between two dead-strip rows - <see cref="DeadStripSeparatorKind.Encounter"/>
    /// (1px dotted, <c>TerminalTheme.Palette[3]</c>, the same amber the off-nominal outcome tint
    /// already uses, so the strip does not gain a second yellow) or
    /// <see cref="DeadStripSeparatorKind.Reset"/> (2px solid, <see cref="Ink"/> rather than
    /// <see cref="InkBright"/> - deliberately not the brightest thing on the panel). Centered in the
    /// <paramref name="allowance"/> reserved for it, the same figure
    /// <c>RailSlotGeometry.PlanDeadStrip</c> budgeted this boundary against.
    /// </summary>
    private void DrawDeadStripSeparator(
        SKCanvas canvas, float rowY, float allowance, DeadStripSeparatorKind kind)
    {
        // In the ALLOWANCE, which is the space reserved for exactly this - just below the row that
        // has finished, well above the next row's first line. A row is two lines and extends UPWARD
        // from rowY; the separator's place is defined by the gap, not by what is above it.
        var lineY = rowY + DeadRowDescent + (allowance / 2f);
        if (kind == DeadStripSeparatorKind.Reset)
        {
            _stroke.Color = Ink;
            _stroke.StrokeWidth = 2f;
            _stroke.StrokeCap = SKStrokeCap.Butt;
            canvas.DrawLine(Pad, lineY, Pad + Content, lineY, _stroke);
            _stroke.StrokeWidth = 1f;
            return;
        }

        _stroke.Color = TerminalTheme.Palette[3];
        _stroke.StrokeWidth = 1f;
        _stroke.StrokeCap = SKStrokeCap.Round;
        _stroke.PathEffect = DashResetRule;
        canvas.DrawLine(Pad, lineY, Pad + Content, lineY, _stroke);
        _stroke.PathEffect = null;
        _stroke.StrokeCap = SKStrokeCap.Butt;
    }

    /// <summary>How long an ending stays drawn in bold: four combat ticks, derived from
    /// <see cref="Mucka.Combat.CombatTiming.TickMilliseconds"/> rather than hardcoded so the two can
    /// never drift apart. Four is the generous end of the range rather than the stingy one: on this
    /// rail, missing a recent ending reads as it having already faded from notice, which is worse than
    /// it staying bold one tick longer than strictly necessary.</summary>
    private static readonly double DeadStripRecentWindowMs = Mucka.Combat.CombatTiming.TickMilliseconds * 4.0;

    /// <summary>
    /// The dead strip's tint mapping over <see cref="FightOutcome"/>: a slight yellow tint marks the
    /// off-nominal kill endings, and a slight red marks a player flee - light, so it does not draw the
    /// eye. This recolours the OUTCOME WORD only, never the name beside it, and never replaces the word
    /// - the word is MUD2's own vocabulary and is what actually says how the fight ended.
    /// <see cref="HueOnly"/> is what keeps the tint from turning into brightness.
    ///
    /// <para><b>Yellow: endings where the creature was not cleanly killed.</b> <see cref="FightOutcome.CFled"/>
    /// and <see cref="FightOutcome.CFledFail"/> both leave it alive (fled clean, or broke off still
    /// standing in the room); <see cref="FightOutcome.Withdraw"/> is a mutual accepted withdraw, not a
    /// kill either; <see cref="FightOutcome.NoMore"/> is explicitly NOT a kill by its own doc comment -
    /// MUD2 printed no kill line and credited nothing - which is exactly "lost to something that was
    /// not your blow"; <see cref="FightOutcome.EndOther"/> is the game closing a fight without saying
    /// why, which is not a kill claim either; <see cref="FightOutcome.Interrupted"/> is the client
    /// ending the encounter because the world went away (reset/logout/room change/exit), where the
    /// creature is not merely un-killed but was never finished with. <see cref="FightOutcome.Named"/>
    /// is the one whose semantics differ from the rest: the row retires because SIGHT RETURNED and an
    /// anonymous word acquired a name, so the fight goes on under that name - neither a kill nor a
    /// loss, and the only member of this group where nothing ended at all. None of these belongs
    /// anywhere near
    /// <see cref="FightOutcome.Kill"/>'s own doc comment, which is unambiguous about what a genuine kill
    /// looks like on the wire.</para>
    ///
    /// <para><b>Red: endings that cost the PLAYER.</b> <see cref="FightOutcome.UFled"/> and
    /// <see cref="FightOutcome.UFledFail"/> are the player's own flee, successful or not - and per
    /// UFledFail's own doc comment, a failed attempt is not free either (102 points and a whole
    /// experience level in the captured example). Both are the player paying to leave.</para>
    ///
    /// <para><see cref="FightOutcome.Kill"/> gets no tint - the nominal ending. <see cref="FightOutcome.Died"/>
    /// (the player's own permadeath) is excluded from this mapping entirely and stays drawn in full
    /// <see cref="Hostile"/> at the call site rather than reduced to a tint.</para>
    /// </summary>
    private static SKColor OutcomeTint(SKColor baseColor, FightOutcome outcome) => outcome switch
    {
        FightOutcome.CFled or FightOutcome.CFledFail or FightOutcome.Withdraw
            or FightOutcome.NoMore or FightOutcome.EndOther or FightOutcome.Interrupted
            or FightOutcome.Named
            => HueOnly(baseColor, TerminalTheme.Palette[3], OutcomeTintMix),
        FightOutcome.UFled or FightOutcome.UFledFail
            => HueOnly(baseColor, TerminalTheme.Palette[9], OutcomeTintMix),
        _ => baseColor,
    };

    /// <summary>Width of the outcome word's column in the dead strip - "KILLED YOU" is the longest at
    /// 10f, and the names start clear of it so the two never run together.</summary>
    /// <summary>The gap between an ending's two lines, and the room its left column takes. The name
    /// sits on the upper line and the outcome word under it, so both are ellipsized against the same
    /// width - a long creature name must not run into the exchange summary opposite it.</summary>
    private const float DeadSubLineHeight = 11f;

    /// <summary>How far the small font's descenders reach below a dead-strip baseline. Used to place
    /// the grouping separator clear of the row above it - approximate on purpose, since it only has to
    /// keep a 1px line out of a "g".</summary>
    private const float DeadRowDescent = 4f;

    private const float DeadNameWidth = 104f;
    private const float DeadScoreWidth = 44f;

    /// <summary>The overflow row's own height. One line of text, so it takes a line's worth rather than
    /// a live slot's - the same accounting the dead strip is built on.</summary>
    private const float OverflowRowHeight = 22f;

    /// <summary>The gap between the bottom row's top edge and the lowest opponent slot. Named so the
    /// geometry published to the float overlay reads from the same number the paint path does.</summary>
    private const float SlotsGap = 6f;

    /// <summary>
    /// This panel's slot-layout constants, handed to <see cref="RailSlotGeometry"/> - which owns the
    /// arithmetic but deliberately owns none of the numbers, so the constants stay in the class that
    /// draws with them and there is still exactly one copy.
    /// </summary>
    public static RailSlotMetrics SlotMetricsFor(bool showStats) => new(
        RailWidth: RailWidthFor(showStats), Pad: Pad, SlotHeight: SlotHeight, SlotGap: SlotGap,
        SlotsGap: SlotsGap, BottomRowHeight: BottomRowHeight, TickRowHeight: TickRowHeight,
        PlayerTileHeight: PlayerTileHeight, MaxSlots: MaxSlots);

    private RailSlotMetrics SlotMetrics => SlotMetricsFor(_showStats);

    /// <summary>
    /// Where roster row <paramref name="rosterIndex"/> is actually drawn, in dp from the panel
    /// content box's top-left, or null when that row has no slot of its own (it is in the overflow
    /// tail, or the panel has not been measured). Same contract and same reason as
    /// <see cref="TickTrackDp"/>: this canvas lays itself out in its design space (376 units, or 296
    /// with the stat rows off - <see cref="RailWidthFor"/>) and scales
    /// it, so anything positioned in real dp needs the same factor and must not re-derive it.
    /// </summary>
    public static RailRect? OpponentSlotDp(
        double panelWidthDp, double panelHeightDp, int rosterIndex, int participantCount,
        bool showStats)
        => RailSlotGeometry.OpponentSlotDp(
            SlotMetricsFor(showStats), panelWidthDp, panelHeightDp, rosterIndex, participantCount);

    /// <summary>Where live roster row <paramref name="rosterIndex"/> of <paramref name="rows"/> is drawn,
    /// in dp from the panel content box's top-left, span and all - the rectangle the unknown badge's
    /// marquee rings. The caller's rows rather than this canvas's own <c>Live</c>, so a consumer
    /// reacting to the view model's republish is placed against the roster it is reacting to whether
    /// or not this canvas's binding has caught up. Null when the row has no pane of its own.</summary>
    public RailRect? RowRectDp(
        double panelWidthDp, double panelHeightDp, IReadOnlyList<RosterRow> rows, int rosterIndex, int liveCount)
        => RailSlotGeometry.RowRectDp(SlotMetrics, panelWidthDp, panelHeightDp, rows, rosterIndex, liveCount);

    /// <summary>Where the player's own tile is drawn, in dp from the panel content box's top-left.</summary>
    public static RailRect PlayerTileDp(double panelWidthDp, double panelHeightDp, bool showStats)
        => RailSlotGeometry.PlayerTileDp(SlotMetricsFor(showStats), panelWidthDp, panelHeightDp);

    /// <summary>
    /// The TOP-LEFT corner a damage float starts from, in dp - not a pane to centre on. The float
    /// rises out of the badge from here.
    ///
    /// <para>Just right of the NAME AS DRAWN, level with it:</para>
    /// <code>
    ///   Fred  -1
    ///   Jabberwocky427 -2
    /// </code>
    /// <para>so the number sits against the name it belongs to rather than at a fixed x. That
    /// deliberately gives up a straight column - floats step in and out with the length of each
    /// Creature's name - and it is the trade the operator chose, because beside the name is the only
    /// reliably empty space a badge has. Centring on the pane put the number on the wound phrase and
    /// the ladder bar; the pane's left edge put it straight on the name's own glyphs; a fixed column
    /// past the widest possible name stranded it out by the weapon.</para>
    ///
    /// <para><b>An instance method, and it has to be.</b> The x depends on the measured width of a
    /// particular string in a particular font, and both the font and the roster live here - the same
    /// <see cref="Ellipsize"/> call and the same bold-on-damage rule the name is actually drawn with,
    /// so the measurement cannot drift from the drawing.</para>
    ///
    /// <para>Null when the row has no pane of its own. Returns a zero-size rect: this is a point,
    /// and the caller's own box goes at it.</para>
    /// </summary>
    /// <param name="rosterIndex"><see cref="Mucka.Combat.RailFloat.PlayerAnchor"/> for the player's
    /// own tile, which carries the persona name in the same place under the same rules.</param>
    public RailRect? FloatOriginDp(
        double panelWidthDp, double panelHeightDp, int rosterIndex, int participantCount)
    {
        if (panelWidthDp <= 0 || panelHeightDp <= 0)
            return null;

        float top;
        string name;
        bool tookDamage;
        if (rosterIndex == Mucka.Combat.RailFloat.PlayerAnchor)
        {
            top = (float)PlayerTileDp(panelWidthDp, panelHeightDp, _showStats).Top;
            name = _live.PlayerName ?? string.Empty;
            tookDamage = _live.PlayerTookDamageThisTick;
        }
        else
        {
            var rows = _live.Roster.Rows;
            if (RailSlotGeometry.RowRectDp(
                    SlotMetrics, panelWidthDp, panelHeightDp, rows, rosterIndex, participantCount)
                is not RailRect slot)
                return null;
            top = (float)slot.Top;
            // The badge is titled by the word as a subject, and its floats sit beside that title.
            name = rows[rosterIndex].IsUnseen ? (AnonymousOpponent.Subject(rows[rosterIndex].Name) ?? rows[rosterIndex].Name) : rows[rosterIndex].Name;
            tookDamage = rows[rosterIndex].TookDamageThisTick;
        }

        var k = panelWidthDp / RailWidthFor(_showStats);
        var font = tookDamage ? _nameBoldFont : _nameFont;
        // Ellipsized first, exactly as DrawOpponentSlot and the player tile do: past SlotNameWidth
        // the name stops growing and so must this.
        var drawn = font.MeasureText(Ellipsize(name, SlotNameWidth, font));
        return new RailRect(
            (TileTextLeft + drawn + FloatNameGap) * k, top + (FloatOriginTop * k), 0, 0);
    }

    /// <summary>The breathing room between the name and the number beside it.</summary>
    private const float FloatNameGap = 8f;

    /// <summary>A float's top edge within its pane: the name row, so the rise carries it up and out
    /// of the badge rather than across the rest of it.</summary>
    private const float FloatOriginTop = 2f;

    /// <summary>One opponent: the ladder bar, the name, and the game's own wound phrase. The phrase is
    /// verbatim from the MUD and set in the terminal's own monospace, because echoing what the player
    /// just read in the scroll is what anchors the panel to it.</summary>
    private void DrawOpponentSlot(SKCanvas canvas, float y, RosterRow row, bool isGreatestThreat, int span = 1)
    {
        // A Creature's row is one slot. An unknown badge is as many as the Unseen opponents it stands
        // for (RosterRow.SlotSpan, clamped by RailSlotGeometry.RowPlacement to what fits): the
        // operator's rule that the badge's SIZE is the count. Its standard tile content sits in the
        // top slot's worth; the slots below list who it stands for.
        var height = (float)RailSlotGeometry.RowHeight(SlotMetrics, span);
        _fill.Color = row.IsCurrentTarget
            ? new SKColor(0x61, 0xd6, 0xd6, 0x14)
            : new SKColor(0xff, 0xff, 0xff, 0x08);
        canvas.DrawRoundRect(Pad, y, Content, height, 5f, 5f, _fill);

        // The current target is marked inside its own slot - never by being bigger. A slot that
        // changes size moves everything around it.
        //
        // Drawn for the target ONLY. The non-target bar was Rule (#2F2F2F) on the panel's #101618 -
        // 1.35:1, below the contrast at which a 3-unit sliver is visible at all - so it marked nothing
        // while occupying the strip the badge now needs.
        if (row.IsCurrentTarget)
        {
            _fill.Color = TerminalTheme.Palette[14];
            canvas.DrawRect(Pad, y, 3f, height, _fill);
        }

        // The engagement's own frame, carrying the same dash-density language as its prediction bands:
        // solid when blows are landing, decaying to a dotted outline when they are not. Its rectangle
        // is the slot's own, so the frame can never reflow - only the dash changes. The unknown badge
        // has its own frame instead: the tempo of blows on "something" says nothing about any one of
        // the Creatures it stands for.
        if (row.IsUnseen)
            DrawUnseenFrame(canvas, Pad, y, Content, height);
        else
            DrawTempoFrame(canvas, Pad, y, Content, height, row.YourTempo, isGreatestThreat);

        // Line 1: the creature and what it is holding.
        //
        // Bold ONLY on a tick this creature took a blow on. Tied to damage, the weight means
        // something that changes: in a pack, which of them is actually being worked on. See
        // Mucka.Combat.TickDamageEmphasis for the rule and the carry-forward behind the flag.
        //
        // The badge's title is the word MUD2 used, capitalised as the game prints it as a subject.
        var nameFont = row.TookDamageThisTick ? _nameBoldFont : _nameFont;
        var title = row.IsUnseen ? (AnonymousOpponent.Subject(row.Name) ?? row.Name) : row.Name;
        DrawMarkedText(
            canvas, Ellipsize(title, SlotNameWidth, nameFont), TileTextLeft, y + TileNameBaseline,
            SKTextAlign.Left, nameFont,
            row.IsCurrentTarget ? InkBright : Ink, row.Novelty);

        if (row.IsUnseen)
            DrawUnseenList(canvas, y, span, row.UnseenLabel!);

        // What THIS creature is fighting with, right-aligned opposite its name. A per-participant fact
        // belongs on the participant, not in the player's own weapon column where only one of a pack
        // could ever be named.
        //
        // BLANK when unknown, never "unarmed". RosterRow.NpcWeapon means "the weapon it has ANNOUNCED",
        // and its only source is the "has started to use the X to fight!" line - which a creature that
        // walked in already holding an axe never prints. Writing "unarmed" there turned an unknown into
        // a measured claim on nearly every opponent, which is rule 5 inverted, on the readout that
        // decides whether a fight is survivable. The client cannot tell empty hands from silence, so it
        // says nothing and the slot simply stays reserved. The badge's name row carries its list
        // instead - a weapon "something" announced cannot be pinned on any one of them either.
        if (!row.IsUnseen && row.NpcWeapon is { Length: > 0 } npcWeapon)
        {
            _text.Color = Hostile;
            canvas.DrawText(
                Ellipsize(CombatComposition.DisplayName(npcWeapon), SlotWeaponWidth, _weaponFont),
                TileTextRight, y + TileNameBaseline, SKTextAlign.Right, _weaponFont, _text);
        }

        // Line 2: the game's own words.
        DrawWoundPhrase(canvas, TileTextLeft, y + TileHealthBaseline, row);

        // The ladder. Same seven rungs and the same two prediction lanes the ring carried, unrolled -
        // see DrawVitalityBar for why the shape changed and what did not.
        DrawVitalityBar(
            canvas, y + TileBarTop, Pad, Content, row.Vitality, row.NextBlow, row.BlowAfter,
            Vitality, row.IsHealthStale);

        // The exchange: what the player has done to it, what it has done back, and the timeline of
        // the swings themselves.
        // An opponent's tile: what IT is taking on top (the player's blows, white), what it is dealing
        // underneath (its own, red). See DrawStatRow for why position and colour answer different
        // questions.
        DrawStatRow(canvas, y + TileUpperBaseline, row.Dealt, inbound: true, byPlayer: true);
        DrawStatRow(canvas, y + TileLowerBaseline, row.Taken, inbound: false, byPlayer: false);
        // ONLY the spark answers to the stats switch. The two damage rows above are the badge's own
        // figures and stay whatever the switch says: they run from StatMarkLeft to
        // StatDptLeft + StatDptWidth (260 units), inside the narrow content width (296) as well as
        // the wide one, so nothing about hiding the spark makes them not fit. The spark, from
        // SparkLeft to the right edge, is the part the extra 80dp buys.
        if (_showStats)
            DrawSpark(canvas, y + TileSparkCentre, row.Exchange, subjectIsPlayer: false);

        // The current-target stripe is redrawn LAST, over the ladder. The bar spans the tile's full
        // width from x=Pad, which is the same three units the stripe occupies - drawn in the other
        // order, six units of the stripe were punched out by the bar's fill.
        if (row.IsCurrentTarget)
        {
            _fill.Color = TerminalTheme.Palette[14];
            canvas.DrawRect(Pad, y, 3f, height, _fill);
        }
    }

    /// <summary>
    /// The unknown badge's frame: a still dashed edge in the caution colour, on every platform. The
    /// operator's marquee - dots stepping along the edge just outside this frame - is not drawn here,
    /// because this canvas never animates (Invariant #1); on Windows it is a Composition sibling,
    /// <c>UnseenMarquee</c>, ringing the rectangle <see cref="RowRectDp"/> reports for the row.
    /// </summary>
    private void DrawUnseenFrame(SKCanvas canvas, float x, float y, float width, float height)
    {
        var inset = UnseenStroke / 2f;
        _stroke.Color = Caution;
        _stroke.StrokeWidth = UnseenStroke;
        _stroke.StrokeCap = SKStrokeCap.Butt;
        _stroke.PathEffect = DashUnseen;
        canvas.DrawRoundRect(x + inset, y + inset, width - UnseenStroke, height - UnseenStroke, 5f, 5f, _stroke);
        _stroke.PathEffect = null;
        _stroke.StrokeWidth = 1f;
    }

    private const float UnseenStroke = 2f;

    /// <summary>
    /// Who the unknown badge stands for - its <see cref="RosterRow.UnseenLabel"/>, one entry per slot
    /// of its height. The first entry sits on the name row, right-aligned where a Creature's weapon
    /// would go; each further entry gets the slot below, on that slot's own name baseline, so a badge
    /// three deep reads as three rows of "rat3 / ??? / ???" down its left edge under the word. The
    /// first slot's worth of the badge is otherwise the ordinary tile - the exchange rows and spark are
    /// the blows that landed on the word - and the entries below are drawn dim: they are the client's
    /// bookkeeping, not anything the game said.
    /// </summary>
    private void DrawUnseenList(SKCanvas canvas, float y, int span, string label)
    {
        var entries = label.Split(", ");
        var pitch = (float)RailSlotGeometry.SlotPitch(SlotMetrics);
        // More entries than slots: the badge was clamped to what fits (RailSlotGeometry.RowPlacement)
        // and says so - on the name row when that is the only row it has, else in its last slot.
        var clipped = entries.Length - span;
        var clippedMark = "+" + clipped.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var head = clipped > 0 && span == 1 ? entries[0] + " " + clippedMark : entries[0];
        _text.Color = Caution;
        canvas.DrawText(
            Ellipsize(head, SlotWeaponWidth, _weaponFont),
            TileTextRight, y + TileNameBaseline, SKTextAlign.Right, _weaponFont, _text);
        for (var i = 1; i < entries.Length && i < span; i++)
        {
            var slotTop = y + (i * pitch);
            _stroke.Color = Rule;
            canvas.DrawLine(TileTextLeft, slotTop - (SlotGap / 2f), TileTextRight, slotTop - (SlotGap / 2f), _stroke);
            _text.Color = entries[i] == ParticipantRoster.UnknownMark ? Caution : InkDim;
            canvas.DrawText(
                Ellipsize(entries[i], SlotNameWidth, _nameFont),
                TileTextLeft, slotTop + TileNameBaseline, SKTextAlign.Left, _nameFont, _text);
            if (i == span - 1 && clipped > 0)
            {
                _text.Color = Caution;
                canvas.DrawText(clippedMark, TileTextRight, slotTop + TileNameBaseline, SKTextAlign.Right, _nameFont, _text);
            }
        }
    }

    /// <summary>
    /// Room for the creature's name and for the weapon opposite it. Both fixed rather than measured off
    /// the content: a name allowed to grow into the weapon's space would move the weapon.
    ///
    /// <para>Both grew when the four per-row damage figures were removed: they were unreadable at this
    /// size, and were the last descendant of the numeric table the whole panel was a reaction to. What
    /// each was for now lives somewhere it reads better: how hard this creature hits is the two rDPT
    /// lanes on the player's own ring, positioned against the player's actual stamina rather than stated
    /// as a bare figure; how fast the fight is going is the tempo frame; and how much of the creature is
    /// left is the badge itself.</para></summary>
    private const float SlotNameWidth = 168f;
    private const float SlotWeaponWidth = 152f;
    private const float TileInset = 6f;
    private const float TileTextLeft = Pad + TileInset;
    private float TileTextRight => Pad + Content - TileInset;

    /// <summary>Room for the game's wound phrase on the row below. Wider than the name's, because the
    /// only thing to its right on that row is the damage pair, which starts further over than the
    /// weapon does. The longest phrase in NpcHealthRungs ("superficially injured") is 21 monospace
    /// characters, about 139 units at 11f.</summary>
    private float SlotPhraseWidth => Content - (TileInset * 2f);

    /// <summary>
    /// The game's own words about this creature, verbatim, under its name: the wound phrase, and the
    /// <c>diagnose</c> band beside it when one has been taken.
    ///
    /// <para><b>It stays even now the bar carries a number-shaped reading</b>, and it is the ONLY
    /// reading when the bar has none. It is MUD2's own vocabulary - the words the player just read in
    /// the scroll - and it is per-INDIVIDUAL where the bar's track is per-species.</para>
    ///
    /// <para><b>It is never blanked.</b> MUD2 prints a descriptor after every landed blow that does not
    /// kill, so silence between descriptors is not a missing observation but positive evidence that
    /// nothing of the player's landed - which means the creature has taken nothing from them and the
    /// last reading very probably still holds. Fading it after three ticks is the whole of the
    /// staleness treatment, and it carries
    /// the residual honestly: a creature CAN change untouched (NPC-versus-NPC combat is in the corpus,
    /// and zombies regenerate), so dim says "this is what it last said" without hiding it or
    /// overclaiming it. No timestamp, no duration, no words about age - see
    /// <see cref="RosterRow.StaleAfterSeconds"/>, which owns the policy and the reasoning.</para>
    ///
    /// <para><b>The diagnose band is an exception to the no-absolute-figures rule, and the only one.</b>
    /// Every stamina figure the ESTIMATOR produces stays under the hood, because the player has no way
    /// to check it. A diagnose probe is the opposite case: MUD2 printed the number to them in so many
    /// words. What is drawn is that number carried forward by the damage landed since - still a
    /// statement about this individual, with no species model in it, and the same arithmetic the seal
    /// beside it is already filling to. The printed pair on its own stops being true on the very next
    /// blow, which is the whole reason it moves. Drawn brighter than the phrase because it is a
    /// measurement rather than an adjective, right-aligned so a long phrase ellipsizes into the gap
    /// instead of pushing it off the row.</para>
    ///
    /// <para><b>The figure has its own fade, on its own clock.</b> Damage is subtracted from it, so a
    /// landed blow keeps it current; what nothing here can account for is regeneration, which MUD2
    /// never announces and this client cannot gauge. That uncertainty grows with the wall clock alone,
    /// so the figure dims to <see cref="StaleReadAlpha"/> once the probe is older than
    /// <see cref="RosterRow.StaminaReadStaleAfterSeconds"/> and stays there - see that constant for why
    /// it does not key on the wound phrase's staleness, which is a different claim.</para>
    /// </summary>
    private void DrawWoundPhrase(SKCanvas canvas, float x, float baseline, RosterRow row)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        if (row.StaminaRead is { } read)
        {
            // The printed pair is the fallback for the one case the arithmetic refuses: the player has
            // out-dealt even the probe's upper end and the Creature is demonstrably still standing, so
            // the aged band contradicts itself. The game's own sentence is what survives there - never a
            // zero, which would render a live Creature as a measured empty (NpcStaminaReading.Current).
            var (low, high) = read.TryCurrent(out var agedLow, out var agedHigh)
                ? (agedLow, agedHigh)
                : (read.PrintedLow, read.PrintedHigh);
            var band = low.ToString(culture) + "-" + high.ToString(culture);
            _text.Color = row.IsStaminaReadStale ? InkBright.WithAlpha(StaleReadAlpha) : InkBright;
            canvas.DrawText(band, x + SlotPhraseWidth, baseline, SKTextAlign.Right, _rungFont, _text);
        }

        // What killing it is worth, after the rung word - the slot the player's own tile uses for
        // their stamina figure. Its own fixed origin, so it does not move as the phrase beside it
        // changes length.
        //
        // THE RISK, and how it is answered: a bare number in the position where the player's badge
        // shows "(61/105)" invites being read as the CREATURE's stamina, which is the one figure this
        // panel may never imply it knows. Four things separate them: it is never a fraction, it
        // is gold rather than the condition's own tone, it carries its own UNIT ("{value}pts", with a
        // tooltip that says "{id} is worth {value} points if killed"), and it has a hover readout that
        // says the whole sentence. "205pts" cannot be a stamina reading.
        //
        // Null is "never probed" and draws nothing; ZERO is a legal answer (the ox) and draws. See
        // FightAccumulator.Value for why the two must stay distinguishable.
        if (row.Value is int value)
        {
            var points = Ellipsize(value.ToString(culture) + "pts", NpcValueWidth, _rungFont);
            _text.Color = TerminalTheme.Palette[3];
            canvas.DrawText(points, x + NpcValueLeft, baseline, SKTextAlign.Left, _rungFont, _text);

            // The half link's dotted underline, and unlike the encounter headings' it is drawn at
            // rest rather than on hover: there is no other state in which this figure announces that
            // it has a readout behind it, and a hover target that is silent until pointed at can only
            // be found by accident. See HalfLinkInk - the idiom is the client's, not this row's.
            _stroke.Color = HalfLinkInk;
            _stroke.PathEffect = DashHalfLink;
            canvas.DrawLine(
                x + NpcValueLeft, baseline + 2f,
                x + NpcValueLeft + _rungFont.MeasureText(points), baseline + 2f, _stroke);
            _stroke.PathEffect = null;
        }

        if (row.HealthPhrase is not { Length: > 0 } raw)
            return;

        _text.Color = row.IsHealthStale ? InkDim : Ink;
        canvas.DrawText(
            Ellipsize(NpcHealthRungs.Label(raw), NpcPhraseWidth, _rungFont),
            x, baseline, SKTextAlign.Left, _rungFont, _text);
    }

    /// <summary>
    /// Room for a creature's wound phrase, and where its value sits after it.
    ///
    /// <para>Wider than the player tile's <see cref="ConditionWordWidth"/> because this line has no
    /// alt-weapon group on its right to make room for - only the diagnose band, which is right-aligned
    /// at the far edge. The longest phrase NpcHealthRungs can produce is "superficially injured", 21
    /// monospace characters, which is 151.2 units at the rung size; 156 clears it with room rather
    /// than truncating the game's own words, which this file's remarks call the last thing on the tile
    /// that may be traded for space.</para>
    ///
    /// <para>Both fixed, so neither moves when the other's content changes.</para>
    /// </summary>
    private const float NpcPhraseWidth = 156f;
    private const float NpcValueLeft = NpcPhraseWidth + 4f;

    /// <summary>Room for the value and its unit. Cascadia Mono at the rung size is 7.2 units a
    /// character, so this is eight of them - five digits plus "pts", which covers everything the
    /// corpus has seen with a digit in hand (the highest observed is the levelled thief at 1400).
    /// The diagnose band's worst case - seven characters right-aligned at the tile's far edge - still
    /// starts about 76 units to the right of where this ends.</summary>
    private const float NpcValueWidth = 58f;

    /// <summary>The value's hover box, relative to its own text baseline: how far above the baseline
    /// it starts and how tall it is. The rung font's cap height is about 8.5 units and the dotted
    /// underline sits 2 below the baseline, so this is the drawn extent plus a unit of slack either
    /// side - a target the pointer can actually land on without being larger than the thing it
    /// describes.</summary>
    private const float NpcValueBoxLift = 11f;
    private const float NpcValueBoxHeight = 15f;

    /// <summary>
    /// The seven-rung ladder as a horizontal bar, filling from the left with what is LEFT.
    ///
    /// <para><b>A ladder, not a stamina bar.</b> Every creature has exactly seven rungs. A giant does
    /// not get more rungs - its rungs are worth more stamina and it labels them with different words.
    /// So the bar is notched into sevenths and the boundary is how far up that ladder the creature has
    /// been driven. The estimator's absolute figures stay under the hood, placing the boundary more
    /// finely INSIDE the seventh the descriptor gave; none of them is drawn.</para>
    ///
    /// <para><b>The boundary's two sides differ on purpose.</b> Hard on the near side - the creature
    /// certainly still has that much - and a fade beyond it. Averaging them into one edge would turn
    /// what the game said into a number it never gave. A <c>diagnose</c> probe, the one direct
    /// measurement MUD2 offers, earns a bright tick at the hard edge, and is the only exception to the
    /// no-absolute-figures rule because it is a number the game printed to the player in so many
    /// words.</para>
    ///
    /// <para><b>An unmet creature draws FULL, never empty and never absent.</b> Empty reads as nearly
    /// dead and absent reads as safe, and a creature nobody has a reading on is the most dangerous
    /// thing that can be standing in the room. It is an ordinary state, not an edge case: the player
    /// swings at ONE creature at a time, so in a pack every other tile sits here for the whole fight.
    /// It is drawn in <see cref="SealUnknown"/> with a mark in it, which is what distinguishes it from
    /// a reading.</para>
    ///
    /// <para><b>The wound phrase beside this bar is load-bearing</b> - see
    /// <see cref="DrawWoundPhrase"/>. It is the bind between the bar and the words MUD2 actually
    /// printed, and it is the last thing on the tile that may be traded for space, never the
    /// first.</para>
    ///
    /// <para><b>The plan comes from <see cref="StaminaSeal.Plan"/></b>, which is unchanged and still
    /// shared with the player's own bar, so the two can never disagree about where a boundary is. Its
    /// arcs are degrees clockwise from the top with the LIT portion running to 360, so a
    /// remaining-fraction is <c>(360 - start) / 360</c>; that conversion lives here and nowhere
    /// else.</para>
    ///
    /// <para><b>The two prediction lanes keep their lanes.</b> They sit in a strip under the fill
    /// rather than at two radii, still one in front of the other, still coloured by which blow they
    /// are - hue alone was never allowed to carry that and still does not.</para>
    /// </summary>
    private void DrawVitalityBar(
        SKCanvas canvas, float top, float x, float width,
        VitalityBand? vitality, DamageBand? nextBlow, DamageBand? blowAfter,
        SKColor color, bool stale)
    {
        var plan = StaminaSeal.Plan(vitality, nextBlow, blowAfter);

        _fill.Color = SealTrack;
        canvas.DrawRect(x, top, width, TileBarFillHeight, _fill);

        if (plan.Shape == SealShape.Unmet)
        {
            // Full width, in the unknown tone, with a mark in it. A creature nobody has a reading on
            // is the most dangerous thing in the room and this is an ordinary state - in a pack every
            // row but the one being swung at sits here for the whole fight.
            _fill.Color = SealUnknown;
            canvas.DrawRect(x, top, width, TileBarFillHeight, _fill);
            DrawRungNotches(canvas, x, top, width);
            _text.Color = InkBright;
            canvas.DrawText("?", x + (width / 2f), top + TileBarFillHeight - 1f,
                SKTextAlign.Center, _statSmallFont, _text);
            return;
        }

        var lit = stale ? VitalityStale : color;

        // The soft half first, so the hard edge draws over it: the creature has AT LEAST the hard
        // fraction and possibly as much as the soft one, and painting the certain part last is what
        // makes the boundary read as a boundary rather than as a gradient someone chose.
        if (plan.LitSoft is { } soft)
        {
            _fill.Color = Dim(lit, 0.42f);
            canvas.DrawRect(
                x, top, RailReadout.RemainingWidth(soft.StartDegrees, width), TileBarFillHeight, _fill);
        }

        if (plan.LitHard is { } hard)
        {
            _fill.Color = lit;
            canvas.DrawRect(
                x, top, RailReadout.RemainingWidth(hard.StartDegrees, width), TileBarFillHeight, _fill);
        }

        DrawRungNotches(canvas, x, top, width);

        // A diagnose reading is a number MUD2 printed to the player in so many words, so its hard edge
        // earns a bright tick - the one measurement on this side of the panel.
        if (plan.Measured && plan.LitHard is { } measured)
        {
            _fill.Color = InkBright;
            canvas.DrawRect(
                x + RailReadout.RemainingWidth(measured.StartDegrees, width) - 1f, top - 1f, 1.5f,
                TileBarFillHeight + 2f, _fill);
        }

        DrawPredictionLane(canvas, plan.BlowAfter, x, top, width, PredictAfter);
        DrawPredictionLane(canvas, plan.NextBlow, x, top, width, PredictNext);
    }

    /// <summary>Six cuts in the fill, one per rung boundary. Painted in the panel's own ground rather
    /// than a colour, so they read as gaps in the ladder rather than as marks on it - the same trick
    /// the ring's notches used.</summary>
    private void DrawRungNotches(SKCanvas canvas, float x, float top, float width)
    {
        _fill.Color = PanelGround;
        for (var rung = 1; rung < NpcHealthRungs.Rungs; rung++)
            canvas.DrawRect(x + (width * rung / NpcHealthRungs.Rungs), top, 1f, TileBarFillHeight, _fill);
    }

    /// <summary>One prediction band, in the lane under the fill. Nothing is drawn when the band is
    /// absent: an unsupported prediction has to look like no prediction, never like a small one.</summary>
    private void DrawPredictionLane(
        SKCanvas canvas, SealArc? arc, float x, float top, float width, SKColor color)
    {
        if (arc is not { } band)
            return;

        var far = RailReadout.RemainingWidth(band.StartDegrees, width);
        var near = RailReadout.RemainingWidth(band.StartDegrees + band.SweepDegrees, width);
        var span = Math.Max(far - near, 1.5f);

        _fill.Color = color;
        canvas.DrawRect(x + near, top + (TileBarLaneTop - TileBarTop), span, TileBarLaneHeight, _fill);
    }

    /// <summary>The incoming side's second colour. Dimmed rather than alpha-faded because every other
    /// de-emphasis on this canvas is a <see cref="Dim"/> against the panel's near-black ground, where
    /// the two are visually the same thing and one of them does not have to be composited.</summary>
    private static readonly SKColor HostileDim = Dim(Hostile, 0.72f);

    /// <summary>How far a cell that has nothing to say is pushed back. On this ground a 0.8 dim is the
    /// same reading as 0.8 alpha and matches the rest of the file.</summary>
    private const float GhostDim = 0.8f;

    /// <summary>
    /// One direction of the exchange: mark, running total, blow shape, drain rate.
    ///
    /// <para><b>Four fixed columns, so the tile's two rows lock to each other.</b> The columns are
    /// POSITIONS, not a measured flow, so the sizes inside a group cannot move the group.</para>
    ///
    /// <para><b>An empty row names its own cells</b> rather than printing dashes: the tile teaches its
    /// layout while it has nothing to report and goes quiet the moment a figure lands. Either way rule
    /// 5 holds, because a word can no more be read as a measurement than a dash can.</para>
    ///
    /// <para><b>Row POSITION and row COLOUR answer different questions.</b> Every badge is about its
    /// own subject: the UPPER row is what that subject is TAKING and the lower row is what it is
    /// DEALING, so an opponent's tile leads with the damage the player put into it and the player's
    /// tile leads with the damage coming back. The COLOUR says who threw the blow - white for the
    /// player, red for a creature - which is why the two tiles do not simply invert into each other's
    /// colours.</para>
    /// </summary>
    /// <param name="inbound">Which mark to draw: the upper row of a tile takes the inward mark, the
    /// lower row the outward one, regardless of whose blow the row describes.</param>
    /// <param name="byPlayer">Whose blow this is. Decides the colour and whether the total is a
    /// bracket - MUD2 brackets the player's own blows and states a creature's exactly.</param>
    private void DrawStatRow(
        SKCanvas canvas, float baseline, ExchangeLine line, bool inbound, bool byPlayer)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var bright = byPlayer ? InkBright : Hostile;
        var dim = byPlayer ? InkDim : HostileDim;

        DrawDirectionMark(canvas, StatMarkLeft + 2f, baseline - 8.5f, !inbound, dim);

        if (!line.HasSamples)
        {
            _text.Color = Dim(dim, GhostDim);
            canvas.DrawText(
                byPlayer ? "dmg done by" : "dmg done to",
                StatTotalLeft + (StatTotalWidth / 2f), baseline, SKTextAlign.Center, _statSmallFont, _text);
            // "low/high/avg", not "min/max/avg". On the outgoing side the outer two are the UPPER
            // bounds of the smallest and largest blows, while the mean pools both ends - so three
            // identical (5-9) blows read 9 / 9 / 7, and a
            // label promising a minimum below the average would be contradicted by the commonest case
            // of all. "low" and "high" describe WHICH BLOW, which is what these actually are.
            canvas.DrawText(
                "low/high/avg",
                StatShapeLeft + (StatShapeWidth / 2f), baseline, SKTextAlign.Center, _statSmallFont, _text);
            canvas.DrawText(
                "dmg/tick", StatDptLeft + StatDptWidth, baseline, SKTextAlign.Right, _statSmallFont, _text);
            return;
        }

        // Centred so a wide outgoing bracket and a bare incoming figure share an axis instead of
        // drifting apart against a right edge.
        var total = byPlayer && line.Total.High > line.Total.Low
            ? line.Total.Low.ToString("0.#", culture) + "-" + line.Total.High.ToString("0.#", culture)
            : line.Total.High.ToString("0.#", culture);
        _text.Color = bright;
        canvas.DrawText(
            Ellipsize(total, StatTotalWidth, _rungFont),
            StatTotalLeft + (StatTotalWidth / 2f), baseline, SKTextAlign.Center, _rungFont, _text);

        DrawShapeGroup(canvas, baseline, line, bright, dim);

        // Zero means "under one tick elapsed", not "no damage" - see SidePanelViewModel.PerTick. It
        // draws as the unknown it is.
        if (line.PerTick > 0)
        {
            _text.Color = bright;
            canvas.DrawText(
                Ellipsize(line.PerTick.ToString("0.#", culture) + "/t", StatDptWidth, _rungFont),
                StatDptLeft + StatDptWidth, baseline, SKTextAlign.Right, _rungFont, _text);
        }
        else
        {
            _text.Color = Dim(dim, GhostDim);
            canvas.DrawText(
                "dmg/tick", StatDptLeft + StatDptWidth, baseline, SKTextAlign.Right, _statSmallFont, _text);
        }
    }

    /// <summary>
    /// The blow shape: smallest, LARGEST, mean. The largest is drawn full size because it is the
    /// figure that kills you; the other two sit a size down, in three fixed slots with fixed
    /// separators so the mixed sizes cannot shift the group between rows.
    ///
    /// <para>On the outgoing side the outer two are UPPER bounds of brackets, and the mean pools both
    /// ends of every bracket. On the incoming side all three are exact.
    /// See <see cref="ExchangeLine"/>.</para>
    /// </summary>
    private void DrawShapeGroup(
        SKCanvas canvas, float baseline, ExchangeLine line, SKColor bright, SKColor dim)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        // One measured blow means the three figures cannot differ yet, so the outer two are pushed
        // back - the eye is told there is nothing to compare rather than left to work out why it is
        // reading the same number three times.
        var outer = line.AllAlike ? Dim(dim, GhostDim) : dim;

        // Every figure BOUNDED to its own slot. Nothing here was, which made this the one group on the
        // tile that could run into its neighbour: the mean is drawn left-aligned from ShapeMeanLeft
        // and the dmg/tick column starts 30 units later, so a three-digit mean sat flush against it
        // and a four-digit one crossed into it - while a four-digit rate, right-aligned in its own 52,
        // reached back the other way. MUD2's creatures level with no cap on their stats, so the fight
        // where those figures get long is exactly the fight this panel is for.
        //
        // Ellipsizing rather than widening the columns: the row's four fixed origins are what hold the
        // two stat rows in register with each other and with every other tile (see DrawStatRow), and a
        // rare truncated figure is a far smaller loss than a group that shifts.
        _text.Color = outer;
        canvas.DrawText(
            Ellipsize(line.Min.ToString("0.#", culture), ShapeLowWidth, _statSmallFont),
            ShapeLowLeft + ShapeLowWidth, baseline, SKTextAlign.Right, _statSmallFont, _text);
        canvas.DrawText(
            "/", ShapeSep1Left + (ShapeSepWidth / 2f), baseline, SKTextAlign.Center, _statSmallFont, _text);
        canvas.DrawText(
            "/", ShapeSep2Left + (ShapeSepWidth / 2f), baseline, SKTextAlign.Center, _statSmallFont, _text);
        canvas.DrawText(
            Ellipsize(line.Mean.ToString("0.#", culture), StatDptLeft - ShapeMeanLeft, _statSmallFont),
            ShapeMeanLeft, baseline, SKTextAlign.Left, _statSmallFont, _text);

        _text.Color = bright;
        canvas.DrawText(
            Ellipsize(line.Max.ToString("0.#", culture), ShapeHighWidth, _rungFont),
            ShapeHighLeft + (ShapeHighWidth / 2f), baseline, SKTextAlign.Center, _rungFont, _text);
    }

    /// <summary>
    /// The exchange spark: one fixed slot per swing, in the order they arrived, off a shared
    /// baseline - the player's above it, the creature's below.
    ///
    /// <para><b>Why one strip and not two.</b> Stacked per-side strips read as two separate charts;
    /// a fight is one thing happening. Interleaving them puts a swing and the answer to it next to
    /// each other, which is how the ogre case reads at a glance - two flat dashes and one full-height
    /// red bar says "rarely connects, connects like a truck" before a digit has been parsed.</para>
    ///
    /// <para><b>A miss is a flat dash on its own side, and a blow of unknown size is a minimum bar,
    /// never a missing slot.</b> The swing happened either way, and the rhythm is the point: it is
    /// what separates a creature that keeps missing from one that is not swinging at all.</para>
    ///
    /// <para>Incoming bars carry severity twice, in height and in hue, both saturating at
    /// <see cref="RailReadout.SparkDamageCap"/>.</para>
    ///
    /// <para><b>It mirrors between the two kinds of tile</b>, for the same reason the stat rows do:
    /// UP is what the badge's subject is TAKING. On an opponent's tile the player's blows rise; on the
    /// player's own tile the creatures' blows rise.</para>
    ///
    /// <para><b>The newest mark is at the RIGHT EDGE, always.</b> Marks are laid out backwards from
    /// the right rather than forwards from the left, so a fight three swings old has its three marks
    /// against the right edge with empty track to their left, and every later swing pushes the strip
    /// leftward. Filling from the left instead made "now" sit wherever the fight happened to have
    /// reached, which is the re-find-it-on-every-glance failure the panel is laid out to avoid.</para>
    /// </summary>
    /// <param name="subjectIsPlayer">Whose tile this is. Decides which side of the baseline each
    /// swing goes: the subject's own blows fall, the blows landing on it rise.</param>
    private void DrawSpark(
        SKCanvas canvas, float centre, IReadOnlyList<SwingMark>? exchange, bool subjectIsPlayer)
    {
        _fill.Color = FrameTone;
        canvas.DrawRect(SparkLeft, centre, SparkWidth, 1f, _fill);

        if (exchange is null || exchange.Count == 0)
            return;

        var slots = (int)(SparkWidth / SparkPitch);
        var shown = Math.Min(exchange.Count, slots);
        var right = SparkLeft + SparkWidth;

        for (var n = 0; n < shown; n++)
        {
            // n = 0 is the NEWEST, drawn hard against the right edge; each older mark steps left.
            var mark = exchange[exchange.Count - 1 - n];
            var x = right - SparkBarWidth - (n * SparkPitch);

            // "Rises" means "this blow landed on the badge's subject". A creature's tile lifts the
            // player's blows; the player's tile lifts the creatures'.
            var rises = mark.Mine != subjectIsPlayer;

            if (!mark.IsHit)
            {
                _fill.Color = InkDim;
                canvas.DrawRect(x, rises ? centre - 3f : centre + 2f, SparkBarWidth, 2f, _fill);
                continue;
            }

            var height = mark.Measured && mark.Damage > 0
                ? RailReadout.SparkBarHeight(mark.Damage)
                : RailReadout.SparkMinBar;

            // Colour follows the ACTOR, never the side of the line - that is what keeps the player's
            // own blows white on both tiles while their position flips. An UNMEASURED blow is the one
            // exception: it takes the neutral dim tone rather than the bottom of the severity ramp,
            // because the ramp's colour is a claim about how hard the blow was and nobody knows. It
            // still draws, at the minimum height - the swing happened (rule 5: unknown must look
            // unknown, not like a measured nothing).
            _fill.Color = !mark.Measured ? InkDim : mark.Mine ? Ink : IncomingRamp(mark.Damage);
            canvas.DrawRect(x, rises ? centre - height : centre + 1f, SparkBarWidth, height, _fill);
        }
    }

    /// <summary>Yellow through to bright red, saturating at <see cref="RailReadout.SparkDamageCap"/>.
    /// A blow of unknown size gets the bottom of it rather than a colour of its own: the height
    /// already says "unknown" by sitting at the minimum, and a fourth colour on a three-unit mark
    /// would be a distinction nobody can see.</summary>
    private static SKColor IncomingRamp(double damage)
    {
        var t = Math.Clamp(damage / RailReadout.SparkDamageCap, 0.0, 1.0);
        return t < 0.5
            ? Tint(Caution, NoveltyUnfought, (float)(t * 2.0))
            : Tint(NoveltyUnfought, Hostile, (float)((t - 0.5) * 2.0));
    }

    /// <summary>The direction mark: a small filled triangle, DRAWN rather than typed. An arrow
    /// character would put a non-ASCII literal in the source, which this codebase rejects, so the
    /// vector settles both at once.</summary>
    private void DrawDirectionMark(SKCanvas canvas, float x, float top, bool outgoing, SKColor color)
    {
        const float w = 6f;
        const float h = 8f;
        _arcPath.Reset();
        if (outgoing)
        {
            _arcPath.MoveTo(x, top);
            _arcPath.LineTo(x + w, top + (h / 2f));
            _arcPath.LineTo(x, top + h);
        }
        else
        {
            _arcPath.MoveTo(x + w, top);
            _arcPath.LineTo(x, top + (h / 2f));
            _arcPath.LineTo(x + w, top + h);
        }
        _arcPath.Close();

        _fill.Color = color;
        canvas.DrawPath(_arcPath, _fill);
    }

    /// <summary>
    /// The tempo frame: a border around an engaged widget whose DASH DENSITY is how often blows are
    /// landing in it.
    ///
    /// <para>Dash density reads as how soon: not hitting the thing decays it to 1 dot of color every 5
    /// pixels; swinging like a pro, it is solid. An opponent's slot carries the player's rate against
    /// that creature; the player's own tile carries the pooled incoming rate. Same language
    /// on both, so the pair reads as one exchange.</para>
    ///
    /// <para><b>Three states, on two channels, and the third one matters.</b> Density alone cannot
    /// carry them, because a hit rate of zero and no swings at all are different claims:</para>
    /// <list type="bullet">
    /// <item><b>Nothing swung yet</b> - four CORNER BRACKETS, no sides. A shape, not a density, so it
    /// cannot be read as a position on the dash scale. This is not a one-tick corner case: the player
    /// swings at ONE creature at a time, so in a pack every other live row sits here for the whole
    /// fight. It is also the only route into the state, since MUD2 prints a descriptor after every
    /// non-killing landed hit - see <c>DamagePrediction.Tempo</c>, which records why near-total
    /// coverage is a reason to keep this state rather than to drop it.</item>
    /// <item><b>Blows landing below the corpus rate</b> - a dashed frame, density carrying how far
    /// below.</item>
    /// <item><b>At or above it</b> - solid.</item>
    /// </list>
    ///
    /// <para><b>No frame at all means not engaged</b> - a resolved opponent, or the player between
    /// fights. Absence is unmistakable at a glance and must never be confused with a quiet reading,
    /// which is exactly why the no-evidence state got a shape of its own rather than a fainter
    /// frame.</para>
    ///
    /// <para>The rectangle is fixed in all three. Only the stroke changes, so nothing here reflows when
    /// a fight speeds up or stalls.</para>
    /// </summary>
    private void DrawTempoFrame(
        SKCanvas canvas, float x, float y, float width, float height, SwingTempo tempo, bool accent = false)
    {
        var tempoStroke = DamagePrediction.Tempo(tempo);
        var inset = FrameStroke / 2f;

        _stroke.Color = accent ? FrameAccent : FrameTone;
        _stroke.StrokeWidth = FrameStroke;
        _stroke.StrokeCap = SKStrokeCap.Butt;

        if (tempoStroke.Reading == DamagePrediction.TempoReading.NoEvidence)
        {
            DrawCornerBrackets(canvas, x + inset, y + inset, width - FrameStroke, height - FrameStroke);
            _stroke.StrokeWidth = 1f;
            return;
        }

        if (tempoStroke.Reading == DamagePrediction.TempoReading.Landing)
            _stroke.PathEffect = TempoDash(tempoStroke.Dot, tempoStroke.Gap);

        canvas.DrawRoundRect(
            x + inset, y + inset, width - FrameStroke, height - FrameStroke, 5f, 5f, _stroke);

        _stroke.PathEffect = null;
        _stroke.StrokeWidth = 1f;
    }

    /// <summary>The four corners of a frame and nothing between them - "this widget is engaged and has
    /// nothing to report yet". Straight Ls meeting at the true corner: at a 1.2 stroke the missing
    /// corner radius is not visible, and drawing the arc would cost a path per corner for nothing.</summary>
    private void DrawCornerBrackets(SKCanvas canvas, float x, float y, float width, float height)
    {
        const float leg = 9f;
        var right = x + width;
        var bottom = y + height;

        foreach (var (cornerX, cornerY, dx, dy) in new[]
                 {
                     (x, y, 1f, 1f), (right, y, -1f, 1f),
                     (x, bottom, 1f, -1f), (right, bottom, -1f, -1f),
                 })
        {
            canvas.DrawLine(cornerX, cornerY, cornerX + (leg * dx), cornerY, _stroke);
            canvas.DrawLine(cornerX, cornerY, cornerX, cornerY + (leg * dy), _stroke);
        }
    }

    /// <summary>
    /// The opponent row to pair with the player's device, or -1 for none.
    ///
    /// <para>This row is the one <c>MudSharp.Combat.ReachAggregate.GreatestThreat</c> picks for the
    /// player's own prediction lanes (<c>CombatLiveView.YourNextBlow</c>/<c>YourBlowAfter</c>) to
    /// project from - that selection runs on every refresh whether or not anything marks it, and the
    /// accent this drives is the ONLY on-screen sign that a selection is happening at all - see
    /// <see cref="FrameAccent"/>'s own remarks for what breaks if this is deleted again without
    /// replacing what it shows.</para>
    ///
    /// <para>Gated on being in combat and out of the grace window here (needs <see cref="CombatLiveView"/>,
    /// so cannot move to <c>ReachAggregate</c>); the two-live-opponent threshold and the actual row
    /// selection are <see cref="MudSharp.Combat.ReachAggregate.GreatestThreatForAccent"/>, pulled out
    /// pure and tested directly since this method itself - an <c>SKCanvasView</c> - is not reachable
    /// from mudsharp.Tests. The two call sites (the opponent slot and the player's own bottom row) both
    /// go through here rather than testing the conditions separately, so the two frames cannot
    /// disagree.</para>
    /// </summary>
    private int GreatestThreatRow(CombatLiveView live)
    {
        if (!live.InCombat || InGracePeriod)
            return -1;
        return ReachAggregate.GreatestThreatForAccent(live.Roster);
    }

    /// <summary>
    /// Opponents beyond the visible slots: names only, worst first. Placed at the TOP of the stack,
    /// farthest from the gaze, because it is the least actionable thing on the panel - you cannot do
    /// anything about the sixth rat.
    ///
    /// <para>The counts are taken from the PLAN, which knows about participants the row list never
    /// carried - the roster caps its published ROW list at ParticipantRoster.MaxRows == the rail's own
    /// MaxSlots, so counting from the row list alone would undercount whenever the live roster exceeds
    /// that cap.</para>
    ///
    /// <para>The names are still only the ones the roster published. That is the honest limit: past
    /// MaxRows the plan carries counts and not identities, so the list ends in an ellipsis rather than
    /// pretending the tally and the names describe the same set.</para>
    /// </summary>
    private void DrawOverflowRow(SKCanvas canvas, float y, IReadOnlyList<RosterRow> rows, int shown, RosterPlan plan)
    {
        _stroke.Color = Rule;
        _stroke.PathEffect = DashOverflow;
        canvas.DrawRoundRect(Pad, y, Content, OverflowRowHeight, 5f, 5f, _stroke);
        _stroke.PathEffect = null;

        // Every published row without a slot of its own, plus the participants the roster's cap never
        // published. Rows rather than TotalCount: a badge is one row standing for several Unseen
        // participants, and Creatures folded into it are participants with no row at all.
        var hidden = plan.HiddenCount + (rows.Count - shown);
        _text.Color = Hostile;
        canvas.DrawText("+" + hidden.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Pad + 10f, y + 15f, SKTextAlign.Left, _nameFont, _text);

        // Built and ordered by OverflowNames, which owns the sort and caches this string - see its
        // own remarks.
        _text.Color = InkDim;
        canvas.DrawText(OverflowNames(rows, shown, plan.HasHidden), Pad + 42f, y + 15f,
            SKTextAlign.Left, _smallFont, _text);

        // How many of the hidden are still swinging - the exact distinction a bare "+N" cannot make,
        // and the reason RosterPlan exists at all. Counts BOTH tails: rows the height pushed out of a
        // slot, and participants the roster's own cap never published.
        var hiddenLive = plan.HiddenLiveCount;
        for (var i = shown; i < rows.Count; i++)
        {
            if (rows[i].IsLive)
                hiddenLive++;
        }
        if (hiddenLive > 0)
        {
            _text.Color = InkDim;
            canvas.DrawText(hiddenLive.ToString(System.Globalization.CultureInfo.InvariantCulture) + " alive",
                Pad + Content - 8f, y + 15f, SKTextAlign.Right, _smallFont, _text);
        }
    }

    /// <summary>The overflow row's names string, ellipsized and ready to draw - cached, because
    /// <see cref="DrawOverflowRow"/> ran a LINQ <c>Skip</c>/<c>OrderByDescending</c>/<c>Select</c>
    /// chain plus a <c>string.Join</c> and an <see cref="Ellipsize"/> pass on every single paint with
    /// no caching at all, against this codebase's own discipline elsewhere - see
    /// <c>SidePanelViewModel._historyCache</c>, which exists for exactly this reason.
    ///
    /// <para><b>Keyed on the overflow tail's own composition, not on the roster's identity.</b> The
    /// roster is rebuilt fresh on every refresh (<c>ParticipantRoster.Build</c>), including refreshes
    /// where nothing about the OVERFLOW tail changed - a shown slot's health reading ticking, say -
    /// so keying on <c>rows</c>'s reference would never hit. What the drawn string
    /// actually depends on is each hidden row's <see cref="RosterRow.Name"/> and
    /// <see cref="RosterRow.DamageTakenFrom"/> (the sort key) plus <c>hasHidden</c> (the
    /// trailing ellipsis), so the cache key is exactly that tuple set, in the roster's own natural
    /// order - which <see cref="ParticipantRoster.Build"/> constructs deterministically, so two
    /// refreshes with an unchanged tail always produce the same key. A change to any hidden row's
    /// name or damage - the only two things a membership OR ordering change could actually be -
    /// changes that row's own key entry, which invalidates the cache correctly on the same paint.
    /// </para></summary>
    private string? _overflowNamesCache;
    private (string Name, double DamageTakenFrom)[] _overflowNamesCacheKey = [];
    private bool _overflowNamesCacheHasHidden;

    private string OverflowNames(IReadOnlyList<RosterRow> rows, int shown, bool hasHidden)
    {
        var tailCount = rows.Count - shown;
        var key = tailCount <= 0 ? [] : new (string, double)[tailCount];
        for (var i = 0; i < tailCount; i++)
        {
            var row = rows[shown + i];
            key[i] = (row.Name, row.DamageTakenFrom);
        }

        if (_overflowNamesCache is { } cached
            && hasHidden == _overflowNamesCacheHasHidden
            && key.AsSpan().SequenceEqual(_overflowNamesCacheKey))
        {
            return cached;
        }

        // Names only, ordered by how much each has actually hurt the player, worst first - "who is
        // doing the damage" is the only question a names-only row can usefully answer, and it is not
        // the same question as "who joined first", which is the order the slots themselves keep (a
        // slot that reorders itself as damage accrues would have to be re-found on every glance).
        var named = rows.Skip(shown).OrderByDescending(r => r.DamageTakenFrom).Select(r => r.Name);
        // Trailing ellipsis when the ROSTER itself truncated, so a "+9" beside two names does not read
        // as seven creatures the panel forgot to print.
        var names = string.Join(", ", named) + (hasHidden ? ", ..." : string.Empty);
        var ellipsized = Ellipsize(names, Content - 52f, _smallFont);

        _overflowNamesCacheKey = key;
        _overflowNamesCacheHasHidden = hasHidden;
        _overflowNamesCache = ellipsized;
        return ellipsized;
    }

    /// <summary>
    /// The player's own tile: the same four lines every opponent carries, in the same order, at the
    /// same sizes - the stamina bar drawn with the same size and treatment as an NPC's, just a
    /// different colour, which is the whole reason the panel can be read as a comparison at all. Two
    /// identically-shaped things, one blue and one coloured by how much trouble you are in.
    ///
    /// <para>Out of combat the readouts go grey. The numbers are still true (they ride the FES
    /// heartbeat, and the top status strip keeps showing them in full colour), but a lit alarm
    /// colour on this panel means "this is what is happening to you in this fight" - leaving the
    /// them hot after a fight ends would keep raising an alarm about a fight that is over. They are
    /// dimmed rather than removed so the row never changes shape.</para>
    ///
    /// <para><b>The one asymmetry is the numbers, and it is the honest one.</b> Line two carries the
    /// rung word AND the exact figure, where a creature's carries the word alone. The player is the
    /// single creature whose maximum MUD2 states outright; every opponent's is inferred, and an
    /// inference dressed as a reading is the thing this panel is built not to do.</para>
    /// </summary>
    private void DrawPlayerTile(SKCanvas canvas, float y, CombatLiveView live)
    {
        var wash = live.InCombat ? 1f : SpentWash;

        _fill.Color = new SKColor(0xff, 0xff, 0xff, 0x08);
        canvas.DrawRoundRect(Pad, y, Content, PlayerTileHeight, 5f, 5f, _fill);

        // The incoming half of the same border language every engaged opponent tile carries: dash
        // density is how often blows are landing on the player, pooled across everything still
        // swinging. Its rectangle is the tile's own, so only the dash ever changes.
        if (live.InCombat && !InGracePeriod)
            DrawTempoFrame(canvas, Pad, y, Content, PlayerTileHeight, live.IncomingTempo,
                accent: GreatestThreatRow(live) >= 0);

        // The persona's own name, exactly as an opponent tile carries the creature's - including the
        // emphasis rule. Bold means the player took a blow on the tick being drawn and nothing else.
        //
        // Blank until the login handshake names them - never a stand-in word, which is what "you" was.
        if (live.PlayerName is { Length: > 0 } persona)
        {
            var nameFont = live.PlayerTookDamageThisTick ? _nameBoldFont : _nameFont;
            _text.Color = InkBright;
            canvas.DrawText(
                Ellipsize(persona, SlotNameWidth, nameFont),
                TileTextLeft, y + TileNameBaseline, SKTextAlign.Left, nameFont, _text);
        }

        DrawCurrentWeapon(canvas, y + TileNameBaseline, live);
        DrawPlayerCondition(canvas, y + TileHealthBaseline, live);
        DrawAltWeapon(canvas, y + TileHealthBaseline, live);

        // The player's ladder, same seven rungs and the same two prediction lanes - pointed the other
        // way. These are the TIGHTEST bands on the panel: the denominator is a maximum the game
        // printed, where every opponent band divides by an inferred pool.
        var live5 = live.InCombat && !InGracePeriod;
        DrawPlayerBar(
            canvas, y + TileBarTop, live.StaminaCurrent, live.StaminaMax,
            live5 ? live.YourNextBlow : null, live5 ? live.YourBlowAfter : null, wash,
            live5 ? live.StaminaLostLastTick : 0,
            live5 ? live.StaminaLossUtc : null);

        DrawMagLine(canvas, y + PlayerMagTop, live, wash);

        // The player's tile, MIRRORED against an opponent's: what the player is taking on top (the
        // creatures' blows, red), what they are dealing underneath (their own, white).
        DrawStatRow(canvas, y + PlayerUpperBaseline, live.YourTaken, inbound: true, byPlayer: false);
        DrawStatRow(canvas, y + PlayerLowerBaseline, live.YourDealt, inbound: false, byPlayer: true);
        // Only the spark, mirroring the opponent badge above.
        if (_showStats)
            DrawSpark(canvas, y + PlayerSparkCentre, live.YourExchange, subjectIsPlayer: true);
    }

    /// <summary>
    /// What is in the player's hands, opposite "you" exactly as a creature's weapon sits opposite its
    /// name. Bold, because it is the current one - the ALTERNATIVE on the line below is the plain
    /// one.
    ///
    /// <para><b>Empty hands are an alarm only when they are a PROBLEM</b>, which takes two gates.
    /// Stamina below maximum, because every fight begins with the weapon unknown - the aggregator
    /// clears it per encounter and it stays clear until a line names one - so alarming unconditionally
    /// put a yellow UNARMED on the opening of every single fight, and an alarm that fires every time
    /// is one the eye learns to skip. And IN A FIGHT: MUD2 has no persistent wielded weapon at all, so
    /// out of combat there is nothing to be unarmed relative to. That second gate lives on
    /// <see cref="CombatLiveView.IsUnarmed"/>, which is held false outside combat, rather than being
    /// re-tested here - one flag, one meaning.</para>
    ///
    /// <para><b>It blinks on the client's shared cycle</b> - see <see cref="Blink"/>, which owns the
    /// period and the doctrine. In ANTIPHASE to the Death cell, so if both are alarming at once they
    /// stay two alarms rather than merging into one flashing corner. Drawn on the canvas rather than
    /// by a Composition sibling because a sibling can only reach seven characters of text by
    /// duplicating their geometry, and every such duplicate here has drifted at least once; the
    /// repaint that makes it visible is the 1 Hz flush the panel already performs.</para>
    /// </summary>
    private void DrawCurrentWeapon(SKCanvas canvas, float baseline, CombatLiveView live)
    {
        if (live.IsUnarmed)
        {
            const string unarmed = "UNARMED";
            var hurt = live.StaminaCurrent is int sta && live.StaminaMax is int max && sta < max;
            var width = _nameBoldFont.MeasureText(unarmed);
            // Yellow on the quiet half of the cycle, white on the loud one. The underline stays put
            // through both, so the word never appears to move.
            _text.Color = hurt ? (live.BlinkOffPhase ? InkBright : Caution) : InkDim;
            canvas.DrawText(unarmed, TileTextRight, baseline, SKTextAlign.Right, _nameBoldFont, _text);
            if (hurt)
            {
                _fill.Color = Caution;
                canvas.DrawRect(TileTextRight - width, baseline + 2f, width, 1f, _fill);
            }
            return;
        }

        // Carries its novelty mark, exactly as an opponent's name does. See CombatNovelty.WeaponRollup:
        // it is a rollup over everything currently engaged, which is why it belongs on the weapon
        // rather than on any one tile.
        DrawMarkedText(
            canvas, Ellipsize(live.WeaponText, SlotWeaponWidth, _nameBoldFont),
            TileTextRight, baseline, SKTextAlign.Right, _nameBoldFont, Ink, live.WeaponNovelty);
    }

    /// <summary>
    /// The player's condition in the slot a creature uses for its wound phrase: the rung word and,
    /// because this is the one creature whose maximum the game states, the figure itself.
    ///
    /// <para><b>Two fixed columns, word then figure.</b> Drawn as one concatenated string it came out
    /// as "superficially injured (7..." - the half that is a measurement lost to the half that is an
    /// adjective, and the figure is the reason this line exists on the player's tile at all. The word
    /// is ellipsized inside its own column and the figure has its own origin, so a long adjective can
    /// never reach it and the figure never moves as the word changes length.</para>
    ///
    /// <para><b>The colour is MUD2's own</b> (<see cref="CombatLiveView.StaminaAnsiColor"/>), not a
    /// second opinion computed here. The player is already reading the game's coloured stamina in the
    /// status strip, and two different colours for one number is worse than either of them alone.
    /// Falls back to the client's ratio colour only when no code has arrived.</para>
    /// </summary>
    private void DrawPlayerCondition(SKCanvas canvas, float baseline, CombatLiveView live)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        if (live.StaminaCurrent is not int cur || live.StaminaMax is not int max || max <= 0)
        {
            // No reading at all. The word STA rather than a blank or a zero - the slot is reserved and
            // has to say which instrument is silent (rule 5).
            _text.Color = InkDim;
            canvas.DrawText("STA", TileTextLeft, baseline, SKTextAlign.Left, _rungFont, _text);
            return;
        }

        var tone = StaminaTone(live, cur, max);

        // The word, a size down - it is the adjective, and it is what gives when the two cannot both
        // fit.
        if (NpcHealthRungs.LivingLabel(NpcHealthRungs.RungFor(cur, max)) is { } label)
        {
            _text.Color = Dim(tone, 0.82f);
            canvas.DrawText(
                Ellipsize(label, ConditionWordWidth, _statSmallFont),
                TileTextLeft, baseline, SKTextAlign.Left, _statSmallFont, _text);
        }

        _text.Color = tone;
        canvas.DrawText(
            "(" + cur.ToString(culture) + "/" + max.ToString(culture) + ")",
            TileTextLeft + ConditionWordWidth + 4f, baseline, SKTextAlign.Left, _rungFont, _text);
    }

    /// <summary>
    /// Room for the rung word on the player's condition line, before the figure's own fixed origin.
    ///
    /// <para><b>Sized backwards from the figure, not forwards from the word.</b> The alt-weapon group
    /// shares this line, right-aligned, and at its widest starts at about x=182; the figure needs
    /// <see cref="ConditionFigureWidth"/> and must never be clipped, so the word gets whatever is left
    /// - which is why 118 was wrong: an ordinary "(85/120)" then ran to 194 and straight into the
    /// weapon name.</para>
    ///
    /// <para>The longest living-family label, "superficially injured", does not fit and is not meant
    /// to. It ellipsizes; the figure stays put.</para>
    ///
    /// <para><b>Derived, not chosen.</b> Whatever is left of the line once the alt-weapon group and
    /// the figure have taken theirs. Written down as a number, it would silently go wrong the moment
    /// either of the other two changed.</para>
    /// </summary>
    private float ConditionWordWidth =>
        AltGroupWorstCaseLeft - TileTextLeft - ConditionFigureWidth - 4f;

    /// <summary>Reserved for the stamina figure, never ellipsized against. "(999/999)" is nine
    /// characters of Cascadia Mono at <see cref="_rungFont"/>'s 12f. A persona whose maximum runs to
    /// four digits would overflow it, and MUD2 has no such persona.</summary>
    private const float ConditionFigureWidth = 66f;

    /// <summary>
    /// Room for the ALTERNATE weapon's name, narrower than <see cref="SlotWeaponWidth"/> on line 1.
    ///
    /// <para>Line 2 is shared with the player's condition readout, which needs its word column plus an
    /// unclipped stamina figure. At the line-1 width the alt group's worst case reached x=171 and the
    /// figure ran to 174 - a 3dp collision that only appears with a long alt-weapon name, which is
    /// exactly the case nobody tests by eye. The alt name is the half that can afford to ellipsize:
    /// it is a hint about a key, not a measurement.</para>
    /// </summary>
    private const float AltWeaponWidth = 120f;

    /// <summary>What the alt-weapon group reserves besides the name: the <c>^W</c> hint and the swap
    /// mark with its gap, both at the stat size. Approximate by design - they are measured text - and
    /// generous, because this is the figure <see cref="ConditionWordWidth"/> keeps clear of.</summary>
    private const float AltHotkeyReserve = 22f;
    private const float AltMarkReserve = 15f;

    /// <summary>The leftmost x the alt-weapon group can reach, with the longest name it will draw.
    /// The condition line opposite it must end before here.</summary>
    private float AltGroupWorstCaseLeft =>
        TileTextRight - AltHotkeyReserve - AltWeaponWidth - AltMarkReserve;

    /// <summary>MUD2's own colour for the stamina figure, or the client's ratio colour when the game
    /// has not said. The mapping matches GameViewModel.AnsiToColor, which colours the same number in
    /// the status strip - the two must not disagree about one fact.</summary>
    private static SKColor StaminaTone(CombatLiveView live, int current, int max)
        => live.StaminaAnsiColor switch
        {
            1 => TerminalTheme.Palette[1],
            2 => TerminalTheme.Palette[2],
            3 => TerminalTheme.Palette[3],
            9 => TerminalTheme.Palette[9],
            10 => TerminalTheme.Palette[10],
            11 => TerminalTheme.Palette[11],
            _ => RatioColor(current, max),
        };

    /// <summary>
    /// The alternative weapon and the key that reaches it: <c>[swap] a rune axe ^W</c>, together or
    /// not at all. Naming the alternative without the key leaves the player nothing to do about it,
    /// and offering the key without naming what it swaps to is unclear.
    /// </summary>
    private void DrawAltWeapon(SKCanvas canvas, float baseline, CombatLiveView live)
    {
        // Length-checked, not merely null-checked: an empty string would advertise Ctrl+W with nothing
        // behind it, which is a dead key dressed as an affordance.
        if (live.AltWeapon is not { Length: > 0 } alt)
            return;

        var name = Ellipsize(CombatComposition.DisplayName(alt), AltWeaponWidth, _rungFont);
        var hotkey = " ^W";
        var hotkeyWidth = _rungFont.MeasureText(hotkey);

        _text.Color = InkDim;
        canvas.DrawText(hotkey, TileTextRight, baseline, SKTextAlign.Right, _rungFont, _text);

        var nameRight = TileTextRight - hotkeyWidth;
        _text.Color = Ink;
        canvas.DrawText(name, nameRight, baseline, SKTextAlign.Right, _rungFont, _text);

        if (_swapFont is null)
            return;

        var glyphRight = nameRight - _rungFont.MeasureText(name) - 3f;
        _text.Color = InkDim;
        canvas.DrawText(SwapGlyph, glyphRight, baseline, SKTextAlign.Right, _swapFont, _text);
    }

    /// <summary>
    /// The player's stamina ladder. Same bar, same notches, same lanes as an opponent's - it simply
    /// has a real denominator, so the hard and soft edges coincide and there is no fade.
    ///
    /// <para><b>The just-lost slice rides here</b>, immediately past the fill's edge: the stamina this
    /// tick took, drawn where it used to be. It fades to nothing across one tick
    /// (<see cref="Mucka.Combat.TickStaminaLoss.FadeFactor"/>, a pure function of the loss timestamp and now,
    /// sampled at paint time - no timer, Invariant #1). A slice that simply persisted would read as a
    /// live part of the gauge, and one dimmed instead of faded could not be seen at all.</para>
    /// </summary>
    private void DrawPlayerBar(
        SKCanvas canvas, float top, int? current, int? max,
        MudSharp.Combat.DamageBand? nextBlow, MudSharp.Combat.DamageBand? blowAfter, float wash,
        double lostLastTick, DateTime? lostAtUtc)
    {
        _fill.Color = SealTrack;
        canvas.DrawRect(Pad, top, Content, TileBarFillHeight, _fill);

        if (current is int cur && max is int maximum && maximum > 0)
        {
            var unit = Content / maximum;
            var filled = Math.Clamp(cur * unit, 0f, Content);
            _fill.Color = Dim(RatioColor(cur, maximum), wash);
            canvas.DrawRect(Pad, top, filled, TileBarFillHeight, _fill);

            // Past the edge, in the track: what the last tick took, still in the place it occupied.
            if (lostLastTick > 0 && lostAtUtc is DateTime lostAt)
            {
                var strength = Mucka.Combat.TickStaminaLoss.FadeFactor(lostAt, DateTime.UtcNow);
                if (strength > 0f)
                {
                    var width = Math.Min((float)lostLastTick * unit, Content - filled);
                    if (width > 0f)
                    {
                        _fill.Color = Tint(SealTrack, Hostile, strength / Mucka.Combat.TickStaminaLoss.PeakTintStrength);
                        canvas.DrawRect(Pad + filled, top, width, TileBarFillHeight, _fill);
                    }
                }
            }
        }

        DrawRungNotches(canvas, Pad, top, Content);
        DrawPredictionLane(canvas, StaminaSeal.BandArc(blowAfter), Pad, top, Content, PredictAfter);
        DrawPredictionLane(canvas, StaminaSeal.BandArc(nextBlow), Pad, top, Content, PredictNext);
    }

    /// <summary>Magic, as a two-unit strip notched at the quarters. It answers "roughly how much is
    /// left" and nothing more. A persona with no magic gets the empty track, which is a measurement
    /// (max is zero, and the game said so) rather than an unknown.
    ///
    /// <para><b>The magic FIGURE is not on the panel at all.</b> If it is wanted, it needs a slot of
    /// its own; it is not drawn anywhere else on the tile.</para></summary>
    private void DrawMagLine(SKCanvas canvas, float top, CombatLiveView live, float wash)
    {
        _fill.Color = SealTrack;
        canvas.DrawRect(Pad, top, Content, PlayerMagHeight, _fill);

        if (live.MagicMax is int magMax && magMax > 0 && live.MagicCurrent is int magCur)
        {
            _fill.Color = Dim(magCur < 20 ? Hostile : Magic, wash);
            canvas.DrawRect(Pad, top, Content * Math.Clamp(magCur / (float)magMax, 0f, 1f),
                PlayerMagHeight, _fill);
        }

        _fill.Color = PanelGround;
        for (var quarter = 1; quarter < 4; quarter++)
            canvas.DrawRect(Pad + (Content * quarter / 4f), top - 1f, 1f, PlayerMagHeight + 2f, _fill);
    }

    /// <summary>
    /// The encounter table's five columns, as WHOLE WORDS.
    ///
    /// <para>Whole words rather than abbreviations: a column nobody can name is a column nobody can
    /// use, and the headings only appear on hover anyway, which is exactly when the reader wants the
    /// word rather than the crossword clue.</para>
    ///
    /// <para>"Participants" is the widest at 12 characters, which is what the 71-unit column takes at
    /// the heading size. Do not lengthen one without checking the others still fit.</para>
    /// </summary>
    private static readonly string[] EncounterHeadings =
        ["Participants", "Opponents", "Duration", "Victory", "Death"];

    /// <summary>
    /// What each column means, shown when the pointer is on that column.
    ///
    /// <para>There is no (i) glyph: a full word
    /// plus a marker does not fit a 71-unit column, and this codebase already has an idiom for "there
    /// is more here if you ask" - the half link (see <see cref="HalfLinkInk"/>). So the hovered
    /// heading takes the dotted blue underline and its description appears above the row. Same
    /// promise, one glyph cheaper, and consistent with whatever the fly-out does next.</para>
    /// </summary>
    private static readonly string[] EncounterTips =
    [
        "Live participants in the fight, not including you.",
        "Opponents faced in this encounter so far, dead ones included.",
        "How many ticks this encounter has lasted.",
        "Projected ticks until you would win.",
        "Projected ticks until you may die.",
    ];

    /// <summary>
    /// <b>The half link - this client's idiom for "there is more here if you ask".</b>
    ///
    /// <para>A mark in this blue under a DOTTED underline means: hovering reveals more, and one day
    /// clicking may open more still. It is deliberately a broken hyperlink rather than a whole one -
    /// the thing is not a link, it does not navigate, and a solid underline would promise that it
    /// does.</para>
    ///
    /// <para><b>This is a client-wide style decision, not a combat-rail one.</b> It lives here
    /// because the rail is the first surface to need it; any other affordance of the same kind - the
    /// fly-out anchors, the fight card, whatever comes after - takes this colour and this dash rather
    /// than inventing its own, and should point at this remark rather than restating it.</para>
    ///
    /// <para><b>The anchor is FALSE, and that is intentional.</b> The whole table is the hover target,
    /// not the mark; the mark exists purely so the target announces itself, because a row that is
    /// silent until pointed at can only be found by accident. It is drawn in the heading row's own
    /// space at the heading row's own size, so revealing the headings changes ink and never
    /// geometry.</para>
    /// </summary>
    private static readonly SKColor HalfLinkInk = TerminalTheme.Palette[12];

    /// <summary>
    /// The encounter table: five fixed columns above the tick gauge.
    ///
    /// <para><b>Quiet by default, explained on demand.</b> At rest it is five numbers and nothing
    /// else. Pointing at it paints the headings and the separators that were reserved all along - the
    /// row's height never changes, because the space is reserved rather than inserted.</para>
    ///
    /// <para><b>Die is the only coloured cell</b>, and it carries the verdict for the pair: green when
    /// the fight is won with ticks to spare, through orange when it is level, to red when the creature
    /// outlasts the player. One coloured cell means red there means exactly one thing.</para>
    ///
    /// <para><b>Vic and Die are blank for the opening of every fight and that is deliberate.</b>
    /// CombatOutlook will not project before ten seconds and two landed hits, because a projection off
    /// one lucky blow is worse than none in a game where death is deletion. They draw as unknown, never
    /// as a zero.</para>
    /// </summary>
    private void DrawEncounterTable(SKCanvas canvas, float y, CombatLiveView live)
    {
        // Only when there is an ENCOUNTER to describe - a live one, or one whose duration is still on
        // screen through the post-combat window. HasEncounter is not that test: it is also set true
        // whenever the dead strip has session history to show (see SidePanelViewModel's idle branch),
        // which is most of the time. Gated on it, an idle panel drew a table whose every cell was
        // unknown - Par and Op self-suppress at one or fewer, so what reached the screen was three
        // dashes floating over nothing, reading as three stray marks rather than as a table.
        //
        // The dashes themselves stay: inside a fight, "no answer yet" is exactly what Vic and Die
        // have to say for the first few ticks, and saying it is the point.
        if (!live.InCombat && live.EncounterTicks is null)
            return;

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var column = Content / EncounterHeadings.Length;
        var headingBaseline = y + EncounterHeadingHeight - 3f;
        var valueBaseline = y + EncounterRowHeight - 3f;

        var hovered = EncounterHoverColumn;
        if (hovered >= 0)
        {
            _fill.Color = Rule;
            for (var i = 1; i < EncounterHeadings.Length; i++)
                canvas.DrawRect(Pad + (column * i), y + 1f, 1f, EncounterRowHeight - 2f, _fill);

            for (var i = 0; i < EncounterHeadings.Length; i++)
            {
                var centre = Pad + (column * i) + (column / 2f);
                var label = Ellipsize(EncounterHeadings[i], column - 4f, _statSmallFont);
                _text.Color = i == hovered ? InkBright : InkDim;
                canvas.DrawText(label, centre, headingBaseline, SKTextAlign.Center, _statSmallFont, _text);

                // The hovered heading takes the half link's dotted underline - this IS the affordance,
                // in place of an (i) that would not fit beside a whole word.
                if (i != hovered)
                    continue;
                var half = _statSmallFont.MeasureText(label) / 2f;
                _stroke.Color = HalfLinkInk;
                _stroke.PathEffect = DashHalfLink;
                canvas.DrawLine(
                    centre - half, headingBaseline + 2f, centre + half, headingBaseline + 2f, _stroke);
                _stroke.PathEffect = null;
            }
        }
        else
        {
            DrawHalfLinkAnchor(canvas, headingBaseline);
        }

        // Par and Op are counts; Dur, Vic and Die are ticks and say so on the VALUE, not the heading.
        //
        // Par and Op go BLANK while the encounter has produced at most one creature.
        // "1" and "1" is the overwhelmingly common case and says nothing anyone needs;
        // the pair earns its ink the moment either becomes a fight worth counting. Blank rather than
        // a dash here because this is not an unknown - it is known and uninteresting, which is a
        // different thing and rule 5 has no opinion about it.
        var countsWorthStating = live.LiveOpponents > 1 || live.OpponentsFaced > 1;

        // Stack-allocated rather than `new[] { ... }`: this runs on every paint of a docked panel. The
        // strings themselves are unavoidable (Skia takes strings), but the array they sit in is
        // not.
        Span<string> values =
        [
            countsWorthStating ? live.LiveOpponents.ToString(culture) : string.Empty,
            countsWorthStating ? live.OpponentsFaced.ToString(culture) : string.Empty,
            RailReadout.Ticks(live.EncounterTicks, culture),
            RailReadout.Ticks(live.TicksToVictory, culture),
            RailReadout.Ticks(live.TicksToDeath, culture),
        ];

        for (var i = 0; i < values.Length; i++)
        {
            // A count that is deliberately not stated draws nothing at all; a PROJECTION that cannot
            // be made yet draws a dimmed dash. The two look different because they are different: one
            // is a column with nothing to say, the other is the panel declining to guess - and Vic and
            // Die decline for the opening ticks of every fight, by CombatOutlook's own refusal to
            // project off one lucky blow.
            if (values[i].Length == 0)
            {
                if (i < 2)
                    continue;

                _text.Color = Dim(InkDim, GhostDim);
                canvas.DrawText(
                    "-", Pad + (column * i) + (column / 2f), valueBaseline,
                    SKTextAlign.Center, _rungFont, _text);
                continue;
            }

            var centre = Pad + (column * i) + (column / 2f);

            // The Death column is the only coloured cell on the panel, and at its worst it INVERTS
            // rather than merely reddening - see Blink for why the top of a scale escalates by
            // blinking, and why two characters of red text is not a signal where a reversed block is.
            if (i == 4 && live.Survival == MudSharp.Combat.SurvivalReading.Dire && live.BlinkOn)
            {
                var half = (_rungFont.MeasureText(values[i]) / 2f) + 4f;
                _fill.Color = Hostile;
                canvas.DrawRoundRect(
                    centre - half, valueBaseline - 11f, half * 2f, 15f, 2f, 2f, _fill);
                _text.Color = PanelGround;
            }
            else
            {
                _text.Color = i == 4 ? SurvivalTone(live.Survival) : Ink;
            }

            canvas.DrawText(
                values[i], centre, valueBaseline, SKTextAlign.Center, _rungFont, _text);
        }

        // Last, so it sits over its neighbour rather than under it.
        if (hovered >= 0 && hovered < EncounterTips.Length)
            DrawEncounterTip(canvas, y + EncounterRowHeight, EncounterTips[hovered], live);
    }

    /// <summary>
    /// One direction of a finished fight, right-aligned on a dead-strip line:
    /// <c>[mark] 155-209 @ 9.1t</c> - what was dealt in total, and the rate it went out at.
    ///
    /// <para>Same drawn direction marks and the same monospace figures the live tile uses, a size
    /// down. A fight with nothing measured draws nothing at all rather than a zero (rule 5) - an
    /// all-miss fight really did deal nothing, but a narrative-mode one merely never said.</para>
    /// </summary>
    /// <param name="inbound">Upper line takes the inward mark, lower the outward - the corpse is the
    /// subject of its own row, exactly as it was of its tile.</param>
    /// <param name="byPlayer">Whose blows these were: the colour, and whether the total is a bracket.</param>
    private void DrawDeadExchange(
        SKCanvas canvas, float baseline, ExchangeLine line, bool inbound, bool byPlayer)
    {
        if (!line.HasSamples)
            return;

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var total = byPlayer && line.Total.High > line.Total.Low
            ? line.Total.Low.ToString("0.#", culture) + "-" + line.Total.High.ToString("0.#", culture)
            : line.Total.High.ToString("0.#", culture);
        var text = line.PerTick > 0
            ? total + " @ " + line.PerTick.ToString("0.#", culture) + "t"
            : total;

        var right = Pad + Content;
        var tone = byPlayer ? InkDim : HostileDim;
        _text.Color = tone;
        canvas.DrawText(text, right, baseline, SKTextAlign.Right, _statSmallFont, _text);

        DrawDirectionMark(
            canvas, right - _statSmallFont.MeasureText(text) - 10f, baseline - 7f, !inbound, tone);
    }

    /// <summary>
    /// The hovered column's description, in a chip immediately BELOW the table - over the tick gauge.
    ///
    /// <para>Drawn on the canvas rather than as a platform tooltip because the rail is
    /// InputTransparent and takes no gestures of its own, and because a tooltip that appears where the
    /// eye already is beats one that chases the pointer.</para>
    ///
    /// <para><b>Below, not above.</b> Above the table is the player's own tile, and a chip there
    /// covered the "what you dealt" stat row and the tail of the spark every single time the pointer
    /// crossed the table. Below it is the tick gauge, which is a timer - losing sight of it for as
    /// long as someone deliberately holds the pointer on a heading costs nothing.</para>
    ///
    /// <para><b>Except when the flee pill is up.</b> The pill lives in that same row and is the one
    /// alarm on the panel that means "leave now". A label explaining what "Duration" means does not
    /// get to cover it.</para>
    /// </summary>
    private void DrawEncounterTip(SKCanvas canvas, float below, string text, CombatLiveView live)
    {
        if (live.FleePill != FleePillStatus.Hidden && !InGracePeriod)
            return;

        // Bounded and centred inside the room LEFT of the metronome toggle, not inside the whole
        // content width - centring on the full width put the chip's right edge at 351 with the toggle
        // starting at 340, so a long description covered a control. And +1 rather than +3 below the
        // table, which keeps the chip clear of the tick track at 18.5.
        DrawTipChip(canvas, text, below + 1f, Pad, Content - MetronomeReserve - 4f);
    }

    /// <summary>
    /// One hover readout, as a chip: dark ground, hairline border, one line of small text, centred
    /// inside <paramref name="usableWidth"/> from <paramref name="usableLeft"/> and clamped to it.
    ///
    /// <para>Every hover readout on this canvas draws through here, so the two - the encounter
    /// table's column descriptions and the creature-value sentence - cannot drift into two different
    /// chips. Drawn on the canvas rather than as a platform tooltip because the rail is
    /// InputTransparent and takes no gestures of its own (Invariant #0), and because a tooltip that
    /// appears where the eye already is beats one that chases the pointer.</para>
    /// </summary>
    private void DrawTipChip(SKCanvas canvas, string text, float top, float usableLeft, float usableWidth)
    {
        const float padX = 6f;
        const float height = 16f;

        var width = Math.Min(_statSmallFont.MeasureText(text) + (padX * 2f), usableWidth);
        var left = Math.Clamp(
            usableLeft + ((usableWidth - width) / 2f), usableLeft, usableLeft + usableWidth - width);

        _fill.Color = new SKColor(0x1c, 0x24, 0x27, 0xF2);
        canvas.DrawRoundRect(left, top, width, height, 3f, 3f, _fill);
        _stroke.Color = Rule;
        canvas.DrawRoundRect(left + 0.5f, top + 0.5f, width - 1f, height - 1f, 3f, 3f, _stroke);

        _text.Color = Ink;
        canvas.DrawText(
            Ellipsize(text, width - (padX * 2f), _statSmallFont),
            left + (width / 2f), top + height - 5f, SKTextAlign.Center, _statSmallFont, _text);
    }

    /// <summary>
    /// The half-link anchor: a small drawn magnifier over a dotted blue underline, at the right end of
    /// the heading row. See <see cref="HalfLinkInk"/> for what the idiom means and why it is dotted.
    ///
    /// <para>Drawn only while the table is NOT hovered - once the headings are up they have said
    /// everything the anchor was advertising. It occupies the heading row's own space either way, so
    /// nothing on the rail moves when the pointer arrives.</para>
    ///
    /// <para>Drawn rather than typed, like the direction marks: U+1F50D would need a font that has it
    /// and would be a non-ASCII literal in source, and a five-unit vector is more legible at this size
    /// than any glyph would be.</para>
    /// </summary>
    private void DrawHalfLinkAnchor(SKCanvas canvas, float baseline)
    {
        const float radius = 3.1f;
        const float underlineWidth = 11f;

        var right = Pad + Content - 2f;
        var cx = right - underlineWidth + radius + 1f;
        var cy = baseline - 4f;

        _stroke.Color = HalfLinkInk;
        _stroke.StrokeWidth = 1.1f;
        canvas.DrawCircle(cx, cy, radius, _stroke);
        canvas.DrawLine(
            cx + (radius * 0.75f), cy + (radius * 0.75f),
            cx + radius + 2f, cy + radius + 2f, _stroke);
        _stroke.StrokeWidth = 1f;

        _stroke.Color = HalfLinkInk;
        _stroke.PathEffect = DashHalfLink;
        canvas.DrawLine(right - underlineWidth, baseline + 1.5f, right, baseline + 1.5f, _stroke);
        _stroke.PathEffect = null;
    }

    /// <summary>
    /// The Death column's tone. One lookup - the reading itself is resolved once, in
    /// <see cref="MudSharp.Combat.Survival"/>, off the same projection the tier resolver uses.
    ///
    /// <para><b>Dire has no tone of its own</b>, because it is drawn inverted and blinking. Its entry
    /// here is the appearance it takes on the dark half of the cycle, which is the same red Losing
    /// wears - so the alarm reads as Losing escalating rather than as a sixth colour to learn.</para>
    /// </summary>
    private static SKColor SurvivalTone(MudSharp.Combat.SurvivalReading reading) => reading switch
    {
        MudSharp.Combat.SurvivalReading.Commanding => TerminalTheme.Palette[10],
        MudSharp.Combat.SurvivalReading.Winning => TerminalTheme.Palette[2],
        MudSharp.Combat.SurvivalReading.Even => NoveltyUnfought,
        MudSharp.Combat.SurvivalReading.Losing => Hostile,
        MudSharp.Combat.SurvivalReading.Dire => Hostile,
        _ => Ink,
    };

    /// <summary>Which encounter-table column the pointer is over, or -1 for none - the only thing on
    /// this canvas that responds to hover. Set by GamePage's pointer-only hit test; see Invariant #0
    /// for why hovering is admissible where clicking is not.
    ///
    /// <para>A column index rather than a bool because the headings and the tooltip need different
    /// answers from the same gesture: any column paints all five headings, and the one under the
    /// pointer additionally takes the half-link underline and shows its description.</para></summary>
    public int EncounterHoverColumn
    {
        get => (int)GetValue(EncounterHoverColumnProperty);
        set => SetValue(EncounterHoverColumnProperty, value);
    }

    public static readonly BindableProperty EncounterHoverColumnProperty = BindableProperty.Create(
        nameof(EncounterHoverColumn), typeof(int), typeof(CombatRailView), -1,
        propertyChanged: (bindable, _, _) => ((CombatRailView)bindable).InvalidateSurface());

    /// <summary>
    /// Whether a pointer at <paramref name="xDp"/>,<paramref name="yDp"/> is inside the encounter
    /// table, for a panel measured <paramref name="panelWidthDp"/> by <paramref name="panelHeightDp"/>.
    ///
    /// <para>Here rather than in GamePage for the same reason <see cref="TickTrackDp"/> and
    /// <see cref="FleePillDp"/> are: this canvas lays itself out in a fixed design space and scales
    /// it, so anything asked about in real dp needs the same factor, and a second copy of the
    /// arithmetic would disagree the first time a row's height changed. The bottom-up chain below is
    /// OnPaintSurface's own, read in the same order.</para>
    ///
    /// <para><b>Pointer only, and that is the whole of the exception.</b> Invariant #0 says the
    /// command box keeps the keyboard; hovering takes no focus, so a hover-driven readout does not
    /// touch it. Nothing here may grow into a click handler without answering that invariant
    /// properly.</para>
    /// </summary>
    public static int EncounterColumnAt(
        double xDp, double yDp, double panelWidthDp, double panelHeightDp, bool showStats)
    {
        if (panelWidthDp <= 0 || panelHeightDp <= 0)
            return -1;

        var design = RailWidthFor(showStats);
        var content = design - (Pad * 2f);
        var k = panelWidthDp / design;
        var x = xDp / k;
        var y = yDp / k;
        var height = panelHeightDp / k;

        var tableTop = height - EncounterBottomOffset - EncounterRowHeight;

        if (x < Pad || x > Pad + content || y < tableTop || y > tableTop + EncounterRowHeight)
            return -1;

        var column = (int)((x - Pad) / (content / EncounterHeadings.Length));
        return Math.Clamp(column, 0, EncounterHeadings.Length - 1);
    }

    /// <summary>Which opponent row's VALUE figure the pointer is over, or -1 for none. Set by
    /// GamePage's pointer-only hit test, exactly like <see cref="EncounterHoverColumn"/>, and subject
    /// to the same Invariant #0 reasoning: hovering takes no focus.</summary>
    public int NpcValueHoverRow
    {
        get => (int)GetValue(NpcValueHoverRowProperty);
        set => SetValue(NpcValueHoverRowProperty, value);
    }

    public static readonly BindableProperty NpcValueHoverRowProperty = BindableProperty.Create(
        nameof(NpcValueHoverRow), typeof(int), typeof(CombatRailView), -1,
        propertyChanged: (bindable, _, _) => ((CombatRailView)bindable).InvalidateSurface());

    /// <summary>
    /// Which opponent row's value figure a pointer at <paramref name="xDp"/>,<paramref name="yDp"/>
    /// sits on, or -1 for none.
    ///
    /// <para>An instance method where <see cref="EncounterColumnAt"/> is static, because this one
    /// needs the live count to know how many slots are on screen and the canvas already holds it -
    /// asking the caller for a number it would have to read off this same object is how two copies of
    /// a fact start disagreeing. The slot rectangle itself still comes from
    /// <see cref="RailSlotGeometry"/>, so the row this returns is the row that was drawn; only the
    /// sub-rectangle WITHIN a tile is added here, from the same constants
    /// <see cref="DrawWoundPhrase"/> draws with.</para>
    ///
    /// <para>It answers for a row whether or not that row has a value to show; the renderer draws the
    /// chip only when there is one. Keeping the hit test ignorant of the content is what stops the
    /// target moving as probes land mid-fight.</para>
    /// </summary>
    public int NpcValueRowAt(double xDp, double yDp, double panelWidthDp, double panelHeightDp)
    {
        if (panelWidthDp <= 0 || panelHeightDp <= 0)
            return -1;

        var live = _live;
        if (!live.HasEncounter)
            return -1;

        var k = panelWidthDp / RailWidth;
        var left = (Pad + TileInset + NpcValueLeft) * k;
        if (xDp < left || xDp > left + (NpcValueWidth * k))
            return -1;

        for (var i = 0; i < live.Roster.Rows.Count; i++)
        {
            if (RailSlotGeometry.RowRectDp(
                    SlotMetrics, panelWidthDp, panelHeightDp, live.Roster.Rows, i, live.Roster.LiveCount)
                is not RailRect slot)
                continue;

            var top = slot.Top + ((TileHealthBaseline - NpcValueBoxLift) * k);
            if (yDp >= top && yDp <= top + (NpcValueBoxHeight * k))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// The flee pill: <c>FLEE</c> and the key that sends it, centred at the top of the tick row.
    ///
    /// <para><b>Not a button.</b> The metronome toggle next door has a real (invisible) hit target over
    /// it, and this deliberately does not. An accidental flee is one of the most expensive single
    /// misclicks in the game - MUD2 charges a share of total score to leave, and it charges for a FAILED
    /// attempt too (a captured frame shows 102 points and a whole experience level for one that never
    /// moved the player) - and there is no room for a confirmation step inside a two-second tick. So the
    /// pill advertises Ctrl+F; it does not offer a second, softer way to fire it. Same reason the rail
    /// stays InputTransparent everywhere else (Invariant #0), with an actual cost attached.</para>
    ///
    /// <para><b>The motion is not drawn here.</b> Both alarm states pulse, and this canvas never
    /// animates (Invariant #1) - the pulsing border and background come from a Composition sibling laid
    /// OVER this canvas, sized from <see cref="FleePillDp"/> and driven by
    /// <c>GamePage.UpdateCombatFleePill</c>. What this method draws is the still chip: fill, text, and
    /// the border in the quiet state only.</para>
    ///
    /// <para><b>Why the border is conditional.</b> At Caution and EscapeNow the ring is the thing that
    /// pulses, and a static full-strength ring of the same colour underneath a pulsing one would swallow
    /// the pulse whole - <c>#FF0000</c> composited over <c>#FF0000</c> does not dim, so the border would
    /// appear to brighten from full to full and read as motionless. The fill and the text stay drawn
    /// here at every visible state, because neither of those is what moves.</para>
    /// </summary>
    private void DrawFleePill(SKCanvas canvas, float rowTop, FleePillStatus status, CombatLiveView live)
    {
        if (status == FleePillStatus.Hidden)
            return;

        var top = rowTop + PillTopInset + TickRowDrop;
        var wash = status == FleePillStatus.Visible ? PillQuietWash : 1f;

        _fill.Color = Dim(PillFill, wash);
        canvas.DrawRoundRect(PillLeft, top, PillWidth, PillHeight, PillRadius, PillRadius, _fill);

        if (status == FleePillStatus.Visible)
        {
            // Inset by half the stroke width so the ring sits inside the chip's bounds rather than
            // straddling them - the pulsing sibling is laid on the same rectangle, and a ring drawn half
            // outside it would be a pixel wider than the one that replaces it.
            _stroke.Color = Dim(PillEdge, wash);
            _stroke.StrokeWidth = PillStroke;
            canvas.DrawRoundRect(
                PillLeft + (PillStroke / 2f), top + (PillStroke / 2f),
                PillWidth - PillStroke, PillHeight - PillStroke,
                PillRadius, PillRadius, _stroke);
            _stroke.StrokeWidth = 1f;
        }

        // One centred string:
        //     ^F:  FLEE  sta:23
        // Bold at every state, like the dreamword chip's own word: the escalation is the chip's
        // brightness and the pulse, never the letterforms changing under the eye.
        //
        // The key leads, because it is the only thing that can be DONE about any of it - "^F", not
        // "Ctrl+F", for a player whose hand is already on the keyboard.
        //
        // The STAMINA is on the pill rather than left to the bar above it because this is the number
        // the decision is actually about, and at the moment of deciding the eye is on the chip.
        //
        // The PRICE is MUD2's own arithmetic (MudSharp.Combat.FleeWorth - Bartle's formula, replayed
        // exactly against every recorded flight), so the rule is right or absent: null (an input is
        // missing) prints nothing, and an estimate is never printed - the estimator this replaced
        // disagreed with every flee observed after it. Zero prints "free" rather than "-0", because it
        // is a known answer and "-0" reads as a rounding artefact. A plain word, NOT a highlight: a
        // price tag never frames the cheap band as an achievement.
        var baseline = top + (PillHeight / 2f) + 4f;
        var label = "^F:  FLEE";
        if (live.StaminaCurrent is int sta)
            label += "  sta:" + sta.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (MudSharp.Combat.FleeWorth.Cost(live.Score, live.StaminaCurrent, live.StaminaMax) is int cost)
            label += cost == 0
                ? "  free"
                : "  -" + cost.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

        // Ellipsized against the chip's own padding. The worst realistic string fits at 12f with room
        // to spare, so this should never fire - but font metrics are a platform's to choose, and a
        // label running off the chip would be unreadable at the one moment it matters.
        _text.Color = Dim(PillText, wash);
        canvas.DrawText(Ellipsize(label, PillWidth - 20f, _pillFont),
            PillLeft + (PillWidth / 2f), baseline, SKTextAlign.Center, _pillFont, _text);
    }

    /// <summary>
    /// Where the flee pill sits, in device-independent units, for a rail rendered at
    /// <paramref name="panelWidthDp"/> wide - the geometry the Composition border/background behind this
    /// canvas has to match exactly. Same contract and same reason as <see cref="TickTrackDp"/>: the rail
    /// lays itself out in its design space (376 units, or 296 with the stat rows off) and scales it, so
    /// a sibling in real dp needs the same factor, and two copies of the arithmetic would silently
    /// disagree the first time this row's height or padding changed.
    ///
    /// <para>The corner radius travels with the bounds for that same reason - it has to carry the same
    /// scale factor. It happens to equal <c>height / 2</c> today (<see cref="PillRadius"/> 10 on
    /// <see cref="PillHeight"/> 20), so re-deriving it at the call site would currently agree; that
    /// agreement is a coincidence of the two numbers, not a property to rely on.</para>
    /// </summary>
    public static (double Left, double Right, double Bottom, double Height, double Radius) FleePillDp(
        double panelWidthDp, bool showStats)
    {
        var design = RailWidthFor(showStats);
        var pillLeft = Pad + ((design - (Pad * 2f) - PillWidth) / 2f);
        var k = panelWidthDp / design;
        return (
            pillLeft * k,
            (design - pillLeft - PillWidth) * k,
            // Measured up from the panel's bottom edge, off the same offsets OnPaintSurface uses. The
            // pill lives INSIDE the tick row, which now sits above the player's tile - so that tile's
            // height is part of the chain. TickRowDrop subtracts, because a drop measured downward
            // from the row's top is a reduction measured upward from the panel's bottom.
            (TickRowBottomOffset + TickRowHeight - PillTopInset - PillHeight - TickRowDrop) * k,
            PillHeight * k,
            PillRadius * k);
    }

    /// <summary>
    /// The tick meter. Pale and grey - it is a timer, not a judgement, so it carries no colour coding
    /// and no label. It turns red at 30 stamina and glows at 20, and those are the only exceptions.
    /// </summary>
    private void DrawTickRow(SKCanvas canvas, float y, CombatLiveView live)
    {
        // The toggle is a control, not a readout, so it is drawn whether or not a fight is running.
        DrawMetronomeToggle(canvas, y, MetronomeEnabled);

        // The rest of the row is a fight instrument and exists only while something is actually
        // swinging. A tick still running in the tea room reads as a fight that never ended, and the
        // opponent count over it would be counting corpses.
        //
        // Grace counts as "not fighting". InCombat stays true for a few seconds after the last tracked
        // opponent dies so a pack straggler can rejoin the same encounter - useful bookkeeping, but it
        // does not mean anything is attacking. Leaving the track drawn with its fill stopped at zero
        // would be worse than drawing nothing: on a countdown, empty means the swing is due NOW.
        if (!live.InCombat || InGracePeriod)
            return;

        // Only the empty track is drawn here. The moving fill inside it is a Composition-driven
        // sibling behind this canvas (TickSweep) - see that class for why the UI thread must never
        // be the thing animating a 2-second progress bar.
        _fill.Color = new SKColor(0xff, 0xff, 0xff, 0x10);
        canvas.DrawRoundRect(Pad, TrackTopIn(y), TickTrackWidth, TickTrackHeight, 3f, 3f, _fill);

        // The flee pill sits OVER the gauge. Drawn last so it covers the track, and only while there
        // is a reason to leave - between fights the gauge is unobstructed.
        //
        // Grace counts as not fighting here too, and is already handled by the early return above:
        // nothing is attacking, so an instrument telling the player to run would be charging them for
        // an escape from a fight that is over.
        DrawFleePill(canvas, y, live.FleePill, live);
    }

    /// <summary>
    /// The metronome toggle, at the right end of the tick row. Drawn unconditionally - in combat and
    /// out of it - because it is a control rather than a readout: a switch that vanished when the
    /// fight ended could only be operated during a fight, which is the one moment nobody wants to be
    /// hunting for a switch.
    ///
    /// <para>Lit when armed, outline when not. The hit target is a real (invisible) MAUI button laid
    /// over this in GamePage.xaml - this canvas stays InputTransparent and takes no gestures, so
    /// Invariant #0 is kept by construction rather than by care.</para>
    /// </summary>
    private void DrawMetronomeToggle(SKCanvas canvas, float rowTop, bool armed)
    {
        var cx = Pad + Content - (MetronomeReserve / 2f) + 3f;
        var cy = rowTop + (TickRowHeight / 2f) + TickRowDrop;
        var tone = armed ? Vitality : Dim(InkDim, 0.8f);

        // A metronome: tapered body, and a pendulum arm that leans when armed and stands upright
        // when it is not - the lean is what reads as "running" at a glance, with no motion needed.
        // _metronomeBodyPath is reused rather than `new SKPath()`'d every paint - see its own remarks;
        // Reset() clears it before this call rebuilds it.
        _metronomeBodyPath.Reset();
        _metronomeBodyPath.MoveTo(cx - 5.5f, cy + 6f);
        _metronomeBodyPath.LineTo(cx + 5.5f, cy + 6f);
        _metronomeBodyPath.LineTo(cx + 2.5f, cy - 6f);
        _metronomeBodyPath.LineTo(cx - 2.5f, cy - 6f);
        _metronomeBodyPath.Close();

        _stroke.Color = tone;
        _stroke.StrokeWidth = 1.2f;
        canvas.DrawPath(_metronomeBodyPath, _stroke);

        if (armed)
        {
            _fill.Color = tone.WithAlpha(0x30);
            canvas.DrawPath(_metronomeBodyPath, _fill);
        }

        _stroke.StrokeWidth = 1.4f;
        canvas.DrawLine(cx, cy + 5f, armed ? cx + 3.5f : cx, cy - 8f, _stroke);
        _stroke.StrokeWidth = 1f;

        _fill.Color = tone;
        canvas.DrawCircle(armed ? cx + 2.2f : cx, cy - 2.5f, 1.6f, _fill);
    }

    /// <summary>
    /// How far the tick row's CONTENTS sit below the row's own centre line.
    ///
    /// <para>Applied to the contents rather than to the row's top edge on purpose. The row's position
    /// is the last link in the bottom-up chain that RailSlotGeometry divides against to decide how
    /// many opponent slots fit; moving the row itself would move the player's tile and the whole
    /// opponent stack with it and quietly change that count. Moving what is drawn INSIDE the row
    /// changes exactly what was asked for and nothing else.</para>
    /// </summary>
    private const float TickRowDrop = 6f;

    /// <summary>The tick track's top edge within its row. Shared with <see cref="TickTrackDp"/>, so
    /// the Composition sweep cannot drift off the track the canvas draws.</summary>
    private static float TrackTopIn(float rowTop)
        => rowTop + (TickRowHeight / 2f) - (TickTrackHeight / 2f) + TickRowDrop;

    /// <summary>
    /// Where the tick track sits, in device-independent units, for a rail rendered at
    /// <paramref name="panelWidthDp"/> wide - the geometry the Composition sweep behind this canvas
    /// has to match exactly.
    ///
    /// <para>The rail lays itself out in a design coordinate space - 376 units, or 296 with the stat
    /// rows off - and scales that space to
    /// whatever width it is given (see OnPaintSurface), so a sibling element positioned in real dp
    /// has to have the same scale factor applied to it. Exposing it from here rather than restating
    /// the numbers in XAML is deliberate: two copies of this arithmetic would silently disagree the
    /// first time the row's height or padding changed.</para>
    /// </summary>
    public static (double Left, double Right, double Bottom, double Height) TickTrackDp(
        double panelWidthDp, bool showStats)
    {
        var k = panelWidthDp / RailWidthFor(showStats);
        return (
            Pad * k,
            (Pad + MetronomeReserve) * k,
            // Measured up from the bottom edge, so the row-internal drop SUBTRACTS here.
            (TickRowBottomOffset + (TickRowHeight / 2f) - (TickTrackHeight / 2f) - TickRowDrop) * k,
            TickTrackHeight * k);
    }

    /// <summary>Whether the metronome click is armed. A view-level display flag rather than part of
    /// <see cref="CombatLiveView"/>: it is a client preference, not something about the fight, and
    /// putting it in the frame state would mean republishing the whole fight to toggle a switch.</summary>
    public static readonly BindableProperty MetronomeEnabledProperty = BindableProperty.Create(
        nameof(MetronomeEnabled), typeof(bool), typeof(CombatRailView), true,
        propertyChanged: (b, _, _) => ((CombatRailView)b).InvalidateSurface());

    public bool MetronomeEnabled
    {
        get => (bool)GetValue(MetronomeEnabledProperty);
        set => SetValue(MetronomeEnabledProperty, value);
    }

    /// <summary>
    /// True while the encounter is being held open ONLY by the post-kill grace window - every tracked
    /// opponent is already dead or gone.
    ///
    /// <para>Its own bindable property rather than a field of <see cref="CombatLiveView"/> because the
    /// grace flag changes on its own notification without the frame state being rebuilt; folding it
    /// into the frame would leave it stale exactly when it matters.</para>
    /// </summary>
    public static readonly BindableProperty InGracePeriodProperty = BindableProperty.Create(
        nameof(InGracePeriod), typeof(bool), typeof(CombatRailView), false,
        propertyChanged: (b, _, _) => ((CombatRailView)b).InvalidateSurface());

    public bool InGracePeriod
    {
        get => (bool)GetValue(InGracePeriodProperty);
        set => SetValue(InGracePeriodProperty, value);
    }

    // ---- Drawn icons. ASCII source only, so every glyph is a path, never a font character. --

    // ---- Helpers ---------------------------------------------------------------------------

    private static string Ellipsize(string value, float maxWidth, SKFont font)
    {
        if (string.IsNullOrEmpty(value) || font.MeasureText(value) <= maxWidth)
            return value;
        for (var len = value.Length - 1; len > 1; len--)
        {
            var candidate = string.Concat(value.AsSpan(0, len), "...");
            if (font.MeasureText(candidate) <= maxWidth)
                return candidate;
        }
        return value;
    }
}
