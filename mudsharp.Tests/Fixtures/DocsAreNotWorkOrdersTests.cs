using System.Text.RegularExpressions;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// A doc in <c>docs/</c> says what the system DOES, not what a stage is going to do to it
/// (CLAUDE.md, "What a doc may say": a design doc is future-tense until its code lands, then
/// present-tense forever, and the commit that lands the work deletes the work order).
///
/// <para><b>Why this is a build failure rather than a policy.</b> The rule was stated and
/// unenforced, and the two documents that broke it broke it for months: a persistence design
/// carrying a numbered "Landing it" procedure and a "Still to delete" list whose four directories
/// had all been deleted, and a spec for a command-line tool whose directory is empty. Neither is
/// visible to the two citation gates beside this one - every symbol a stale work order names still
/// exists, which is exactly why it reads as current. A reader cannot tell a plan from a description
/// by looking, so the marker has to be the thing that fails.</para>
///
/// <para><b>Evidence is exempt by FORM, not by allowlist.</b> Bartle's quoted email, a captured
/// frame, a transcribed table and a worked example are quotations of something outside this repo,
/// and a quotation may say anything it likes, including "I haven't checked any of this". In these
/// docs evidence is always a fenced block, a blockquote or a table row, so "the doc's own voice is
/// prose outside a fence, a quote and a table" is a checkable rule rather than a heuristic. Nothing
/// here may edit evidence to agree with the code, so nothing here may fire on it either.</para>
///
/// <para><b>What is deliberately NOT gated: bare future tense.</b> "will be", "we will" and "later"
/// were considered and rejected - they have legitimate present-tense uses in a rule about what the
/// system does under a condition that has not arisen yet. Two in this corpus: "it must exist, and
/// it will be seen" (the overflow row is reachable in ordinary play) and "if the rail costs even
/// 1 ms of input latency it will be switched off" (an operator's standing condition). Gating those
/// would make the test noise, and a gate that is routinely worked around stops being a gate. Bare
/// "not yet" is out for the same reason: "the guide's weapons page, not yet transcribed" is a fact
/// about somebody else's document. The forms below name a STAGE, a MIGRATION STEP or an unbuilt
/// piece of OUR OWN system, which no present-tense description of a shipped thing needs.</para>
/// </summary>
public class DocsAreNotWorkOrdersTests
{
    /// <summary>
    /// Work-order markers. Each one asserts that something is pending rather than describing what
    /// is there, and each is matched case-insensitively as a substring of a prose line.
    /// </summary>
    private static readonly string[] WorkOrderMarkers =
    [
        // Staging language: the doc is narrating a plan's slices.
        // "in a later" was tried and rejected: "case 8 in a later one" means a later CAPTURE.
        "this stage", "that stage", "next stage", "later stage", "a stage of its own",
        "stage brief",
        // Leftovers a landed commit was supposed to take with it.
        "still to delete", "still to do", "to be deleted", "to be removed", "comes later",
        "landing it", "once this lands", "once the new build", "migration steps",
        // Our own system, asserted absent. Bare "not yet" is deliberately absent - see the remarks.
        "not yet implemented", "not yet built", "not yet wired", "not yet persisted",
        "not yet done", "not implemented yet",
        // The bare markers - the three plainest ways of writing a work order. This corpus carries
        // none of them, so they cost no rewriting and catch the next one on the way in.
        // "wip" is deliberately absent and cannot be added: matched as a substring it fires on
        // "wiping", and this corpus writes "a reset wiping the game state mid-fight". A marker that
        // fires on ordinary prose is how a gate gets worked around, and a gate that is routinely
        // worked around stops being one.
        "todo", "fixme", "tbd",
    ];

    private static readonly string[] DocExtensions = [".md"];

    /// <summary>
    /// Directories under <c>docs/</c> whose contents are exempt.
    ///
    /// <para><c>archive</c> holds parked work by design (CLAUDE.md, "Where prose lives"), and a
    /// parked document is a work order on purpose - that is the whole of what parking one means.
    /// Listed here rather than assumed, so that creating the directory is what turns the exemption
    /// on and a typo in its name does not silently exempt a live doc.</para>
    /// </summary>
    private static readonly HashSet<string> ExemptDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "archive",
    };

    private static DirectoryInfo FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mucka.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir;
    }

    /// <summary>
    /// A table row: the pipe-delimited rows a transcribed table is made of, and the
    /// <c>|---|---|</c> rule under its heading.
    ///
    /// <para><b>Two pipes, not one.</b> A single leading pipe is the cheapest way out of this gate
    /// there is: prefix a work order with <c>| </c> and it reads as evidence. A real row closes its
    /// first cell, so requiring the second pipe costs nothing - this corpus has no single-pipe line
    /// at all, and a table cannot be written without one.</para>
    /// </summary>
    private static readonly Regex TableRow = new(@"^\s*\|[^|]*\|", RegexOptions.Compiled);

    /// <summary>A blockquote, which is how a quotation from outside this repo is set.</summary>
    private static readonly Regex BlockQuote = new(@"^\s*>", RegexOptions.Compiled);

    /// <summary>A fence, opening or closing, with or without a language tag.</summary>
    private static readonly Regex Fence = new(@"^\s*(```|~~~)", RegexOptions.Compiled);

    /// <summary>
    /// The lines that are the DOCUMENT'S OWN VOICE - everything a fence, a blockquote or a table
    /// row does not claim. Returns each with its 1-based line number, so an offender can be
    /// pointed at.
    ///
    /// <para><b>Indentation is NOT an exemption</b>, though Markdown would read four spaces as a
    /// code block. Every 4-space line in this corpus is the continuation of a numbered decision
    /// (<c>docs/combat-panel-design.md</c>'s D1-D14), which is the doc's own voice at its most
    /// load-bearing; exempting those would blind the gate to the half of the corpus most likely to
    /// carry a stage note. Evidence here is always fenced, quoted or tabulated.</para>
    /// </summary>
    public static IEnumerable<(int Line, string Text)> DocVoiceLines(IEnumerable<string> lines)
    {
        var inFence = false;
        var n = 0;
        foreach (var line in lines)
        {
            n++;
            if (Fence.IsMatch(line))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;
            if (BlockQuote.IsMatch(line) || TableRow.IsMatch(line))
                continue;
            yield return (n, line);
        }
    }

    private static IEnumerable<FileInfo> EnumerateDocs(DirectoryInfo directory)
    {
        if (ExemptDirectories.Contains(directory.Name))
            yield break;

        foreach (var file in directory.EnumerateFiles())
            if (DocExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
                yield return file;

        foreach (var child in directory.EnumerateDirectories())
            foreach (var file in EnumerateDocs(child))
                yield return file;
    }

    [Fact]
    public void NoDocCarriesAWorkOrderInItsOwnVoice()
    {
        var root = FindRepoRoot();
        var docs = new DirectoryInfo(Path.Combine(root.FullName, "docs"));
        Assert.True(docs.Exists, "docs/ not found from " + root.FullName);

        var scanned = 0;
        var offenders = new List<string>();

        foreach (var file in EnumerateDocs(docs))
        {
            scanned++;
            foreach (var (line, text) in DocVoiceLines(File.ReadAllLines(file.FullName)))
            {
                var marker = WorkOrderMarkers.FirstOrDefault(
                    m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
                if (marker is not null)
                    offenders.Add(
                        $"{Path.GetRelativePath(root.FullName, file.FullName)}:{line}: \"{marker}\"");
            }
        }

        // Without this the walk could go wrong and the test would pass on nothing at all.
        Assert.True(scanned > 0, "no docs were scanned - the walk is wrong");

        Assert.True(offenders.Count == 0,
            "A doc states what the system DOES; the commit that lands the work deletes the work "
            + "order (CLAUDE.md, \"What a doc may say\"). Rewrite these in the present tense, or "
            + "park the document in docs/archive/ if the work is genuinely shelved:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Each shape of work order below is caught by some marker. That is the direction the assertion
    /// runs, and the only one a per-sample check can show.
    ///
    /// <para>Marker rot is therefore NOT covered: a marker that matches nothing would need a sample
    /// of its own, and there are more markers than samples by design - each describes a shape rather
    /// than a wording. A description claiming otherwise would be the same defect this fixture sweeps
    /// the corpus for.</para>
    /// </summary>
    [Theory]
    [InlineData("Retention is not in this stage.")]
    [InlineData("**Still to delete**, once the new build has been played:")]
    [InlineData("That is a stage of its own.")]
    [InlineData("Session-scoped; not yet persisted to `mucka.ini`.")]
    [InlineData("## Landing it")]
    // The bare markers.
    [InlineData("TODO: decide whether the rail keeps the badge.")]
    [InlineData("FIXME - this reads the wrong column.")]
    [InlineData("Retention window: TBD.")]
    public void EveryShapeOfWorkOrderIsCaught(string prose)
        => Assert.Contains(WorkOrderMarkers,
            m => prose.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>A lone leading pipe is not a table and does not exempt the line. It was the cheapest
    /// evasion this gate had: one character in front of a work order and the sweep skipped it.
    /// </summary>
    [Fact]
    public void ALeadingPipeAloneIsStillTheDocsOwnVoice()
    {
        string[] file = ["| Still to delete: the old path.", "| a | real row |"];

        var voice = DocVoiceLines(file).ToList();

        Assert.Single(voice);
        Assert.Equal(1, voice[0].Line);
    }

    /// <summary>The exemptions, proved on the forms this corpus actually uses: a fenced capture, a
    /// quoted email and a transcribed table row may all say anything. The indented line is there to
    /// show it is NOT exempt - see <see cref="DocVoiceLines"/>.</summary>
    [Fact]
    public void EvidenceIsNeverTheDocsOwnVoice()
    {
        string[] file =
        [
            "Prose.",
            "```",
            "Still to delete: the wyvern.",
            "```",
            "> I haven't checked any of this, it comes later.",
            "| stage brief | 1 |",
            "    D1. An indented decision is still the doc talking.",
            "More prose.",
        ];

        var voice = DocVoiceLines(file).Select(v => v.Text).ToList();

        Assert.Equal(
            ["Prose.", "    D1. An indented decision is still the doc talking.", "More prose."],
            voice);
    }

    /// <summary>A marker in the document's own voice is still caught when it sits after a fenced
    /// block - i.e. the fence state is tracked rather than the first fence disabling the rest of
    /// the file.</summary>
    [Fact]
    public void AFenceDoesNotBlindTheRestOfTheFile()
    {
        string[] file = ["```", "capture", "```", "Deleting the old path is still to delete."];

        var voice = DocVoiceLines(file).ToList();

        Assert.Single(voice);
        Assert.Equal(4, voice[0].Line);
        Assert.Contains(WorkOrderMarkers,
            m => voice[0].Text.Contains(m, StringComparison.OrdinalIgnoreCase));
    }
}
