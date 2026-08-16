using Mucka.ViewModels;
using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// The Combat Rail's alternate-weapon offer (Ctrl+W).
///
/// <para>Every rule here is a safety rule, not a preference. MUD2 charges for a wrong wield: switching
/// weapons drops your guard and hands the opponent a free swing, and naming something you are not
/// carrying wastes the attempt outright. So the offer must never name an item that is not in the pack,
/// never name the weapon already in hand, and never name an object the client only GUESSES is a weapon
/// - which is why candidacy is decided by the fight record rather than by any noun list.</para>
/// </summary>
public sealed class AltWeaponTests
{
    private static readonly HashSet<string> KnownWeapons =
        new(["axe0", "falchion3", "dagger0", "pick2"], StringComparer.OrdinalIgnoreCase);

    private static bool IsKnown(string name) => KnownWeapons.Contains(name);

    private static WeaponHistorySummary Weapon(string name, double? medianPerHit) =>
        new(name, new FightHistorySummary { SampleSize = 4, FightCount = 4, MedianDamagePerHit = medianPerHit });

    [Fact]
    public void ChooseAltWeapon_PrefersTheBestPerHitAgainstThisGroup()
    {
        var alt = CombatComposition.ChooseAltWeapon(
            ["lamp", "axe0", "falchion3"],
            currentWeapon: null,
            byWeapon: [Weapon("axe0", 5.0), Weapon("falchion3", 9.5)],
            IsKnown);

        Assert.Equal("falchion3", alt);
    }

    [Fact]
    public void ChooseAltWeapon_NeverOffersTheWeaponAlreadyInHand()
    {
        // falchion3 is the better weapon here AND is being held; the offer has to fall to the axe,
        // because "switch to what you are already using" spends a guard drop for nothing.
        var alt = CombatComposition.ChooseAltWeapon(
            ["axe0", "falchion3"],
            currentWeapon: "falchion3",
            byWeapon: [Weapon("axe0", 5.0), Weapon("falchion3", 9.5)],
            IsKnown);

        Assert.Equal("axe0", alt);
    }

    [Fact]
    public void ChooseAltWeapon_IgnoresCarriedItemsThatAreNotKnownWeapons()
    {
        var alt = CombatComposition.ChooseAltWeapon(
            ["lamp", "postcard", "bouncy-ball"],
            currentWeapon: null,
            byWeapon: [],
            IsKnown);

        Assert.Null(alt);
    }

    [Fact]
    public void ChooseAltWeapon_OffersAnUnevidencedWeaponWhenNothingHasARecordHere()
    {
        // No record against THIS creature yet - a known weapon is still a real offer, so the key
        // stays usable on a first encounter rather than going dead exactly when it is needed.
        var alt = CombatComposition.ChooseAltWeapon(
            ["rope", "pick2"], currentWeapon: null, byWeapon: [], IsKnown);

        Assert.Equal("pick2", alt);
    }

    [Fact]
    public void ChooseAltWeapon_RanksEveryEvidencedWeaponAboveAnUnevidencedOne()
    {
        // dagger0 comes first in the pack but has no record against this group; the axe does, so the
        // axe wins however the inventory happens to be ordered.
        var alt = CombatComposition.ChooseAltWeapon(
            ["dagger0", "axe0"], currentWeapon: null, byWeapon: [Weapon("axe0", 3.0)], IsKnown);

        Assert.Equal("axe0", alt);
    }

    [Fact]
    public void ChooseAltWeapon_TiesResolveToInventoryOrder()
    {
        var alt = CombatComposition.ChooseAltWeapon(
            ["axe0", "falchion3"],
            currentWeapon: null,
            byWeapon: [Weapon("axe0", 4.0), Weapon("falchion3", 4.0)],
            IsKnown);

        Assert.Equal("axe0", alt);
    }

    [Fact]
    public void ChooseAltWeapon_TreatsNamesCaseInsensitivelyThroughout()
    {
        var alt = CombatComposition.ChooseAltWeapon(
            ["AXE0", "Falchion3"],
            currentWeapon: "falchion3",
            byWeapon: [Weapon("axe0", 5.0)],
            IsKnown);

        Assert.Equal("AXE0", alt);
    }

    [Fact]
    public void ChooseAltWeapon_IgnoresBlankInventoryEntries()
    {
        var alt = CombatComposition.ChooseAltWeapon(
            ["", "   ", "pick2"], currentWeapon: null, byWeapon: [], IsKnown);

        Assert.Equal("pick2", alt);
    }

    // ---- CommandNoun: what actually gets typed at the game ---------------------------------

    [Theory]
    [InlineData("axe0", "axe0")]
    [InlineData("a rusty pick2", "pick2")]
    [InlineData("the ornate falchion3", "falchion3")]
    [InlineData("  dagger0  ", "dagger0")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void CommandNoun_SendsOnlyTheIdentifyingToken(string name, string expected)
        => Assert.Equal(expected, CombatComposition.CommandNoun(name));

    /// <summary>The display rule may shorten only for the column; it must never be what decides the
    /// command. A short two-word name is left alone for display but still reduces to its noun when
    /// typed - if these two ever agreed by accident, one of them would be wrong.</summary>
    [Fact]
    public void CommandNoun_IsNotTheDisplayRule()
    {
        Assert.Equal("a sword", CombatComposition.DisplayName("a sword"));
        Assert.Equal("sword", CombatComposition.CommandNoun("a sword"));
    }
}
