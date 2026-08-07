using Mucka.ViewModels;
using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Covers <see cref="CombatHistoryFormatter.BuildReview"/> - the review-only tail the Combat Rail's
/// canvas draws below the new live hero section (threat indicator/opposition roster, composed
/// directly for the canvas - see <see cref="CombatLiveView"/>). This is a genuinely separate method
/// from <see cref="CombatHistoryFormatter.Build"/> specifically so the existing full-formatter test
/// suite (<c>CombatHistoryFormatterTests</c>) keeps exercising <see cref="CombatHistoryFormatter.Build"/>
/// unchanged; these tests only need to confirm the review method omits what the live section now
/// owns, and keeps everything genuinely still useful post-fight.
/// </summary>
public sealed class CombatHistoryFormatterReviewTests
{
    private static FightSnapshot Snap(string npcName = "rat0", string? weapon = "axe0")
        => new(npcName, NpcGroups.Normalize(npcName), weapon, null, null,
            3, 1, 1, 3, 30, 6, TimeSpan.FromSeconds(52), FightOutcome.Unresolved, false, [], []);

    private static CombatEncounterSnapshot Encounter(bool inCombat, params FightSnapshot[] fights)
        => new(HasEncounter: true, InCombat: inCombat, StartedUtc: DateTime.UtcNow, CurrentWeapon: "axe0",
            ActiveNpcs: fights.Where(f => !f.IsResolved).Select(f => f.NpcName).ToList(),
            YouHits: 3, YouMisses: 1, TheyHits: 1, TheyMisses: 3, YouHitRate: 0.75, TheyHitRate: 0.25,
            ApproxDamageDone: 30, ApproxDamageTaken: 6, Duration: TimeSpan.FromSeconds(52),
            ApproxDps: 0.5, TheirApproxDps: 0.1, Fights: fights);

    private static string PlainText(IReadOnlyList<ClogLine> lines)
        => string.Join("\n", lines.Select(line => string.Concat(line.Spans.Select(s => s.Text))));

    [Fact]
    public void BuildReview_OmitsTheHeadlineSurvivabilityParticipantsAndDeficitRows()
    {
        var deficits = new CombatStatDeficits(-11, -9, StaminaCurrent: 50, StaminaMax: 100, 200, 1);
        var lines = CombatHistoryFormatter.BuildReview(Encounter(true, Snap()), CombatHistoryContext.Empty);
        var text = PlainText(lines);

        // The old headline/survivability/participant text is gone from this method's output - it now
        // lives in SidePanelViewModel.Live, composed directly for the canvas.
        Assert.DoesNotContain("axe0 vs rat0", text);
        Assert.DoesNotContain("winning", text);
        Assert.DoesNotContain("LOSING", text);
        Assert.DoesNotContain("str -11", text);   // deficits line also omitted - it moved to Live too
    }

    [Fact]
    public void BuildReview_StillIncludesTheExchangeTable()
    {
        var lines = CombatHistoryFormatter.BuildReview(Encounter(true, Snap()), CombatHistoryContext.Empty);
        var text = PlainText(lines);

        Assert.Contains("per hit", text);
        Assert.Contains("you", text);
        Assert.Contains("them", text);
    }

    [Fact]
    public void BuildReview_StillIncludesTheResultBannerOncePostCombat()
    {
        var finished = Encounter(false, Snap("zombie0", "axe0") with { Outcome = FightOutcome.Killed, IsResolved = true });
        var lines = CombatHistoryFormatter.BuildReview(finished, CombatHistoryContext.Empty);
        var text = PlainText(lines);

        Assert.Contains("killed", text);
        Assert.Contains("zombie0", text);
    }

    [Fact]
    public void BuildReview_NoEncounterAtAll_FallsBackToSessionTotalsOnly()
    {
        var idle = new CombatEncounterSnapshot(false, false, null, null, [], 0, 0, 0, 0, 0, 0, 0, 0,
            TimeSpan.Zero, 0, 0, []);
        var session = new SessionCombatTotals(1, 2, 1, 0, 0, 30, 5, TimeSpan.FromSeconds(60));

        var lines = CombatHistoryFormatter.BuildReview(idle, CombatHistoryContext.Empty, session);
        var text = PlainText(lines);

        Assert.Contains("session", text);
        Assert.Contains("2 fights", text);
    }
}
