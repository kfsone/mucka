namespace MudSharp.Tests.Fixtures;

/// <summary>
/// The repo is ASCII (CLAUDE.md, Layout): code, comments, XAML, documentation, scripts. A glyph
/// the UI draws is a constant in Core/Glyph.cs written as its escape, or an XML entity in XAML.
/// This walks every text file under the repo root, tracked or not (an untracked draft that
/// breaks the rule is still in the tree), and names the first line in each file that breaks it,
/// so the rule has a gate instead of a grep somebody has to remember.
/// </summary>
public class RepoIsAsciiTests
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".md", ".csproj", ".props", ".targets", ".slnx", ".yml", ".yaml", ".json", ".jsonl",
        ".xml", ".ps1", ".txt", ".tsv", ".iss", ".svg", ".manifest", ".appxmanifest", ".editorconfig",
        ".gitignore", ".gitattributes", ".ini", ".sh", ".cmd",
    };

    private static readonly HashSet<string> TextNamesWithoutExtension = new(StringComparer.Ordinal)
    {
        "TODO", "LICENSE",
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
    public void EveryTextFile_IsSevenBitAscii()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();
        foreach (var file in EnumerateTextFiles(root))
        {
            var bytes = File.ReadAllBytes(file.FullName);
            int line = 1;
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == (byte)'\n')
                {
                    line++;
                }
                else if (bytes[i] >= 0x80)
                {
                    offenders.Add($"{Path.GetRelativePath(root.FullName, file.FullName)}:{line}");
                    break;
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Non-ASCII bytes in tracked text (see CLAUDE.md, Layout):\n  " + string.Join("\n  ", offenders));
    }

    private static IEnumerable<FileInfo> EnumerateTextFiles(DirectoryInfo dir)
    {
        foreach (var file in dir.EnumerateFiles())
        {
            bool isText = TextExtensions.Contains(file.Extension)
                || (file.Extension.Length == 0 && TextNamesWithoutExtension.Contains(file.Name));
            if (isText)
                yield return file;
        }

        foreach (var sub in dir.EnumerateDirectories())
        {
            if (SkippedDirectories.Contains(sub.Name))
                continue;
            foreach (var file in EnumerateTextFiles(sub))
                yield return file;
        }
    }
}
