
namespace Mucka.Audio;

/// <summary>
/// Bookmarks each MUD2 combat tick rollover with two percussion clicks - one shortly BEFORE the
/// boundary and one shortly AFTER - so the player can feel where a turn ends without watching the bar.
///
/// <para>Marks the ROLLOVER, nothing more. MUD2 is not an MMO or an FPS: there is no button to press
/// on the beat, and every decision has to be typed and transmitted well before the boundary anyway.
/// What the click buys is a sense of timing for reading the combat TEXT over in the terminal - and it
/// earns that most in the case the text itself cannot cover, where many ticks pass with no swing at
/// all and the player is otherwise blind to whether the fight is still running to schedule.</para>
///
/// <para>The two SOUNDS sit symmetrically either side of the boundary - see
/// <see cref="OffsetMilliseconds"/> - producing a "tik-tok" with neither click landing on it: the
/// boundary is the silence between the two sounds, not a warning announcement.</para>
///
/// <para><b>One alternating chain, not two independent schedules.</b> Each beat's own job is to
/// schedule the next, and every delay is recomputed from the ANCHOR rather than from the instant the
/// callback happened to run - see <see cref="Mucka.Combat.CombatTiming.NextBeat"/>, which owns that
/// arithmetic and is unit-tested against injected lateness.</para>
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
/// locate the boundary through <c>Mucka.Combat.CombatTiming</c> - one implementation, so the sound and
/// the bar cannot disagree about where the rollover is. Note this holds only because the schedule is
/// anchor-derived: the bar consults the lattice ONCE per fight and then runs a compositor animation
/// that keeps its own time, so a click chain that drifted would drift away from a bar that did
/// not.</para>
/// </summary>
internal sealed class CombatMetronome : IDisposable
{
    /// <summary>
    /// How far either side of the rollover the two sounds sit - the pre-tick's last non-silent sample at
    /// <c>boundary - N</c>, the after-tick's first non-silent sample at <c>boundary + N</c>. Operator-set:
    /// 25 ms, for a 50 ms silence centred on the boundary.
    ///
    /// <para><b>N is measured to the non-silent edges, not to the files.</b> The clips'
    /// leading and trailing silence is subtracted by <see cref="Mucka.Sounds.BracketPlacement"/>, using
    /// each clip's span - see <see cref="_preSpan"/>.</para>
    ///
    /// <para>Must stay under half a tick - <see cref="Mucka.Combat.CombatTiming.NextBeat"/> throws
    /// otherwise, since past that the two beats cross and the lattice stops being a bracket.</para>
    /// </summary>
    private const int OffsetMilliseconds = 25;

    /// <summary>Pre-tick: "the rollover is about to happen".</summary>
    private const string PreTickClick = "sounds/Perc_Stick_hi.wav";

    /// <summary>
    /// Where each click's non-silent content sits inside its file, so the two can be scheduled by the
    /// sound rather than by where the files begin and end.
    ///
    /// <para>The shipped assets run 199.6 ms, with their first non-silent sample at 30 ms and their last
    /// near 110 ms - see <see cref="Mucka.Sounds.WavProbe.SilenceFloorDbfs"/>.</para>
    ///
    /// <para>Resolved ONCE, on the thread pool, in <see cref="SetEnabled"/>. The scheduling path only
    /// reads these fields - it must not resolve them, because <see cref="Start"/> runs on the UI thread
    /// inside <c>_gate</c> and this is file I/O.</para>
    /// </summary>
    private volatile object? _preSpan;
    private volatile object? _afterSpan;

    /// <summary>Where the pre-tick clip is STARTED, before the boundary, so its last non-silent sample
    /// lands at <c>boundary - N</c>.</summary>
    private double PreTickLeadMilliseconds()
        => Mucka.Sounds.BracketPlacement.PreLeadMs(OffsetMilliseconds, _preSpan as Mucka.Sounds.ClipSpan?);

    /// <summary>Where the after-tick clip is STARTED relative to the boundary, so its first non-silent
    /// sample lands at <c>boundary + N</c>. Negative when its leading silence is longer than N: it then
    /// starts before the boundary (the shipping asset's 30 ms of lead against N=25 is -5 ms).</summary>
    private double AfterTickOffsetMilliseconds()
        => Mucka.Sounds.BracketPlacement.AfterOffsetMs(OffsetMilliseconds, _afterSpan as Mucka.Sounds.ClipSpan?);

    /// <summary>After-tick: "it has happened - whatever this turn did is on screen now".</summary>
    private const string AfterTickClick = "sounds/Perc_Stick_lo.wav";

    /// <summary>How long before a beat's due instant its timer fires; the rest is spun out in
    /// <see cref="Beat"/>. EVIDENCE: a pre-click timer without it fired 12 ms after its target on Windows.
    /// </summary>
    private const double SpinLeadMilliseconds = 20.0;

    private readonly object _gate = new();
    private DateTime _dueUtc;
    private Timer? _timer;
    private DateTime _anchorUtc;
    private bool _disposed;

    /// <summary>
    /// Which click the NEXT beat is, taken from the same call that computed its delay - NOT toggled,
    /// because after a skipped beat a toggle would keep alternating and put every later click on the
    /// wrong sample. Held as state rather than passed to the callback because
    /// <see cref="Timer.Change(int, int)"/> reschedules a timer but CANNOT replace its callback, so a
    /// click kind captured as a callback parameter at arming time would never update again.
    /// </summary>
    private bool _nextIsAfterTick;

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

    /// <summary>
    /// Idempotent, and cheap when nothing changes - the driver calls this on every combat state
    /// refresh (which is every combat event, every heartbeat and every 1 Hz tick) to keep this flag
    /// and the view model's own from drifting apart. So the media-open work happens on the
    /// TRANSITION only, to avoid repeated file work on the UI thread at that call frequency
    /// (Invariant #1).
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        bool becameEnabled;
        lock (_gate)
        {
            if (Enabled == enabled)
                return;
            Enabled = enabled;
            becameEnabled = enabled;
            if (!enabled)
                StopLocked();
        }

        if (becameEnabled)
        {
            // Open both clips before they are ever needed. The first PlayPrepared for an asset pays
            // the WinRT media-open cost, and paying it on the first click of a fight is exactly the
            // click that most needs to be on time. (PrepareSound hands the open to the thread pool
            // itself, so this is outside the lock for tidiness rather than to avoid blocking a beat -
            // what actually keeps this cheap on the driver's hot path is the transition check above.)
            SoundService.PrepareSound(PreTickClick);
            SoundService.PrepareSound(AfterTickClick);

            // Both clips' audible spans, resolved off the UI thread and exactly once. Deliberately NOT
            // done on demand from the scheduling path - see _preSpan for what that cost.
            if (_preSpan is null || _afterSpan is null)
                Task.Run(() =>
                {
                    _preSpan ??= SoundService.WavClipSpan(PreTickClick);
                    _afterSpan ??= SoundService.WavClipSpan(AfterTickClick);
                });
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
    /// <returns>
    /// True when a chain is armed and will click. False when this call declined - disposed, not
    /// <see cref="Enabled"/>, or already running.
    ///
    /// <para>Returned rather than void because the driver caches "the metronome is running" to avoid
    /// re-arming on every combat event; if a decline were not reported the cache would assert a chain
    /// that does not exist and never retry, going silent for the rest of the fight.</para>
    /// </returns>
    public bool Start(DateTime tickAnchorUtc, Func<bool> stillInCombat)
    {
        lock (_gate)
        {
            if (_disposed || !Enabled || _timer is not null)
                return false;

            _anchorUtc = tickAnchorUtc;
            _stillInCombat = stillInCombat;
            var generation = ++_generation;

            // Whichever beat comes first on the lattice, which may be either kind - forcing the first
            // beat to always be the after-tick would throw away one pre-tick per arming and make the
            // first sound of every fight always the low click, at precisely the moment a listener is
            // calibrating their sense of the beat.
            var (delay, afterTick) = Mucka.Combat.CombatTiming.NextBeat(
                tickAnchorUtc, DateTime.UtcNow, AfterTickOffsetMilliseconds(), PreTickLeadMilliseconds());
            _nextIsAfterTick = afterTick;

            if (Mucka.Core.TickDiag.Enabled)
                Mucka.Core.TickDiag.Log(
                $"anchor   metronome armed on {tickAnchorUtc:HH:mm:ss.fff} (gen {generation}); "
                + $"first beat {(afterTick ? "after" : "pre")}-tick in {delay:F0} ms, N={OffsetMilliseconds}");

            _dueUtc = DateTime.UtcNow.AddMilliseconds(delay);
            _timer = new Timer(_ => Beat(generation), null,
                Math.Max(0, (int)Math.Round(delay - SpinLeadMilliseconds)), Timeout.Infinite);
            return true;
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
    /// <remarks>Which click this is comes from <see cref="_nextIsAfterTick"/>, not from a parameter -
    /// see that field for why a parameter could not work. The two alternate, and the gaps between them
    /// are what encode the phase: after-tick to pre-tick is a whole tick less both offsets, pre-tick to
    /// after-tick is just the two offsets.</remarks>
    private void Beat(int generation)
    {
        DateTime dueUtc;
        lock (_gate)
        {
            if (_disposed || _timer is null || generation != _generation)
                return;
            dueUtc = _dueUtc;
        }

        // The timer fires up to SpinLeadMilliseconds early and the remainder is waited out here, because
        // a thread-pool timer resolves to the OS clock tick and only ever fires late. Off the UI thread.
        var spin = new SpinWait();
        while (DateTime.UtcNow < dueUtc && (dueUtc - DateTime.UtcNow).TotalMilliseconds < SpinLeadMilliseconds * 2)
            spin.SpinOnce(-1);

        Func<bool>? stillInCombat;
        bool afterTick;
        lock (_gate)
        {
            if (_disposed || _timer is null || generation != _generation)
                return;

            stillInCombat = _stillInCombat;
            afterTick = _nextIsAfterTick;

            // Re-arm FIRST, so however long the sound takes cannot push the next beat late - and derive
            // the delay from the ANCHOR, never from this callback's own execution instant. Every delay
            // comes off the lattice, so one beat's lateness is absorbed by the next delay instead of
            // being added to all of them.
            //
            // The kind comes back from the same call rather than being toggled here: after a skipped
            // beat a toggle would keep alternating and put every later click on the wrong sample.
            var (next, nextAfterTick) = Mucka.Combat.CombatTiming.NextBeat(
                _anchorUtc, DateTime.UtcNow, AfterTickOffsetMilliseconds(), PreTickLeadMilliseconds());
            _nextIsAfterTick = nextAfterTick;
            if (Mucka.Core.TickDiag.Enabled)
                Mucka.Core.TickDiag.Log(
                $"beat {(afterTick ? "after" : "pre  ")}  late={(DateTime.UtcNow - dueUtc).TotalMilliseconds,6:F1} ms   "
                + $"next {(nextAfterTick ? "after" : "pre  ")}-tick in {next,6:F0} ms");
            _dueUtc = DateTime.UtcNow.AddMilliseconds(next);
            _timer.Change(Math.Max(0, (int)Math.Round(next - SpinLeadMilliseconds)), Timeout.Infinite);
        }

        // Checked after re-arming, deliberately: a lull must leave the chain ON the lattice rather than
        // tear it down, so that it is still correct for the ticks the player cannot see any text for -
        // which is the whole reason this instrument exists. Silence, not desynchronisation.
        if (stillInCombat is null || !stillInCombat())
        {
            Mucka.Core.TickDiag.Log("beat     silent: no longer in combat");
            return;
        }

        PlayTimed(afterTick ? AfterTickClick : PreTickClick, afterTick ? "after" : "pre");
    }

    /// <summary>Plays a click and, with tick diagnostics on, records how long the audio call took to
    /// return. A quick return is not proof the sound was audible on time, but a slow return IS proof it
    /// was not.</summary>
    private static void PlayTimed(string asset, string label)
    {
        if (!Mucka.Core.TickDiag.Enabled)
        {
            Play(asset);
            return;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Play(asset);
        Mucka.Core.TickDiag.Log($"beat audio   {label,-5} play call returned in {sw.Elapsed.TotalMilliseconds,6:F1} ms");
    }

    /// <summary>Master mute wins over the toggle, matching how every other client-initiated sound in
    /// the app behaves. Not gated on the per-sound catalogue though:
    /// this is a client instrument the player armed deliberately, not a server-triggered effect, so
    /// the switch beside the tick bar is its own enablement.</summary>
    private static void Play(string asset)
    {
        if (SoundService.MasterEnabled)
            SoundService.PlayPrepared(asset);
        else
            Mucka.Core.TickDiag.Log("beat     silent: master sounds off");
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
