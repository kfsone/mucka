namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Schema DDL lives in <c>Mucka.Util/Mucka.Store/Migrations/*.sql</c> and nowhere else.
///
/// <para><b>Why this is a build failure rather than a policy.</b> Releases go out on GitHub, so
/// databases created by this client sit on machines nobody here can inspect, and no human reviews the
/// engineering that changes them (CLAUDE.md, "One operator, no reviewer"). An <c>ALTER TABLE</c>
/// dropped into a writer would run against a stranger's file outside the journal - unversioned,
/// unrepeatable, and impossible to reason about afterwards, because nothing would record that it had
/// happened. A policy in a document does not stop that; a red build does.</para>
///
/// <para>The allowlist is deliberately by exact path rather than by folder or by "tests are exempt".
/// Adding a file to it is a line in a diff that someone has to write on purpose, which is the entire
/// mechanism.</para>
/// </summary>
public class SchemaDdlLivesInMigrationsOnlyTests
{
    /// <summary>What counts as changing the shape of the database. DML (INSERT/UPDATE/DELETE) is
    /// absent on purpose: writers exist to do that, and 0003 does its own row pruning inside a
    /// script.</summary>
    private static readonly string[] DdlTokens =
    [
        "CREATE TABLE", "CREATE INDEX", "CREATE VIEW", "CREATE TRIGGER",
        "ALTER TABLE", "DROP TABLE", "DROP INDEX", "DROP VIEW", "DROP COLUMN",
        "PRAGMA user_version",
    ];

    /// <summary>
    /// The only C# files allowed to contain DDL, by exact repo-relative path.
    ///
    /// <para><c>LegacySchemaAdopter</c> is the one production exception and is frozen: SQLite has no
    /// ADD COLUMN IF NOT EXISTS, so the two columns v0.20.0 added by probe cannot be expressed as an
    /// idempotent script. The two test files build old database shapes to migrate FROM, which is the
    /// one honest way to test a migration.</para>
    /// </summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        Path.Combine("Mucka.Util", "Mucka.Store", "LegacySchemaAdopter.cs"),
        Path.Combine("Mucka.Util", "Mucka.Util.Tests", "FightHistoryStoreTests.cs"),
        Path.Combine("Mucka.Util", "Mucka.Util.Tests", "WireLogTests.cs"),
        Path.Combine("mudsharp.Tests", "Fixtures", "SchemaDdlLivesInMigrationsOnlyTests.cs"),
    };

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "tools", "node_modules",
    };

    private static DirectoryInfo FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mucka.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir;
    }

    [Fact]
    public void NoCSharpFileOutsideTheAllowlist_ContainsSchemaDdl()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var file in EnumerateCSharpFiles(root))
        {
            var relative = Path.GetRelativePath(root.FullName, file.FullName);
            if (Allowed.Contains(relative))
                continue;

            var lines = File.ReadAllLines(file.FullName);
            for (var i = 0; i < lines.Length; i++)
            {
                var token = DdlTokens.FirstOrDefault(
                    t => lines[i].Contains(t, StringComparison.OrdinalIgnoreCase));
                if (token is not null)
                    offenders.Add($"{relative}:{i + 1}: {token}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Schema DDL belongs in Mucka.Util/Mucka.Store/Migrations/*.sql - add the next numbered "
            + "script instead. If a file genuinely needs DDL (building an old shape to migrate from), "
            + "add its path to Allowed here, deliberately:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>Every allowlisted path exists. Without this the list rots into a set of names that
    /// exempt nothing, and the first one to go stale hides the next real offender that replaces
    /// it.</summary>
    [Fact]
    public void EveryAllowlistedFile_StillExists()
    {
        var root = FindRepoRoot();
        var missing = Allowed
            .Where(relative => !File.Exists(Path.Combine(root.FullName, relative)))
            .ToList();

        Assert.True(missing.Count == 0,
            "Allowlisted for schema DDL but not in the tree - remove the entry:\n  "
            + string.Join("\n  ", missing));
    }

    private static IEnumerable<FileInfo> EnumerateCSharpFiles(DirectoryInfo directory)
    {
        if (SkippedDirectories.Contains(directory.Name))
            yield break;

        foreach (var file in directory.EnumerateFiles("*.cs"))
            yield return file;

        foreach (var child in directory.EnumerateDirectories())
            foreach (var file in EnumerateCSharpFiles(child))
                yield return file;
    }
}
