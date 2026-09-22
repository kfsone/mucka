using System.Text.RegularExpressions;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// A doc in <c>docs/</c> may not point at code by line number (CLAUDE.md, "What a doc may say").
/// It names the symbol instead, because a symbol survives an edit above it and a line number does
/// not.
///
/// <para><b>Why this has a gate rather than a convention.</b> The citations rot silently and fast.
/// One design document was written with six of them and every one was already wrong by the time the
/// commit that introduced it landed; another carried twelve, one of which came to point at a line
/// added later for an unrelated feature. Nothing about that is visible to a compiler, a reviewer or
/// a reader - the reference still looks like a reference.</para>
///
/// <para><b>Scope.</b> <c>docs/</c>, and the comment lines of every <c>.cs</c> file, and only source
/// extensions. CLAUDE.md lives at the repo root and is outside the sweep, so it can quote the banned
/// shape when it states the rule. Citations of a CAPTURE - a session recording and a record number
/// inside it - are evidence and are not touched: they name a file that is not source, and they are
/// how a wire fact is made checkable. A cross-reference to another document is likewise left alone.
/// Source comments are swept because they rot the same way: the first sweep of them found a test
/// citing <c>MudSession.cs:166</c> for a claim that had moved to a method 1,150 lines below.</para>
///
/// <para><b>The one shape it cannot see.</b> A citation whose FILENAME is split by a line wrap
/// (<c>Foo.c</c> / <c>s:44</c>) survives, because the sweep joins at most two adjacent lines and the
/// halves do not read as a filename on either side of the join. Wrapping mid-token is not something
/// a writer does on purpose; the wrap that does happen - after the colon, or before the number - is
/// caught. Recorded rather than chased.</para>
/// </summary>
public class DocsCiteSymbolsNotLinesTests
{
    /// <summary>
    /// A source file followed by a line number, in the three shapes that get written: <c>Foo.cs:44</c>,
    /// <c>Foo.cs line 44</c>, and the GitHub anchor <c>Foo.cs#L44</c>. Whitespace is allowed around
    /// the separator so a citation broken across a line wrap is still caught.
    ///
    /// <para>Source extensions only, and case-insensitive. A <c>.md</c>, <c>.jsonl</c>, <c>.tsv</c>
    /// or <c>.txt</c> followed by a number is a cross-reference or a captured record, not a pointer
    /// into code.</para>
    /// </summary>
    private static readonly Regex CodeLineCitation = new(
        @"[A-Za-z0-9_.]+\.(cs|xaml|csproj|props|targets)\s*(:\s*|\#L\s*|\s+line\s+)[0-9]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Text files in <c>docs/</c>. Not just markdown: a citation in the bestiary or in a
    /// captured-frame table would rot exactly the same way.</summary>
    private static readonly string[] DocExtensions = [".md", ".tsv", ".txt"];

    private static DirectoryInfo FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mucka.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir;
    }

    [Fact]
    public void NoDocCitesCodeByLineNumber()
    {
        var root = FindRepoRoot();
        var docs = new DirectoryInfo(Path.Combine(root.FullName, "docs"));
        Assert.True(docs.Exists, "docs/ not found from " + root.FullName);

        var files = docs.EnumerateFiles("*", SearchOption.AllDirectories)
                        .Where(f => DocExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
                        .ToList();
        Assert.NotEmpty(files);   // a sweep that found nothing to sweep proves nothing

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file.FullName);
            for (int i = 0; i < lines.Length; i++)
            {
                // The line itself, then the line joined to the one below it: a citation that a
                // markdown wrap split after the filename is invisible to a per-line scan.
                var hit = CodeLineCitation.Match(lines[i]);
                if (!hit.Success && i + 1 < lines.Length)
                    hit = CodeLineCitation.Match(lines[i] + " " + lines[i + 1].TrimStart());
                if (hit.Success)
                    offenders.Add(
                        $"{Path.GetRelativePath(root.FullName, file.FullName)}:{i + 1}: {hit.Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Docs must cite a symbol, not a line number (see CLAUDE.md, \"What a doc may say\"):\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>Only comment lines are swept, so a string literal carrying the shape (this file's
    /// own test data, an error message) is not a citation. XML doc comments are included: they are
    /// where an agent writes "see Foo.cs:44" most often.</summary>
    private static readonly Regex CommentLine = new(@"^\s*//", RegexOptions.Compiled);

    /// <summary>
    /// Which lines carry comment text, <c>//</c> and <c>/* */</c> alike.
    ///
    /// <para><see cref="CommentLine"/> alone sees only the first form, so a citation written inside
    /// a block comment - or on any continuation line of one - is invisible to the sweep. This tree's
    /// 30 block comments are all the single-line trailing kind (<c>catch { /* diagnostics only */
    /// }</c>), so the hole is unexploited rather than absent, and a gate is worth no more than the
    /// text it can actually see.</para>
    ///
    /// <para>A line that OPENS a block counts, whatever sits before the <c>/*</c>: the sweep reports
    /// a whole line, so a citation either side of the marker is the same offence at the same place.
    /// </para>
    /// </summary>
    public static bool[] CommentLines(string[] lines)
    {
        var flags = new bool[lines.Length];
        var inBlock = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var text = lines[i];
            if (inBlock)
            {
                flags[i] = true;
                if (text.Contains("*/", StringComparison.Ordinal))
                    inBlock = false;
                continue;
            }

            var lineComment = text.IndexOf("//", StringComparison.Ordinal);
            var blockOpen = text.IndexOf("/*", StringComparison.Ordinal);
            // A "/*" sitting inside a // comment is prose and opens nothing. This tree writes one:
            // an arithmetic aside in CombatRailResizeTests whose "*/" is on the NEXT line, also
            // inside a // comment. Treated as an opener it would swallow whatever followed until a
            // stray "*/" turned up, and every line between would be swept as if it were a comment.
            if (blockOpen >= 0 && lineComment >= 0 && lineComment < blockOpen)
                blockOpen = -1;

            flags[i] = blockOpen >= 0 || CommentLine.IsMatch(text);
            if (blockOpen >= 0 && text.IndexOf("*/", blockOpen, StringComparison.Ordinal) < 0)
                inBlock = true;
        }
        return flags;
    }

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "tools", "node_modules",
    };

    [Fact]
    public void NoSourceCommentCitesCodeByLineNumber()
    {
        var root = FindRepoRoot();
        var files = EnumerateSourceFiles(root)
            .Where(f => !f.Name.Equals(nameof(DocsCiteSymbolsNotLinesTests) + ".cs", StringComparison.Ordinal))
            .ToList();
        Assert.True(files.Count > 100, "source sweep found almost nothing - the walk is wrong");

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file.FullName);
            var isComment = CommentLines(lines);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!isComment[i]) continue;
                var hit = CodeLineCitation.Match(lines[i]);
                // The join is for a citation the wrap split; one the next line carries whole is
                // that line's own offence and is not reported twice.
                if (!hit.Success && i + 1 < lines.Length && isComment[i + 1]
                    && !CodeLineCitation.IsMatch(lines[i + 1]))
                    hit = CodeLineCitation.Match(lines[i] + " " + lines[i + 1].TrimStart(' ', '/'));
                if (hit.Success)
                    offenders.Add(
                        $"{Path.GetRelativePath(root.FullName, file.FullName)}:{i + 1}: {hit.Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A source comment must cite a symbol, not a line number (see CLAUDE.md, \"What a comment may say\"):\n  "
            + string.Join("\n  ", offenders));
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

    /// <summary>The gate has to be able to fail, and the pattern is the whole of it - a regex that
    /// matched nothing would pass the sweep above on any clean tree and prove nothing.</summary>
    [Theory]
    // The plain shape, backticked and bare.
    [InlineData("see `MuckaConnection.cs:44` for why")]
    [InlineData("`Pages/GamePage.xaml:275`")]
    [InlineData("mudsharp/Models/StatusEffect.cs:66-77")]
    // The alternates an agent actually writes.
    [InlineData("see CombatRailView.cs line 999 for details")]
    [InlineData("see CombatRailView.cs#L999 for details")]
    // Case, because the extension list is not the place to be strict.
    [InlineData("SETTINGSSTORE.CS:197")]
    public void ThePatternCatchesACitation(string text)
        => Assert.Matches(CodeLineCitation, text);

    [Theory]
    // A symbol reference, which is what a doc is supposed to carry.
    [InlineData("see `MuckaConnection.ConnectAsync`")]
    [InlineData("`CombatRailView.SlotHeight`")]
    // A captured session and a record number in it: evidence, and the reason the pattern is
    // restricted to source extensions.
    [InlineData("session-rec.mud2.co.uk.20260826-134435.jsonl:2905")]
    // A cross-reference between documents.
    [InlineData("docs/persistence-design.md:154")]
    // Prose that happens to put a number near a filename.
    [InlineData("`CombatRailView.cs` is 2956 lines long")]
    // Times and ratios.
    [InlineData("at 12:30, a contrast of 3.8:1")]
    public void ThePatternLeavesLegitimateProseAlone(string text)
        => Assert.DoesNotMatch(CodeLineCitation, text);

    /// <summary>A citation inside a block comment is seen. With <c>^\s*//</c> as the only test it
    /// was not, so an entire comment form was exempt by accident rather than by decision - including
    /// every continuation line of a multi-line block, which is where a wrapped citation would
    /// land.</summary>
    [Fact]
    public void ACitationInsideABlockCommentIsSeen()
    {
        string[] file =
        [
            "var x = 1;",
            "/*",
            " * See MudSession.cs:1740 for the wiring.",
            " */",
            "var y = 2;",
        ];

        Assert.Equal([false, true, true, true, false], CommentLines(file));
        Assert.Matches(CodeLineCitation, file[2]);
    }

    /// <summary>The trailing single-line form this tree actually uses closes on its own line, so it
    /// does not exempt everything after it.</summary>
    [Fact]
    public void ASingleLineBlockCommentDoesNotBlindTheRestOfTheFile()
    {
        string[] file = ["catch { /* diagnostics only */ }", "var y = 2;"];

        Assert.Equal([true, false], CommentLines(file));
    }

    /// <summary>A "/*" inside a // comment is prose and opens no block. The shape is in this tree -
    /// an arithmetic aside whose "*/" lands on the next line, also inside a // comment - and reading
    /// it as an opener would sweep the code after it as comment text until a stray "*/" appeared.
    /// </summary>
    [Fact]
    public void ABlockMarkerInsideALineCommentOpensNothing()
    {
        // The code line sits BETWEEN the false opener and the "*/", which is what makes this
        // discriminating: read as a real block, the marker swallows it and it is swept as comment
        // text. With the "*/" on the very next line - the shape this tree has - both readings agree
        // and the test would prove nothing.
        string[] file =
        [
            "        // width is 72 /* 70 + 2 breathing",
            "        var width = Compute();",
            "        // columns */ and the rail keeps the rest.",
            "        Execute();",
        ];

        Assert.Equal([true, false, true, false], CommentLines(file));
    }

    /// <summary>A citation split by a line wrap after the filename is caught by the two-line join,
    /// which is the whole reason the sweep looks at pairs.</summary>
    [Fact]
    public void AWrappedCitationIsStillCaught()
    {
        Assert.DoesNotMatch(CodeLineCitation, "see `ConnectViewModel.cs:");
        Assert.Matches(CodeLineCitation, "see `ConnectViewModel.cs:" + " " + "155` for the call site");
    }
}
