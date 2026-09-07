using MudSharp.Combat;
using Mucka.Terminal;
using Mucka.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace Mucka.Rendering;

/// <summary>
/// The Combat Rail's render surface. Built to tools/combat/COMBAT-RAIL-SPEC.md - read that
/// before changing anything here; most of what looks arbitrary below is a settled decision.
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
public sealed class CombatRailView : SKCanvasView
{
    // ---- Geometry (logical units; the canvas is scaled to these at paint time) -------------
    // Internal (not private): GamePage.xaml.cs sizes the host Border's WidthRequest off this value so
    // the rail's 376-unit design space maps 1:1 onto the Border's dp content area (WidthRequest minus
    // its own stroke inset) at every DPI - the rail was previously hosted in a panel narrower than
    // this by more than the Border's own stroke inset, so every glyph drew at ~0.887x its designed
    // size REGARDLESS of DPI. That is distinct from OnPaintSurface's own `scale` below, which still
    // carries the DPI factor on top of this fix: e.Info.Width is the SKCanvasView surface in PHYSICAL
    // pixels (IgnorePixelScaling is not set anywhere in this repo), so scale == 1.0 only at 100% OS
    // scaling - at 150% it is 1.5, etc.
    //
    // Derived from CombatRailResize.CombatPanelContentWidthDp (Mucka.Terminal), not the other way
    // round: that project is plain net10.0 with no dependency on this one, so it cannot reference
    // this constant - this project (Mucka, Windows-only) DOES already depend on Mucka.Terminal, so
    // the dependency has to run this direction for there to be exactly one source of truth instead of
    // two hand-kept copies. GamePage.xaml.cs's window-resize arithmetic reads the SAME
    // CombatRailResize constant, so the two sides genuinely cannot drift apart any more.
    internal const float RailWidth = (float)CombatRailResize.CombatPanelContentWidthDp;
    private const float Pad = 10f;
    private const float Content = RailWidth - (Pad * 2);

    /// <summary>
    /// A LIVE opponent's slot. 46 until the resolved rows moved out of this stack (2026-09-01) - seven
    /// corpses were eating seven full-height slots for one word each, which is where the room for a
    /// badge worth reading came from. Sized so five live slots still fit the shortest realistic rail:
    /// at a 560-unit rail the available height is 408 and five slots at 81 pitch is 405. Five covers
    /// 99.45% of encounters in the clog corpus (1268 of 1275, tools/combat/concurrency.py against the
    /// whole clog corpus, 2026-09-02 - see tools/combat/README.md's own stored result for that run),
    /// so the sizing is bought against the case that actually happens rather than against the one
    /// that happened once.
    /// </summary>
    private const float SlotHeight = 76f;
    private const float SlotGap = 5f;

    // ---- the tile, 2026-09-06 ----
    //
    // The ring seal used to sit on the left of every tile and every text line began to the right of
    // it, at x=91. Replacing it with a full-width bar gave all four lines the whole 356 units back -
    // 81 more than they had - which is what let the two damage rows and the spark fit at a size worth
    // reading. The tile height did not move: four lines and a bar still land inside the original 76.
    //
    // The PANEL's capacity did change, though, and not because of the tile: the encounter table added
    // 17 units to BottomRowHeight, which RailSlotGeometry.SlotsBottom subtracts before it divides. Five
    // live slots needed a rail of 552 units and now need 569. Five covered 99.45% of encounters in the
    // corpus, so this is a real trade and it is the owner's to accept - it is recorded here rather
    // than left for someone to rediscover from the arithmetic.
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
    /// text baseline.
    ///
    /// <para><b>It was <c>TileUpperBaseline - 9</c> and that overdrew the ladder.</b> Marks grow up to
    /// <see cref="SparkMaxBar"/> either side of this line, so a centre at 47 put a player-side bar's
    /// top at 33 - through the prediction lane at 41-43 for any blow over ~1.8, and into the ladder
    /// fill at 34-40 for anything over ~7.3, which is the midpoint of an ordinary
    /// <c>You hit the rat (5-9)</c>. The spark occupies x 264-366 where the ladder spans the full
    /// width, so the collision was real and took out the last two rungs of the health readout.</para>
    ///
    /// <para>Sits below the bar's band (34-43) with the full bar height clear above it, and the tile's
    /// own bottom edge clear below. There is no clip anywhere on this canvas; the geometry has to be
    /// right rather than contained.</para>
    /// </summary>
    private const float TileSparkCentre = 59f;

    /// <summary>The same, on the player's tile, which additionally has the magic strip at 45-47 to
    /// clear.</summary>
    private const float PlayerSparkCentre = 64f;

    /// <summary>The magic line's own strip, drawn on the player's tile only, immediately under the
    /// stamina bar. Two units high with notches at the quarters - it answers "roughly how much is
    /// left" and nothing more, because a caster who needs the exact figure has it in the seal slot
    /// above.</summary>
    private const float PlayerMagTop = 45f;
    private const float PlayerMagHeight = 2f;
    private const float PlayerUpperBaseline = 61f;
    private const float PlayerLowerBaseline = 76f;
    private const float PlayerTileHeight = 81f;

    // The stat row's four fixed columns and the spark that follows them. Fixed centres, so the two
    // rows of a tile line up with each other and with every other tile's - the owner's requirement
    // when the sizes inside the blow-shape group stopped them lining up on their own.
    private const float StatMarkWidth = 14f;
    private const float StatTotalWidth = 88f;
    private const float StatShapeWidth = 96f;
    private const float StatDptWidth = 52f;
    private const float StatMarkLeft = Pad;
    private const float StatTotalLeft = StatMarkLeft + StatMarkWidth;
    private const float StatShapeLeft = StatTotalLeft + StatTotalWidth;
    private const float StatDptLeft = StatShapeLeft + StatShapeWidth;
    private const float SparkLeft = StatDptLeft + StatDptWidth + 4f;
    private const float SparkWidth = Pad + Content - SparkLeft;

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

    /// <summary>One swing's slot in the spark, and the widest bar it can draw. Damage saturates the
    /// bar at <see cref="SparkDamageCap"/>: beyond that the creature is already hitting for more than
    /// any single tick of headroom the player is likely to have, and a taller mark would only be
    /// re-stating that in a way that squeezed every other mark shorter.</summary>
    private const float SparkPitch = 5.5f;
    private const float SparkBarWidth = 3f;
    private const float SparkMinBar = 3f;
    private const float SparkMaxBar = 14f;
    private const double SparkDamageCap = 20.0;

    /// <summary>
    /// One line of the top-anchored dead strip.
    ///
    /// <para><b>Why the dead move and the living do not.</b> The owner, 2026-09-01: "the dead npcs
    /// should be in a separate horizontal grid/list, which is top anchored. we have a block on
    /// shuffling, but when an npc dies, the adjustment is genuinely valuable - its information."</para>
    ///
    /// <para>That is the flight-instrumentation rule applied correctly rather than suspended. The rule
    /// exists so the eye never has to re-find something that has NOT changed. A death has changed
    /// something, and the strip growing by a line is the panel saying so - signal, not noise. The live
    /// stack stays bottom-anchored and never moves, because the fight is what the eye is trained on.
    /// Do not "fix" this back into a fixed grid.</para>
    ///
    /// <para><b>2026-09-02: one row per ending, appended at the BOTTOM, chronological.</b> The strip
    /// used to group resolved participants by outcome ("KILLED rat3, rat7, rat9"), which collapsed the
    /// one thing the owner actually wanted - "I'd like each kill to be its own row, a history...
    /// otherwise just shows as many as the window size allows". New rows go at the bottom of the
    /// strip, not the top: the strip is top-anchored and grows downward, and the "nothing moves" rule
    /// above applies to EVERY row already drawn, including the ones from a fight that finished minutes
    /// ago - prepending would shift the whole history down by one line on every death, which is
    /// exactly the re-find-it-on-every-glance failure this section exists to prevent. This was also
    /// true in intent but NOT in fact for one release: the fold and the live tail both iterated their
    /// source in first-engaged order rather than resolution order, so two rows could still swap
    /// relative position (and move) the moment the earlier-engaged one resolved - fixed by sorting
    /// both by EndedUtc; see <see cref="Mucka.ViewModels.CombatEndingOrder"/>. See
    /// <see cref="DrawDeadStrip"/> and <c>SidePanelViewModel.BuildDeadStripHistory</c>.</para>
    /// </summary>
    /// <summary>One ending's whole block, which is now TWO lines rather than one (owner, 2026-09-06:
    /// the strip carries the exchange summary and the kill award as well as the name and the outcome).
    /// Named "line" because RailSlotGeometry.PlanDeadStrip budgets in these units and does not care
    /// what is drawn inside one - it is the strip's row PITCH, and the two sub-lines are
    /// DeadSubLineHeight apart within it.</summary>
    private const float DeadLineHeight = 24f;

    /// <summary>
    /// The dead strip's two grouping separators' own vertical allowance - 2026-09-02, the owner
    /// verbatim: "put a 1px yellow dotted separator between encounters... put a 2px white solid line
    /// between resets (I run the client for long times)". This is the EXTRA space reserved between two
    /// rows for the line and its clearance, not the stroke width itself: <see cref="DeadLineHeight"/>'s
    /// own ~3f of slack around a 10f font is not enough room for even a 1px stroke to sit in without
    /// touching the glyphs on either side of it, so a separator gets its own budget rather than
    /// borrowing that gap. Equal for both kinds on purpose - the allowance's job is clearance, not
    /// visual weight, which the stroke width and dash/solid style already carry.
    ///
    /// <para>Read by <see cref="Mucka.ViewModels.RailSlotGeometry.PlanDeadStrip"/> too, as the two
    /// height parameters passed in from here - the single source of truth for what counts as a
    /// boundary is <see cref="Mucka.ViewModels.RailSlotGeometry.SeparatorBetween"/>, shared by both
    /// this drawing method and that arithmetic so they can never disagree about where a line
    /// falls.</para>
    /// </summary>
    private const float DeadStripEncounterSeparatorHeight = 6f;
    private const float DeadStripResetSeparatorHeight = 6f;

    /// <summary>The encounter table above the tick gauge: a heading row that is RESERVED always and
    /// painted only on hover, and the value row under it. Reserved rather than inserted because the
    /// owner's standing preference is against dynamic positioning - headings that appeared on hover
    /// would push the whole rail down every time the pointer crossed it.</summary>
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
    private const float TickTrackHeight = 5f;

    /// <summary>Width reserved at the RIGHT end of the tick row for the metronome toggle. Taken out
    /// of the track rather than laid beside it, so the row's overall geometry is unchanged and the
    /// toggle occupies space that is reserved whether or not it is switched on (spec rule 3).</summary>
    private const float MetronomeReserve = 26f;

    private const float TickTrackWidth = Content - MetronomeReserve;
    private const float SealSize = 92f;

    /// <summary>The tempo border's inset inside an opponent slot and around the player's device, and
    /// its stroke. Both fixed: the border is a frame on a rectangle whose bounds never change, so
    /// nothing here can reflow when a fight's rate does (only the DASH changes).</summary>
    private const float FrameStroke = 1.2f;

    /// <summary>The flee pill, at the TOP of the middle column - the band above the weapon line, level
    /// with the upper arc of both seal rings, so the pill sits visually between the two stamina-shaped
    /// readouts the flee decision is actually about. Reserved whether or not the pill is drawn (rule 3);
    /// nothing else in the column moves when it lights up.</summary>
    private const float PillHeight = 20f;
    /// <summary>4, not 5: the pill sits inside the tick row and the row's contents are dropped
    /// <see cref="TickRowDrop"/>, so 5 + 6 + 20 put its lower edge one unit past the row. It landed in
    /// the panel's bottom padding and was invisible, which is exactly the kind of off-by-one that stops
    /// being invisible the next time the row's height changes.</summary>
    private const float PillTopInset = 4f;

    /// <summary>The pill is WIDER than the middle column the weapon lines use, and centred on the panel
    /// rather than on that column. It has to be: the chip now carries stamina and a price as well as the
    /// word and the key, and 116dp could not hold them.
    ///
    /// <para>The extra width is taken over the seals' bounding BOXES without touching their drawn rings.
    /// A seal is a circle of radius 39 centred 46dp down its 92dp box, so at the pill's vertical centre
    /// (~15dp from the row's top) the ring has narrowed to cx +/- 23.7 - the left ring reaches x=83 at
    /// most, the right no further left than x=253. 92..244 clears both by roughly 9dp. Do not widen
    /// further without redoing that arithmetic; the rings bulge fast further down.</para></summary>
    /// <summary>Centred over the tick gauge, where the pill now lives (owner, 2026-09-06). It covers
    /// the gauge only while it is showing, which is the point: between fights the tick bar is
    /// unobstructed, and when there is a reason to leave the alarm sits on top of the one instrument
    /// the eye is already returning to every two seconds.</summary>
    private const float PillWidth = 220f;
    private const float PillLeft = Pad + ((Content - PillWidth) / 2f);

    /// <summary>The pill is the floating dreamword chip's treatment in reds - a FILLED chip with a
    /// bright 2dp border and white bold text, not the thin dim outline the rest of this panel favours.
    /// Owner's call, 2026-08-28. Geometry matched to that chip: corner radius 10, 2dp stroke, the same
    /// roughly-10dp horizontal breathing room its Padding gives.
    ///
    /// <para>Note these are the panel's only colours NOT derived from <c>TerminalTheme.Palette</c>
    /// (section 11) - <c>#FF0000</c> is a pure red the Campbell palette does not contain (its bright red
    /// is <c>#E74856</c>), and the owner named it directly. Recorded as an explicit override rather than
    /// left looking like drift.</para></summary>
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
    /// is 13 (2026-08-29, one encounter; tools/combat/concurrency.py against the whole clog corpus,
    /// 1275 encounters, re-run 2026-09-02 - see tools/combat/README.md's own stored result for that
    /// run, which superseded an earlier 984-encounter/max-7 figure this comment used to cite).
    ///
    /// <para>This comment used to say 4, then 7, and call the cap a guard for "the pathological case,
    /// not the normal one" - all three readings wrong, or at least each stale the moment the corpus
    /// grew past it. The peak has moved twice already and there is no reason to expect it has finished
    /// moving, so the cap deliberately does NOT chase it: live opponents already exceed MaxSlots by a
    /// comfortable margin in the worst observed cases, and the overflow row
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
    private static readonly SKColor Caution = TerminalTheme.Palette[11];
    /// <summary>How far up its ladder an opponent still is. ONE hue for every creature and every rung:
    /// the arc carries the position and the notches carry the steps, and colouring the fill by the rung
    /// would say the same thing twice in a channel the player would then have to learn. The player's own
    /// seal IS ratio-coloured, because it agrees with the status strip at the top of the window about a
    /// number the game prints them; nothing prints an opponent's.</summary>
    private static readonly SKColor Vitality = TerminalTheme.Palette[6];   // #3A96DD
    private static readonly SKColor VitalityStale = new(0x3d, 0x55, 0x59);

    /// <summary>The seal's unfilled track - the whole ladder, unclimbed. Same value the player's own
    /// seals use for theirs, so the two read as the same instrument.</summary>
    private static readonly SKColor SealTrack = Dim(TerminalTheme.Palette[8], 0.30f);

    // The spent slice that has just gone lived here as a fixed-strength static field
    // (SealTrackJustLost, Tint(SealTrack, Hostile, 0.35f)) until 2026-09-02. The owner: "it seems to
    // stay red for multiple ticks; also, I'd like to reduce the *amount* of red... I'd like it to fade
    // red->final gray over the combat tick." A tint that fades cannot be a compile-time constant, so
    // it is now computed at paint time in DrawPlayerBar from Mucka.Core.TickStaminaLoss.FadeFactor - see
    // that method's own remarks for the peak (down to 0.18) and the one-tick linear fade.

    /// <summary>The ring drawn for a species nothing is known about. Deliberately bright enough to
    /// read (3.8:1 on the panel's #101618) rather than the near-invisible greys the rest of the
    /// panel's chrome uses: this is the MOST dangerous state on the slot and it may not be the
    /// quietest thing in it.</summary>
    private static readonly SKColor SealUnknown = Dim(TerminalTheme.Palette[7], 0.55f);

    /// <summary>The panel's own ground, for the notches that cut a ring into MUD2's seven rung steps.
    /// Matches GamePage.xaml's Border background - the canvas clears to transparent and composites over
    /// it, so a notch in this colour reads as a gap in the ring.</summary>
    private static readonly SKColor PanelGround = new(0x10, 0x16, 0x18);

    /// <summary>
    /// The two rDPT prediction bands: the next landed blow, and the one after it.
    ///
    /// <para><b>The colour clash, and the rule that resolves it.</b> Red already means "the enemy" on
    /// this panel - the creature's weapon and its damage figures are drawn in it. Here red marks the
    /// player's own progress, which is good news. The rule that makes both readable is POSITIONAL, not
    /// chromatic: everything in the lane outside a ring is where the NEXT BLOW lands, whoever throws
    /// it - drawn in the same two-lane grammar on every ring the panel has, opponent and player alike
    /// (the player's own device used to be the one exception, carrying a reach chevron instead; that
    /// is gone as of 2026-09-02, an "indicator too many" on the owner's own reading once these bands
    /// existed - see MudSharp.Combat.StaminaSeal's remarks on ReachAggregate). Every other red on the
    /// panel is text inside a slot. The owner has explicitly not settled colour coding here ("The next
    /// cue would be color coding that or something"), so this is the working rule and not a finding.</para>
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
    /// <para><b>What it means, as of the 2026-09-02 rewrite.</b> <c>ReachAggregate.GreatestThreat</c>
    /// picks the ONE live opponent the player's own prediction lanes (<c>CombatLiveView.YourNextBlow</c>/
    /// <c>YourBlowAfter</c>) project their damage bracket from - that selection runs on every refresh
    /// whether or not anything marks it. This accent is the ONLY on-screen indication that it is
    /// happening at all: without it, if the selection ever picked the wrong creature, nothing would
    /// show it - the prediction lanes would just be quietly wrong about which opponent they describe,
    /// with no way to notice from the screen. <b>Do not delete this as decoration without moving that
    /// visibility somewhere else first.</b></para>
    ///
    /// <para><b>This field was deleted 2026-09-02 and restored the SAME day.</b> It used to be
    /// justified as a pairing with the reach chevron on the player's own seal ("the single opponent
    /// whose measured reach is the one the player's chevron was drawn from") - when the chevron was
    /// deleted (an owner-requested "indicator too many"), that reading went with it and the accent was
    /// deleted too, on the reasoning that a pairing with a now-nonexistent mark could not mean anything.
    /// That reasoning was wrong: it is not a pairing with the chevron, it never was one in substance,
    /// and the selection it reflects is a real, currently-running piece of state
    /// (<c>SidePanelViewModel.IncomingPerBlowOf</c>) that has nothing to do with the chevron's own
    /// existence. The accent was restored the same day once that was noticed.</para>
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
    private static readonly SKColor NoveltyUnfought = new(0xF0, 0x88, 0x3E);
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
    /// <para><b>Why brightness is held and only hue moves.</b> The owner asked for "some distinction
    /// between the results with a bit of color -- but light, so it's not drawing the eye too much".
    /// Those two wants pull against each other under a plain mix: pushing a grey toward yellow
    /// brightens it, and brightness is the channel that actually pulls the eye. Holding luminance
    /// constant and moving only the hue gives a difference the eye can read once it looks at the strip,
    /// without giving the strip a reason to be looked at. The rail is glance instrumentation and the
    /// live stack below it is what the eye is trained on; a settled ending must never outrank a live
    /// opponent.</para>
    ///
    /// <para><b>This replaced an absolute per-channel nudge that was invisible on screen.</b> The first
    /// attempt took the owner's "(0x0a in rgb)" literally as a magnitude - 10/255 against
    /// <see cref="InkDim"/>'s #767676, a shift of under 9% - and he confirmed from a screenshot that it
    /// read as no colour at all. The instruction was about restraint, not about that arithmetic.</para>
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
    /// Clio's colorcode() ladder, ported so the stamina seal agrees with the status strip at
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
    /// before every mark rather than <c>new SKPath()</c>'d per call (2026-09-02 review finding, then
    /// against the ring arcs this replaced: it runs twice per tile, so ~12 times per paint). Never held
    /// past the <c>DrawPath</c> call that consumes it, so reusing it across draws is safe - Skia has
    /// already copied whatever it needs onto the canvas by the time the next caller resets it.</summary>
    private readonly SKPath _arcPath = new();
    /// <summary>Scratch path for <see cref="DrawMetronomeToggle"/> - same reasoning as
    /// <see cref="_arcPath"/>, and safe for the same reason: consumed by <c>DrawPath</c>/fill before
    /// the next paint ever touches it again.</summary>
    private readonly SKPath _metronomeBodyPath = new();
    private readonly SKFont _nameFont = new(SKTypeface.Default, 13.5f);
    /// <summary>A live creature's name, bold (owner, 2026-09-06). Falls back to the regular cut where
    /// the platform has no bold face, exactly as <see cref="_pillFont"/> does - the name loses its
    /// emphasis rather than going missing. A RESOLVED row keeps <see cref="_nameFont"/>: the dead
    /// strip is a record, not a thing to be watched.</summary>
    private readonly SKFont _nameBoldFont = new(
        SKTypeface.FromFamilyName(SKTypeface.Default.FamilyName, SKFontStyle.Bold) ?? SKTypeface.Default, 13.5f);
    private readonly SKFont _phraseFont = new(SKTypeface.FromFamilyName("Cascadia Mono") ?? SKTypeface.Default, 13f);
    /// <summary>The wound phrase and the stat rows, a point down from <see cref="_phraseFont"/>
    /// (owner, 2026-09-06). Monospace and tabular, which is what actually holds the two stat rows in
    /// register: the fixed column origins place the groups, and equal digit advances line the figures
    /// up inside them.</summary>
    private readonly SKFont _rungFont = new(SKTypeface.FromFamilyName("Cascadia Mono") ?? SKTypeface.Default, 12f);
    /// <summary>The blow-shape group's outer two figures, and the ghost labels. Same face as the stat
    /// row so the digits still align, two points down so the eye is told which figure is the one that
    /// matters without the colour having to carry it alone.</summary>
    private readonly SKFont _statSmallFont = new(
        SKTypeface.FromFamilyName("Cascadia Mono") ?? SKTypeface.Default, 10f);

    /// <summary>The swap mark on the alternate-weapon line, U+1F5D8 (owner's choice, 2026-09-06).
    /// Written as an escape because this codebase rejects non-ASCII characters in source; the escape
    /// is the same character either way.</summary>
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
    // Every one of these used to be an SKPathEffect.CreateDash() INSIDE the paint handler, run on
    // every paint and never disposed - five sites, two of them inside the per-opponent loop, so a
    // busy pack fight allocated (and leaked the native handle of) well over a dozen per frame. A
    // dash effect is a native Skia object behind a finalizable wrapper; churning them on the render
    // path is exactly the allocation storm Invariant #1 forbids, and dropping the reference without
    // Dispose leaves the native side to the finalizer queue.
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
    private SKPathEffect? _dashHalfLink;    // 1 on, 2 off - the half-link underline (see HalfLinkInk)

    private SKPathEffect DashResetRule => _dashResetRule ??= SKPathEffect.CreateDash([1f, 3f], 0f);
    private SKPathEffect DashOverflow  => _dashOverflow  ??= SKPathEffect.CreateDash([3f, 3f], 0f);
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
    /// match here: <c>SidePanelViewModel.RefreshCombatSignals</c> allocates a fresh
    /// <see cref="CombatLiveView"/> on every refresh (a <c>with</c>-expression in the idle branch,
    /// <c>new</c> in the two in-combat branches), including the 1 Hz anti-idle tick that runs
    /// whether or not anything actually changed - so a reference check alone forced a repaint every
    /// second for as long as the summary stayed on screen, contradicting Invariant #1.
    /// <c>ReferenceEquals</c> is kept ahead of the value compare purely as a fast path, since the
    /// idle branch's <c>with</c>-expression reuses the same instance often enough for that to pay
    /// for itself.</para>
    ///
    /// <para><b>Member-wise equality is NOT automatically structural, and adding this compare was
    /// once mistaken for the whole fix (2026-09-02).</b> <see cref="CombatLiveView"/> is a record
    /// and <c>RosterPlan</c> a record struct, so both get synthesized member-wise equality - but
    /// <c>RosterPlan.Rows</c> is an <c>IReadOnlyList&lt;RosterRow&gt;</c>, and the synthesized
    /// compare for a reference-typed member of that shape is REFERENCE equality.
    /// <c>ParticipantRoster.Build</c> allocates a fresh list every refresh, so on the in-combat
    /// branch - the only branch reached while a fight is open - the compare below decided
    /// "different" every time and the rail still repainted at 1 Hz. <c>RosterPlan</c> now declares
    /// its own element-wise <c>Equals</c>, which is what actually makes this setter's check bite;
    /// see that method's remarks. If a collection-typed member is ever added to
    /// <see cref="CombatLiveView"/> itself, it needs the same treatment or it will silently
    /// reintroduce this - the record's synthesized equality will not tell you.
    /// (<c>DeadStripHistory</c> is safe today only because the view model publishes a CACHED
    /// instance and reallocates it only when the archive actually grows.)</para>
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

        // Everything is placed relative to the BOTTOM edge and worked upward.
        var tickTop = height - Pad - TickRowHeight;
        var bottomRowTop = tickTop - BottomRowHeight;

        DrawTickRow(canvas, tickTop, live);
        DrawBottomRow(canvas, bottomRowTop, live);
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

        // LIVE opponents only, bottom-anchored. Capacity, the overflow rule and the per-slot y all come
        // from RailSlotGeometry rather than being computed here: GamePage's float overlay has to place a
        // number over the same rectangle this loop draws, and a second copy of the arithmetic would
        // disagree the first time either side's constants moved.
        //
        // Counted on LIVE participants rather than the published row list. The roster caps its rows at
        // ParticipantRoster.MaxRows, so deciding the overflow on rows.Count made the tail invisible at
        // any height that fits the whole capped list - which is every ordinary window - and a
        // fourteen-rat fight drew eight rats and said nothing about the other six.
        var liveCount = live.Roster.LiveCount;
        var overflow = liveCount > RailSlotGeometry.Capacity(SlotMetrics, height);
        // Clamped against the published rows purely so an indexing slip can never be a crash. Live rows
        // sort first, so this cannot actually bind.
        var shown = Math.Min(RailSlotGeometry.ShownSlots(SlotMetrics, height, liveCount), rows.Count);

        var y = (float)RailSlotGeometry.SlotsBottom(SlotMetrics, height) - SlotHeight;
        var threatRow = GreatestThreatRow(live);
        for (var i = 0; i < shown; i++)
        {
            // Every live row gets its own prediction, not just the current target: the bands come from
            // per-creature brackets and per-creature pool estimates, so each one is about the creature
            // it is drawn on.
            DrawOpponentSlot(canvas, y, rows[i], i == threatRow);
            y -= SlotHeight + SlotGap;
        }

        // The overflow row is a tail of the LIVE roster, so it belongs with the live region - directly
        // above the last live slot, not up with the dead. Compact for the same reason the dead strip is:
        // it is one line of text and used to reserve a whole slot for it.
        if (overflow)
        {
            y += SlotHeight - OverflowRowHeight;
            DrawOverflowRow(canvas, y, rows, shown, live.Roster);
        }

        var stack = RailSlotGeometry.LiveStackHeight(
            SlotMetrics, shown, overflow ? OverflowRowHeight : 0);
        DrawDeadStrip(
            canvas, (float)RailSlotGeometry.DeadStripBottom(SlotMetrics, height, stack),
            live.DeadStripHistory);
    }

    /// <summary>
    /// The settled dead: every combat ENDING this session, one row each, in a compact top-anchored
    /// strip.
    ///
    /// <para><b>Why the dead move and the living do not.</b> The owner, 2026-09-01: "the dead npcs
    /// should be in a separate horizontal grid/list, which is top anchored. we have a block on
    /// shuffling, but when an npc dies, the adjustment is genuinely valuable - its information."</para>
    ///
    /// <para>That is the flight-instrumentation rule applied, not suspended. The rule exists so the eye
    /// never has to re-find something that has NOT changed; a death HAS changed something, and the
    /// strip growing a line is the panel saying so. The live stack below stays bottom-anchored and is
    /// sized from the full rail height, so a death can never move it. The two regions grow toward the
    /// gap between them, and when they meet it is the DEAD that gives - which is the region where
    /// movement is acceptable. Do not "fix" this back into a fixed grid.</para>
    ///
    /// <para><b>One row per ending, session-scoped, not grouped and not per-encounter.</b> The owner,
    /// 2026-09-02: "I'd like each kill to be its own row, a history, for this session, the recent ones
    /// (last 3-4 ticks) with bold text... otherwise just shows as many as the window size allows
    /// without intruding over live npcs." <paramref name="history"/> already IS that history,
    /// chronological oldest-first - <see cref="SidePanelViewModel.BuildDeadStripHistory"/> owns
    /// stitching the session archive to the current encounter's own endings, so this method only lays
    /// rows out and truncates from the front when there are more than fit.</para>
    ///
    /// <para><b>Combat ENDINGS, not just kills</b> - the owner's own distinction: "that way you can
    /// *tell* the dwarf13 fled instead of died." Every <see cref="FightOutcome"/> a fight can resolve
    /// to gets its own row and its own word (<see cref="OutcomeWord"/>); a slight tint on the word
    /// marks the off-nominal endings (<see cref="OutcomeTint"/>) without ever replacing the word
    /// itself.</para>
    ///
    /// <para><b>Truncation drops the OLDEST, newest always wins.</b> When more endings exist than the
    /// available height can show, the top row becomes a dimmed "+N earlier" marker and the newest
    /// entries fill the rest - the reverse of the old grouped strip's trailing "...", which said
    /// nothing about which endings were missing.</para>
    /// </summary>
    private void DrawDeadStrip(SKCanvas canvas, float floor, IReadOnlyList<CombatEnding> history)
    {
        if (history.Count == 0)
            return;

        var startY = Pad + DeadLineHeight;
        // Row-count/truncation arithmetic lives in RailSlotGeometry (2026-09-02) so mudsharp.Tests can
        // pin it directly - this class is an SKCanvasView and unreachable from a unit test. See
        // RailSlotGeometry.PlanDeadStrip for the capacity-1 rule (show the newest ending rather than a
        // marker with nothing under it) that replaced this method's own off-by-one: the marker used to
        // be reserved a row before ShownStart accounted for it, so the printed "+N" always undercounted
        // by exactly the row it had just taken. The two separator heights are the SAME constants this
        // method draws with, below - PlanDeadStrip needs them to plan around, and passing anything else
        // would let the plan and the paint disagree about how much room a boundary takes.
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

            // Two lines per ending (owner, 2026-09-06): the creature and what it cost above, how it
            // ended and what it cost the player below. The exchange summary is the only place a
            // finished fight's figures survive - its tile is gone.
            var nameBaseline = y - DeadSubLineHeight;

            _text.Color = Ink;
            canvas.DrawText(
                Ellipsize(ending.Name, DeadNameWidth, font), Pad, nameBaseline, SKTextAlign.Left,
                font, _text);

            _text.Color = ending.Outcome == FightOutcome.Died ? Hostile : OutcomeTint(InkDim, ending.Outcome);
            canvas.DrawText(
                Ellipsize(OutcomeWord(ending.Outcome), DeadNameWidth, font), Pad, y,
                SKTextAlign.Left, font, _text);

            // The award, when one was paired to this kill. Never a zero - see CombatEnding.ScoreAwarded
            // for why the pairing is an inference and null means "no announcement", not "worth nothing".
            if (ending.ScoreAwarded is int award)
            {
                _text.Color = Caution;
                canvas.DrawText(
                    "+" + award.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
                    DrawDeadStripSeparator(canvas, y, DeadLineHeight, allowance, kind);
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
    /// allowance <paramref name="rowLineHeight"/> and <paramref name="allowance"/> reserve for it, the
    /// same figures <c>RailSlotGeometry.PlanDeadStrip</c> budgeted this boundary against.
    /// </summary>
    private void DrawDeadStripSeparator(
        SKCanvas canvas, float rowY, float rowLineHeight, float allowance, DeadStripSeparatorKind kind)
    {
        var lineY = rowY + ((rowLineHeight + allowance) / 2f);
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
    /// <see cref="Mucka.Core.CombatTiming.TickMilliseconds"/> rather than hardcoded so the two can
    /// never drift apart. The owner asked for "the recent ones (last 3-4 ticks)"; four is the generous end of that
    /// range rather than the stingy one; on this rail, missing a recent ending reads as it having
    /// already faded from notice, which is worse than it staying bold one tick longer than strictly
    /// necessary.</summary>
    private static readonly double DeadStripRecentWindowMs = Mucka.Core.CombatTiming.TickMilliseconds * 4.0;

    /// <summary>
    /// The dead strip's tint mapping over <see cref="FightOutcome"/> - the owner's own words, 2026-09-01:
    /// "perhaps we use a *slight* yellow tint to the off-nominal kill endings, and a slight red if you
    /// fled (0x0a in rgb)", and after seeing the first attempt on screen, 2026-09-02: "definitely want
    /// some distinction between the results with a bit of color -- but light, so it's not drawing the
    /// eye too much". "Slight" and "light" are doing real work: this recolours the OUTCOME WORD only,
    /// never the name beside it, and never replaces the word - the word is MUD2's own vocabulary and is
    /// what actually says how the fight ended. <see cref="HueOnly"/> is what keeps "a bit of color" from
    /// turning into "brighter", and its remarks record why the literal 0x0a reading failed.
    ///
    /// <para><b>Yellow: endings where the creature was not cleanly killed.</b> <see cref="FightOutcome.CFled"/>
    /// and <see cref="FightOutcome.CFledFail"/> both leave it alive (fled clean, or broke off still
    /// standing in the room); <see cref="FightOutcome.Withdraw"/> is a mutual accepted withdraw, not a
    /// kill either; <see cref="FightOutcome.NoMore"/> is explicitly NOT a kill by its own doc comment -
    /// "MUD2 printed no 'You have killed the X.' and... credited nothing" - which is exactly "lost to
    /// something that was not your blow"; <see cref="FightOutcome.EndOther"/> is the game closing a
    /// fight without saying why, which is not a kill claim either; <see cref="FightOutcome.Interrupted"/>
    /// is the client ending the encounter because the world went away (reset/logout/room change/exit),
    /// where the creature is not merely un-killed but was never finished with. None of these belongs anywhere near
    /// <see cref="FightOutcome.Kill"/>'s own doc comment, which is unambiguous about what a genuine kill
    /// looks like on the wire.</para>
    ///
    /// <para><b>Red: endings that cost the PLAYER.</b> <see cref="FightOutcome.UFled"/> and
    /// <see cref="FightOutcome.UFledFail"/> are the player's own flee, successful or not - and per
    /// UFledFail's own doc comment, a failed attempt is not free either (102 points and a whole
    /// experience level in the captured example). Both are the player paying to leave, which is the
    /// "if you fled" the owner named.</para>
    ///
    /// <para><see cref="FightOutcome.Kill"/> gets no tint - the nominal ending. <see cref="FightOutcome.Died"/>
    /// (the player's own permadeath) is excluded from this mapping entirely and stays drawn in full
    /// <see cref="Hostile"/> at the call site, per the owner's instruction not to reduce it to a
    /// tint.</para>
    /// </summary>
    private static SKColor OutcomeTint(SKColor baseColor, FightOutcome outcome) => outcome switch
    {
        FightOutcome.CFled or FightOutcome.CFledFail or FightOutcome.Withdraw
            or FightOutcome.NoMore or FightOutcome.EndOther or FightOutcome.Interrupted
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
    public static readonly RailSlotMetrics SlotMetrics = new(
        RailWidth: RailWidth, Pad: Pad, SlotHeight: SlotHeight, SlotGap: SlotGap,
        SlotsGap: SlotsGap, BottomRowHeight: BottomRowHeight, TickRowHeight: TickRowHeight,
        PlayerTileHeight: PlayerTileHeight, MaxSlots: MaxSlots);

    /// <summary>
    /// Where roster row <paramref name="rosterIndex"/> is actually drawn, in dp from the panel
    /// content box's top-left, or null when that row has no slot of its own (it is in the overflow
    /// tail, or the panel has not been measured). Same contract and same reason as
    /// <see cref="TickTrackDp"/>: this canvas lays itself out in a fixed 376-unit space and scales
    /// it, so anything positioned in real dp needs the same factor and must not re-derive it.
    /// </summary>
    public static RailRect? OpponentSlotDp(
        double panelWidthDp, double panelHeightDp, int rosterIndex, int participantCount)
        => RailSlotGeometry.OpponentSlotDp(SlotMetrics, panelWidthDp, panelHeightDp, rosterIndex, participantCount);

    /// <summary>Where the player's own tile is drawn, in dp from the panel content box's top-left -
    /// what a player-anchored damage float centres on.</summary>
    public static RailRect PlayerTileDp(double panelWidthDp, double panelHeightDp)
        => RailSlotGeometry.PlayerTileDp(SlotMetrics, panelWidthDp, panelHeightDp);

    /// <summary>One opponent: the ladder bar, the name, and the game's own wound phrase. The phrase is
    /// verbatim from the MUD and set in the terminal's own monospace, because echoing what the player
    /// just read in the scroll is what anchors the panel to it.
    ///
    /// <para>The seal replaced a seven-pip ladder with the phrase laid over it. Same ladder, same seven
    /// rungs, bent into the same ring the player's own stamina is drawn on - the owner's call, because
    /// the shape is what makes two of them comparable at a glance where two bars are not. What the ring
    /// adds over the pips is a position INSIDE the current rung, which is where the estimator's
    /// under-the-hood arithmetic goes, and a lane for the race projection. What it does not add is a
    /// number.</para></summary>
    private void DrawOpponentSlot(SKCanvas canvas, float y, RosterRow row, bool isGreatestThreat)
    {
        _fill.Color = row.IsCurrentTarget
            ? new SKColor(0x61, 0xd6, 0xd6, 0x14)
            : new SKColor(0xff, 0xff, 0xff, 0x08);
        canvas.DrawRoundRect(Pad, y, Content, SlotHeight, 5f, 5f, _fill);

        // The current target is marked inside its own slot - never by being bigger. A slot that
        // changes size moves everything around it.
        //
        // Drawn for the target ONLY. The non-target bar was Rule (#2F2F2F) on the panel's #101618 -
        // 1.35:1, below the contrast at which a 3-unit sliver is visible at all - so it marked nothing
        // while occupying the strip the badge now needs.
        if (row.IsCurrentTarget)
        {
            _fill.Color = TerminalTheme.Palette[14];
            canvas.DrawRect(Pad, y, 3f, SlotHeight, _fill);
        }

        // The engagement's own frame, carrying the same dash-density language as its prediction bands:
        // solid when blows are landing, decaying to a dotted outline when they are not. Its rectangle
        // is the slot's own, so the frame can never reflow - only the dash changes.
        DrawTempoFrame(canvas, Pad, y, Content, SlotHeight, row.YourTempo, isGreatestThreat);

        // Line 1: the creature and what it is holding. Bold while it is alive (owner, 2026-09-06) -
        // the tile is a thing to watch until it is not.
        var nameFont = row.IsLive ? _nameBoldFont : _nameFont;
        DrawMarkedText(
            canvas, Ellipsize(row.Name, SlotNameWidth, nameFont), TileTextLeft, y + TileNameBaseline,
            SKTextAlign.Left, nameFont,
            row.IsCurrentTarget ? InkBright : Ink, row.Novelty);

        // What THIS creature is fighting with, right-aligned opposite its name. A per-participant fact
        // belongs on the participant, not in the player's own weapon column where only one of a pack
        // could ever be named.
        //
        // BLANK when unknown, never "unarmed". RosterRow.NpcWeapon means "the weapon it has ANNOUNCED",
        // and its only source is the "has started to use the X to fight!" line - which a creature that
        // walked in already holding an axe never prints. Writing "unarmed" there turned an unknown into
        // a measured claim on nearly every opponent, which is rule 5 inverted, on the readout that
        // decides whether a fight is survivable. The client cannot tell empty hands from silence, so it
        // says nothing and the slot simply stays reserved.
        if (row.NpcWeapon is { Length: > 0 } npcWeapon)
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
        DrawSpark(canvas, y + TileSparkCentre, row.Exchange, subjectIsPlayer: false);

        // The current-target stripe is redrawn LAST, over the ladder. The bar spans the tile's full
        // width from x=Pad, which is the same three units the stripe occupies - drawn in the other
        // order, six units of the stripe were punched out by the bar's fill.
        if (row.IsCurrentTarget)
        {
            _fill.Color = TerminalTheme.Palette[14];
            canvas.DrawRect(Pad, y, 3f, SlotHeight, _fill);
        }
    }

    /// <summary>
    /// Room for the creature's name and for the weapon opposite it. Both fixed rather than measured off
    /// the content: a name allowed to grow into the weapon's space would move the weapon.
    ///
    /// <para>Both grew when the four per-row damage figures were removed. The owner had reported them
    /// twice - "they're not readable, and I had no idea what the numbers were because they're so tiny
    /// and the labels are unreadable" - and they were the last descendant of the numeric table the
    /// whole panel was a reaction to. What each was for now lives somewhere it reads better: how hard
    /// this creature hits is the two rDPT lanes on the player's own ring, positioned against the
    /// player's actual stamina rather than stated as a bare figure (this used to be a single reach
    /// chevron, deleted 2026-09-02 once the two per-blow lanes made it redundant - see
    /// MudSharp.Combat.StaminaSeal's remarks on ReachAggregate); how fast the fight is going is the
    /// tempo frame; and how much of the creature is left is the badge itself.</para></summary>
    private const float SlotNameWidth = 168f;
    private const float SlotWeaponWidth = 152f;
    private const float TileInset = 6f;
    private const float TileTextLeft = Pad + TileInset;
    private const float TileTextRight = Pad + Content - TileInset;

    /// <summary>Room for the game's wound phrase on the row below. Wider than the name's, because the
    /// only thing to its right on that row is the damage pair, which starts further over than the
    /// weapon does. The longest phrase in NpcHealthRungs ("superficially injured") is 21 monospace
    /// characters, about 139 units at 11f.</summary>
    private const float SlotPhraseWidth = Content - (TileInset * 2f);

    /// <summary>
    /// The game's own words about this creature, verbatim, under its name: the wound phrase, and the
    /// <c>diagnose</c> band beside it when one has been taken.
    ///
    /// <para><b>It stays even now the seal carries a number-shaped reading</b>, and it is the ONLY
    /// reading when the seal has none. It is MUD2's own vocabulary - the words the player just read in
    /// the scroll - and it is per-INDIVIDUAL where the seal's track is per-species.</para>
    ///
    /// <para><b>It is never blanked.</b> Owner's instruction, 2026-09-01: "also don't stop displaying
    /// the health read on npcs". A ten-second cutoff used to erase it; that is gone. The evidence backs
    /// him: MUD2 prints a descriptor after every landed blow that does not kill, so silence between
    /// descriptors is not a missing observation but positive evidence that nothing of the player's
    /// landed - which means the creature has taken nothing from them and the last reading very probably
    /// still holds. Fading it after three ticks is the whole of the staleness treatment, and it carries
    /// the residual honestly: a creature CAN change untouched (NPC-versus-NPC combat is in the corpus,
    /// and zombies regenerate), so dim says "this is what it last said" without hiding it or
    /// overclaiming it. No timestamp, no duration, no words about age - see
    /// <see cref="RosterRow.StaleAfterSeconds"/>, which owns the policy and the reasoning.</para>
    ///
    /// <para><b>The diagnose band is an exception to the no-absolute-figures rule, and the only one.</b>
    /// Every stamina figure the ESTIMATOR produces stays under the hood, because the player has no way
    /// to check it. A diagnose probe is the opposite case: it is a number MUD2 printed to them in so
    /// many words, so echoing it is the same act as echoing the wound phrase. Drawn brighter than the
    /// phrase because it is a measurement rather than an adjective, right-aligned so a long phrase
    /// ellipsizes into the gap instead of pushing it off the row.</para>
    /// </summary>
    private void DrawWoundPhrase(SKCanvas canvas, float x, float baseline, RosterRow row)
    {
        var room = SlotPhraseWidth;

        if (row.StaminaRead is { } read)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var band = read.PrintedLow.ToString(culture) + "-" + read.PrintedHigh.ToString(culture);
            _text.Color = row.IsHealthStale ? Ink : InkBright;
            canvas.DrawText(band, x + SlotPhraseWidth, baseline, SKTextAlign.Right, _rungFont, _text);
            room -= _rungFont.MeasureText(band) + 8f;
        }

        if (row.HealthPhrase is not { Length: > 0 } raw)
            return;

        _text.Color = row.IsHealthStale ? InkDim : Ink;
        canvas.DrawText(
            Ellipsize(NpcHealthRungs.Label(raw), room, _rungFont),
            x, baseline, SKTextAlign.Left, _rungFont, _text);
    }

    /// <summary>
    /// The seven-rung ladder as a horizontal bar, filling from the left with what is LEFT.
    ///
    /// <para><b>This was a ring until 2026-09-06</b>, on the left of every tile, pushing all four
    /// text lines to x=91. Unrolling it gave them the full width back, which is what the two damage
    /// rows and the exchange spark are drawn in. Only the SHAPE changed; everything below survived
    /// the move and none of it is negotiable.</para>
    ///
    /// <para><b>A ladder, not a stamina bar.</b> Every creature has exactly seven rungs. A giant does
    /// not get more rungs - its rungs are worth more stamina and it labels them with different words.
    /// So the bar is notched into sevenths and the boundary is how far up that ladder the creature has
    /// been driven. The estimator's absolute figures stay under the hood, placing the boundary more
    /// finely INSIDE the seventh the descriptor gave; none of them is drawn. Owner's call: "We're not
    /// asking the player to do math."</para>
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
            canvas.DrawRect(x, top, RemainingWidth(soft.StartDegrees, width), TileBarFillHeight, _fill);
        }

        if (plan.LitHard is { } hard)
        {
            _fill.Color = lit;
            canvas.DrawRect(x, top, RemainingWidth(hard.StartDegrees, width), TileBarFillHeight, _fill);
        }

        DrawRungNotches(canvas, x, top, width);

        // A diagnose reading is a number MUD2 printed to the player in so many words, so its hard edge
        // earns a bright tick - the one measurement on this side of the panel.
        if (plan.Measured && plan.LitHard is { } measured)
        {
            _fill.Color = InkBright;
            canvas.DrawRect(
                x + RemainingWidth(measured.StartDegrees, width) - 1f, top - 1f, 1.5f,
                TileBarFillHeight + 2f, _fill);
        }

        DrawPredictionLane(canvas, plan.BlowAfter, x, top, width, PredictAfter);
        DrawPredictionLane(canvas, plan.NextBlow, x, top, width, PredictNext);
    }

    /// <summary>Where a boundary at <paramref name="startDegrees"/> falls along a bar that fills from
    /// the left with what remains. The ring's lit arc runs from its start round to 360, so the
    /// fraction still standing is everything the arc covers.</summary>
    private static float RemainingWidth(float startDegrees, float width)
        => Math.Clamp((360f - startDegrees) / 360f, 0f, 1f) * width;

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

        var far = RemainingWidth(band.StartDegrees, width);
        var near = RemainingWidth(band.StartDegrees + band.SweepDegrees, width);
        var span = Math.Max(far - near, 1.5f);

        _fill.Color = color;
        canvas.DrawRect(x + near, top + (TileBarLaneTop - TileBarTop), span, TileBarLaneHeight, _fill);
    }

    /// <summary>The incoming side's second colour. Dimmed rather than alpha-faded because every other
    /// de-emphasis on this canvas is a <see cref="Dim"/> against the panel's near-black ground, where
    /// the two are visually the same thing and one of them does not have to be composited.</summary>
    private static readonly SKColor HostileDim = Dim(Hostile, 0.72f);

    /// <summary>How far a cell that has nothing to say is pushed back. The owner asked for 0.8 alpha;
    /// on this ground a 0.8 dim is the same reading and matches the rest of the file.</summary>
    private const float GhostDim = 0.8f;

    /// <summary>
    /// One direction of the exchange: mark, running total, blow shape, drain rate.
    ///
    /// <para><b>Four fixed columns, so the tile's two rows lock to each other.</b> The owner's
    /// requirement, 2026-09-06, when the blow-shape group's mixed point sizes stopped the rows lining
    /// up on their own: the answer is that the columns are POSITIONS, not a measured flow, so the
    /// sizes inside a group cannot move the group.</para>
    ///
    /// <para><b>An empty row names its own cells</b> rather than printing dashes - the owner's
    /// suggestion, and a better one: the tile teaches its layout while it has nothing to report and
    /// goes quiet the moment a figure lands. Either way rule 5 holds, because a word can no more be
    /// read as a measurement than a dash can.</para>
    ///
    /// <para><b>Row POSITION and row COLOUR answer different questions, and conflating them was the
    /// bug.</b> Every badge is about its own subject: the UPPER row is what that subject is TAKING and
    /// the lower row is what it is DEALING, so an opponent's tile leads with the damage the player put
    /// into it and the player's tile leads with the damage coming back. The COLOUR says who threw the
    /// blow - white for the player, red for a creature - which is why the two tiles do not simply
    /// invert into each other's colours.
    ///
    /// <para>Until 2026-09-07 both tiles were drawn from the player's point of view, which made the
    /// player's tile a verbatim copy of the opponent's in any one-on-one fight. The owner spotted it
    /// as transposition; it was worse than that - it was the same row twice.</para></para>
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
            // bounds of the smallest and largest blows (the owner's spec, with his worked example),
            // while the mean pools both ends - so three identical (5-9) blows read 9 / 9 / 7, and a
            // label promising a minimum below the average would be contradicted by the commonest case
            // of all. "low" and "high" describe WHICH BLOW, which is what these actually are.
            canvas.DrawText(
                "low/high/avg",
                StatShapeLeft + (StatShapeWidth / 2f), baseline, SKTextAlign.Center, _statSmallFont, _text);
            canvas.DrawText(
                "dmg/tick", StatDptLeft + StatDptWidth, baseline, SKTextAlign.Right, _statSmallFont, _text);
            return;
        }

        // Centred (owner, 2026-09-06) so a wide outgoing bracket and a bare incoming figure share an
        // axis instead of drifting apart against a right edge.
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
                line.PerTick.ToString("0.#", culture) + "/t",
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
    /// ends of every bracket - the owner's own definition. On the incoming side all three are exact.
    /// See <see cref="ExchangeLine"/>.</para>
    /// </summary>
    private void DrawShapeGroup(
        SKCanvas canvas, float baseline, ExchangeLine line, SKColor bright, SKColor dim)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        // One measured blow means the three figures cannot differ yet, so the outer two are pushed
        // back (owner, 2026-09-06) - the eye is told there is nothing to compare rather than left to
        // work out why it is reading the same number three times.
        var outer = line.AllAlike ? Dim(dim, GhostDim) : dim;

        _text.Color = outer;
        canvas.DrawText(
            line.Min.ToString("0.#", culture),
            ShapeLowLeft + ShapeLowWidth, baseline, SKTextAlign.Right, _statSmallFont, _text);
        canvas.DrawText(
            "/", ShapeSep1Left + (ShapeSepWidth / 2f), baseline, SKTextAlign.Center, _statSmallFont, _text);
        canvas.DrawText(
            "/", ShapeSep2Left + (ShapeSepWidth / 2f), baseline, SKTextAlign.Center, _statSmallFont, _text);
        canvas.DrawText(
            line.Mean.ToString("0.#", culture),
            ShapeMeanLeft, baseline, SKTextAlign.Left, _statSmallFont, _text);

        _text.Color = bright;
        canvas.DrawText(
            line.Max.ToString("0.#", culture),
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
    /// <see cref="SparkDamageCap"/> - the owner's ramp, 2026-09-06.</para>
    ///
    /// <para><b>It mirrors between the two kinds of tile</b>, for the same reason the stat rows do:
    /// UP is what the badge's subject is TAKING. On an opponent's tile the player's blows rise; on the
    /// player's own tile the creatures' blows rise. Drawn the same way on both until 2026-09-07, which
    /// left the player's tile saying the opposite of what its rows said.</para>
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

            var height = mark.Measured && mark.Damage > 0 ? SparkBarHeight(mark.Damage) : SparkMinBar;

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

    private static float SparkBarHeight(double damage)
        => SparkMinBar + (float)(Math.Clamp(damage / SparkDamageCap, 0.0, 1.0) * (SparkMaxBar - SparkMinBar));

    /// <summary>Yellow through to bright red, saturating at <see cref="SparkDamageCap"/> - the owner's
    /// ramp. A blow of unknown size gets the bottom of it rather than a colour of its own: the height
    /// already says "unknown" by sitting at the minimum, and a fourth colour on a three-unit mark
    /// would be a distinction nobody can see.</summary>
    private static SKColor IncomingRamp(double damage)
    {
        var t = Math.Clamp(damage / SparkDamageCap, 0.0, 1.0);
        return t < 0.5
            ? Tint(Caution, NoveltyUnfought, (float)(t * 2.0))
            : Tint(NoveltyUnfought, Hostile, (float)((t - 0.5) * 2.0));
    }

    /// <summary>The direction mark: a small filled triangle, DRAWN rather than typed. The owner ruled
    /// out an arrow character (2026-09-06, "MUD2 is a text game, and we're a text-focused UI, we're
    /// not a TUI") - a glyph would also have put a non-ASCII literal in the source, which this
    /// codebase rejects, so the vector settles both at once.</summary>
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
    /// <para>The owner's "how soon" cue, applied at widget level: "if you're just not hitting the
    /// thing, then it decays to 1 dot of color every 5 pixels; swinging like a pro, it's solid." An
    /// opponent's slot carries the player's rate against that creature; the player's own two-seal
    /// device carries the pooled incoming rate. Same language on both, so the pair reads as one
    /// exchange.</para>
    ///
    /// <para><b>Three states, on two channels, and the third one matters.</b> Density alone cannot
    /// carry them, because a hit rate of zero and no swings at all are different claims:</para>
    /// <list type="bullet">
    /// <item><b>Nothing swung yet</b> - four CORNER BRACKETS, no sides. A shape, not a density, so it
    /// cannot be read as a position on the dash scale. This is not a one-tick corner case: the player
    /// swings at ONE creature at a time, so in a pack every other live row sits here for the whole
    /// fight, and the previous version drew all of them as if the player were whiffing at them. It is
    /// also the only route into the state, since MUD2 prints a descriptor after every non-killing
    /// landed hit - see <c>DamagePrediction.Tempo</c>, which records why near-total coverage is a
    /// reason to keep this state rather than to drop it.</item>
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
    /// <para><b>Restored 2026-09-02, the same day it was deleted.</b> It was cut along with the reach
    /// chevron on the reasoning that the pairing existed only to point at what the chevron drew, and
    /// with the chevron gone there was nothing left to point at. That reasoning was wrong: this row is
    /// the one <c>MudSharp.Combat.ReachAggregate.GreatestThreat</c> picks for the player's own
    /// prediction lanes (<c>CombatLiveView.YourNextBlow</c>/<c>YourBlowAfter</c>) to project from, a
    /// selection that runs on every refresh regardless of the chevron, and the accent this drives is
    /// the ONLY on-screen sign that a selection is happening at all - see <see cref="FrameAccent"/>'s
    /// own remarks for what breaks if this is deleted again without replacing what it shows.</para>
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
    /// <para><b>This row was effectively dead and its count was wrong.</b> It only ran when the roster's
    /// published ROW list outgrew the visible capacity, and the roster caps that list at
    /// ParticipantRoster.MaxRows == the rail's own MaxSlots, so at any rail height from 560dp up - every
    /// ordinary window - eight rows could never outgrow eight slots and the row never drew. A
    /// fourteen-rat fight showed eight rats and nothing whatever about the other six. The counts here
    /// are now taken from the PLAN, which knows about participants the row list never carried; the
    /// caller's overflow test moved to the same figure.</para>
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

        var hidden = plan.TotalCount - shown;
        _text.Color = Hostile;
        canvas.DrawText("+" + hidden.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Pad + 10f, y + 15f, SKTextAlign.Left, _nameFont, _text);

        // Names only, ordered by how much each has actually hurt the player, worst first - "who is
        // doing the damage" is the only question a names-only row can usefully answer, and it is not
        // the same question as "who joined first", which is the order the slots themselves keep (a
        // slot that reorders itself as damage accrues would have to be re-found on every glance).
        // Built by OverflowNames, which caches this string - see its own remarks.
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
    /// no caching at all (2026-09-02 review finding), against this codebase's own discipline
    /// elsewhere - see <c>SidePanelViewModel._historyCache</c>, which exists for exactly this
    /// reason.
    ///
    /// <para><b>Keyed on the overflow tail's own composition, not on the roster's identity.</b> The
    /// roster is rebuilt fresh on every refresh (<c>ParticipantRoster.Build</c>), including refreshes
    /// where nothing about the OVERFLOW tail changed - a shown slot's health reading ticking, say -
    /// so keying on <paramref name="rows"/>'s reference would never hit. What the drawn string
    /// actually depends on is each hidden row's <see cref="RosterRow.Name"/> and
    /// <see cref="RosterRow.DamageTakenFrom"/> (the sort key) plus <paramref name="hasHidden"/> (the
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
    /// Stamina seal, weapon, magic seal - one row, three fixed columns.
    ///
    /// <para>Out of combat both seals go grey. The numbers are still true (they ride the FES
    /// heartbeat, and the top status strip keeps showing them in full colour), but a lit alarm
    /// colour on this panel means "this is what is happening to you in this fight" - leaving the
    /// seals hot after a fight ends would keep raising an alarm about a fight that is over. They are
    /// dimmed rather than removed so the row never changes shape.</para>
    /// </summary>
    private void DrawBottomRow(SKCanvas canvas, float y, CombatLiveView live)
    {
        DrawPlayerTile(canvas, y, live);
        DrawEncounterTable(canvas, y + PlayerTileHeight + EncounterRowGap, live);
    }

    /// <summary>
    /// The player's own tile: the same four lines every opponent carries, in the same order, at the
    /// same sizes. The owner's instruction, 2026-09-06 - "give our stamina seal the same size and
    /// treatment as the npcs ones, it's just a different color" - and it is the whole reason the
    /// panel can be read as a comparison at all. Two identically-shaped things, one blue and one
    /// coloured by how much trouble you are in.
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

        // The persona's own name, exactly as an opponent tile carries the creature's. Blank until the
        // login handshake names them - never a stand-in word, which is what "you" was.
        if (live.PlayerName is { Length: > 0 } persona)
        {
            _text.Color = InkBright;
            canvas.DrawText(
                Ellipsize(persona, SlotNameWidth, _nameBoldFont),
                TileTextLeft, y + TileNameBaseline, SKTextAlign.Left, _nameBoldFont, _text);
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
        // creatures' blows, red), what they are dealing underneath (their own, white). Both tiles were
        // drawn player-first until 2026-09-07, which made this one a verbatim copy of the opponent's
        // tile in any one-on-one fight.
        DrawStatRow(canvas, y + PlayerUpperBaseline, live.YourTaken, inbound: true, byPlayer: false);
        DrawStatRow(canvas, y + PlayerLowerBaseline, live.YourDealt, inbound: false, byPlayer: true);
        DrawSpark(canvas, y + PlayerSparkCentre, live.YourExchange, subjectIsPlayer: true);
    }

    /// <summary>
    /// What is in the player's hands, opposite "you" exactly as a creature's weapon sits opposite its
    /// name. Bold, because it is the current one (owner, 2026-09-06) - the ALTERNATIVE on the line
    /// below is the plain one, which is the way round it had been drawn before and read as backwards.
    ///
    /// <para><b>Empty hands are an alarm only when they are a PROBLEM.</b> Yellow and underlined once
    /// stamina is below maximum; dim and plain at full. Every fight begins with the weapon unknown -
    /// the aggregator clears it per encounter and it stays clear until a line names it - so alarming
    /// unconditionally meant a yellow underlined UNARMED at the opening of every single fight. An
    /// alarm that fires every time is one the eye learns to skip, which on a permadeath readout is
    /// worse than no alarm at all. The gate is the one the deleted DrawWeapon carried and its reason
    /// was already written down: "an unarmed opening is normal and must not raise an alarm."</para>
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
            // Yellow on the quiet half of the cycle, white on the loud one - the owner's pairing. The
            // underline stays put through both, so the word never appears to move.
            _text.Color = hurt ? (live.BlinkOffPhase ? InkBright : Caution) : InkDim;
            canvas.DrawText(unarmed, TileTextRight, baseline, SKTextAlign.Right, _nameBoldFont, _text);
            if (hurt)
            {
                _fill.Color = Caution;
                canvas.DrawRect(TileTextRight - width, baseline + 2f, width, 1f, _fill);
            }
            return;
        }

        // Carries its novelty mark, exactly as an opponent's name does. This was lost when the ring
        // seal's DrawWeapon was deleted - CombatLiveView.WeaponNovelty went on being computed with
        // nothing reading it, so "you have never finished anything with this weapon" silently stopped
        // being a thing the panel said. See CombatNovelty.WeaponRollup: it is a rollup over everything
        // currently engaged, which is why it belongs on the weapon rather than on any one tile.
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

        // The word, a size down (owner, 2026-09-07) - it is the adjective, and it is what gives when
        // the two cannot both fit.
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
    /// <see cref="ConditionFigureWidth"/> and must never be clipped (owner, 2026-09-07), so the word
    /// gets whatever is left - which is why 118 was wrong: an ordinary "(85/120)" then ran to 194 and
    /// straight into the weapon name.</para>
    ///
    /// <para>The longest living-family label, "superficially injured", does not fit and is not meant
    /// to. It ellipsizes; the figure stays put.</para>
    ///
    /// <para><b>Derived, not chosen.</b> Whatever is left of the line once the alt-weapon group and
    /// the figure have taken theirs. Written down as a number, it silently became wrong the moment
    /// either of the other two changed - which is how the 3dp collision got in.</para>
    /// </summary>
    private const float ConditionWordWidth =
        AltGroupWorstCaseLeft - TileTextLeft - ConditionFigureWidth - 4f;

    /// <summary>Reserved for the stamina figure, never ellipsized against. "(999/999)" is nine
    /// characters of Cascadia Mono at the stat size, which is what this allows for - a persona whose
    /// maximum runs to four digits would overflow it, and MUD2 has no such persona.</summary>
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
    private const float AltGroupWorstCaseLeft =
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
    /// not at all (owner, 2026-09-06). Naming the alternative without the key leaves the player
    /// nothing to do about it, and offering the key without naming what it swaps to is the version he
    /// called clouded.
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
    /// (<see cref="Mucka.Core.TickStaminaLoss.FadeFactor"/>, a pure function of the loss timestamp and now,
    /// sampled at paint time - no timer, Invariant #1). Both halves of that are the owner's, from
    /// living with the un-faded version: a slice that persisted read as a live part of the gauge, and
    /// one dimmed instead of faded could not be seen at all.</para>
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
                var strength = Mucka.Core.TickStaminaLoss.FadeFactor(lostAt, DateTime.UtcNow);
                if (strength > 0f)
                {
                    var width = Math.Min((float)lostLastTick * unit, Content - filled);
                    if (width > 0f)
                    {
                        _fill.Color = Tint(SealTrack, Hostile, strength / Mucka.Core.TickStaminaLoss.PeakTintStrength);
                        canvas.DrawRect(Pad + filled, top, width, TileBarFillHeight, _fill);
                    }
                }
            }
        }

        DrawRungNotches(canvas, Pad, top, Content);
        DrawPredictionLane(canvas, StaminaSeal.BandArc(blowAfter), Pad, top, Content, PredictAfter);
        DrawPredictionLane(canvas, StaminaSeal.BandArc(nextBlow), Pad, top, Content, PredictNext);
    }

    /// <summary>Magic, as a two-unit strip notched at the quarters (owner, 2026-09-06). It answers
    /// "roughly how much is left" and nothing more. A persona with no magic gets the empty track,
    /// which is a measurement (max is zero, and the game said so) rather than an unknown.
    ///
    /// <para><b>The magic FIGURE is not on the panel at all any more.</b> The deleted MAG seal drew it
    /// at 25f; nothing replaced it, and two comments here claimed otherwise until 2026-09-06 - one
    /// pointing at a "seal slot" that no longer exists, one at the condition line, which prints
    /// stamina only. If the number is wanted back it needs a slot of its own; do not assume it is
    /// somewhere else on the tile, because it is not.</para></summary>
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
    /// The encounter table's five columns, in the owner's order (2026-09-06), as WHOLE WORDS.
    ///
    /// <para>They were three-letter abbreviations because the values used to carry a <c>/t</c> suffix
    /// in the heading. Moving the <c>t</c> onto the values freed the room, and a column nobody can
    /// name is a column nobody can use - the headings only appear on hover anyway, which is exactly
    /// when the reader wants the word rather than the crossword clue.</para>
    ///
    /// <para>"Participants" is the widest at 12 characters, which is what the 71-unit column takes at
    /// the heading size. Do not lengthen one without checking the others still fit.</para>
    /// </summary>
    private static readonly string[] EncounterHeadings =
        ["Participants", "Opponents", "Duration", "Victory", "Death"];

    /// <summary>
    /// What each column means, shown when the pointer is on that column.
    ///
    /// <para>The owner asked for these back as "(i) mouse-overs". There is no (i) glyph: a full word
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
    /// does. Owner's decision and his own words, 2026-09-06: "hovers should have a dotted blue
    /// underline, 'half link'".</para>
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
    /// row's height never changes, which is the owner's standing objection to dynamic positioning
    /// answered by reserving the space rather than by doing without the headings.</para>
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
        if (!live.HasEncounter)
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

        // Par and Op are counts; Dur, Vic and Die are ticks and say so on the VALUE (owner,
        // 2026-09-06), which is what lets the headings stay three letters wide.
        //
        // Par and Op go BLANK while the encounter has produced at most one creature (owner,
        // 2026-09-06). "1" and "1" is the overwhelmingly common case and says nothing anyone needs;
        // the pair earns its ink the moment either becomes a fight worth counting. Blank rather than
        // a dash here because this is not an unknown - it is known and uninteresting, which is a
        // different thing and rule 5 has no opinion about it.
        var countsWorthStating = live.LiveOpponents > 1 || live.OpponentsFaced > 1;

        // Stack-allocated rather than `new[] { ... }`: this runs on every paint of a docked panel, and
        // a 2026-09-02 review finding removed exactly this allocation from the band drawing a few
        // hundred lines away. The strings themselves are unavoidable (Skia takes strings), but the
        // array they sit in is not.
        Span<string> values =
        [
            countsWorthStating ? live.LiveOpponents.ToString(culture) : string.Empty,
            countsWorthStating ? live.OpponentsFaced.ToString(culture) : string.Empty,
            Ticks(live.EncounterTicks, culture),
            Ticks(live.TicksToVictory, culture),
            Ticks(live.TicksToDeath, culture),
        ];

        for (var i = 0; i < values.Length; i++)
        {
            // A count that is deliberately not stated draws nothing at all; a PROJECTION that cannot
            // be made yet draws a dimmed dash (owner, 2026-09-06). The two look different because
            // they are different: one is a column with nothing to say, the other is the panel
            // declining to guess - and Vic and Die decline for the opening ticks of every fight, by
            // CombatOutlook's own refusal to project off one lucky blow.
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

        const float padX = 6f;
        const float height = 16f;

        // Bounded and centred inside the room LEFT of the metronome toggle, not inside the whole
        // content width - centring on the full width put the chip's right edge at 351 with the toggle
        // starting at 340, so a long description covered a control. And +1 rather than +3 below the
        // table, which keeps the chip clear of the tick track at 18.5.
        var usable = Content - MetronomeReserve - 4f;
        var width = Math.Min(_statSmallFont.MeasureText(text) + (padX * 2f), usable);
        var left = Math.Clamp(Pad + ((usable - width) / 2f), Pad, Pad + usable - width);
        var top = below + 1f;

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

    /// <summary>Ticks, rounded and suffixed, or empty for "no answer" - which the caller draws as the
    /// column's own name rather than as a figure.</summary>
    private static string Ticks(double? ticks, System.Globalization.CultureInfo culture)
        => ticks is double value && value >= 0 ? value.ToString("0", culture) + "t" : string.Empty;

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
        double xDp, double yDp, double panelWidthDp, double panelHeightDp)
    {
        if (panelWidthDp <= 0 || panelHeightDp <= 0)
            return -1;

        var k = panelWidthDp / RailWidth;
        var x = xDp / k;
        var y = yDp / k;
        var height = panelHeightDp / k;

        var tickTop = height - Pad - TickRowHeight;
        var tableTop = tickTop - BottomRowHeight + PlayerTileHeight + EncounterRowGap;

        if (x < Pad || x > Pad + Content || y < tableTop || y > tableTop + EncounterRowHeight)
            return -1;

        var column = (int)((x - Pad) / (Content / EncounterHeadings.Length));
        return Math.Clamp(column, 0, EncounterHeadings.Length - 1);
    }

    /// <summary>
    /// The flee pill: <c>FLEE</c> and the key that sends it, at the top of the middle column.
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

        // "Flee 23 (-2.1k)" left, "^F" right. Bold at every state, like the dreamword chip's own word:
        // the escalation is the chip's brightness and the pulse, never the letterforms changing under
        // the eye.
        //
        // The STAMINA is on the pill rather than left to the seal beside it because this is the number
        // the decision is actually about, and at the moment of deciding the eye is on the chip. The
        // PRICE is a parenthetical and is absent when there is none - see FleeCostEstimate for how
        // rough it is, and for why "no parenthetical" means free or unpriceable rather than zero.
        // One centred string in the owner's own wording, 2026-09-06:
        //     ^F:  FLEE  sta:23  cost:-2.1k
        //
        // The key leads, because it is the only thing that can be DONE about any of it - "^F", not
        // "Ctrl+F", the owner's shorthand for a player whose hand is already on the keyboard.
        //
        // The STAMINA is on the pill rather than left to the bar above it because this is the number
        // the decision is actually about, and at the moment of deciding the eye is on the chip. The
        // PRICE is labelled and is absent when there is none - see FleeCostEstimate for how rough it
        // is, and FleeCostParenthetical for why "no cost clause" means free or unpriceable rather than
        // zero.
        var baseline = top + (PillHeight / 2f) + 4f;
        var label = "^F:  FLEE";
        if (live.StaminaCurrent is int sta)
            label += "  sta:" + sta.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (live.FleeCostParenthetical is string price)
            label += "  cost:-" + price;

        // Ellipsized against the chip's own padding. The worst realistic string fits at 12f with room
        // to spare, so this should never fire - but font metrics are a platform's to choose, and a
        // price running off the chip would be unreadable at the one moment it matters.
        _text.Color = Dim(PillText, wash);
        canvas.DrawText(Ellipsize(label, PillWidth - 20f, _pillFont),
            PillLeft + (PillWidth / 2f), baseline, SKTextAlign.Center, _pillFont, _text);
    }

    /// <summary>
    /// Where the flee pill sits, in device-independent units, for a rail rendered at
    /// <paramref name="panelWidthDp"/> wide - the geometry the Composition border/background behind this
    /// canvas has to match exactly. Same contract and same reason as <see cref="TickTrackDp"/>: the rail
    /// lays itself out in a fixed 376-unit space and scales it, so a sibling in real dp needs the same
    /// factor, and two copies of the arithmetic would silently disagree the first time this row's height
    /// or padding changed.
    /// </summary>
    /// <para>The corner radius travels with the bounds rather than being re-derived from the height at
    /// the call site: the chip is not fully rounded (radius 10 on a 24dp chip, matching the dreamword
    /// chip it copies), so <c>height / 2</c> would be a visibly different curve on the ring than on the
    /// fill it is supposed to trace.</para>
    public static (double Left, double Right, double Bottom, double Height, double Radius) FleePillDp(
        double panelWidthDp)
    {
        var k = panelWidthDp / RailWidth;
        return (
            PillLeft * k,
            (RailWidth - PillLeft - PillWidth) * k,
            // Measured up from the panel's bottom edge, mirroring OnPaintSurface's own bottom-up
            // chain. The pill lives INSIDE the tick row now (2026-09-06), so the chain is one term
            // shorter than it was: the bottom row no longer enters it at all. TickRowDrop subtracts,
            // because a drop measured downward from the row's top is a reduction measured upward
            // from the panel's bottom.
            (Pad + TickRowHeight - PillTopInset - PillHeight - TickRowDrop) * k,
            PillHeight * k,
            PillRadius * k);
    }

    /// <summary>
    /// The tick meter. Pale and grey - it is a timer, not a judgement, so it carries no colour coding
    /// and no label. It turns red at 30 stamina and glows at 20, and those are the only exceptions.
    ///
    /// <para><b>The crossed-swords opponent gauge that used to sit over it is gone.</b> It centred
    /// itself on a width computed from the live opponent count, so every death and every joiner slid
    /// the whole row of marks sideways - the one thing this panel is laid out never to do (see the
    /// class remarks: a critical readout that shifts under the eye has to be re-found on every glance).
    /// It was also the third place the same count appeared: the slots themselves are one per opponent,
    /// and the overflow row states the tail. Nothing was lost by deleting it.</para>
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

        // The flee pill sits OVER the gauge (owner, 2026-09-06). Drawn last so it covers the track,
        // and only while there is a reason to leave - between fights the gauge is unobstructed.
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
        // _metronomeBodyPath is reused rather than `new SKPath()`'d every paint (2026-09-02 review
        // finding) - see its own remarks; Reset() clears it before this call rebuilds it.
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
    /// How far the tick row's CONTENTS sit below the row's own centre line (owner, 2026-09-06: "we
    /// can afford to move the combat ticker down 6px").
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
    /// <para>The rail lays itself out in a fixed 376-unit coordinate space and scales that space to
    /// whatever width it is given (see OnPaintSurface), so a sibling element positioned in real dp
    /// has to have the same scale factor applied to it. Exposing it from here rather than restating
    /// the numbers in XAML is deliberate: two copies of this arithmetic would silently disagree the
    /// first time the row's height or padding changed.</para>
    /// </summary>
    public static (double Left, double Right, double Bottom, double Height) TickTrackDp(double panelWidthDp)
    {
        var k = panelWidthDp / RailWidth;
        return (
            Pad * k,
            (Pad + MetronomeReserve) * k,
            // Measured up from the bottom edge, so the row-internal drop SUBTRACTS here.
            (Pad + (TickRowHeight / 2f) - (TickTrackHeight / 2f) - TickRowDrop) * k,
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

    private static string OutcomeWord(FightOutcome outcome) => outcome switch
    {
        FightOutcome.Kill => "killed",
        FightOutcome.Died => "KILLED YOU",
        FightOutcome.CFled => "fled",
        // "broke off", not "fled": the creature is still standing in the room. Calling this a flee on
        // the roster row would have the player chase something that never left - see
        // FightOutcome.CFledFail.
        FightOutcome.CFledFail => "broke off",
        FightOutcome.UFled => "you fled",
        // The player is also still in the room, and still has to deal with this thing.
        FightOutcome.UFledFail => "flee failed",
        FightOutcome.Withdraw => "withdrew",
        // The outcome's own word rather than the observed cause: NoMore is an open family (poison so
        // far, the owner expects others) and the label has to cover the ones not yet seen. Not
        // "killed" - nothing in these frames says the player's blow finished it - and not "died",
        // which on a roster row beside "KILLED YOU" invites exactly the wrong reading.
        FightOutcome.NoMore => "no more",
        // The game closed it and gave no reason; saying more than that on the roster row would be
        // inventing one. Distinct from a blank label, which means we never saw it end at all.
        FightOutcome.EndOther => "ended",
        // The client stopped it, not the game - a reset, a logout, a room change, app exit. "cut
        // short" rather than "interrupted" only because the word has to fit DeadNameWidth on the dead
        // strip's second line; which of the four reasons it was is in the clog's EncounterForceEnded
        // event, not on a roster row.
        FightOutcome.Interrupted => "cut short",
        _ => string.Empty,
    };

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
