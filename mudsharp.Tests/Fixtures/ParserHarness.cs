using MudSharp.Models;
using MudSharp.Protocol;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Test harness: feeds bytes into MudStreamParser and captures all emitted events.
/// </summary>
internal sealed class ParserHarness
{
    public MudStreamParser Parser { get; } = new();
    public List<StyledLine> Lines { get; } = new();
    public List<GameStatsSnapshot> Stats { get; } = new();
    /// <summary>"(Persona saved on ...)" score announcements - see MudStreamParser.ScoreSaved.</summary>
    public List<ScoreSave> ScoreSaves { get; } = new();
    /// <summary>"You have completed a Task." announcements - see MudStreamParser.TaskCompleted.</summary>
    public List<TaskCompletion> TaskCompletions { get; } = new();
    /// <summary>Task and score events interleaved in the order they were raised, as
    /// "task" / "+100" / "-872" strings. The ORDER is the whole reason the task event exists, so it
    /// needs a capture that can actually see it - two separate lists cannot.</summary>
    public List<string> ScoringOrder { get; } = new();
    /// <summary>How many frames closed - see MudStreamParser.FrameClosed. Also interleaved into
    /// <see cref="ScoringOrder"/> as "frame", because where the boundary falls RELATIVE to the score
    /// lines is the entire reason the signal exists.</summary>
    public int FrameClosedCount { get; private set; }
    public int PersonaWipedCount { get; private set; }
    public List<byte[]> Outgoing { get; } = new();
    public int GameModeEnteredCount { get; private set; }
    public int GameModeExitedCount { get; private set; }
    /// <summary>Index into <see cref="Lines"/> at which game mode was first entered (-1 = not yet).</summary>
    public int GameModeEnteredAtLineIndex { get; private set; } = -1;
    public List<string?> Dreamwords { get; } = new();
    public List<string> ClientModeData { get; } = new();
    public List<string> Sounds { get; } = new();
    public List<string> TellSenders { get; } = new();
    public List<string> FewPlayers { get; } = new();
    public List<StaleStats> ProbeHints { get; } = new();
    public List<string> PresenceNames { get; } = new();
    public List<StatusEffectChange> StatusEffects { get; } = new();
    public int FewListStartingCount { get; private set; }
    public int FewListCompleteCount { get; private set; }
    public int RoomEnteredCount { get; private set; }
    public List<string> RoomShorts { get; } = new();
    public List<string> FeiItems { get; } = new();
    public int FeiListStartingCount { get; private set; }
    public int FeiListCompleteCount { get; private set; }
    public List<string> FexItems { get; } = new();
    public int FexListStartingCount { get; private set; }
    public int FexListCompleteCount { get; private set; }
    /// <summary>Creature-presence sentences captured from the C04 scope - see
    /// MudStreamParser.CreatureTextReady.</summary>
    public List<string> CreatureTexts { get; } = new();
    public List<string> LongDescLines { get; } = new();
    public List<(string Dir, string Dest)> ExitLines { get; } = new();
    public List<int> ConfirmedWidths { get; } = new();

    public ParserHarness()
    {
        Parser.LineReady          += l => Lines.Add(l);
        Parser.StatsUpdated       += s => Stats.Add(s);
        Parser.PersonaWiped       += () => PersonaWipedCount++;
        Parser.ScoreSaved         += s => { ScoreSaves.Add(s); ScoringOrder.Add(s.Delta is int d ? d.ToString("+0;-0") : "="); };
        Parser.TaskCompleted      += t => { TaskCompletions.Add(t); ScoringOrder.Add("task"); };
        Parser.FrameClosed        += () => { FrameClosedCount++; ScoringOrder.Add("frame"); };
        Parser.GameModeEntered    += () => { if (GameModeEnteredAtLineIndex < 0) GameModeEnteredAtLineIndex = Lines.Count; GameModeEnteredCount++; };
        Parser.GameModeExited     += () => GameModeExitedCount++;
        Parser.OutgoingBytes      += b => Outgoing.Add(b);
        Parser.DreamwordChanged   += w => Dreamwords.Add(w);
        Parser.ClientModeReceived += d => ClientModeData.Add(d);
        Parser.SoundRequested     += s => Sounds.Add(s);
        Parser.TellReceived       += n => TellSenders.Add(n);
        Parser.FewPlayerReady     += (n, _) => FewPlayers.Add(n);
        Parser.ProbeHintReceived  += k => ProbeHints.Add(k);
        Parser.PresenceNameSeen   += n => PresenceNames.Add(n);
        Parser.StatusEffectChanged += e => StatusEffects.Add(e);
        Parser.FewListStarting    += () => FewListStartingCount++;
        Parser.FewListComplete    += () => FewListCompleteCount++;
        Parser.RoomEntered        += () => RoomEnteredCount++;
        Parser.RoomShortReady     += name => RoomShorts.Add(name);
        Parser.FeiItemReady       += item => FeiItems.Add(item);
        Parser.FeiListStarting    += () => FeiListStartingCount++;
        Parser.FeiListComplete    += () => FeiListCompleteCount++;
        Parser.FexItemReady       += item => FexItems.Add(item);
        Parser.FexListStarting    += () => FexListStartingCount++;
        Parser.FexListComplete    += () => FexListCompleteCount++;
        Parser.CreatureTextReady  += text => CreatureTexts.Add(text);
        Parser.LongDescLineReady  += text => LongDescLines.Add(text);
        Parser.ExitLineReady      += (dir, dest) => ExitLines.Add((dir, dest));
        Parser.TerminalWidthConfirmed += w => ConfirmedWidths.Add(w);
    }

    public void Feed(params byte[] data) => Parser.Feed(data);
    public void Feed(string ascii) => Feed(System.Text.Encoding.Latin1.GetBytes(ascii));
    public void Reset() => Parser.Reset();

    /// <summary>
    /// Clears all captured event data (lines, stats, counters) without resetting the
    /// underlying parser state. Use this after feeding setup bytes to discard noise.
    /// </summary>
    public void ClearCounters()
    {
        Lines.Clear();
        Stats.Clear();
        ScoreSaves.Clear();
        PersonaWipedCount = 0;
        Outgoing.Clear();
        Dreamwords.Clear();
        ClientModeData.Clear();
        Sounds.Clear();
        TellSenders.Clear();
        FewPlayers.Clear();
        ProbeHints.Clear();
        PresenceNames.Clear();
        FewListStartingCount = 0;
        FewListCompleteCount = 0;
        RoomEnteredCount = 0;
        RoomShorts.Clear();
        FeiItems.Clear();
        FeiListStartingCount = 0;
        FeiListCompleteCount = 0;
        FexItems.Clear();
        FexListStartingCount = 0;
        FexListCompleteCount = 0;
        CreatureTexts.Clear();
        LongDescLines.Clear();
        ExitLines.Clear();
        ConfirmedWidths.Clear();
    }

    /// <summary>Bytes helper: concatenate multiple byte arrays.</summary>
    public static byte[] Bytes(params byte[] b) => b;
}
