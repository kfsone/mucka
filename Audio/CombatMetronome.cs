namespace Mucka.Audio;

/// <summary>
/// Brackets each MUD2 combat tick with two percussion clicks - a high one shortly BEFORE the
/// boundary and a low one shortly AFTER - so the fight's rhythm can be heard instead of watched.
///
/// <para><b>Why a bracket rather than a beat.</b> MUD2 is not a reaction game. Nothing is gained by
/// cueing the player to press something at the instant of the tick; every decision has to be typed
/// and TRANSMITTED before the boundary, so by the time a single on-the-beat click sounds it is
/// already too late to act on. What the tick actually delivers is a status update - the swing lines,
/// the health descriptor, the stamina change. The two clicks therefore bracket the interval in which
/// that information lands: the high click says "it is about to arrive", the low click says "it has,
/// and this is your turn now". Attention, not action.</para>
///
/// <para>The tick bar answers the same question visually, but reading it costs a glance away from the
/// terminal text, which is the one thing the rail exists to avoid. Hearing it costs nothing.</para>
///
/// <para><b>Not a UI-thread timer.</b> Invariant #1 forbids repeating UI-thread timers, and this is
/// exactly the kind of thing that would tempt one. It uses a thread-pool <see cref="Timer"/> and never
/// touches the UI thread at all: <c>SoundService.Play</c> is fire-and-forget and explicitly safe to
/// call from a background thread (it is already called from the TCP thread). Nothing here draws,
/// measures, or invalidates anything.</para>
///
/// <para>Started and stopped from the same place as the visual tick sweep, so the sound and the bar
/// are two renderings of one clock rather than two clocks that will drift apart on screen.</para>
/// </summary>
internal sealed class CombatMetronome : IDisposable
{
    /// <summary>One MUD2 combat tick - shared with <c>Mucka.Rendering.TickSweep</c> via
    /// <see cref="Mucka.Core.CombatTiming.TickMilliseconds"/> so the click and the bar can never
    /// independently drift apart.</summary>
    private const int TickMilliseconds = (int)Mucka.Core.CombatTiming.TickMilliseconds;

    /// <summary>
    /// How far ahead of the boundary the high click sits - "the update is about to land".
    ///
    /// <para>Wide on purpose. At the original 100 ms the two clicks sat 300 ms apart with the
    /// boundary buried between them, and the pair read as one event rather than as a bracket around
    /// the turn's end. A quarter-second of warning is enough to be heard AS warning - it is about the
    /// length of a spoken syllable - while still being unmistakably part of the same beat as the
    /// trailing click rather than a separate one.</para>
    /// </summary>
    private const int LeadMilliseconds = 275;

    /// <summary>
    /// How far after the boundary the low click sits - "it has landed, this is your turn".
    ///
    /// <para>Deliberately NOT symmetric with the lead: the pair has to be heard as bracketing the
    /// boundary, and a close trailing click is what pins the boundary to a definite instant. The wide
    /// lead announces, the tight trail marks.</para>
    ///
    /// <para><b>Known tradeoff.</b> Swing text arrives on a one-sided late tail: 88% within 25 ms of
    /// the lattice, but ~9% between 120 and 196 ms late (source analysis:
    /// tools/combat/archive/TICK-PHASE-REVIEW.md). At +100 the low click therefore precedes the
    /// text it marks on roughly one swing-carrying tick in eleven. The earlier +200 cleared that worst
    /// case, at the cost of a bracket too wide and too late to read as one. Being right about the
    /// TICK, and near-always right about the text, was judged the better trade; if the early-marker
    /// case turns out to be audible in play, this is the number to move back.</para>
    /// </summary>
    private const int TrailMilliseconds = 100;

    private const string HighClick = "sounds/Perc_Stick_hi.wav";
    private const string LowClick = "sounds/Perc_Stick_lo.wav";

    private readonly object _gate = new();
    private Timer? _timer;
    private DateTime _anchorUtc;
    private bool _disposed;

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
            SoundService.PrepareSound(HighClick);
            SoundService.PrepareSound(LowClick);
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
    /// Starts clicking on the beat, if the player has it switched on. Idempotent - calling it while
    /// already running does nothing, so it can be driven from the same combat-state handler as the
    /// visual sweep without restarting the beat several times a second.
    /// </summary>
    /// <param name="tickAnchorUtc">A known tick boundary - the same instant the visual sweep was
    /// started from. The first click is delayed to the next boundary measured from here, NOT fired
    /// immediately, which is the whole point: arming the metronome halfway through a fight must not
    /// re-anchor the beat to the moment the switch was flipped. A metronome that clicks on the
    /// player's button press instead of on the game's tick is worse than silence - it would look
    /// authoritative while being wrong by up to a full tick, and the entire value of this thing is
    /// that the click coincides with the swing.</param>
    public void Start(DateTime tickAnchorUtc)
    {
        lock (_gate)
        {
            if (_disposed || !Enabled || _timer is not null)
                return;

            _anchorUtc = tickAnchorUtc;
            // ONE-SHOT, re-armed from the anchor after every click - deliberately not a periodic
            // timer. A System.Threading.Timer with a 2000 ms period schedules each firing relative to
            // the last, so Windows timer slop (~15 ms granularity) ACCUMULATES: over a sixty-tick
            // fight the click walks away from the boundary entirely, which is what "they don't seem
            // synced at all" looks like from the outside. Recomputing the delay against the absolute
            // anchor every time makes each click self-correcting, so error can never build up.
            _timer = new Timer(_ => Click(), null, DelayToNextLead(tickAnchorUtc), Timeout.Infinite);
        }
    }

    /// <summary>Milliseconds until the next high click - one lead-width before the next tick
    /// boundary measured from <paramref name="anchorUtc"/>.</summary>
    private static int DelayToNextLead(DateTime anchorUtc)
    {
        var elapsed = (DateTime.UtcNow - anchorUtc).TotalMilliseconds;
        // Shift the phase forward by the lead so the arithmetic below is about the LEAD instant
        // rather than the boundary, which keeps the modulo from having to handle a negative target.
        var intoCycle = (elapsed + LeadMilliseconds) % TickMilliseconds;
        if (intoCycle < 0)
            intoCycle += TickMilliseconds;

        var remaining = TickMilliseconds - intoCycle;
        // Never schedule a click so close to now that it lands after the moment it is announcing.
        return remaining < 20 ? (int)Math.Round(remaining + TickMilliseconds) : (int)Math.Round(remaining);
    }

    public void Stop()
    {
        lock (_gate)
            StopLocked();
    }

    private void StopLocked()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Fires one lead-width BEFORE a tick boundary: sounds the high click, then schedules
    /// the low one for a trail-width after the boundary.</summary>
    private void Click()
    {
        lock (_gate)
        {
            if (_disposed || _timer is null)
                return;
            // Re-arm first, so the next beat's schedule is not affected by however long this one
            // takes, and is measured from the anchor rather than from now.
            _timer.Change(DelayToNextLead(_anchorUtc), Timeout.Infinite);
        }

        Play(HighClick);
        _ = PlayTrailingClickAsync();
    }

    /// <summary>The low click. Deliberately a delayed
    /// continuation rather than a second timer: it is a one-shot follow-up to a click that has already
    /// happened, and giving it its own Timer would mean a second disposable object per tick and a
    /// second thing that can outlive Stop(). Re-checks state on waking, so a fight that ends inside
    /// the bracket does not get a trailing click after the silence has started.</summary>
    private async Task PlayTrailingClickAsync()
    {
        await Task.Delay(LeadMilliseconds + TrailMilliseconds).ConfigureAwait(false);

        lock (_gate)
        {
            if (_disposed || _timer is null)
                return;
        }

        Play(LowClick);
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
