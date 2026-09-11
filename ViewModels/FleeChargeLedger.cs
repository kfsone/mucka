namespace Mucka.ViewModels;

/// <summary>
/// Pairs the player's own flights with what MUD2 charged for them, so a dead-strip row can say what
/// leaving cost.
///
/// <para><b>The charge comes BEFORE the flee line</b>, which is why this is not
/// <see cref="KillAwardLedger"/> run backwards: a kill's award follows the kill line, so that ledger
/// queues the ending and waits; here the announcement has already gone past by the time the client's
/// YouFled event exists, so the fall is held and the flee claims it.</para>
/// <code>
///   (Persona saved on -438 = 1,656).
///   Croquet mallet dropped.
///   You have fled by going east.
/// </code>
/// <para>Every transcribed flee the owner supplied (2026-09-09) and every one of the flights in
/// <c>FleeWorthTests.RecordedFlights</c> has the fall stamped at or before the flee event. The
/// reverse order is not handled: a flee that finds nothing held records nothing, and does NOT lie in
/// wait for the next fall - which, in the one frame where a flee can be followed by another fall (a
/// free flight from a shark, then the drowning), would have been the wrong figure.</para>
///
/// <para><b>Only the FIRST fall of a frame is the flight cost</b> (owner, 2026-09-09: "there are
/// *some* rare cases where fleeing has two penalties - such as fleeing from a shark and then drowning
/// (one for flight, one for a non-combat death)"). A held charge is never overwritten by a later one,
/// and a held charge that no flight claims dies with the frame - <c>MudStreamParser.FrameClosed</c>
/// is the only bound; see there for why the one-tick hold window this replaced was unsound.</para>
///
/// <para>Pure and MAUI-free; linked into mudsharp.Tests. Every caller is on the UI thread (see
/// <c>SidePanelViewModel.OnScoreSaved</c>).</para>
/// </summary>
public sealed class FleeChargeLedger
{
    /// <summary>How many resolved flights to keep figures for. Small: a flee charge is only ever read
    /// back by a dead-strip row, and the strip hides everything past what fits behind its "+N earlier"
    /// marker.</summary>
    public const int MaxRemembered = 64;

    private readonly Dictionary<FlightKey, int> _byFlight = [];
    private readonly Queue<FlightKey> _order = new();

    private int? _held;

    private readonly record struct FlightKey(int Encounter, long AtTicks);

    /// <summary>MUD2 announced a score FALL. Held for the rest of this frame in case a flight is about
    /// to resolve; a second fall in the same frame is not the flight's and is ignored.</summary>
    /// <param name="points">The magnitude of the fall - positive.</param>
    public void NoteScoreFall(int points)
    {
        if (points > 0)
            _held ??= points;
    }

    /// <summary>The player fled, or tried to and failed - MUD2 charges for both (see
    /// <c>FightOutcome.UFledFail</c>). Claims the fall held from this frame, if any.</summary>
    /// <param name="atUtc">When the flight resolved; identifies the row for <see cref="ChargeFor"/>.</param>
    /// <returns>True when a held fall was claimed, i.e. when a row's figure just changed.</returns>
    public bool NoteFled(int encounterOrdinal, DateTime atUtc)
    {
        var held = _held;
        _held = null;
        return held is int points && Record(new FlightKey(encounterOrdinal, atUtc.Ticks), points);
    }

    /// <summary>
    /// What the flight that ended at <paramref name="endedUtc"/> cost, or null if nothing was paired
    /// to it.
    ///
    /// <para>Keyed without a creature name: one flee ends every active fight in the encounter and is
    /// charged once, so every row it produced looks the same up here. Spending the figure on exactly
    /// one of them is the caller's job - <c>SidePanelViewModel.FillAwards</c>.</para>
    /// </summary>
    public int? ChargeFor(int encounterOrdinal, DateTime? endedUtc)
        => endedUtc is DateTime ended
            && _byFlight.TryGetValue(new FlightKey(encounterOrdinal, ended.Ticks), out var points)
                ? points
                : null;

    /// <summary>The prompt closed, ending the frame. A held fall no flight claimed was not a flight
    /// cost.</summary>
    public void NoteFrameClosed() => _held = null;

    private bool Record(FlightKey key, int points)
    {
        if (_order.Count >= MaxRemembered)
            _byFlight.Remove(_order.Dequeue());

        // TryAdd rather than an assignment, so a stray second fall can never rewrite a figure already
        // shown. With the flee's own timestamp in the key two flights cannot collide.
        if (!_byFlight.TryAdd(key, points))
            return false;
        _order.Enqueue(key);
        return true;
    }
}
