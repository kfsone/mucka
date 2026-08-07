using Mucka.Core;
using Mucka.ViewModels;
using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// The single most important regression this whole redesign phase exists to keep passing:
/// DESIGN_FINAL.md's own review flagged self-comparison exclusion as "the single easiest thing to
/// break" when replacing the old <c>FightHistory.ExcludingEncounterFrom</c> runtime filter with the
/// incremental HistoryIndex. These tests reproduce the EXACT sequence that made the bug possible in
/// the first place: FightHistoryRecorder flushes a closed encounter's rows to the store BEFORE the
/// view model gets a chance to render, so without protection "now" and "usual" become identical the
/// moment a fight resolves.
/// </summary>
public sealed class CombatHistoryCacheTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-historycache-tests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, FightHistoryStore.DefaultFileName);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static FightRecord Fight(string npcName, double damageDone)
        => new()
        {
            NpcName = npcName,
            NpcGroup = NpcGroups.Normalize(npcName),
            WeaponUsed = "dagger0",
            Outcome = nameof(FightOutcome.Killed),
            YouHits = 3,
            ApproxDamageDone = damageDone,
            DurationMs = 30_000,
        };

    [Fact]
    public void Resolve_CalledDuringTheFight_NeverSeesThatSameFightsOwnRowEvenAfterItIsAppended()
    {
        // Prior history exists (rat0 fought twice before, median damage 30).
        var store = new FightHistoryStore(FilePath);
        store.Append(Fight("rat0", 20));
        store.Append(Fight("rat0", 40));

        var cache = new CombatHistoryCache();
        var encounterStart = DateTime.UtcNow;

        // The live fight starts: SidePanelViewModel.RefreshCombatDisplay would call Resolve here,
        // BEFORE the recorder has any row for the fight currently in progress.
        var duringFight = cache.Resolve(store, "rat0", "rats", "dagger0", encounterStart);
        Assert.Equal(2, duringFight.Instance.FightCount);
        Assert.Equal(30.0, duringFight.Instance.MedianDamageDone!.Value, 3);

        // The fight resolves. FightHistoryRecorder.FlushLocked appends the JUST-FINISHED fight's own
        // row to the SAME store, for the SAME encounter, BEFORE the view model's post-combat summary
        // render runs - reproducing the exact ordering that made the original bug possible.
        store.Append(Fight("rat0", 999));   // a wildly different value - would visibly shift the
                                             // median if it leaked into the comparison

        // The post-combat summary render calls Resolve again for the SAME encounter (StartedUtc
        // unchanged) - this is the moment that must NOT pick up the fight's own just-flushed row.
        var afterFlush = cache.Resolve(store, "rat0", "rats", "dagger0", encounterStart);

        Assert.Equal(2, afterFlush.Instance.FightCount);
        Assert.Equal(30.0, afterFlush.Instance.MedianDamageDone!.Value, 3);
    }

    [Fact]
    public void Resolve_ANewEncounter_DoesSeeThePreviousEncountersNowClosedFight()
    {
        // The flip side of the guarantee above: once the encounter genuinely changes, the
        // PREVIOUS encounter's now-resolved fight is legitimate history and must show up.
        var store = new FightHistoryStore(FilePath);
        var cache = new CombatHistoryCache();

        var firstEncounter = DateTime.UtcNow;
        var beforeAnyHistory = cache.Resolve(store, "rat0", "rats", "dagger0", firstEncounter);
        Assert.Equal(0, beforeAnyHistory.Instance.FightCount);

        store.Append(Fight("rat0", 30));   // encounter 1 closes and flushes

        var secondEncounter = firstEncounter.AddMinutes(5);
        var duringSecondFight = cache.Resolve(store, "rat0", "rats", "dagger0", secondEncounter);

        Assert.Equal(1, duringSecondFight.Instance.FightCount);
        Assert.Equal(30.0, duringSecondFight.Instance.MedianDamageDone!.Value, 3);
    }

    [Fact]
    public void Resolve_AWeaponSwitchMidEncounter_RefreshesTheCurrentWeaponGlobalFigure()
    {
        var store = new FightHistoryStore(FilePath);
        store.Append(new FightRecord
        {
            NpcName = "rat9", NpcGroup = "rats", WeaponUsed = "axe0",
            Outcome = nameof(FightOutcome.Killed), YouHits = 4, ApproxDamageDone = 40, DurationMs = 1000,
        });

        var cache = new CombatHistoryCache();
        var encounterStart = DateTime.UtcNow;

        var withDagger = cache.Resolve(store, "rat0", "rats", "dagger0", encounterStart);
        Assert.Equal(0, withDagger.CurrentWeaponGlobal.FightCount);

        // Same instance, same encounter, but the player switched weapons mid-fight - the cache must
        // not keep serving dagger0's (empty) global figures under the new weapon.
        var withAxe = cache.Resolve(store, "rat0", "rats", "axe0", encounterStart);
        Assert.Equal(1, withAxe.CurrentWeaponGlobal.FightCount);
    }
}
