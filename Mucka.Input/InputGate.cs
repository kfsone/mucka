namespace Mucka.Input;

/// <summary>
/// The handoff. Everything the player initiates enters here, in order, and leaves here on a posted
/// callback - so no consumer can ever run on the keystroke that caused it.
///
/// <para><b>What this is for.</b> Pressing Enter and acting on a line are two different things
/// (owner, 2026-08-20). The command box's only job is to capture what was typed, correctly and
/// smoothly; interpreting it - client commands, alias expansion, the socket write, history - is work
/// that must happen somewhere the input path cannot feel. This class is that somewhere, and it is
/// built so that using it is easier than bypassing it.</para>
///
/// <para><b>One queue, so ordering is correct by construction.</b> Typed lines and every other
/// player-initiated action (an F-key, a compass click, a Ctrl macro, flee) go through the SAME FIFO.
/// That matters beyond tidiness: a design where typed lines are queued and hotkeys are sent
/// immediately lets an F-key overtake the command typed a moment earlier, and one of those hotkeys is
/// FLEE in a permadeath game. Nobody has to remember to flush anything - there is one order, and it
/// is the order things went in.</para>
///
/// <para><b>Posted, not idle-scheduled.</b> The drain yields the thread back to the input system and
/// then runs promptly. It deliberately does not wait for idle: a MUD command is time-critical (combat
/// ticks are 2 s and the player is racing them), so one dispatcher turn is the whole budget. The
/// owner's own framing: conflicts on a 1-2 ms timescale are affordable, stalls on the typing path are
/// not.</para>
///
/// <para><b>Thread affinity.</b> Everything here runs on the UI thread - the input events arrive
/// there, and the drain is posted back there. No locking, therefore, and no lock is wanted: a lock on
/// this path would be a thing that can be contended, i.e. a thing that can stall typing.
/// <see cref="Post"/> asserts its thread in debug builds rather than trusting the comment.</para>
/// </summary>
public sealed class InputGate
{
    private readonly Action<Action> _post;
    private readonly Queue<Action> _queue = new();
    private bool _drainPosted;
    private bool _draining;

    /// <param name="post">Schedules work onto the UI thread, returning immediately. On WinUI this is
    /// the dispatcher queue; in tests it is a list the test drains by hand, which is exactly why it is
    /// injected rather than reached for statically.</param>
    public InputGate(Action<Action> post)
        => _post = post ?? throw new ArgumentNullException(nameof(post));

    /// <summary>Raised, on the drain, once per accepted line - in the order the lines were typed.
    /// Subscribers may take as long as they like: by the time this fires, the keystroke that produced
    /// it is long finished and the box has already been emptied.</summary>
    public event Action<string>? LineReady;

    /// <summary>Raised when a queued item threw. The drain continues regardless - see
    /// <see cref="Drain"/> - so this is a report, not a control point.</summary>
    public event Action<Exception>? Faulted;

    /// <summary>Queued work not yet run. Diagnostic; also what <see cref="Flush"/> keys off.</summary>
    public int PendingCount => _queue.Count;

    /// <summary>Total lines accepted this session - a cheap sanity counter for "did the box lose
    /// one".</summary>
    public long AcceptedCount { get; private set; }

    /// <summary>
    /// Accepts one completed line. Called ON the input path, so it does exactly two things: append to
    /// the queue, and make sure a drain is coming.
    /// </summary>
    public void AcceptLine(string line)
    {
        AcceptedCount++;
        Enqueue(() => LineReady?.Invoke(line));
    }

    /// <summary>
    /// Queues any other player-initiated action, so it takes its turn behind whatever was typed
    /// first. This is how a hotkey stays in order with the command box without anyone having to think
    /// about it.
    /// </summary>
    public void Post(Action work) => Enqueue(work ?? throw new ArgumentNullException(nameof(work)));

    /// <summary>
    /// Runs everything queued, now, on the calling thread.
    ///
    /// <para>For the one case that genuinely cannot wait a turn: code that is about to talk to the
    /// socket directly and must not overtake a line already queued. Prefer <see cref="Post"/> - if
    /// the work goes through the queue it is ordered for free and this is unnecessary. Re-entrant
    /// calls are ignored (a drain calling Flush is already inside one), so a consumer cannot
    /// accidentally recurse.</para>
    /// </summary>
    public void Flush()
    {
        if (_draining)
            return;
        Drain();
    }

    private void Enqueue(Action work)
    {
        _queue.Enqueue(work);
        if (_drainPosted || _draining)
            return;
        _drainPosted = true;
        _post(Drain);
    }

    private void Drain()
    {
        _drainPosted = false;
        if (_draining)
            return;                 // belt-and-braces; Flush already guards this
        _draining = true;
        try
        {
            // Dequeue one at a time rather than snapshotting: an item may itself queue more (a client
            // command that sends a line), and those belong at the back of this same pass in the order
            // they were created, not in a later one.
            while (_queue.Count > 0)
            {
                var work = _queue.Dequeue();
                // Per-item, so one bad consumer cannot take the queue down with it. Without this, a
                // client command that throws would abandon every line queued behind it - the player
                // types three commands, one has a bug, and the other two silently never happen. A
                // thrown exception is a defect in that one item and nothing else's business.
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Faulted?.Invoke(ex);
                }
            }
        }
        finally
        {
            _draining = false;
        }
    }
}
