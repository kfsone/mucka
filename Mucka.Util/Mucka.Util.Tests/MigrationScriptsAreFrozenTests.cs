using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
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
        ("0003_persona_sessions.sql", "5F4B291843751A6C530452FB82C39D0C363B74A782A57C6212250566430DA753"),
        ("0004_prune_unattributable.sql", "9835288C29DEDBEC73125AC18EB0A91D50BC65D735D06F2A47C7BFBD35194BD1"),
        ("0005_some_kinds.sql",        "08353A1C62D9EFD8A06D9974A1E704B684FAF1DAA510F2DCE44FF56F8DDEE404"),
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
            + "changes what a FRESH install gets and leaves every existing one behind, permanently - "
            + "and no test can see that divergence afterwards, because both convergence fixtures "
            + "replay from scratch. Add the NEXT numbered script instead.\n\n"
            + "Do NOT resolve this by pasting the new hash. A brand-new script does not reach this "
            + "test at all; it is caught by EveryRegisteredScriptIsFrozen, which prints the hash to "
            + "add. If you are here, the script already ran somewhere.\n  "
            + string.Join("\n  ", drifted));
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
            Assert.Equal(i + 1, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
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
    /// A brand-new file and an adopted v0.20.0 file end up with the SAME schema.
    ///
    /// <para>This is the entire promise of the adopter seam, and until now it was checked by hand and
    /// asserted in a commit message. Without it, script N+1 can make the two paths diverge - a fresh
    /// install getting a shape no existing install ever reaches - and every other test stays green.
    /// </para>
    ///
    /// <para>Compared as column SETS and index/view NAMES, not as raw <c>sqlite_master</c> text.
    /// Column ORDER legitimately differs: SQLite appends an ALTER-added column where a CREATE declares
    /// it inline, so a migrated file carries persona_session_id last and a fresh one does not. Nothing
    /// reads these tables by ordinal, and pinning order would fail on a difference that is not one.
    /// </para>
    /// </summary>
    [Fact]
    public void AFreshFileAndAnAdoptedV0200File_ReachTheSameSchema()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mucka-converge", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var fresh = Path.Combine(directory, "fresh.db");
        var legacy = Path.Combine(directory, "legacy.db");

        try
        {
            using (MuckaDb.Open(fresh)) { }

            // A real pre-v0.20.0 file: the baseline alone, no journal, with BOTH columns that release
            // added by probe taken back out. Both, deliberately - the adopter's whole job is to
            // restore exactly that set, and a fixture missing only one of them would still converge
            // with an adopter that had dropped the other, which is the failure this is here to catch.
            using (var connection = new SqliteConnection(MuckaDb.ConnectionString(legacy)))
            {
                connection.Open();
                using var build = connection.CreateCommand();
                build.CommandText = MigrationScripts.All[0].Contents
                    + "ALTER TABLE fights DROP COLUMN prev_same_name_ended_ms;"
                    + "ALTER TABLE score_events DROP COLUMN after_task_line;";
                build.ExecuteNonQuery();
            }
            using (MuckaDb.Open(legacy)) { }

            Assert.Equal(Shape(fresh), Shape(legacy));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>Every table with its columns sorted, plus every index and view name - the whole
    /// schema, in a form where a difference reads as a difference.</summary>
    private static string Shape(string path)
    {
        using var connection = MuckaDb.OpenRead(path);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT group_concat(line, char(10)) FROM (
              SELECT m.type || ' ' || m.name || ': ' ||
                     COALESCE((SELECT group_concat(c.name, ',') FROM (
                         SELECT name FROM pragma_table_info(m.name) ORDER BY name) c), '') AS line
              FROM sqlite_master m
              WHERE m.name NOT LIKE 'sqlite_%'
              ORDER BY m.type, m.name);
            """;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
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
        => Assert.Equal(LegacySchemaAdopter.AddedColumnsAtAdoption, LegacySchemaAdopter.AddedColumnsNow);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mucka.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
