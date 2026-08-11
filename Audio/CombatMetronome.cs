namespace Mucka.Audio;

/// <summary>
/// Clicks once per MUD2 combat tick, alternating a high and a low percussion stick, so the fight's
/// rhythm can be heard instead of watched.
///
/// <para>This is the point of it: the tick bar tells you when the next swing lands, but reading it
/// costs a glance away from the terminal text, which is the one thing the rail exists to avoid.
/// Hearing the beat costs nothing at all - and MUD2's tick is exactly 2.000 s and phase-locked, so a
/// metronome started with the fight stays true to it for the fight's duration.</para>
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
    /// <summary>One MUD2 combat tick. Deliberately the same measured constant the visual sweep uses -
    /// see <c>Mucka.Rendering.TickSweep</c>. If these two ever disagree, the click and the bar will
    /// visibly separate within a dozen ticks.</summary>
    private const int TickMilliseconds = 2000;

    private const string HighClick = "sounds/Perc_Stick_hi.wav";
    private const string LowClick = "sounds/Perc_Stick_lo.wav";

    private readonly object _gate = new();
    private Timer? _timer;
    private bool _high;
    private bool _disposed;

    /// <summary>Whether the player has asked for the click at all. Independent of whether a fight is
    /// happening: the metronome only runs when BOTH this is set and combat is live.</summary>
    public bool Enabled { get; private set; }

    public void SetEnabled(bool enabled)
    {
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
    /// Starts clicking, from now, if the player has it switched on. Idempotent - calling it while
    /// already running does nothing, so it can be driven from the same combat-state handler as the
    /// visual sweep without restarting the beat several times a second.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || !Enabled || _timer is not null)
                return;

            // The first click fires immediately rather than after a delay: this is started at the
            // fight's first refresh, so "now" IS the top of a tick, and waiting 2 s would put the
            // whole sequence a beat behind the fight for as long as it lasts.
            _high = true;
            _timer = new Timer(_ => Click(), null, 0, TickMilliseconds);
        }
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

    private void Click()
    {
        // Read the asset under the lock but PLAY outside it - Play is fire-and-forget, but holding a
        // lock across any call into audio is how a click ends up able to block a Stop().
        string asset;
        lock (_gate)
        {
            if (_disposed || _timer is null)
                return;
            asset = _high ? HighClick : LowClick;
            _high = !_high;
        }

        // Master mute wins over the toggle, matching how every other client-initiated sound in the
        // app behaves (see MappingSession's own guard). Not gated on the per-sound catalogue though:
        // this is a client instrument the player armed deliberately, not a server-triggered effect,
        // so the switch beside the tick bar is its own enablement.
        if (SoundService.MasterEnabled)
            SoundService.Play(asset);
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
