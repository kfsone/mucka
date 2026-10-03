using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// A glyph enters C# only through <c>Core/Glyph.cs</c> (that file's own header states the rule):
/// every non-ASCII character the UI draws, named once. Hard-coded copies drifted - the same square
/// written in two letter-cases on two properties that must match, beside a comment describing
/// circles the code no longer drew.
///
/// <para>So: no string or char literal outside <c>Core/Glyph.cs</c> may spell a non-ASCII code
/// point as an escape. Control-character escapes (below U+0080, such as <c>\u0000</c> as a key
/// separator or <c>\u001B</c> in a captured line) are ASCII and are not glyphs. Test projects are
/// out of scope: they quote wire text, which is evidence, not UI.</para>
/// </summary>
public class GlyphsLiveInTheGlyphTableTests
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "tools", "node_modules", "TestResults",
    };

    private static readonly Regex Escape = new(@"\\(u[0-9A-Fa-f]{4}|U[0-9A-Fa-f]{8})", RegexOptions.Compiled);

    private static DirectoryInfo FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mucka.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir;
    }

    private static IEnumerable<FileInfo> SourceFiles(DirectoryInfo dir)
    {
        if (SkippedDirectories.Contains(dir.Name) || dir.Name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase))
            yield break;
        foreach (var f in dir.EnumerateFiles("*.cs"))
            yield return f;
        foreach (var sub in dir.EnumerateDirectories())
            foreach (var f in SourceFiles(sub))
                yield return f;
    }

    /// <summary>Every non-ASCII escape written inside a string or char literal in
    /// <paramref name="source"/>, as the literal's source text.</summary>
    public static List<string> GlyphEscapes(string source)
    {
        var hits = new List<string>();
        foreach (var token in CSharpSyntaxTree.ParseText(source).GetRoot().DescendantTokens())
        {
            if (!token.IsKind(SyntaxKind.StringLiteralToken) && !token.IsKind(SyntaxKind.CharacterLiteralToken)
                && !token.IsKind(SyntaxKind.InterpolatedStringTextToken))
                continue;
            foreach (Match m in Escape.Matches(token.Text))
            {
                var codePoint = long.Parse(m.Value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (codePoint >= 0x80)
                {
                    hits.Add(token.Text);
                    break;
                }
            }
        }
        return hits;
    }

    [Fact]
    public void NoGlyphIsWrittenOutsideTheGlyphTable()
    {
        var root = FindRepoRoot();
        var files = SourceFiles(root)
            .Where(f => !(f.Name == "Glyph.cs" && f.Directory?.Name == "Core"))
            .ToList();
        Assert.True(files.Count > 100, "source sweep found almost nothing - the walk is wrong");

        var offenders = new List<string>();
        foreach (var file in files)
        {
            foreach (var literal in GlyphEscapes(File.ReadAllText(file.FullName)))
                offenders.Add($"{Path.GetRelativePath(root.FullName, file.FullName)}: {literal}");
        }

        Assert.True(offenders.Count == 0,
            "A glyph enters C# only through Core/Glyph.cs - add a named constant there and use it:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void TheScan_FindsGlyphEscapes_AndIgnoresControlEscapesAndComments()
    {
        var hits = GlyphEscapes("""
            class C
            {
                string A = "\u25A0";
                string B = $"x \u2192 {y}";
                char D = '\u2500';
                string E = "\U0001F512";
                string Sep = "\u0000";
                string Esc = "\u001B[0m";
                // "\u2603" in a comment is not a literal
            }
            """);

        Assert.Equal(4, hits.Count);
    }
}
