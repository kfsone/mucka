namespace MudSharp.Combat;

/// <summary>
/// Minimal per-participant facts the roster/opposition-count decision needs. Deliberately NOT the
/// app's own <c>FightSnapshot</c> record - that type lives in the Mucka/MAUI assembly, which this
/// project does not and must not reference (mudsharp is the plain class library mudsharp.Tests links
/// against via a ProjectReference with zero MAUI dependency). This keeps the decision pure,
/// primitive-typed, and directly testable, matching <see cref="CombatTierResolver"/>/
/// <see cref="FleeCostLadder"/>/<see cref="CombatWhyLine"/>'s own pattern in this same folder.
/// </summary>
public readonly record struct ParticipantFact(string Name, bool IsResolved, FightOutcome Outcome);

/// <summary>
/// One row of the opposition list as actually drawn. <see cref="IsCurrentTarget"/> marks the ONE live
/// row (at most) that the player is actually trading blows with right now - the same fight
/// <see cref="CombatOutlook"/>'s projection describes - so the render surface can make it draw the eye
/// distinctly from a live NPC merely still standing elsewhere in the pack.
/// </summary>
public readonly record struct RosterRow(string Name, bool IsLive, bool IsCurrentTarget, FightOutcome Outcome);

/// <summary>
/// The whole opposition readout for one encounter: a capped, ordered row list PLUS the counts a
/// capped list alone cannot convey.
///
/// <para>This exists because of a direct, named failure: a 14-rat fight rendered "5 dead rats and 9
/// more" - the 9 hidden participants' status was simply unknown from that line, when "how many are
/// still up" is exactly the number that matters in a pack fight. <see cref="LiveCount"/>/
/// <see cref="ResolvedCount"/> answer that regardless of how many rows the fixed row cap can actually
/// show, and <see cref="HiddenLiveCount"/> answers it even for the hidden tail - a pack large enough
/// that live participants alone exceed the cap must not report "N more" in a way indistinguishable
/// from "N more, already dead".</para>
/// </summary>
public readonly record struct RosterPlan(
    IReadOnlyList<RosterRow> Rows,
    int LiveCount,
    int ResolvedCount,
    int HiddenCount,
    int HiddenLiveCount)
{
    public static readonly RosterPlan Empty = new([], 0, 0, 0, 0);

    public int TotalCount => LiveCount + ResolvedCount;

    /// <summary>Hidden participants that have already resolved (killed/fled/withdrawn) - the common
    /// case once the row cap is exceeded, since live targets sort first.</summary>
    public int HiddenResolvedCount => HiddenCount - HiddenLiveCount;

    public bool HasHidden => HiddenCount > 0;
}

/// <summary>
/// Builds the roster plan: DESIGN_FINAL.md's "make the count and the live/dead split immediately
/// readable" requirement, replacing the previous implementation's truncated name list with no
/// breakdown at all.
/// </summary>
public static class ParticipantRoster
{
    /// <summary>Row cap - mirrors <c>CombatHistoryFormatter.MaxParticipantRows</c> exactly (same
    /// reasoning: nobody reads eleven names mid-swing, and each row is its own draw call the render
    /// surface bounds by a fixed count regardless of pack size - DESIGN_FINAL.md section 7's
    /// performance contract).</summary>
    public const int MaxRows = 5;

    /// <summary>
    /// Live participants first (in their original first-engaged order), then resolved ones, capped at
    /// <see cref="MaxRows"/> - the same ordering <c>CombatHistoryFormatter.OrderedTargets</c> already
    /// uses, so a truncated pack fight always keeps whoever is still swinging and drops finished
    /// fights first. The very first row is marked <see cref="RosterRow.IsCurrentTarget"/> exactly when
    /// it is live - mirroring <c>CombatHistoryFormatter.PrimaryFight</c>'s own "first still-unresolved
    /// fight in original order" rule, so the roster's bolded row and the outlook/threat projection can
    /// never describe two different fights.
    /// </summary>
    public static RosterPlan Build(IReadOnlyList<ParticipantFact> fights)
    {
        if (fights.Count == 0)
            return RosterPlan.Empty;

        var live = new List<ParticipantFact>();
        var resolved = new List<ParticipantFact>();
        foreach (var fact in fights)
            (fact.IsResolved ? resolved : live).Add(fact);

        var ordered = new List<ParticipantFact>(fights.Count);
        ordered.AddRange(live);
        ordered.AddRange(resolved);

        var shownCount = Math.Min(ordered.Count, MaxRows);
        var rows = new List<RosterRow>(shownCount);
        for (var i = 0; i < shownCount; i++)
        {
            var fact = ordered[i];
            rows.Add(new RosterRow(fact.Name, !fact.IsResolved, IsCurrentTarget: i == 0 && !fact.IsResolved, fact.Outcome));
        }

        var hiddenCount = ordered.Count - shownCount;
        var hiddenLiveCount = 0;
        for (var i = shownCount; i < ordered.Count; i++)
        {
            if (!ordered[i].IsResolved)
                hiddenLiveCount++;
        }

        return new RosterPlan(rows, live.Count, resolved.Count, hiddenCount, hiddenLiveCount);
    }
}
