using System.Text.RegularExpressions;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// A dotted symbol a doc cites must exist in the code (CLAUDE.md, "What a doc may say").
///
/// <para><b>Why this is the more important half of the pair.</b> Banning line numbers
/// (<see cref="DocsCiteSymbolsNotLinesTests"/>) pushes docs toward naming symbols instead, and a
/// name that has gone stale is WORSE than a number that has: a wrong line number looks suspicious,
/// a wrong name reads exactly like a right one. The commit that first wrote the rule shipped three
/// dead ones - `JsonlWireLogSink`, `SessionCapture.Emit`, `TryStartWireLog`, all deleted from the
/// code stages earlier and still cited in the present tense.</para>
///
/// <para><b>This is a grep, not a compiler.</b> It asks only whether the right-hand identifier
/// appears as a whole word somewhere in the tree's source. That is enough to catch a deleted class
/// or a renamed method, which is the whole failure; it deliberately does not check that a member
/// belongs to the type named beside it, because that needs a compiler and the cheap version already
/// catches what actually goes wrong.</para>
/// </summary>
public class DocsCitedSymbolsExistTests
{
    /// <summary>A backtick-quoted dotted identifier: `Foo.Bar`, `Foo.Bar.Baz`, `Foo.Bar()`. Only
    /// inside backticks, so ordinary prose with a full stop in it is never a candidate.</summary>
    private static readonly Regex CitedSymbol = new(
        @"`([A-Z][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)(?:\(\))?`",
        RegexOptions.Compiled);

    /// <summary>A bare name is only a candidate when it is unmistakably a type: multi-word
    /// PascalCase, which `JsonlWireLogSink` and `TryStartWireLog` are and `MUD2`, `FLEE` and
    /// `GameFAQs` are not. Single words in backticks are table names, commands and SQL, and guessing
    /// at those would make this gate noise.</summary>
    private static readonly Regex BareTypeName = new(
        @"^[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]+)+$", RegexOptions.Compiled);

    private static readonly string[] DocExtensions = [".md"];

    /// <summary>
    /// Names that are legitimately cited and legitimately absent from this repo's source.
    ///
    /// <para>Clio's C functions and tables (it is another project, at <c>G:\Source\clio-1.8a</c>),
    /// MUD2's own server-side vocabulary, .NET and SQLite API names, and file names that happen to
    /// look like dotted identifiers. Each entry is a decision that the name is real but lives
    /// outside the tree - not a way to silence a stale citation.</para>
    /// </summary>
    private static readonly HashSet<string> External = new(StringComparer.Ordinal)
    {
        // Clio - the North Star client, a separate C codebase.
        "ldisplay", "wdisplay", "o_codepage", "waddstr", "wopen", "start_logging", "stop_logging",
        // MUD2 server-side and protocol vocabulary.
        "colorcode",
        // Platform and library surface.
        "System.Text", "System.Threading", "Microsoft.Data", "Microsoft.Maui",
        "DateTimeOffset.UtcNow", "DateTime.UtcNow", "Math.Round", "Math.Ceiling",
        "File.ReadAllBytes", "Path.Combine", "Task.Run", "Task.Wait",
        "FileSystem.AppDataDirectory", "FileSystem.Current", "CacheDirectory",
        // WinUI Composition, used by name in the panel's performance contract.
        "ScalarKeyFrameAnimation",
    };

    private static DirectoryInfo FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mucka.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir;
    }

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "tools", "node_modules",
    };

    /// <summary>The two doc gates are excluded from the sweep they perform. Both name dead symbols
    /// on purpose - in their own remarks and their own test data - and a sweep that reads them finds
    /// those names in "the code" and clears the very citations they exist to catch. Verified: with
    /// these files included, `SessionCapture`, `JsonlWireLogSink` and `TryStartWireLog` all passed.
    /// </summary>
    private static readonly HashSet<string> SelfReferentialFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "DocsCitedSymbolsExistTests.cs", "DocsCiteSymbolsNotLinesTests.cs",
    };

    private static string ReadAllSource(DirectoryInfo root)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(DirectoryInfo dir)
        {
            foreach (var f in dir.EnumerateFiles())
                if (f.Extension is ".cs" or ".xaml" or ".csproj" && !SelfReferentialFiles.Contains(f.Name))
                    sb.Append(File.ReadAllText(f.FullName)).Append('\n');
            foreach (var sub in dir.EnumerateDirectories())
                if (!SkippedDirectories.Contains(sub.Name))
                    Walk(sub);
        }
        Walk(root);
        return sb.ToString();
    }

    [Fact]
    public void EverySymbolADocCitesExistsInTheCode()
    {
        var root = FindRepoRoot();
        var docs = new DirectoryInfo(Path.Combine(root.FullName, "docs"));
        Assert.True(docs.Exists, "docs/ not found from " + root.FullName);

        var source = ReadAllSource(root);
        Assert.True(source.Length > 100_000, "source sweep found almost nothing - the walk is wrong");

        var offenders = new List<string>();
        foreach (var file in docs.EnumerateFiles("*", SearchOption.AllDirectories)
                     .Where(f => DocExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase)))
        {
            var lines = File.ReadAllLines(file.FullName);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match m in CitedSymbol.Matches(lines[i]))
                {
                    // A bare single word is only considered when it is unmistakably a type name.
                    if (!m.Groups[1].Value.Contains('.') && !BareTypeName.IsMatch(m.Groups[1].Value))
                        continue;
                    var whole = m.Groups[1].Value;
                    if (External.Contains(whole)) continue;

                    // EVERY segment has to exist, not just the last. `SessionCapture.Emit` is the
                    // case that proves it: the class was deleted, but `Emit` still exists on the
                    // class that replaced it, so a leaf-only check reports nothing at all.
                    foreach (var part in whole.Split('.'))
                    {
                        if (External.Contains(part) || part.Length < 4) continue;
                        if (!char.IsUpper(part[0])) continue;   // a private member like `_startSequence`
                        if (!Regex.IsMatch(source, @"\b" + Regex.Escape(part) + @"\b"))
                        {
                            offenders.Add(
                                $"{Path.GetRelativePath(root.FullName, file.FullName)}:{i + 1}: "
                                + $"{whole} ({part} not found)");
                            break;
                        }
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Docs cite symbols that no longer exist (see CLAUDE.md, \"What a doc may say\").\n"
            + "Rename the citation, or add it to External if it genuinely lives outside this repo:\n  "
            + string.Join("\n  ", offenders));
    }

    [Theory]
    [InlineData("`SessionCapture.Emit`", "SessionCapture.Emit")]
    [InlineData("see `JsonlWireLogSink` for the old sink", "JsonlWireLogSink")]
    [InlineData("`CombatRailView.SlotHeight` owns it", "CombatRailView.SlotHeight")]
    public void ThePatternFindsACitedSymbol(string text, string expected)
    {
        var m = CitedSymbol.Match(text);
        Assert.True(m.Success, "no symbol found in: " + text);
        Assert.Equal(expected, m.Groups[1].Value);
    }

    [Theory]
    // Prose, a file name, and a sentence that merely contains a full stop.
    [InlineData("`mucka.db`")]
    [InlineData("`docs/persistence-design.md`")]
    [InlineData("the bar is full. the notches are rungs")]
    public void ThePatternIgnoresProseAndFileNames(string text)
        => Assert.False(CitedSymbol.IsMatch(text), "wrongly treated as a symbol: " + text);
}
