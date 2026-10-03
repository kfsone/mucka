namespace MudSharp.Tests.Fixtures;

/// <summary>
/// No member carries two <c>&lt;summary&gt;</c> blocks.
///
/// <para><b>Why this has a gate.</b> Nothing else sees it. With <c>GenerateDocumentationFile</c> on,
/// two consecutive summary blocks above one member build with no warning, and the generated XML holds
/// both under one <c>&lt;member&gt;</c>; a blank line between the blocks does not detach the first. The
/// pattern is what a member's doc becomes when the member above it is deleted or moved and its doc is
/// left behind, so the orphaned block describes code that is somewhere else. A 111-finding review of
/// this tree found it four times.</para>
///
/// <para><b>Scope.</b> Every <c>.cs</c> file outside <c>bin</c>, <c>obj</c> and <c>tools</c>. A
/// doc-comment block is a run of <c>///</c> lines, read across blank lines because the compiler reads
/// across them too; any other line ends it.</para>
/// </summary>
public class DocCommentsHaveOneSummaryTests
{
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

    private static IEnumerable<FileInfo> EnumerateSourceFiles(DirectoryInfo dir)
    {
        if (SkippedDirectories.Contains(dir.Name))
            yield break;
        foreach (var f in dir.EnumerateFiles("*.cs"))
            yield return f;
        foreach (var sub in dir.EnumerateDirectories())
            foreach (var f in EnumerateSourceFiles(sub))
                yield return f;
    }

    /// <summary>The 1-based line of the first <c>///</c> line of every doc-comment block that opens
    /// more than one <c>&lt;summary&gt;</c>.</summary>
    public static List<int> BlocksWithTwoSummaries(string[] lines)
    {
        var offenders = new List<int>();
        var blockStart = -1;
        var summaries = 0;
        for (int i = 0; i <= lines.Length; i++)
        {
            var text = i < lines.Length ? lines[i].TrimStart() : null;
            if (text is not null && text.StartsWith("///", StringComparison.Ordinal))
            {
                if (blockStart < 0)
                {
                    blockStart = i;
                    summaries = 0;
                }
                for (var at = text.IndexOf("<summary>", StringComparison.Ordinal); at >= 0;
                     at = text.IndexOf("<summary>", at + 1, StringComparison.Ordinal))
                    summaries++;
                continue;
            }
            if (text is not null && text.Length == 0)
                continue;   // a blank line does not end a block
            if (blockStart >= 0 && summaries > 1)
                offenders.Add(blockStart + 1);
            blockStart = -1;
        }
        return offenders;
    }

    [Fact]
    public void NoMemberCarriesTwoSummaries()
    {
        var root = FindRepoRoot();
        var files = EnumerateSourceFiles(root).ToList();
        Assert.True(files.Count > 100, "source sweep found almost nothing - the walk is wrong");

        var offenders = new List<string>();
        foreach (var file in files)
        {
            foreach (var line in BlocksWithTwoSummaries(File.ReadAllLines(file.FullName)))
                offenders.Add($"{Path.GetRelativePath(root.FullName, file.FullName)}:{line}");
        }

        Assert.True(offenders.Count == 0,
            "A doc-comment block opens two <summary> elements - the first usually belongs to a member "
            + "that has moved. Re-parent it or delete it:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>The detector has to be able to fail; a sweep that matches nothing passes any tree.</summary>
    [Fact]
    public void TwoConsecutiveBlocks_AreFound()
    {
        string[] lines =
        [
            "    /// <summary>Belongs to a member that moved.</summary>",
            "    /// <summary>Belongs to this one.</summary>",
            "    public void M() { }",
        ];
        Assert.Equal([1], BlocksWithTwoSummaries(lines));
    }

    [Fact]
    public void BlocksSeparatedByABlankLine_AreFound()
    {
        string[] lines =
        [
            "    /// <summary>",
            "    /// Orphaned.",
            "    /// </summary>",
            "",
            "    /// <summary>Attached.</summary>",
            "    public int P { get; }",
        ];
        Assert.Equal([1], BlocksWithTwoSummaries(lines));
    }

    [Fact]
    public void OneSummaryPerMember_PassesEvenWhenMembersAreAdjacent()
    {
        string[] lines =
        [
            "    /// <summary>First.</summary>",
            "    public int A;",
            "    /// <summary>Second.</summary>",
            "    /// <remarks>Not a summary.</remarks>",
            "    public int B;",
        ];
        Assert.Empty(BlocksWithTwoSummaries(lines));
    }

    [Fact]
    public void ABlockEndingTheFile_IsStillChecked()
    {
        string[] lines =
        [
            "/// <summary>One.</summary>",
            "/// <summary>Two.</summary>",
        ];
        Assert.Equal([1], BlocksWithTwoSummaries(lines));
    }
}
