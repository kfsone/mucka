namespace Mucka.ViewModels;

/// <summary>
/// Pairs each fight ending with the score MUD2 announced for it, so a dead-strip row can say what the
/// creature was worth.
///
/// <para>MUD2 prints an ending's award on the line AFTER the ending line, and the ending line is what
/// closes the fight - so no fight record can contain its own award (<c>FightHistoryRecorder.
/// OnScoreSave</c>). An ending goes on the queue when its line lands and comes off against the next
/// announcement that raises the score.</para>
///
/// <para><b>The frame is the scope.</b> The award arrives inside the frame carrying its ending or not
/// at all (<c>MudStreamParser.FrameClosed</c> has the argument, and why no time window may stand in
/// for it), so <see cref="NoteFrameClosed"/> drops whatever is still unpaired rather than carrying it
/// forward - an unclaimed ending must never take the next frame's award instead of its own.</para>
///
/// <para>Endings go unpaired because a creature's flight is often not scored at all, and MUD2 prints
/// nothing rather than <c>+0</c>. The presumed rule is Bartle's flee arithmetic applied to the creature
/// (see <c>MudSharp.Combat.FleeWorth</c>: nothing is paid below 6% of maximum stamina), but the
/// creature-side inputs are not observable, so nothing here computes an award; it only pairs announced
/// ones.</para>
///
/// <para>Pure and MAUI-free; linked into mudsharp.Tests like <see cref="FleeChargeLedger"/>. Every
/// caller is on the UI thread - see <c>SidePanelViewModel.OnScoreSaved</c> for why that hop exists and
/// why it preserves the arrival order of ending, award and frame close.</para>
/// </summary>
public sealed class KillAwardLedger
{
    /// <summary>How many resolved awards to keep figures for. Bounded because nothing else prunes it:
    /// a long session kills thousands of creatures, and the dead strip hides everything past what fits
    /// behind its "+N earlier" marker, so evicting the oldest drops figures for rows that cannot be
    /// seen.</summary>
    public const int MaxRemembered = 512;

    private readonly Queue<EndingKey> _awaiting = new();
    private readonly Dictionary<EndingKey, int> _byEnding = [];
    private readonly Queue<EndingKey> _order = new();
    private int _pendingTaskPayouts;

    /// <summary>
    /// One fight ending, identified well enough to survive a creature being re-summoned and killed
    /// again inside the same encounter: a re-summoned creature can reuse the same instance name, so
    /// name alone is not a stable key.
    ///
    /// <para>The timestamp is exact rather than approximate: a fight resolves with the ending EVENT's
    /// own stamp (<c>CombatStatsAggregator.ResolveFight</c>), the same value this queue was given when
    /// that event arrived. Two endings of one name cannot share it, because the player cannot be
    /// engaged with the same creature twice inside one combat slice.</para>
    ///
    /// <para>Kills and creature flights only. The player's OWN flights are keyed without a creature
    /// name, in <see cref="FleeChargeLedger"/>.</para>
    /// </summary>
    public readonly record struct EndingKey(int Encounter, string Name, long EndedTicks);

    /// <summary>A creature died or broke off and ran. Queued to await the announcement that follows it
    /// inside this frame, or to be dropped by <see cref="NoteFrameClosed"/> if none does.</summary>
    public void NoteEnding(int encounterOrdinal, string name, DateTime endedUtc)
        => _awaiting.Enqueue(new EndingKey(encounterOrdinal, name, endedUtc.Ticks));

    /// <summary>
    /// MUD2 discharged one of the eight tasks. Arms a swallow for the payout that follows.
    ///
    /// <para>Two of the eight tasks are completed by killing something, and when one is, the frame
    /// carries TWO rises with the task's first: <c>You have killed the water-snake4.</c>, <c>You have
    /// completed a Task.</c>, <c>(Persona saved on +100 = 7,058).</c>, <c>(Persona saved on +84 =
    /// 7,142).</c> Without this the kill's row would show the task's flat +100 instead of the +84 the
    /// creature was worth. Counted rather than flagged, because nothing observed says two tasks cannot
    /// discharge in one frame.</para>
    /// </summary>
    public void NoteTaskCompleted() => _pendingTaskPayouts++;

    /// <summary>
    /// MUD2 announced a score RISE. Either it is a task's own payout - swallowed - or it settles the
    /// oldest ending still waiting in this frame.
    /// </summary>
    /// <param name="delta">The magnitude of the rise - positive.</param>
    /// <returns>True when it attached to an ending, i.e. when a row's figure just changed.</returns>
    public bool NoteScoreRise(int delta)
    {
        if (delta <= 0)
            return false;

        // Swallowed BEFORE the queue is consulted, so the ending at the head keeps its place and
        // collects the NEXT rise, which is its own.
        if (_pendingTaskPayouts > 0)
        {
            _pendingTaskPayouts--;
            return false;
        }

        if (_awaiting.Count == 0)
            return false;

        return Record(_awaiting.Dequeue(), delta);
    }

    /// <summary>The prompt closed, ending the frame. Anything still waiting - an ending without an
    /// award, a task line without a payout - was not scored and never will be; see the class.</summary>
    public void NoteFrameClosed()
    {
        _awaiting.Clear();
        _pendingTaskPayouts = 0;
    }

    /// <summary>What the ending that resolved at <paramref name="endedUtc"/> was awarded, or null if
    /// nothing was paired to it. Null wherever no announcement arrived, never a zero.</summary>
    public int? AwardFor(int encounterOrdinal, string name, DateTime? endedUtc)
        => endedUtc is DateTime ended
            && _byEnding.TryGetValue(new EndingKey(encounterOrdinal, name, ended.Ticks), out var award)
                ? award
                : null;

    private bool Record(EndingKey key, int delta)
    {
        if (_order.Count >= MaxRemembered)
            _byEnding.Remove(_order.Dequeue());

        // TryAdd rather than an assignment, so a repeat can never silently rewrite an earlier row's
        // figure. With the ending's own timestamp in the key it should be unreachable - see EndingKey.
        if (!_byEnding.TryAdd(key, delta))
            return false;
        _order.Enqueue(key);
        return true;
    }
}
