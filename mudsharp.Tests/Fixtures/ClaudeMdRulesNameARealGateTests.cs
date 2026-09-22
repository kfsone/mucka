using System.Text.RegularExpressions;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Every RULE in <c>CLAUDE.md</c> ends with an <c>enforced_by:</c> line, and every gate it names is
/// real: a type declared somewhere in the solution, a file that exists in the tree, or the exact
/// literal <c>none: judgement</c>.
///
/// <para><b>Why this is a build failure rather than a convention.</b> The measured failure of a
/// governing document is not that it is worded badly, it is that compliance decays with session
/// length: a rule read at token 2k competes with everything since by token 300k. The one thing that
/// does not decay is a red build, so a rule that claims a gate has to actually have one, and a rule
/// that has none has to say so out loud rather than leave the reader to guess. <c>none: judgement</c>
/// is a legitimate answer and most rules here carry it - the point is that the answer is written
/// down.</para>
///
/// <para><b>What this cannot check.</b> That the gate named is the gate that would catch a breach.
/// Naming <c>RepoIsAsciiTests</c> against a focus rule passes this sweep and is nonsense; the only
/// reader for that is a human or a model reading both. This gate closes the cheaper failure - a
/// gate name that was invented, renamed or deleted - which is the one nothing else can see.</para>
/// </summary>
public class ClaudeMdRulesNameARealGateTests
{
    /// <summary>The four statement kinds CLAUDE.md tags with. A statement runs from its tag line to
    /// the next tag line or the next heading, so a rule may carry blank lines and bullet lists and
    /// still be one statement.</summary>
    private static readonly Regex StatementTag = new(@"^(FACT|RULE|DECISION|EVIDENCE)\s", RegexOptions.Compiled);

    private static readonly Regex EnforcedBy = new(@"^enforced_by:\s*(.+?)\s*$", RegexOptions.Compiled);

    /// <summary>A type declaration, in the five forms C# writes one. Deliberately textual: the gates
    /// live in four different test assemblies (<c>MigrationScriptsAreFrozenTests</c> is in
    /// Mucka.Util.Tests, not here), so reflection over loaded assemblies would see some of them and
    /// not others, and would pass a name it simply could not reach.</summary>
    private static readonly Regex TypeDeclaration = new(
        @"\b(?:class|record|struct|interface|enum)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    /// <summary>A block comment, across as many lines as it spans.</summary>
    private static readonly Regex BlockComment = new(
        @"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>A line comment to end of line. <c>///</c> is a <c>//</c> and needs no second
    /// pattern.</summary>
    private static readonly Regex LineComment = new(@"//[^\r\n]*", RegexOptions.Compiled);

    /// <summary>
    /// The file with its comments cut out, so <see cref="TypeDeclaration"/> harvests a name only
    /// from a real declaration.
    ///
    /// <para>Measured, not hypothetical: 201 comment lines in this tree satisfy that pattern - "see
    /// the class remarks", "the one thing that class exists", "The scope flags record which" - so a
    /// sweep of raw text registers <c>remarks</c>, <c>exists</c> and <c>which</c> as declared types,
    /// and an <c>enforced_by:</c> naming one of them passes. A gate that accepts a word taken out of
    /// its own prose is not a gate, and the failure is silent: the rule reads as enforced.</para>
    ///
    /// <para>String literals are left alone. A declaration spelled inside one is a shape this tree
    /// does not have, and cutting them properly needs a lexer rather than a pattern - the cost of
    /// the remaining hole is one more name accepted, the cost of a half-written lexer is names
    /// wrongly rejected.</para>
    /// </summary>
    private static string StripComments(string source)
        => LineComment.Replace(BlockComment.Replace(source, " "), " ");

    /// <summary>A trailing parenthetical says HOW a gate fires (<c>(RS0030)</c>) and is not part of
    /// the name.</summary>
    private static readonly Regex TrailingNote = new(@"\s*\([^)]*\)\s*$", RegexOptions.Compiled);

    private const string JudgementOnly = "none: judgement";

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

    private static string[] ClaudeMdLines(DirectoryInfo root)
    {
        var path = Path.Combine(root.FullName, "CLAUDE.md");
        Assert.True(File.Exists(path), "CLAUDE.md not found at " + root.FullName);
        return File.ReadAllLines(path);
    }

    /// <summary>Every type name declared anywhere in the tree. Built once per test; the walk is the
    /// same one the other fixtures use.</summary>
    private static HashSet<string> DeclaredTypeNames(DirectoryInfo root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in EnumerateCSharpFiles(root))
            foreach (Match m in TypeDeclaration.Matches(StripComments(File.ReadAllText(file.FullName))))
                names.Add(m.Groups[1].Value);
        return names;
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

    /// <summary>
    /// The whole judgement, factored out so it can be tested against a known set of names rather
    /// than against whatever the tree happens to hold.
    /// </summary>
    /// <returns>null when the value is acceptable, otherwise why it is not.</returns>
    internal static string? WhyNotAGate(string value, ISet<string> declaredTypes, Func<string, bool> fileExists)
    {
        if (value == JudgementOnly)
            return null;

        // A near miss on the literal is the mistake worth naming precisely: "none", "judgement",
        // "none - judgement" all read as the same intent and none of them is the agreed token.
        if (value.Contains("none", StringComparison.OrdinalIgnoreCase)
            || value.Contains("judgement", StringComparison.OrdinalIgnoreCase)
            || value.Contains("judgment", StringComparison.OrdinalIgnoreCase))
            return $"'{value}' is not the literal '{JudgementOnly}'";

        foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var entry = TrailingNote.Replace(raw, string.Empty).Trim();
            if (entry.Length == 0)
                return $"'{value}' has an empty gate in its list";

            // A path names a gate that is data rather than code - the banned-symbol list the
            // analyzer reads. It still has to exist.
            if (entry.Contains('/') || entry.Contains('\\'))
            {
                if (!fileExists(entry))
                    return $"'{entry}' is not a file in the tree";
                continue;
            }

            if (!declaredTypes.Contains(entry))
                return $"'{entry}' is not a type declared in the solution";
        }

        return null;
    }

    [Fact]
    public void EveryEnforcedByNamesARealGate()
    {
        var root = FindRepoRoot();
        var lines = ClaudeMdLines(root);
        var declared = DeclaredTypeNames(root);

        var found = 0;
        var offenders = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var hit = EnforcedBy.Match(lines[i]);
            if (!hit.Success) continue;
            found++;
            var why = WhyNotAGate(hit.Groups[1].Value, declared,
                                  rel => File.Exists(Path.Combine(root.FullName, rel)));
            if (why is not null)
                offenders.Add($"CLAUDE.md:{i + 1}: {why}");
        }

        Assert.True(found > 0, "no enforced_by: lines in CLAUDE.md - the sweep found nothing to sweep");
        Assert.True(offenders.Count == 0,
            "An enforced_by: must name a type that exists in the solution, a file in the tree, or the "
            + "exact literal '" + JudgementOnly + "'. A gate that does not exist is worse than no gate, "
            + "because the rule reads as covered:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void EveryRuleCarriesAnEnforcedBy()
    {
        var root = FindRepoRoot();
        var lines = ClaudeMdLines(root);

        var rules = 0;
        var offenders = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var tag = StatementTag.Match(lines[i]);
            if (!tag.Success || tag.Groups[1].Value != "RULE") continue;
            rules++;

            // The statement runs to the next tag line or the next heading, whichever comes first.
            var enforced = false;
            for (var j = i + 1; j < lines.Length; j++)
            {
                if (StatementTag.IsMatch(lines[j]) || lines[j].StartsWith('#')) break;
                if (EnforcedBy.IsMatch(lines[j])) { enforced = true; break; }
            }
            if (!enforced)
                offenders.Add($"CLAUDE.md:{i + 1}: {lines[i][..Math.Min(70, lines[i].Length)]}");
        }

        Assert.True(rules > 0, "no RULE statements in CLAUDE.md - the tags are gone or renamed");
        Assert.True(offenders.Count == 0,
            "A RULE states what enforces it, even when the answer is '" + JudgementOnly + "':\n  "
            + string.Join("\n  ", offenders));
    }

    // -- The gate has to be able to fail, and these are what it decides ------------------------

    private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal)
    {
        "RepoIsAsciiTests", "DocsCitedSymbolsExistTests", "MigrationScriptsAreFrozenTests",
    };

    private static bool KnownFile(string path) => path == "Mucka.Input/BannedSymbols.txt";

    [Theory]
    [InlineData("none: judgement")]
    [InlineData("RepoIsAsciiTests")]
    [InlineData("DocsCitedSymbolsExistTests, MigrationScriptsAreFrozenTests")]
    [InlineData("Mucka.Input/BannedSymbols.txt (RS0030)")]
    [InlineData("RepoIsAsciiTests, Mucka.Input/BannedSymbols.txt (RS0030)")]
    public void AGateThatExistsIsAccepted(string value)
        => Assert.Null(WhyNotAGate(value, KnownTypes, KnownFile));

    [Theory]
    // The failure this exists for: a plausible name for a gate nobody wrote.
    [InlineData("ClaudeMdIsFrozenTests")]
    [InlineData("RepoIsAsciiTest")]                       // renamed or mistyped by one character
    [InlineData("RepoIsAsciiTests, InventedGateTests")]   // one good name hiding one bad one
    [InlineData("Mucka.Input/NoSuchList.txt")]            // a data gate that is not in the tree
    // Near misses on the literal, which would otherwise read as an honest "nothing enforces this".
    [InlineData("none")]
    [InlineData("judgement")]
    [InlineData("none: judgment")]
    [InlineData("none - judgement")]
    public void AGateThatDoesNotExistIsRejected(string value)
        => Assert.NotNull(WhyNotAGate(value, KnownTypes, KnownFile));

    /// <summary>A type name written in a comment is not a declaration, so it cannot be named as a
    /// gate. Both comment forms, and both of the real phrasings this tree contains: "see the class
    /// remarks" would otherwise declare <c>remarks</c>, and an <c>enforced_by: remarks</c> would
    /// then read as enforced and be nothing at all.</summary>
    [Fact]
    public void ATypeNameInACommentIsNotADeclaration()
    {
        const string source =
            "// see the class remarks for why\n"
            + "/* The scope flags record which */\n"
            + "public sealed class RealThing { }\n";

        var names = TypeDeclaration.Matches(StripComments(source))
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Equal(["RealThing"], names);
    }
}
