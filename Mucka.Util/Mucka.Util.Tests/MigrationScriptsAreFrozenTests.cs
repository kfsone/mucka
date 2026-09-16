using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mucka.Store;

namespace Mucka.Util.Tests;

/// <summary>
/// The migration ladder itself, under test: that a released script is never edited, that the names
/// sort into the order they must run in, and that the legacy adopter stays sealed.
///
/// <para><b>Why a released script is frozen.</b> It has already run on machines nobody here can
/// reach. Editing it changes what a fresh install gets without changing what those machines have, and
/// the journal will never re-run it to close the gap - so the two databases diverge permanently, with
/// nothing anywhere recording that they did. This is the worst thing that can be done to a fleet you
/// cannot inspect, and it is one careless edit away at all times. Change the schema by adding the
/// next number.</para>
/// </summary>
public sealed class MigrationScriptsAreFrozenTests
{
    /// <summary>
    /// Every released script and the SHA-256 of its contents.
    ///
    /// <para>Hashed with line endings normalised to LF: the repo checks out with
    /// <c>core.autocrlf=true</c>, so the bytes on disk differ between machines while the script does
    /// not. Normalising means this pins the SCRIPT and not the checkout.</para>
    ///
    /// <para>Adding a migration means adding a line here. That is the point - it is a deliberate
    /// entry in the diff, next to the new file, rather than something that happens silently.</para>
    /// </summary>
    private static readonly (string Name, string Sha256)[] Frozen =
    [
        ("0001_baseline.sql",         "8D2BF1E57C100DD9CE6924CEC773B8F526E623D1CC83E7898B535B0C38644AA8"),
        ("0002_drop_level.sql",       "904775539E681EC4C46B9A7D8629B7716C50F25FC267527A0417FBA6DEE777D4"),
        ("0003_persona_sessions.sql", "D92CBE784893CF88F0D6F26E5421242E3BCA4BDEB5BA8079EA431EE7EAF29F79"),
    ];

    private static string HashOf(string contents)
        => Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(contents.Replace("\r\n", "\n"))));

    [Fact]
    public void NoReleasedScriptHasChanged()
    {
        var actual = MigrationScripts.All.ToDictionary(s => s.Name, s => HashOf(s.Contents));
        var drifted = new List<string>();

        foreach (var (name, expected) in Frozen)
        {
            if (!actual.TryGetValue(name, out var hash))
            {
                drifted.Add($"{name}: frozen here but no longer registered in MigrationScripts.All");
                continue;
            }
            if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
                drifted.Add($"{name}: is {hash}, frozen as {expected}");
        }

        Assert.True(drifted.Count == 0,
            "A migration that has already run on databases we cannot reach was edited. Editing it "
            + "changes what a FRESH install gets and leaves every existing one behind, permanently. "
            + "Add the next numbered script instead. (If this is a brand-new script, add its hash to "
            + "Frozen.)\n  " + string.Join("\n  ", drifted));
    }

    /// <summary>The counterpart: a script that exists but nobody froze. Without this, adding a
    /// migration and forgetting the hash would leave it unprotected from the next edit.</summary>
    [Fact]
    public void EveryRegisteredScriptIsFrozen()
    {
        var frozenNames = Frozen.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unfrozen = MigrationScripts.AllNames.Where(n => !frozenNames.Contains(n)).ToList();

        Assert.True(unfrozen.Count == 0,
            "Registered but not frozen - add the name and its SHA-256 to Frozen:\n  "
            + string.Join("\n  ", unfrozen.Select(n =>
                $"{n} = {HashOf(MigrationScripts.All.First(s => s.Name == n).Contents)}")));
    }

    /// <summary>
    /// Names sort into execution order, contiguously, with no gaps or repeats.
    ///
    /// <para>DbUp orders by script NAME, not by the order the array hands them over, so
    /// <c>10_x</c> would run before <c>2_x</c>. Four digits and a strict pattern make that
    /// unexpressible. This is the descendant of the index-before-column bug that would have stopped
    /// the client opening the operator's database: an ordering constraint that used to live in a doc
    /// comment, now a build failure.</para>
    /// </summary>
    [Fact]
    public void NamesAreZeroPaddedContiguousAndInOrder()
    {
        var names = MigrationScripts.AllNames;
        var pattern = new Regex(@"^(\d{4})_[a-z0-9_]+\.sql$");

        for (var i = 0; i < names.Count; i++)
        {
            var match = pattern.Match(names[i]);
            Assert.True(match.Success,
                $"'{names[i]}' must be NNNN_lower_snake.sql - four digits, because DbUp sorts by name "
                + "and '10_x' sorts before '2_x'.");
            Assert.Equal(i + 1, int.Parse(match.Groups[1].Value));
        }

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal).ToArray(), names.ToArray());
    }

    /// <summary>Every registered script is a file in the migrations folder, and every file there is
    /// registered. Catches "added the file, forgot the array" and its inverse, either of which means
    /// a database somewhere does not get a change that shipped.</summary>
    [Fact]
    public void TheRegisteredScriptsAreExactlyTheFilesOnDisk()
    {
        var root = FindRepoRoot();
        var folder = Path.Combine(root, "Mucka.Util", "Mucka.Store", "Migrations");
        var onDisk = Directory.EnumerateFiles(folder, "*.sql")
                              .Select(Path.GetFileName)
                              .OrderBy(n => n, StringComparer.Ordinal)
                              .ToArray();

        Assert.Equal(onDisk, MigrationScripts.AllNames.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The legacy adopter is sealed at the shape it had when the journal was adopted.
    ///
    /// <para>It exists so every database in the world enters the journaled era at ONE known shape.
    /// That property survives only while it does not change - an entry appended here would apply to
    /// files that already passed through it and never to the ones that come after, which is a schema
    /// change that reaches some databases and not others. Tomorrow's change is a numbered script.</para>
    /// </summary>
    [Fact]
    public void TheLegacyAdopterIsSealed()
        => Assert.Equal(LegacySchemaAdopter.AddedColumnCountAtAdoption, LegacySchemaAdopter.AddedColumnCount);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mucka.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
