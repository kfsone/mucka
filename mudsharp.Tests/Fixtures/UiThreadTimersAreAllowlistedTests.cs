using System.Text.RegularExpressions;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// A repeating UI-thread timer is created only in a file named here (CLAUDE.md, Invariant #1: "no
/// repeating UI-thread timers driving animation/opacity/fades - visual fading belongs on the
/// compositor").
///
/// <para><b>What the sweep can and cannot decide.</b> Whether a timer drives animation is a
/// judgement; whether a file creates a dispatcher timer at all is text. So the gate is the second:
/// every site that creates one is a line in this list, and a new site is a deliberate line in a
/// diff rather than a call that nobody reads. The three sites that exist drive an anti-idle ping,
/// a toast dismissal and the raw console's refresh - none of them a fade - and that is the whole
/// reason the list has entries.</para>
///
/// <para>One-shot dispatch (<c>DispatchDelayed</c>) is not swept: it is how a fade's END is
/// scheduled and is not the repeating timer the invariant names.</para>
/// </summary>
public class UiThreadTimersAreAllowlistedTests
{
    /// <summary>The shapes a repeating UI-thread timer takes in MAUI and WinUI code.</summary>
    private static readonly Regex TimerCreation = new(
        @"\.CreateTimer\s*\(|\bIDispatcherTimer\b|\bDispatcherTimer\b|Device\.StartTimer\s*\(",
        RegexOptions.Compiled);

    /// <summary>A comment may name the shape; only code creates a timer.</summary>
    private static readonly Regex CommentLine = new(@"^\s*//", RegexOptions.Compiled);

    /// <summary>Exact repo-relative paths. This file is on it because it spells the shapes out.</summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        Path.Combine("Pages", "GamePage.xaml.cs"),
        Path.Combine("Pages", "RawConsolePage.cs"),
        Path.Combine("mudsharp.Tests", "Fixtures", "UiThreadTimersAreAllowlistedTests.cs"),
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
    public void NoFileOutsideTheAllowlist_CreatesADispatcherTimer()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();
        int swept = 0;
        foreach (var file in EnumerateCSharpFiles(root))
        {
            swept++;
            var relative = Path.GetRelativePath(root.FullName, file.FullName);
            if (Allowed.Contains(relative))
                continue;
            var lines = File.ReadAllLines(file.FullName);
            for (var i = 0; i < lines.Length; i++)
                if (!CommentLine.IsMatch(lines[i]) && TimerCreation.IsMatch(lines[i]))
                    offenders.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(swept > 100, "source sweep found almost nothing - the walk is wrong");

        Assert.True(offenders.Count == 0,
            "A repeating UI-thread timer outside the allowlist (CLAUDE.md, Invariant #1). Schedule the "
            + "work off the UI thread or on the compositor; if this timer genuinely belongs on the UI "
            + "thread, add the file's exact path to Allowed here, deliberately:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>Every allowlisted path exists, so the list cannot rot into names that exempt
    /// nothing.</summary>
    [Fact]
    public void EveryAllowlistedFile_StillExists()
    {
        var root = FindRepoRoot();
        var missing = Allowed.Where(r => !File.Exists(Path.Combine(root.FullName, r))).ToList();
        Assert.True(missing.Count == 0,
            "Allowlisted for a UI-thread timer but not in the tree - remove the entry:\n  "
            + string.Join("\n  ", missing));
    }

    /// <summary>Every allowlisted production file still creates a timer, so an entry outlives its
    /// reason by exactly one build.</summary>
    [Fact]
    public void EveryAllowlistedFile_StillCreatesATimer()
    {
        var root = FindRepoRoot();
        var idle = Allowed
            .Where(r => !r.EndsWith("Tests.cs", StringComparison.Ordinal))
            .Where(r => !File.ReadAllLines(Path.Combine(root.FullName, r))
                .Any(l => !CommentLine.IsMatch(l) && TimerCreation.IsMatch(l)))
            .ToList();
        Assert.True(idle.Count == 0,
            "Allowlisted but creates no timer any more - remove the entry:\n  " + string.Join("\n  ", idle));
    }

    [Theory]
    [InlineData("_toastTimer = Dispatcher.CreateTimer();")]
    [InlineData("private IDispatcherTimer? _updateTimer;")]
    [InlineData("Device.StartTimer(TimeSpan.FromSeconds(1), Tick);")]
    public void ThePatternCatchesATimer(string code) => Assert.Matches(TimerCreation, code);

    [Theory]
    [InlineData("_dispatcher?.DispatchDelayed(TimeSpan.FromMilliseconds(3400), () => Fade());")]
    [InlineData("_timer = new System.Threading.Timer(_ => callback(), null, Timeout.Infinite, Timeout.Infinite);")]
    public void ThePatternLeavesOtherTimersAlone(string code) => Assert.DoesNotMatch(TimerCreation, code);

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
