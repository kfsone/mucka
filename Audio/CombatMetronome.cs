namespace Mucka.Audio;

/// <summary>
/// Bookmarks each MUD2 combat tick rollover with two percussion clicks - one shortly BEFORE the
/// boundary and one shortly AFTER - so the player can feel where a turn ends without watching the bar.
///
/// <para><b>What this is for (owner, 2026-08-19).</b> Marking the ROLLOVER, nothing more. MUD2 is not
/// an MMO or an FPS: there is no button to press on the beat, and every decision has to be typed and
/// transmitted well before the boundary anyway. What the click buys is a sense of timing for reading
/// the combat TEXT over in the terminal - and it earns that most in the case the text itself cannot
/// cover, where many ticks pass with no swing at all and the player is otherwise blind to whether the
/// fight is still running to schedule.</para>
///
/// <para>That is why the two offsets are SYMMETRIC about the boundary
/// (<see cref="OffsetMilliseconds"/>) rather than the earlier wide-lead/tight-trail pair. The old
/// shape was built to be heard AS a warning - a quarter-second announcement, then a marker - which is
/// what a reaction game needs. Bracketing the rollover evenly is what a bookmark needs, and it also
/// lets the trailing click sit far enough past the boundary to follow the swing text rather than
/// precede it: text arrives within 25 ms of the lattice 88% of the time but tails out to ~196 ms late
/// on roughly one swing-carrying tick in eleven (tools/combat/archive/TICK-PHASE-REVIEW.md), which the
/// previous 100 ms trail sat inside.</para>
///
/// <para><b>One alternating chain, not two independent schedules.</b> Each beat's own job is to
/// schedule the next - after-tick, then pre-tick, then after-tick - and every delay is recomputed from
/// the anchor rather than from "now". A <c>System.Threading.Timer</c> with a fixed 2000 ms period
/// schedules each firing relative to the last, so Windows timer slop (~15 ms granularity) would
/// ACCUMULATE and the click would walk off the boundary over a long fight, which is what "they don't
/// seem synced at all" looks like from the outside. Deriving every delay from the absolute anchor makes
/// each beat self-correcting.</para>
///
/// <para><b>Every beat re-checks that the fight is still on.</b> Silence is the correct output for a
/// finished fight, and the driver's own <see cref="Stop"/> cannot be relied on to have arrived yet: it
/// comes through a UI-thread hop, so there is a real window in which this timer is the only thing that
/// knows. The chain keeps running through that - staying on the lattice, just making no sound - and is
/// torn down by <see cref="Stop"/> when the driver does catch up.</para>
///
/// <para><b>Not a UI-thread timer.</b> Invariant #1 forbids repeating UI-thread timers, and this is
/// exactly the kind of thing that would tempt one. It uses a thread-pool <see cref="Timer"/> and never
/// touches the UI thread at all: <c>SoundService.Play</c> is fire-and-forget and explicitly safe to
/// call from a background thread (it is already called from the TCP thread). Nothing here draws,
/// measures, or invalidates anything.</para>
///
/// <para>Armed from the same anchor, in the same synchronous block, as the visual tick sweep, and both
/// locate the boundary through <see cref="Mucka.Core.CombatTiming.MillisecondsToNextBoundary"/> - one
/// implementation, so the sound and the bar cannot disagree about where the rollover is.</para>
/// </summary>
internal sealed class CombatMetronome : IDisposable
{
    /// <summary>One MUD2 combat tick - shared with <c>Mucka.Rendering.TickSweep</c> via
    /// <see cref="Mucka.Core.CombatTiming.TickMilliseconds"/> so the click and the bar can never
    /// independently drift apart.</summary>
    private const int TickMilliseconds = (int)Mucka.Core.CombatTiming.TickMilliseconds;

    /// <summary>
    /// How far either side of the rollover the two clicks sit - the pre-tick at <c>boundary - N</c>,
    /// the after-tick at <c>boundary + N</c>.
    ///
    /// <para>200 ms, so the pair spans 400 ms: far enough apart to be heard as two events bracketing
    /// something rather than as one doubled hit, and far enough past the boundary on the trailing side
    /// to land after the swing text even on its late tail (see the class remarks). This is the knob
    /// worth turning if the bracket reads wrong in play - widen it and the boundary is easier to place
    /// but the pair stops reading as a pair; narrow it and the reverse.</para>
    /// </summary>
    private const int OffsetMilliseconds = 200;

    /// <summary>Pre-tick: "the rollover is about to happen".</summary>
    private const string PreTickClick = "sounds/Perc_Stick_hi.wav";

    /// <summary>After-tick: "it has happened - whatever this turn did is on screen now".</summary>
    private const string AfterTickClick = "sounds/Perc_Stick_lo.wav";

    private readonly object _gate = new();
    private Timer? _timer;
    private DateTime _anchorUtc;
    private bool _disposed;

    /// <summary>Asked at every beat: is there still a fight to bookmark? Supplied by the driver, which
    /// owns that judgement (panel visible, in combat, not in the post-fight grace window); this class
    /// deliberately does not try to infer it.</summary>
    private Func<bool>? _stillInCombat;

    /// <summary>Bumped by every <see cref="Start"/> and every <see cref="StopLocked"/>, and captured by
    /// each scheduled beat.
    ///
    /// <para>Needed because "a timer exists" is the wrong question for a beat that was scheduled a
    /// while ago: the driver re-anchors by calling <c>Stop()</c> then <c>Start()</c>, so a beat waking
    /// later may well find a timer - a NEW one, on a DIFFERENT lattice - and would sound against a
    /// phase it was never scheduled for, off by whatever the gap between the two anchors happened to
    /// be.</para>
    ///
    /// <para>Not hypothetical: the driver re-anchors once per fight, at the moment the encounter's
    /// first swing reveals the phase, so a stray beat lands at the START of every fight - exactly where
    /// a listener is calibrating their sense of the beat. Mirrors the generation guard
    /// <c>TickSweep</c> already carries for the identical reason on the visual side.</para></summary>
    private int _generation;

    /// <summary>Whether the player has asked for the click at all. Independent of whether a fight is
    /// happening: the metronome only runs when BOTH this is set and combat is live.</summary>
    public bool Enabled { get; private set; }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            // Open both clips before they are ever needed. The first PlayPrepared for an asset pays
            // the WinRT media-open cost, and paying it on the first click of a fight is exactly the
            // click that most needs to be on time.
            SoundService.PrepareSound(PreTickClick);
            SoundService.PrepareSound(AfterTickClick);
        }

        lock (_gate)
        {
            if (Enabled == enabled)
                return;
            Enabled = enabled;
            if (!enabled)
                StopLocked();
        }
    }

    /// <summary>
    /// Arms the chain, if the player has the click switched on. Idempotent - calling it while already
    /// running does nothing, so it can be driven from the same combat-state handler as the visual sweep
    /// without restarting the beat several times a second.
    /// </summary>
    /// <param name="tickAnchorUtc">A known tick boundary - the same instant the visual sweep was
    /// started from. Nothing sounds immediately: the first beat is the AFTER-tick click for the
    /// rollover the sweep is currently counting down to. That matters because arming the metronome
    /// halfway through a fight must not re-anchor the beat to the moment the switch was flipped - a
    /// metronome that clicks on the player's button press rather than on the game's tick is worse than
    /// silence, since it would sound authoritative while being wrong by up to a full tick.</param>
    /// <param name="stillInCombat">Re-asked at every beat; false means stay silent. See
    /// <see cref="_stillInCombat"/>.</param>
    public void Start(DateTime tickAnchorUtc, Func<bool> stillInCombat)
    {
        lock (_gate)
        {
            if (_disposed || !Enabled || _timer is not null)
                return;

            _anchorUtc = tickAnchorUtc;
            _stillInCombat = stillInCombat;
            var generation = ++_generation;

            // The after-tick click for the boundary the bar is counting down to right now: the same
            // "remaining" the sweep was just given, plus the offset that puts this click past the
            // rollover rather than on it.
            var remaining = Mucka.Core.CombatTiming.MillisecondsToNextBoundary(tickAnchorUtc, DateTime.UtcNow);
            var delay = (int)Math.Round(remaining) + OffsetMilliseconds;

            Mucka.Core.TickDiag.Log(
                $"anchor   metronome armed on {tickAnchorUtc:HH:mm:ss.fff} (gen {generation}); "
                + $"boundary in {remaining:F0} ms, after-tick in {delay} ms, N={OffsetMilliseconds}");

            _timer = new Timer(_ => Beat(generation, afterTick: true), null, delay, Timeout.Infinite);
        }
    }

    public void Stop()
    {
        lock (_gate)
            StopLocked();
    }

    private void StopLocked()
    {
        // Bump before disposing, so a beat already scheduled finds itself stale rather than sounding
        // into a silence the driver has just asked for.
        _generation++;
        _timer?.Dispose();
        _timer = null;
        _stillInCombat = null;
    }

    /// <summary>
    /// One beat of the chain: schedule the next, then sound this one if the fight is still on.
    /// </summary>
    /// <param name="afterTick">True for the click that follows a rollover, false for the one that
    /// precedes the next. The two alternate, and the gaps between them are what encode the phase:
    /// after-tick to pre-tick is a whole tick less both offsets, pre-tick to after-tick is just the two
    /// offsets.</param>
    private void Beat(int generation, bool afterTick)
    {
        Func<bool>? stillInCombat;
        lock (_gate)
        {
            if (_disposed || _timer is null || generation != _generation)
                return;

            stillInCombat = _stillInCombat;

            // Re-arm FIRST, so however long the sound takes cannot push the next beat late. Both legs
            // are constants rather than "time until the next boundary" on purpose: the alternation
            // itself carries the phase, and the drift figure below is what reveals if that has slipped.
            var next = afterTick ? TickMilliseconds - (2 * OffsetMilliseconds) : 2 * OffsetMilliseconds;
            Mucka.Core.TickDiag.Log(
                $"beat {(afterTick ? "after" : "pre  ")}  drift={DriftMilliseconds(_anchorUtc, afterTick),6:F1} ms   "
                + $"next {(afterTick ? "pre" : "after")}-tick in {next,4} ms");
            _timer.Change(next, Timeout.Infinite);
        }

        // Checked after re-arming, deliberately: a lull must leave the chain ON the lattice rather than
        // tear it down, so that it is still correct for the ticks the player cannot see any text for -
        // which is the whole reason this instrument exists. Silence, not desynchronisation.
        if (stillInCombat is null || !stillInCombat())
            return;

        PlayTimed(afterTick ? AfterTickClick : PreTickClick, afterTick ? "after" : "pre");
    }

    /// <summary>How far this beat ran from the instant it was scheduled for - positive is late.
    /// Diagnostics only: nothing corrects on it. A value that stays small confirms the chain is holding
    /// the lattice; one that grows across a fight means the anchor is being replaced underneath it,
    /// which is a driver problem rather than a timer one.</summary>
    private static double DriftMilliseconds(DateTime anchorUtc, bool afterTick)
    {
        var toNext = Mucka.Core.CombatTiming.MillisecondsToNextBoundary(anchorUtc, DateTime.UtcNow);
        // The after-tick beat should sit OffsetMilliseconds past the previous boundary; the pre-tick
        // beat should sit OffsetMilliseconds before the next one.
        return afterTick ? (TickMilliseconds - toNext) - OffsetMilliseconds : OffsetMilliseconds - toNext;
    }

    /// <summary>Plays a click and, under TICK_DIAG, records how long the audio call took to return.
    ///
    /// <para>Worth measuring rather than assuming: the offsets are only 200 ms, so a play path costing
    /// a comparable amount collapses the bracket onto the boundary and erases the very distinction the
    /// player is listening for. A quick return is not proof the sound was audible on time, but a slow
    /// return IS proof it was not.</para></summary>
    private static void PlayTimed(string asset, string label)
    {
#if TICK_DIAG
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Play(asset);
        Mucka.Core.TickDiag.Log($"beat audio   {label,-5} play call returned in {sw.Elapsed.TotalMilliseconds,6:F1} ms");
#else
        Play(asset);
#endif
    }

    /// <summary>Master mute wins over the toggle, matching how every other client-initiated sound in
    /// the app behaves (see MappingSession's own guard). Not gated on the per-sound catalogue though:
    /// this is a client instrument the player armed deliberately, not a server-triggered effect, so
    /// the switch beside the tick bar is its own enablement.</summary>
    private static void Play(string asset)
    {
        if (SoundService.MasterEnabled)
            SoundService.PlayPrepared(asset);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            StopLocked();
        }
    }
}
