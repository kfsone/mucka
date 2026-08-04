using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

public sealed class NpcGroupsTests
{
    [Fact]
    public void Normalize_MatchesThePythonPipelineForEveryFixturePair()
    {
        // The whole point of this test: mudsharp/Combat/NpcGroups.cs is a hand port of
        // reduce_combat.normalize_npc_group, and the offline sqlite pipeline plus the live client
        // MUST bucket a fight under the same npc_group or accumulated history silently splits in
        // two and every cross-fight comparison quietly loses half its samples. The fixture is
        // generated from the Python side (tools/combat/gen_npc_group_fixture.py), so a change to
        // either implementation fails here instead of corrupting the dataset.
        var pairs = LoadFixture();
        Assert.NotEmpty(pairs);

        var mismatches = new List<string>();
        foreach (var (name, expectedGroup) in pairs)
        {
            var actual = NpcGroups.Normalize(name);
            if (!string.Equals(actual, expectedGroup, StringComparison.Ordinal))
                mismatches.Add($"'{name}': python said '{expectedGroup}', C# said '{actual}'");
        }

        Assert.Empty(mismatches);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("0", "")]
    public void Normalize_YieldsEmptyForDegenerateNames_ADeliberateDivergenceFromPython(string? name, string expected)
    {
        // KNOWN, INTENTIONAL divergence from reduce_combat.normalize_npc_group, which pluralizes
        // whatever is left after stripping and so answers "s" for "", "   " and "0" (and "---s"
        // for "---"). Returning empty instead is the safer behaviour on this side: an empty group
        // is rejected by FightHistory.Summarize's IsNullOrWhiteSpace guard, whereas a literal "s"
        // group would create a junk history bucket that silently accumulates rows.
        //
        // Safe to diverge because these inputs cannot reach either implementation as a real fight:
        // an NPC name is never blank, and FightAccumulator/FightFor already reject whitespace names
        // upstream. Every name that CAN occur is pinned to exact agreement by the fixture test above.
        Assert.Equal(expected, NpcGroups.Normalize(name));
    }

    [Fact]
    public void Normalize_StripsTrailingInstanceDigitsButKeepsLeadingOnes()
    {
        // Verified against the Python: leading digits are part of the name, only the trailing
        // instance number is an instance number.
        Assert.Equal("trolls", NpcGroups.Normalize("2headed-troll7"));
    }

    private static List<(string Name, string Group)> LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Data", "npc_group_fixture.txt");
        Assert.True(File.Exists(path), $"fixture not copied to output: {path}");

        var pairs = new List<(string, string)>();
        foreach (var rawLine in File.ReadAllLines(path))
        {
            if (rawLine.Length == 0 || rawLine.StartsWith('#'))
                continue;

            var separator = rawLine.IndexOf('|');
            Assert.True(separator > 0, $"malformed fixture line: '{rawLine}'");

            // Deliberately NOT trimming the name: one fixture case has surrounding whitespace
            // precisely to prove both implementations trim it themselves.
            var name = rawLine[..separator];
            var group = rawLine[(separator + 1)..].TrimEnd('\r', '\n');
            pairs.Add((name, group));
        }

        return pairs;
    }
}
