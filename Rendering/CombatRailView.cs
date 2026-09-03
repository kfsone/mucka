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
    // the rail's 336-unit design space maps 1:1 onto the Border's dp content area (WidthRequest minus
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
    private const float DeadLineHeight = 13f;

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

    private const float BottomRowHeight = 96f;
    private const float TickRowHeight = 30f;
    private const float TickTrackHeight = 5f;

    /// <summary>Width reserved at the RIGHT end of the tick row for the metronome toggle. Taken out
    /// of the track rather than laid beside it, so the row's overall geometry is unchanged and the
    /// toggle occupies space that is reserved whether or not it is switched on (spec rule 3).</summary>
    private const float MetronomeReserve = 26f;

    private const float TickTrackWidth = Content - MetronomeReserve;
    private const float SealSize = 92f;

    /// <summary>
    /// The player's own ring, and the two prediction lanes outside it.
    ///
    /// <para><b>The ring gave up four units of radius to make room for the lanes.</b> 39 to 35. The
    /// owner asked for the opponent badges' adornment on his own gauge, and the bottom row's height is
    /// fixed by the flee pill's arithmetic and by the slot capacity above it, so the room had to come
    /// from somewhere inside the seal. The number in the middle - which is what the player actually
    /// reads off this device - is untouched.</para>
    ///
    /// <para>Dropped two units below the seal box's own centre so the ring sits centred in the 96-unit
    /// ROW rather than in its 92-unit box, which buys the two units the outer lane needs at the top.
    /// Checked against the flee pill: at the pill's vertical centre the outer lane has narrowed to
    /// cx +/- 28.5, reaching x=84.5 against the pill's left edge at 92.</para>
    /// </summary>
    private const float SealRadius = 35f;
    private const float SealStroke = 7f;
    private const float SealCenterDropY = 2f;
    private const float SealNextBlowInner = 39.4f;
    private const float SealNextBlowOuter = 41.4f;
    private const float SealBlowAfterInner = 42.4f;
    private const float SealBlowAfterOuter = 44.4f;

    /// <summary>The player's lanes never carry a dash. On an opponent's badge the dash reports how
    /// often the PLAYER is landing on it; the player's own device is not answering that question, and
    /// leaving the channel unused is better than giving it a second meaning.</summary>
    private static readonly DamagePrediction.TempoStroke SolidTempo =
        new(DamagePrediction.TempoReading.Fluent, 0f, 0f);

    /// <summary>The opponent seal: the same ring the player's STA and MAG seals are, shrunk to fit an
    /// opponent slot. Owner's call - "I want seals for npcs; they're no less approximate than a bar,
    /// but critically they're the same shape, comparison is trivial."
    ///
    /// <para><b>Sized against a shipped element, not against a guess.</b> The owner reported the badges
    /// "low value due to overly small size" and, after a first attempt, that "the notches are not
    /// countable". The gate is exactly that: whether the six rung notches can be COUNTED, since "down 2
    /// bubbles" is the whole justification for the ladder being a ring rather than a bar.
    ///
    /// <para>The reference is this panel's own history. The seven-pip health ladder that the ring
    /// replaced drew <b>16dp segments, 3dp gaps, 6dp thick</b>, and it sat in the owner's hands for
    /// months without a single complaint that its pips could not be counted. That makes it an empirical
    /// floor for countability at this DPI and in this panel, rather than a number somebody liked. The
    /// rail renders 1:1 dp (see <see cref="RailWidth"/>), so those figures are directly comparable.</para>
    ///
    /// <para>A rung segment on a ring is <c>R x 2pi/7</c> of arc. At the old radius 12 that was
    /// <b>10.8dp</b> - two thirds of the proven floor, and duly reported as uncountable. At 26 it is
    /// <b>23.3dp</b>, which is 1.46x that floor, with the stroke raised to 6dp to match the pips'
    /// proven thickness exactly. Anything that trades this radius down is trading against those two
    /// figures: stay at or above 16dp of segment and 6dp of stroke, or the ladder stops being
    /// countable and the ring stops earning its shape.</para>
    ///
    /// <para>The radius is then capped by the slot: half of 76 is 38, less 1.2 for the tempo frame,
    /// less 1.2 of margin, less the 9.4 the ring stroke and the two prediction lanes need outside
    /// it.</para></summary>
    private const float SlotSealStroke = 6f;
    private const float SlotSealRadius = 26f;

    /// <summary>How far the outermost drawn pixel of a badge sits from its centre: the ring's outer
    /// edge (26 + 2.5), the two prediction lanes and half the outer band's stroke. Everything that
    /// positions a badge is derived from THIS rather than from a nominal box, because the lanes extend
    /// past any box the ring alone would suggest and the old constant let text creep under them.</summary>
    private const float SlotSealExtent = BlowAfterOuter + (BlowAfterStroke / 2f);

    /// <summary>The badge's centre, far enough in that its leftmost pixel clears the slot frame.</summary>
    private const float SlotSealCenterX = Pad + SlotSealExtent + 4.1f;

    /// <summary>
    /// The two rDPT prediction bands ride outside the ring in lanes of their OWN, one per blow, with a
    /// clear gap between them.
    ///
    /// <para><b>Concentric separation is required, not decoration.</b> The two bands are frequently
    /// coincident in ANGLE: whenever the pool has no upper bound - a first encounter, or a species
    /// fought but never killed - both bands start at the boundary itself, so the next blow's band lies
    /// entirely inside the one after it. Drawn in a shared lane they would be one smudge differing only
    /// in hue, and at the 12-degree minimum sweep (about three units of arc) they would be
    /// indistinguishable even when they are NOT coincident. Radius is the only channel that survives
    /// both cases.</para>
    ///
    /// <para>The next blow takes the INNER lane, nearest the ring it is about to move; the blow after
    /// it sits further out. Reading outward is reading forward in time.</para>
    ///
    /// <para>Budget, from the seal's centre: ring outer edge 13.75, outermost band pixel 19.75, against
    /// a slot half-height of 23 whose frame sits 0.6 in. 2.65 units of clearance.</para>
    /// </summary>
    private const float NextBlowInner = 29.9f;
    private const float NextBlowOuter = 31.9f;
    private const float BlowAfterInner = 32.9f;
    private const float BlowAfterOuter = 34.9f;

    /// <summary>Stroke of the prediction outlines. The owner's note on the mockup: "I think the actual
    /// should be more of a bounding box border than a thick outline" - so a band is a thin box drawn
    /// AROUND the segment it predicts, never a fat second arc competing with the fill.
    ///
    /// <para>The nearer blow is drawn slightly heavier. A third channel on top of radius and hue, so
    /// the two remain separable for a reader who cannot rely on colour.</para></summary>
    private const float NextBlowStroke = 1.3f;
    private const float BlowAfterStroke = 1f;

    /// <summary>The tempo border's inset inside an opponent slot and around the player's device, and
    /// its stroke. Both fixed: the border is a frame on a rectangle whose bounds never change, so
    /// nothing here can reflow when a fight's rate does (only the DASH changes).</summary>
    private const float FrameStroke = 1.2f;

    /// <summary>Left edge of an opponent slot's text, clear of its seal.</summary>
    private const float SlotTextLeft = SlotSealCenterX + SlotSealExtent + 6f;

    /// <summary>The bottom row's middle column - weapon, alternate weapon, and the flee pill - between
    /// the two seals. Named rather than restated at each use so a sibling positioned in real dp
    /// (<see cref="FleePillDp"/>) cannot drift off the column the canvas draws.</summary>
    private const float ColumnLeft = Pad + SealSize + 8f;
    private const float ColumnWidth = Content - (SealSize * 2f) - 16f;

    /// <summary>The flee pill, at the TOP of the middle column - the band above the weapon line, level
    /// with the upper arc of both seal rings, so the pill sits visually between the two stamina-shaped
    /// readouts the flee decision is actually about. Reserved whether or not the pill is drawn (rule 3);
    /// nothing else in the column moves when it lights up.</summary>
    private const float PillHeight = 24f;
    private const float PillTopInset = 3f;

    /// <summary>The pill is WIDER than the middle column the weapon lines use, and centred on the panel
    /// rather than on that column. It has to be: the chip now carries stamina and a price as well as the
    /// word and the key, and 116dp could not hold them.
    ///
    /// <para>The extra width is taken over the seals' bounding BOXES without touching their drawn rings.
    /// A seal is a circle of radius 39 centred 46dp down its 92dp box, so at the pill's vertical centre
    /// (~15dp from the row's top) the ring has narrowed to cx +/- 23.7 - the left ring reaches x=83 at
    /// most, the right no further left than x=253. 92..244 clears both by roughly 9dp. Do not widen
    /// further without redoing that arithmetic; the rings bulge fast further down.</para></summary>
    private const float PillLeft = 92f;
    private const float PillWidth = 152f;

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
    // it is now computed at paint time in DrawSeal from Mucka.Core.TickStaminaLoss.FadeFactor - see
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
    /// tinting rather than replacing - see <see cref="DrawSeal"/>'s just-lost slice.</summary>
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
    /// <summary>Scratch path for <see cref="DrawRingArc"/> - built once and <c>Reset()</c> before
    /// every arc rather than <c>new SKPath()</c>'d per call (2026-09-02 review finding: this method
    /// runs up to ~8 times per paint, once per opponent seal plus the player's own two). Never held
    /// past the <c>DrawPath</c> call that consumes it, so reusing it across draws is safe - Skia has
    /// already copied whatever it needs onto the canvas by the time the next caller resets it.</summary>
    private readonly SKPath _arcPath = new();
    /// <summary>Scratch path for <see cref="DrawMetronomeToggle"/> - same reasoning as
    /// <see cref="_arcPath"/>, and safe for the same reason: consumed by <c>DrawPath</c>/fill before
    /// the next paint ever touches it again.</summary>
    private readonly SKPath _metronomeBodyPath = new();
    private readonly SKFont _nameFont = new(SKTypeface.Default, 13.5f);
    private readonly SKFont _phraseFont = new(SKTypeface.FromFamilyName("Cascadia Mono") ?? SKTypeface.Default, 13f);
    private readonly SKFont _sealNumFont = new(SKTypeface.Default, 25f);
    private readonly SKFont _tinyFont = new(SKTypeface.Default, 7.5f);
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
    private SKPathEffect? _dashUnmetSeal;   // 4 on, 3 off - a seal for a creature never yet struck
    private SKPathEffect? _dashOverflow;    // 3 on, 3 off - the overflow row's frame

    private SKPathEffect DashResetRule => _dashResetRule ??= SKPathEffect.CreateDash([1f, 3f], 0f);
    private SKPathEffect DashUnmetSeal => _dashUnmetSeal ??= SKPathEffect.CreateDash([4f, 3f], 0f);
    private SKPathEffect DashOverflow  => _dashOverflow  ??= SKPathEffect.CreateDash([3f, 3f], 0f);

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
        _dashUnmetSeal?.Dispose();
        _dashUnmetSeal = null;
        _dashOverflow?.Dispose();
        _dashOverflow = null;
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

            var word = OutcomeWord(ending.Outcome);
            _text.Color = ending.Outcome == FightOutcome.Died ? Hostile : OutcomeTint(InkDim, ending.Outcome);
            canvas.DrawText(word, Pad, y, SKTextAlign.Left, font, _text);

            _text.Color = Ink;
            canvas.DrawText(
                Ellipsize(ending.Name, Content - DeadOutcomeColumn, font),
                Pad + DeadOutcomeColumn, y, SKTextAlign.Left, font, _text);

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
    private const float DeadOutcomeColumn = 62f;

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
        SealSize: SealSize, MaxSlots: MaxSlots);

    /// <summary>
    /// Where roster row <paramref name="rosterIndex"/> is actually drawn, in dp from the panel
    /// content box's top-left, or null when that row has no slot of its own (it is in the overflow
    /// tail, or the panel has not been measured). Same contract and same reason as
    /// <see cref="TickTrackDp"/>: this canvas lays itself out in a fixed 336-unit space and scales
    /// it, so anything positioned in real dp needs the same factor and must not re-derive it.
    /// </summary>
    public static RailRect? OpponentSlotDp(
        double panelWidthDp, double panelHeightDp, int rosterIndex, int participantCount)
        => RailSlotGeometry.OpponentSlotDp(SlotMetrics, panelWidthDp, panelHeightDp, rosterIndex, participantCount);

    /// <summary>Where the stamina seal is drawn, in dp from the panel content box's top-left.</summary>
    public static RailRect StaminaSealDp(double panelWidthDp, double panelHeightDp)
        => RailSlotGeometry.StaminaSealDp(SlotMetrics, panelWidthDp, panelHeightDp);

    /// <summary>One opponent: a stamina SEAL, the name, and the game's own wound phrase. The phrase is
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

        var nameBaseline = y + (SlotHeight / 2f) - 5f;
        var phraseBaseline = y + (SlotHeight / 2f) + 12f;

        DrawMarkedText(
            canvas, Ellipsize(row.Name, SlotNameWidth, _nameFont), SlotTextLeft, nameBaseline,
            SKTextAlign.Left, _nameFont,
            row.IsCurrentTarget ? InkBright : Ink, row.Novelty);

        DrawOpponentSeal(canvas, SlotSealCenterX, y + (SlotHeight / 2f), row);

        // What THIS creature is fighting with, right-aligned opposite its name. A per-participant fact
        // belongs on the participant, not in the player's own weapon column where only one of a pack
        // could ever be named.
        if (row.NpcWeapon is { Length: > 0 } npcWeapon)
        {
            _text.Color = Hostile;
            canvas.DrawText(
                Ellipsize(CombatComposition.DisplayName(npcWeapon), SlotWeaponWidth, _smallFont),
                Pad + Content - 8f, nameBaseline, SKTextAlign.Right, _smallFont, _text);
        }

        DrawWoundPhrase(canvas, SlotTextLeft, phraseBaseline, row);
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
    private const float SlotNameWidth = 120f;
    private const float SlotWeaponWidth = 88f;

    /// <summary>Room for the game's wound phrase on the row below. Wider than the name's, because the
    /// only thing to its right on that row is the damage pair, which starts further over than the
    /// weapon does. The longest phrase in NpcHealthRungs ("superficially injured") is 21 monospace
    /// characters, about 139 units at 11f.</summary>
    private const float SlotPhraseWidth = 218f;

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
            canvas.DrawText(band, x + SlotPhraseWidth, baseline, SKTextAlign.Right, _phraseFont, _text);
            room -= _phraseFont.MeasureText(band) + 8f;
        }

        if (row.HealthPhrase is not { Length: > 0 } raw)
            return;

        _text.Color = row.IsHealthStale ? InkDim : Ink;
        canvas.DrawText(
            Ellipsize(NpcHealthRungs.Label(raw), room, _phraseFont),
            x, baseline, SKTextAlign.Left, _phraseFont, _text);
    }

    /// <summary>
    /// One opponent's seal: a seven-rung ladder bent into a ring, draining from Fit at the origin
    /// toward dead at the far end.
    ///
    /// <para><b>The structure, from the owner's mockup and his stated convention.</b> The origin is
    /// BOTTOM-MIDDLE and the drain runs ANTICLOCKWISE: grey climbs the right side of the face from 6
    /// o'clock, and the lit run carries on from the boundary round to 6 o'clock again. At half gone the
    /// lit run is the left-hand "C" with its top-right tip at 12 o'clock as the leading edge - the
    /// owner's own description. Every prediction sits immediately ahead of that edge, anticlockwise, in
    /// the direction it is travelling. See <c>MudSharp.Combat.StaminaSeal</c> for the worked
    /// positions.</para>
    ///
    /// <para>The player's own STA and MAG seals drain the same way. The owner specified "the sta seals",
    /// and this was extended to the opponent rings on his own grounds for making them rings at all -
    /// "they're no less approximate than a bar, but critically they're the same shape, comparison is
    /// trivial". Same shape draining opposite ways would destroy exactly that.</para>
    ///
    /// <para><b>A ladder, not a stamina bar.</b> Every creature has exactly seven rungs; a giant does
    /// not get more rungs, its rungs are worth more stamina and it labels them with different words. So
    /// the ring is notched into sevenths and the boundary is how far up that ladder the creature has
    /// been driven. The estimator's absolute figures stay under the hood, placing the boundary more
    /// finely INSIDE the seventh the descriptor gave; none of them is drawn. Owner's call: "We're not
    /// asking the player to do math."</para>
    ///
    /// <para><b>The wound phrase beside this ring is load-bearing</b> - see
    /// <see cref="DrawWoundPhrase"/>. It is the bind between the ring and the words MUD2 actually
    /// printed, and it is the last thing on the slot that may be traded for space, never the first.</para>
    ///
    /// <para><b>The three evidence states must look different, and none may read as safe.</b></para>
    /// <list type="bullet">
    /// <item><b>Unmet</b> - a full ring, dashed, in <see cref="SealUnknown"/>, with a "?" in it. Never
    /// empty and never absent: empty reads as nearly dead and absent reads as safe, and a creature
    /// nobody has a reading on is the most dangerous thing that can be standing in the room. It is an
    /// ordinary state and has to carry its own weight - not because MUD2 is sparing with descriptors
    /// (it prints one after every non-killing landed hit) but because the player swings at ONE creature
    /// at a time, so in a pack every other row sits here for the whole fight. Same RADIUS as every
    /// other seal - nothing on this panel changes size - but
    /// it is the only ring whose whole circumference is drawn in a colour that reads (every other ring
    /// draws its full circumference as the near-invisible track and lights only what is left), the only
    /// dashed one, and the only one with a glyph in the middle.</item>
    /// <item><b>Rung</b> - a boundary whose fade spans a whole seventh, because a whole seventh is what
    /// the game said.</item>
    /// <item><b>Narrowed</b> - the same boundary with a visibly tighter fade, because damage landed
    /// since the descriptor printed has placed it inside that seventh.</item>
    /// </list>
    ///
    /// <para><b>The boundary's two sides differ on purpose.</b> Hard on the far side - the creature
    /// certainly still has that much - and a fade back toward the origin. Averaging them into one edge
    /// would turn what the game said into a number it never gave. A <c>diagnose</c> probe, the one
    /// direct measurement MUD2 offers, earns a bright tick at the hard edge.</para>
    /// </summary>
    private void DrawOpponentSeal(SKCanvas canvas, float cx, float cy, RosterRow row)
    {
        var plan = StaminaSeal.Plan(row.Vitality, row.NextBlow, row.BlowAfter);

        _stroke.StrokeWidth = SlotSealStroke;

        if (plan.Shape == SealShape.Unmet)
        {
            _stroke.Color = SealUnknown;
            _stroke.PathEffect = DashUnmetSeal;
            canvas.DrawCircle(cx, cy, SlotSealRadius, _stroke);
            _stroke.PathEffect = null;
            _stroke.StrokeWidth = 1f;

            _text.Color = Ink;
            canvas.DrawText("?", cx, cy + 4.5f, SKTextAlign.Center, _nameFont, _text);
            return;
        }

        // The whole ladder, unclimbed. What the lit run does not cover is what has been taken off.
        _stroke.Color = SealTrack;
        canvas.DrawCircle(cx, cy, SlotSealRadius, _stroke);

        var tone = row.IsHealthStale ? VitalityStale : Vitality;

        // What it MIGHT still have, as a stepped fade running back toward the origin from the hard
        // edge. Three steps rather than a gradient shader: the canvas repaints on state change only,
        // and steps read as "and possibly this far back" just as well at a 14-unit radius while
        // allocating nothing per slot.
        if (plan.LitSoft is SealArc soft && soft.SweepDegrees > 0f)
        {
            const int steps = 3;
            var each = soft.SweepDegrees / steps;
            for (var i = 0; i < steps; i++)
            {
                _stroke.Color = tone.WithAlpha((byte)(0x2C + (i * 0x30)));
                DrawRingArc(canvas, cx, cy, SlotSealRadius, new SealArc(soft.StartDegrees + (i * each), each));
            }
        }

        // What it CERTAINLY still has.
        if (plan.LitHard is SealArc hard)
        {
            _stroke.Color = tone;
            DrawRingArc(canvas, cx, cy, SlotSealRadius, hard);
        }

        DrawStepNotches(canvas, cx, cy, SlotSealRadius, SlotSealStroke);
        _stroke.StrokeWidth = 1f;

        // A boundary that is a MEASUREMENT rather than an inference. The rest of this ring is the
        // game's own adjective plus arithmetic; a diagnose probe read the number off the creature, and
        // saying so is the difference between the two strongest states on the panel.
        if (plan.Measured && plan.LitHard is SealArc measured)
            DrawRadialTick(canvas, cx, cy, SlotSealRadius, SlotSealStroke, measured.StartDegrees, InkBright, 1.6f);

        // rDPT, one lane per blow. Both carry the engagement's tempo, so predictions for a fight where
        // nothing is landing are drawn as brackets or dots rather than as confident boxes.
        var tempo = DamagePrediction.Tempo(row.YourTempo);
        if (plan.NextBlow is SealArc next)
            DrawBandOutline(canvas, cx, cy, NextBlowInner, NextBlowOuter, next, PredictNext, NextBlowStroke, tempo);
        if (plan.BlowAfter is SealArc after)
            DrawBandOutline(canvas, cx, cy, BlowAfterInner, BlowAfterOuter, after, PredictAfter, BlowAfterStroke, tempo);
    }

    /// <summary>
    /// One rDPT prediction band: a thin box drawn AROUND the segment of ladder the blow is predicted to
    /// take, in its own lane outside the ring.
    ///
    /// <para>A box rather than a fat arc, which is the owner's note on his own mockup ("I think the
    /// actual should be more of a bounding box border than a thick outline"). Two concentric arcs and
    /// two radial ends - a bounding box in polar coordinates - so the band encloses what it predicts
    /// instead of competing with the fill for the same visual weight.</para>
    ///
    /// <para>Its LENGTH is the damage bracket's own width carried through the pool interval, so the
    /// band is wide for a creature the client barely knows and tight for one it has killed a dozen
    /// times. Its LANE says which blow it is - see <see cref="NextBlowInner"/> for why hue alone could
    /// not carry that. Its STROKE says how often blows are actually landing. None of the three is a
    /// borrowed constant.</para>
    ///
    /// <para><b>With no swings yet the long sides are not drawn at all</b> - just the two radial ends,
    /// as brackets. Same grammar as <see cref="DrawTempoFrame"/>'s corners and for the same reason: the
    /// band's position is known, how soon it happens is not, and a dotted box would answer the second
    /// question with a measurement nobody has taken.</para>
    /// </summary>
    private void DrawBandOutline(
        SKCanvas canvas, float cx, float cy, float inner, float outer, SealArc arc,
        SKColor color, float stroke, DamagePrediction.TempoStroke tempo)
    {
        _stroke.Color = color;
        _stroke.StrokeWidth = stroke;

        if (tempo.Reading != DamagePrediction.TempoReading.NoEvidence)
        {
            if (tempo.Reading == DamagePrediction.TempoReading.Landing)
                _stroke.PathEffect = TempoDash(tempo.Dot, tempo.Gap);

            DrawRingArc(canvas, cx, cy, outer, arc);
            DrawRingArc(canvas, cx, cy, inner, arc);
            _stroke.PathEffect = null;
        }

        // The two ends are drawn SOLID at every state. They are what says where the band starts and
        // stops, a dash pattern is free to land a gap exactly on one of them, and with no evidence yet
        // they are the whole mark. Two direct calls rather than a `new[] { start, end }` loop
        // (2026-09-02 review finding) - there are only ever two ends and never more, so the array
        // bought nothing but an allocation on every paint.
        DrawBandEnd(canvas, cx, cy, inner, outer, arc.StartDegrees);
        DrawBandEnd(canvas, cx, cy, inner, outer, arc.EndDegrees);

        _stroke.StrokeWidth = 1f;
    }

    /// <summary>One radial end of a band outline - see <see cref="DrawBandOutline"/>'s own remarks
    /// on why both ends are always drawn solid.</summary>
    private void DrawBandEnd(SKCanvas canvas, float cx, float cy, float inner, float outer, float degrees)
    {
        var (ix, iy) = OnRing(cx, cy, inner, degrees);
        var (ox, oy) = OnRing(cx, cy, outer, degrees);
        _stroke.StrokeCap = SKStrokeCap.Butt;
        canvas.DrawLine(ix, iy, ox, oy, _stroke);
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

    /// <summary>One arc on a ring, from the 6 o'clock anticlockwise convention
    /// <c>MudSharp.Combat.StaminaSeal</c> uses. Skia measures from 3 o'clock, hence the -90.</summary>
    private void DrawRingArc(SKCanvas canvas, float cx, float cy, float radius, SealArc arc)
    {
        if (arc.SweepDegrees <= 0f)
            return;
        // The cap is asserted, not assumed. _stroke is shared across this whole paint and the icons on
        // it set Round; a round cap on an arc extends it by half a stroke at each end, which on a
        // 14-unit ring is about eight degrees of stamina this creature does not have.
        _stroke.StrokeCap = SKStrokeCap.Butt;
        // Reused rather than `new SKPath()` per call (see _arcPath's own remarks) - Reset() clears
        // both the point list and any leftover fill type/bounds cache without freeing the backing
        // buffer, so this method allocates nothing on the up-to-~8-per-paint path it is actually on.
        _arcPath.Reset();
        // Logical-to-Skia. Skia puts 0 at 3 o'clock and sweeps CLOCKWISE for a positive angle; the
        // seals put 0 at 6 o'clock and travel ANTICLOCKWISE. 6 o'clock is Skia 90, and anticlockwise is
        // decreasing, so a logical d sits at Skia (90 - d) and a logical sweep is a negative Skia one.
        _arcPath.AddArc(new SKRect(cx - radius, cy - radius, cx + radius, cy + radius),
            90f - arc.StartDegrees, -arc.SweepDegrees);
        canvas.DrawPath(_arcPath, _stroke);
    }

    /// <summary>
    /// The six interior notches that cut a ring into MUD2's seven rung steps.
    ///
    /// <para>Drawn in the panel's own ground colour rather than in a lighter grey, so they read as gaps
    /// in the ring at any fill level instead of as marks that disappear against whichever of the fill
    /// or the track they happen to cross. Six 1.2-unit slivers over the glow layer behind the canvas is
    /// not a cost worth avoiding.</para>
    ///
    /// <para>Only opponents get these. The player's own seal reports an absolute number the game prints
    /// for them, and MUD2 has no wound ladder for the player - notching that ring into sevenths would
    /// impose a vocabulary the game never applies to them.</para>
    /// </summary>
    private void DrawStepNotches(SKCanvas canvas, float cx, float cy, float radius, float ringStroke)
    {
        for (var step = 1; step < NpcHealthRungs.Rungs; step++)
            DrawRadialTick(canvas, cx, cy, radius, ringStroke, StaminaSeal.StepDegrees(step), PanelGround, 1.2f);
    }

    /// <summary>A short radial mark across a ring's stroke, at a given angle anticlockwise from 6
    /// o'clock.</summary>
    private void DrawRadialTick(
        SKCanvas canvas, float cx, float cy, float radius, float ringStroke,
        float degrees, SKColor color, float width)
    {
        var half = (ringStroke / 2f) + 0.6f;
        var (ix, iy) = OnRing(cx, cy, radius - half, degrees);
        var (ox, oy) = OnRing(cx, cy, radius + half, degrees);

        var previous = _stroke.StrokeWidth;
        _stroke.Color = color;
        _stroke.StrokeWidth = width;
        _stroke.StrokeCap = SKStrokeCap.Butt;
        canvas.DrawLine(ix, iy, ox, oy, _stroke);
        _stroke.StrokeWidth = previous;
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

    /// <summary>A point on a ring, in the seals' own convention: degrees ANTICLOCKWISE from 6 o'clock.
    /// 0 is bottom-middle, 90 is 3 o'clock, 180 is 12 o'clock, 270 is 9 o'clock.</summary>
    private static (float X, float Y) OnRing(float cx, float cy, float radius, float degrees)
    {
        var rad = (90f - degrees) * (float)(Math.PI / 180.0);
        return (cx + (radius * (float)Math.Cos(rad)), cy + (radius * (float)Math.Sin(rad)));
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
        // Applied to the seal colours only, never to the pips or the tick - one grammar for
        // "recorded, not current", in one place.
        var wash = live.InCombat ? 1f : SpentWash;

        // The player's device carries the incoming half of the same border language every engaged
        // opponent slot carries: dash density is how often blows are landing on the player, pooled
        // across everything still swinging. Absent between fights and through the grace window, which
        // is the same "no frame means not engaged" rule that makes a resolved opponent read as
        // resolved. Its rectangle is the row's own, so only the dash ever changes.
        if (live.InCombat && !InGracePeriod)
            DrawTempoFrame(canvas, Pad, y, Content, BottomRowHeight, live.IncomingTempo,
                accent: GreatestThreatRow(live) >= 0);

        DrawSeal(canvas, Pad, y, "STA", live.StaminaCurrent, live.StaminaMax,
            live.StaminaCurrent is int s && live.StaminaMax is int m && m > 0
                ? Dim(RatioColor(s, m), wash)
                : InkDim,
            inert: false,
            // Gated on the fight being live: a prediction drawn in the tea room is instrumentation for
            // a flight that has landed, and a tinted slice would still be marking the last blow of a
            // fight that ended.
            live.InCombat && !InGracePeriod ? live.YourNextBlow : null,
            live.InCombat && !InGracePeriod ? live.YourBlowAfter : null,
            live.InCombat && !InGracePeriod ? live.StaminaLostLastTick : 0,
            live.InCombat && !InGracePeriod ? live.StaminaLossUtc : null);

        var magMax = live.MagicMax ?? 0;
        var magInert = magMax <= 0;
        var magColor = magInert
            ? InkDim
            : Dim(live.MagicCurrent is int mc && mc < 20 ? Hostile : Magic, wash);
        DrawSeal(canvas, Pad + Content - SealSize, y, "MAG", live.MagicCurrent, live.MagicMax,
            magColor, magInert);

        DrawWeapon(canvas, ColumnLeft, y, ColumnWidth, live);
        DrawDealt(canvas, ColumnLeft, y + (SealSize / 2f) + 8f, ColumnWidth, live);

        // Grace counts as not fighting, exactly as it does for the tick meter: nothing is attacking, so
        // an instrument telling the player to run is telling them to pay for an escape from a fight that
        // is already over. Read from the bindable rather than the frame state because it changes without
        // the frame being rebuilt.
        DrawFleePill(canvas, y, InGracePeriod ? FleePillStatus.Hidden : live.FleePill, live);
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

        var top = rowTop + PillTopInset;
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
        var baseline = top + (PillHeight / 2f) + 4.5f;
        var label = "Flee";
        if (live.StaminaCurrent is int sta)
            label += " " + sta.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // FleeCostParenthetical, never FleeCostPoints. That field is null both for a flee that is free
        // and for a stamina above anything ever measured, so branching on it drew "leaving is free" and
        // "leaving costs an unknown amount" as the same empty space - and on a weak character the
        // unmeasured case is most of the bar, not a corner. This prints "(-?)" there and nothing at all
        // only where the price really is nothing.
        if (live.FleeCostParenthetical is string price)
            label += " (-" + price + ")";

        // Ellipsized against the room actually left by "^F" and the two paddings. The worst realistic
        // string fits 152dp at 12f with ~19dp to spare, so this should never fire - but font metrics are
        // a platform's to choose, and a price running into the hotkey would make both unreadable at the
        // one moment they matter. Costs nothing until it truncates.
        const float keyReserve = 26f;
        _text.Color = Dim(PillText, wash);
        canvas.DrawText(Ellipsize(label, PillWidth - 22f - keyReserve, _pillFont),
            PillLeft + 11f, baseline, SKTextAlign.Left, _pillFont, _text);

        // "^F", not "Ctrl+F". The owner's shorthand, and its whole job is to remind a player whose hand
        // is already on the keyboard that they do not have to reach for the mouse - so it is drawn even
        // though the chip is also clickable. Terse enough to leave the room the price needs.
        _text.Color = Dim(PillText, wash * 0.75f);
        canvas.DrawText("^F", PillLeft + PillWidth - 11f, baseline, SKTextAlign.Right, _smallFont, _text);
    }

    /// <summary>
    /// Where the flee pill sits, in device-independent units, for a rail rendered at
    /// <paramref name="panelWidthDp"/> wide - the geometry the Composition border/background behind this
    /// canvas has to match exactly. Same contract and same reason as <see cref="TickTrackDp"/>: the rail
    /// lays itself out in a fixed 336-unit space and scales it, so a sibling in real dp needs the same
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
            // Measured up from the panel's bottom edge, mirroring OnPaintSurface's own bottom-up chain:
            // tick row, then the bottom row, then down to the pill's lower edge inside it.
            (Pad + TickRowHeight + BottomRowHeight - PillTopInset - PillHeight) * k,
            PillHeight * k,
            PillRadius * k);
    }

    /// <summary>A seal: ring, value, and its own dim name INSIDE the ring. Never a label
    /// underneath, and never a separate status dot beside it - the ring carries its own
    /// state.</summary>
    private void DrawSeal(SKCanvas canvas, float x, float y, string label, int? value, int? max,
        SKColor color, bool inert,
        DamageBand? nextBlow = null, DamageBand? blowAfter = null, double lostLastTick = 0,
        DateTime? lostAtUtc = null)
    {
        var cx = x + (SealSize / 2f);
        var cy = y + (SealSize / 2f) + SealCenterDropY;
        const float radius = SealRadius;

        _stroke.StrokeWidth = SealStroke;
        _stroke.Color = SealTrack;
        canvas.DrawCircle(cx, cy, radius, _stroke);

        // Drains from 6 o'clock anticlockwise, exactly as an opponent's ladder does: the grey run
        // climbs the right side of the face, and the lit run carries on from the boundary round to 6
        // o'clock again. Two rings on one panel draining opposite ways would be a contradiction the eye
        // has to translate on every glance - and one direction is what lets one rule cover both
        // devices: a mark AHEAD of the boundary is where the next blow puts that side, whoever throws
        // it.
        if (!inert && value is int v && max is int mx && mx > 0)
        {
            var boundary = StaminaSeal.BoundaryFor(Math.Clamp(v / (double)mx, 0.0, 1.0));

            // The slice that has JUST gone, immediately behind the boundary: the same grey as the rest
            // of the spent run, tinted red. Owner's wording, 2026-09-01 - "the same gray but tinted",
            // so it is a tint of the track colour and not a colour of its own, which would read as a
            // fourth thing on a ring that already has three. One slice per tick however many blows
            // landed in it - see Core.TickStaminaLoss for why it is grouped by arrival.
            //
            // The TINT fades over the tick (2026-09-02); the ARC does not. Only the strength read off
            // Core.TickStaminaLoss.FadeFactor changes here - the sweep and the boundary above are
            // exactly the same arithmetic as before, so the slice's LENGTH still reports exactly how
            // much stamina was actually lost for as long as it is drawn at all. lostAtUtc is null
            // exactly when there is nothing to fade from (see CombatLiveView.StaminaLossUtc), in which
            // case this draws nothing rather than guess a strength - CLAUDE.md forbids inventing a
            // timestamp, and the same rule applies to inventing a fade position for a missing one.
            if (lostLastTick > 0 && lostAtUtc is DateTime lossUtc)
            {
                var fade = Mucka.Core.TickStaminaLoss.FadeFactor(lossUtc, DateTime.UtcNow);
                if (fade > 0f)
                {
                    var lost = StaminaSeal.Sweep(Math.Clamp(lostLastTick / mx, 0.0, 1.0));
                    var from = Math.Max(0f, boundary - lost);
                    _stroke.Color = Tint(SealTrack, Hostile, fade);
                    DrawRingArc(canvas, cx, cy, radius, new SealArc(from, boundary - from));
                }
            }

            _stroke.Color = color;
            DrawRingArc(canvas, cx, cy, radius, new SealArc(boundary, 360f - boundary));
        }
        _stroke.StrokeWidth = 1f;

        // The player's own prediction lanes, in the same two-lane grammar every opponent badge uses:
        // inner is the next blow, outer the one after, and both sit ahead of the boundary in the
        // direction it travels. Tempo is Fluent so they draw solid - the dash on an opponent's badge
        // reports how often the PLAYER is landing, which is not a question this device asks.
        if (StaminaSeal.BandArc(nextBlow) is SealArc yourNext)
        {
            DrawBandOutline(
                canvas, cx, cy, SealNextBlowInner, SealNextBlowOuter, yourNext,
                PredictNext, NextBlowStroke, SolidTempo);
        }
        if (StaminaSeal.BandArc(blowAfter) is SealArc yourAfter)
        {
            DrawBandOutline(
                canvas, cx, cy, SealBlowAfterInner, SealBlowAfterOuter, yourAfter,
                PredictAfter, BlowAfterStroke, SolidTempo);
        }

        _text.Color = inert ? Dim(InkDim, 0.7f) : color;
        var text = inert ? "-" : value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "--";
        canvas.DrawText(text, cx, cy + 4f, SKTextAlign.Center, _sealNumFont, _text);

        _text.Color = Dim(InkDim, 0.85f);
        canvas.DrawText(label, cx, cy - 16f, SKTextAlign.Center, _tinyFont, _text);
    }

    /// <summary>
    /// The weapon in hand and the alternate. An icon carries the armed/unarmed state - the word
    /// "armed" is redundant next to a weapon's name.
    ///
    /// <para>`wield` is per-engagement, not sticky: every fight starts empty-handed until the
    /// player says otherwise, so an unarmed opening is normal and must not raise an alarm. Only
    /// once damage has landed does this go amber, and it never goes straight to red.</para>
    ///
    /// <para>That same rule is why the hand state is drawn ONLY while in combat. Between fights the
    /// client genuinely does not know what is in the player's hands - "UNARMED" out of combat is a
    /// claim, not an observation, and it was reading as one while the player sat in the tea room.
    /// Rule 5 of the spec applies: unknown must never render as a measured state, so the column
    /// stays empty (its space still reserved) until a fight states what is in hand.</para>
    /// </summary>
    private void DrawWeapon(SKCanvas canvas, float x, float y, float width, CombatLiveView live)
    {
        var midY = y + (SealSize / 2f);

        if (live.InCombat)
        {
            // The weapon's novelty mark colours the icon as well as the name. Bare hands are a weapon
            // choice in MUD2 (no equipment slots, and fighting unarmed is ordinary), so the mark
            // applies to UNARMED too - "nothing engaged has ever been fought bare-handed" is the same
            // warning as it is for an axe.
            var mark = live.WeaponNovelty;
            var lit = NoveltyColor(mark);
            if (live.IsUnarmed)
            {
                var hurt = live.StaminaCurrent is int sta && live.StaminaMax is int max && max > 0 && sta < max;
                var tone = hurt ? Caution : InkDim;
                DrawOpenHand(canvas, x + 2f, midY - 16f, lit ?? tone);
                DrawMarkedText(canvas, "UNARMED", x + 20f, midY - 8f, SKTextAlign.Left, _weaponFont,
                    tone, mark);
            }
            else if (!string.IsNullOrEmpty(live.WeaponText))
            {
                DrawSword(canvas, x + 2f, midY - 16f, lit ?? Ink);
                DrawMarkedText(canvas, Ellipsize(live.WeaponText, width - 22f, _weaponFont),
                    x + 20f, midY - 8f, SKTextAlign.Left, _weaponFont, Ink, mark);
            }

        }

        // Alternate weapon: hotkey chip left, name right-aligned. Ctrl+W, consistent with the
        // client's other combat bindings; the accelerator marks the event handled so it never
        // reaches a default close-window action.
        //
        // Drawn only when there IS something to switch to. The chip is the key's only advertisement,
        // so showing it with no candidate would advertise a dead key - and the candidate list is
        // empty exactly when nothing carried is on file as a weapon, which includes every moment
        // outside a fight. Its position is fixed either way, so lighting up mid-fight displaces
        // nothing (spec rule 3).
        if (live.AltWeapon is not { Length: > 0 } alt)
            return;

        const float chipWidth = 36f;
        _stroke.Color = Rule;
        canvas.DrawRoundRect(x, midY + 18f, chipWidth, 13f, 3f, 3f, _stroke);
        _text.Color = InkDim;
        canvas.DrawText("Ctrl+W", x + (chipWidth / 2f), midY + 27.5f, SKTextAlign.Center, _tinyFont, _text);

        _text.Color = Ink;
        canvas.DrawText(Ellipsize(CombatComposition.DisplayName(alt), width - chipWidth - 6f, _smallFont),
            x + width, midY + 27.5f, SKTextAlign.Right, _smallFont, _text);
    }

    /// <summary>
    /// What the player has dealt the current target so far, as the RANGE the game printed.
    ///
    /// <para>This closes the one clause of the reasoning the panel exists to support that neither seal
    /// carries. The owner's description of the thought, 2026-09-01: "ok, I've hit for 15-19 3x, and it's
    /// still fit - it still has 4/5ths of its stamina, at least, and I've lost 1/3rd of mine, so I need
    /// to be dropping items to max my dex/str, and thinking about fleeing." The opponent's seal answers
    /// "still fit, so four fifths at least"; the player's seal answers "I've lost a third"; this answers
    /// "I've hit for 15-19 three times", which was otherwise the player remembering the scroll.</para>
    ///
    /// <para><b>A range, never a total.</b> MUD2 gives the player's own blows as brackets and never as
    /// numbers, so the sum of the brackets is what is shown. CombatOutlook's seconds-to-kill clock runs
    /// on a midpoint of the same exchange; that figure is deliberately not carried to the panel at all,
    /// because drawn it would look like a number the game gave.</para>
    ///
    /// <para><b>It states what happened; it does not say what it means.</b> No "you are winning", no
    /// suggestion to drop items or flee. The inference is the player's, and the panel's job is to make
    /// it fast rather than to make it for them.</para>
    ///
    /// <para>In the player's own middle column, under the weapon that did it, on a line whose position
    /// is fixed whether or not a blow has landed yet (rule 3).</para>
    /// </summary>
    private void DrawDealt(SKCanvas canvas, float x, float baseline, float width, CombatLiveView live)
    {
        if (!live.InCombat || live.TargetDealtBracket is not { } dealt || dealt.High <= 0)
            return;

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var range = dealt.Low.ToString("0.#", culture) + "-" + dealt.High.ToString("0.#", culture);

        _text.Color = InkDim;
        canvas.DrawText("dealt", x, baseline, SKTextAlign.Left, _smallFont, _text);
        _text.Color = Ink;
        canvas.DrawText(Ellipsize(range, width - 34f, _smallFont), x + width, baseline,
            SKTextAlign.Right, _smallFont, _text);
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
        var cy = rowTop + (TickRowHeight / 2f);
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

    /// <summary>The tick track's top edge within its row. Shared with <see cref="TickTrackDp"/>, so
    /// the Composition sweep cannot drift off the track the canvas draws.</summary>
    private static float TrackTopIn(float rowTop)
        => rowTop + (TickRowHeight / 2f) - (TickTrackHeight / 2f);

    /// <summary>
    /// Where the tick track sits, in device-independent units, for a rail rendered at
    /// <paramref name="panelWidthDp"/> wide - the geometry the Composition sweep behind this canvas
    /// has to match exactly.
    ///
    /// <para>The rail lays itself out in a fixed 336-unit coordinate space and scales that space to
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
            (Pad + (TickRowHeight / 2f) - (TickTrackHeight / 2f)) * k,
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

    private void DrawSword(SKCanvas canvas, float x, float y, SKColor color)
    {
        _stroke.Color = color;
        _stroke.StrokeWidth = 1.8f;
        _stroke.StrokeCap = SKStrokeCap.Round;
        canvas.DrawLine(x + 2f, y + 14f, x + 13f, y + 2f, _stroke);
        canvas.DrawLine(x + 8f, y + 2f, x + 14f, y + 8f, _stroke);
        _stroke.StrokeWidth = 1f;
    }

    /// <summary>An open hand - four fingers and a thumb. Deliberately literal: the previous
    /// icon was a curve and a dot and read as nothing at all.</summary>
    private void DrawOpenHand(SKCanvas canvas, float x, float y, SKColor color)
    {
        _stroke.Color = color;
        _stroke.StrokeWidth = 1.5f;
        _stroke.StrokeCap = SKStrokeCap.Round;
        for (var i = 0; i < 4; i++)
        {
            var fx = x + 3f + (i * 3f);
            canvas.DrawLine(fx, y + 3f + (i == 0 || i == 3 ? 2f : 0f), fx, y + 9f, _stroke);
        }
        canvas.DrawLine(x + 1f, y + 8f, x + 3f, y + 6f, _stroke);
        _fill.Color = color;
        canvas.DrawRoundRect(x + 2f, y + 8f, 11f, 6f, 3f, 3f, _fill);
        _stroke.StrokeWidth = 1f;
    }

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
        // short" rather than "interrupted" only because the word is drawn unclipped in a 62f column
        // ahead of the name (see DeadOutcomeColumn); which of the four reasons it was is in the
        // clog's EncounterForceEnded event, not on a roster row.
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
