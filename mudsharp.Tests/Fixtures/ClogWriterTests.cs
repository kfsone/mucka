using System.Text.Json;
using Mucka.Core;
using MudSharp.Combat;
using MudSharp.Models;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// ClogWriter's tail-capture and overlapping-clog behaviour: an encounter closes (Stop()) the
/// instant CombatTracker says so, but its file stays open, draining trailing prose, until the
/// next prompt (IsPartial line) actually arrives — and a brand new encounter can legitimately
/// open its own file while the previous one is still draining that tail. See ClogWriter's own
/// class remarks for the full rationale; this is the owner's exact worked "rat17 dies, rat21
/// attacks before the next prompt" scenario, driven directly against ClogWriter rather than
/// through CombatTracker (CombatTrackerTests already covers CombatTracker's own boundary
/// detection in isolation).
/// </summary>
public sealed class ClogWriterTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-clogwriter-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static StyledLine Line(string text, bool isPartial = false) => new([new StyledSpan(text, TextStyle.Default)], isPartial);

    private static CombatEvent Event(CombatEventKind kind, string? npc = null, string? weapon = null)
        => new(DateTime.UtcNow, kind, CombatActor.Player, npc, weapon, null, null, "");

    private ClogWriter NewWriter()
    {
        Directory.CreateDirectory(_directory);
        return new ClogWriter(_directory);
    }

    private IReadOnlyList<JsonElement> ReadEntries(string path)
        => File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement).ToList();

    private static string TypeOf(JsonElement e) => e.GetProperty("type").GetString()!;

    [Fact]
    public void Stop_DoesNotFinalizeImmediately_TailKeepsDrainingUntilTheNextPrompt()
    {
        using var writer = NewWriter();

        writer.OnInCombatChanged(true);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat17", weapon: "dagger0"));
        writer.OnCombatEvent(Event(CombatEventKind.Kill, "rat17"));
        writer.OnInCombatChanged(false);

        Assert.False(writer.IsRecording);
        Assert.True(writer.IsTailOnly);   // closed, but the clog is still draining its tail

        // Pure trailing prose - the death confirmation and score line MUD2 prints right after the
        // kill line, before the next prompt. Must be captured, not silently dropped.
        writer.OnLineReady(Line("(Persona saved on +22 = 101,389)."));
        writer.OnLineReady(Line("The rat17 has just passed on."));

        Assert.True(writer.IsTailOnly);   // still draining - no prompt yet

        writer.OnLineReady(Line("*", isPartial: true));   // the next prompt: finalizes the tail

        Assert.False(writer.IsTailOnly);

        writer.WaitForDrainsToSettle_TestOnly(TimeSpan.FromSeconds(5));

        var files = Directory.GetFiles(_directory, "*.jsonl");
        var entries = ReadEntries(Assert.Single(files));

        Assert.Equal(
            ["encounter_start", "event", "event", "line", "line", "encounter_end"],
            entries.Select(TypeOf));
        Assert.Equal("(Persona saved on +22 = 101,389).", entries[3].GetProperty("text").GetString());
        Assert.Equal("The rat17 has just passed on.", entries[4].GetProperty("text").GetString());
    }

    [Fact]
    public void NewEncounter_WhilePreviousIsStillDrainingItsTail_OpensASeparateOverlappingFile()
    {
        // The owner's exact worked fragment: rat17 dies, and before the next prompt an unrelated
        // rat21 starts a genuinely new encounter. Both clogs must exist independently, and neither
        // may leak the other's content.
        using var writer = NewWriter();

        writer.OnInCombatChanged(true);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat17", weapon: "dagger0"));
        writer.OnCombatEvent(Event(CombatEventKind.Kill, "rat17"));
        writer.OnInCombatChanged(false);   // rat17's clog starts draining its tail

        writer.OnLineReady(Line("(Persona saved on +22 = 101,389)."));
        writer.OnLineReady(Line("The rat17 has just passed on."));
        // The join line MUD2 prints before the aggro line that actually opens the new encounter -
        // it arrives while rat17's clog is the only one open, so it belongs to rat17's tail.
        writer.OnLineReady(Line("An evil, black rat (rat21) bares its razor-sharp incisors at you."));

        Assert.True(writer.IsTailOnly);

        // rat21 engages - a brand new encounter, opened while rat17's tail is STILL draining
        // (no prompt has arrived yet).
        writer.OnInCombatChanged(true);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat21"));

        Assert.True(writer.IsRecording);    // rat21 is now the actively-recording encounter
        Assert.False(writer.IsTailOnly);    // no longer "tail only" - one entry is live again

        writer.OnLineReady(Line("*", isPartial: true));   // finalizes rat17's tail only

        writer.OnCombatEvent(Event(CombatEventKind.Miss, "rat21"));
        writer.OnInCombatChanged(false);   // rat21 ends too
        writer.OnLineReady(Line("*", isPartial: true));   // finalizes rat21's tail

        writer.WaitForDrainsToSettle_TestOnly(TimeSpan.FromSeconds(5));

        var files = Directory.GetFiles(_directory, "*.jsonl").OrderBy(f => f).ToList();
        Assert.Equal(2, files.Count);   // two independent files - never merged into one

        var rat17File = files.Single(f => ReadEntries(f)[1].GetProperty("npc").GetString() == "rat17");
        var rat21File = files.Single(f => f != rat17File);

        var rat17Entries = ReadEntries(rat17File);
        Assert.Equal(
            ["encounter_start", "event", "event", "line", "line", "line", "encounter_end"],
            rat17Entries.Select(TypeOf));
        Assert.Equal(
            "An evil, black rat (rat21) bares its razor-sharp incisors at you.",
            rat17Entries[5].GetProperty("text").GetString());

        var rat21Entries = ReadEntries(rat21File);
        Assert.Equal(
            ["encounter_start", "event", "event", "encounter_end"],
            rat21Entries.Select(TypeOf));
        Assert.Equal("rat21", rat21Entries[1].GetProperty("npc").GetString());
        // rat17's tail lines must never leak into rat21's file.
        Assert.DoesNotContain(rat21Entries, e => TypeOf(e) == "line");
    }

    [Fact]
    public void Start_WhileAnEncounterIsAlreadyActive_IsANoOp()
    {
        // Defensive only: CombatTracker never fires InCombatChanged(true) twice in a row without
        // a false in between, but ClogWriter must not corrupt state if it somehow did.
        using var writer = NewWriter();

        writer.OnInCombatChanged(true);
        var firstPath = writer.FilePath;
        writer.OnInCombatChanged(true);

        Assert.Equal(firstPath, writer.FilePath);
        Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));
    }

    [Fact]
    public void Dispose_FinalizesWhateverIsStillOpen_IncludingATailThatNeverSawAPrompt()
    {
        var writer = NewWriter();
        writer.OnInCombatChanged(true);
        writer.OnCombatEvent(Event(CombatEventKind.FightStart, "rat0"));
        writer.OnCombatEvent(Event(CombatEventKind.Kill, "rat0"));
        writer.OnInCombatChanged(false);   // tail opens; no prompt ever arrives

        writer.Dispose();   // app exit mid-tail - best-effort finalize anyway

        var files = Directory.GetFiles(_directory, "*.jsonl");
        var entries = ReadEntries(Assert.Single(files));
        Assert.Equal("encounter_end", TypeOf(entries[^1]));
    }
}
