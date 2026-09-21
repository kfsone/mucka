using System.Security.Cryptography;
using System.Text;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// <c>CLAUDE.md</c>'s content, pinned to a SHA-256.
///
/// <para><b>What this is for.</b> Seven fixtures read CLAUDE.md and none of them pins what it says:
/// <see cref="ClaudeMdRulesNameARealGateTests"/> checks that each RULE names a gate that exists, and
/// the doc gates check the prose in <c>docs/</c>. An agent can therefore rewrite a rule, soften a
/// DECISION or drop an EVIDENCE line as a side effect of some other task, and the whole suite stays
/// green. This is the governing document for every session; a silent edit to it changes what the
/// next agent is told the rules are.</para>
///
/// <para><b>What it prevents, exactly.</b> An unnoticed edit. Doctrine can no longer move without
/// somebody typing a new hash in the same diff, which is a line a reader sees.</para>
///
/// <para><b>What it does not prevent, stated so nobody reads more into it than is here.</b> A
/// deliberate edit: the hash is one line and anyone may paste it. Nor can it tell a directed change
/// from a reflex one - unlike <c>MigrationScriptsAreFrozenTests</c>, whose two tests are disjoint by
/// construction (a brand-new script never reaches the frozen one, so arriving there proves the
/// script already ran somewhere), this has no sibling that a legitimate change goes through instead.
/// The same failure message reaches the author who was told to amend doctrine and the author who was
/// about to amend it by accident, and only the author knows which they are. CLAUDE.md's own DECISION
/// under "Who writes, who accepts" says what that is worth: a model reads a policy and talks itself
/// past it. What survives that is the diff line, not the message.</para>
/// </summary>
public class ClaudeMdContentIsPinnedTests
{
    /// <summary>
    /// SHA-256 of CLAUDE.md with line endings normalised to LF - the repo checks out with
    /// <c>core.autocrlf=true</c>, so the bytes on disk differ between machines while the document
    /// does not. This pins the DOCUMENT and not the checkout.
    /// </summary>
    private const string Pinned = "89776D4D79F237954F3007F352C04C9CED9B924F1040D5BE89FF7D546E88B57F";

    private static string HashOf(string contents)
        => Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(contents.Replace("\r\n", "\n"))));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mucka.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }

    [Fact]
    public void ClaudeMdHasNotChanged()
    {
        var path = Path.Combine(FindRepoRoot(), "CLAUDE.md");
        Assert.True(File.Exists(path), "CLAUDE.md not found at " + path);

        var actual = HashOf(File.ReadAllText(path));

        Assert.True(string.Equals(actual, Pinned, StringComparison.OrdinalIgnoreCase),
            "CLAUDE.md changed. It is the governing document for every session here, and the whole "
            + "reason it is pinned is that an edit to it otherwise reaches the next agent with "
            + "nothing anywhere recording that it moved.\n\n"
            + "Do NOT resolve this by pasting the new hash and moving on. Ask first which you are:\n"
            + "  - The operator directed this change. Then it is doctrine, update the hash in the "
            + "SAME diff, and say in your report which statement you changed and what he asked for.\n"
            + "  - You are here after a mid-session correction, or while doing something else. Then "
            + "you were about to amend doctrine as a side effect of a task that was not about "
            + "doctrine. Revert CLAUDE.md, and report the correction you were given instead - a "
            + "correction is his to promote to a rule, not yours.\n\n"
            + $"  is     {actual}\n  pinned {Pinned}");
    }
}
