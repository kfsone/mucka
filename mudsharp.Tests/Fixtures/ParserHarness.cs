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
    public List<byte[]> Outgoing { get; } = new();
    public int GameModeEnteredCount { get; private set; }
    public int GameModeExitedCount { get; private set; }
    /// <summary>Index into <see cref="Lines"/> at which game mode was first entered (-1 = not yet).</summary>
    public int GameModeEnteredAtLineIndex { get; private set; } = -1;
    public List<string?> Dreamwords { get; } = new();
    public List<string> ClientModeData { get; } = new();
    public List<string> Sounds { get; } = new();
    public List<string> FewPlayers { get; } = new();
    public List<StaleStats> ProbeHints { get; } = new();
    public List<string> PresenceNames { get; } = new();
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
    public List<int> ConfirmedWidths { get; } = new();

    public ParserHarness()
    {
        Parser.LineReady          += l => Lines.Add(l);
        Parser.StatsUpdated       += s => Stats.Add(s);
        Parser.GameModeEntered    += () => { if (GameModeEnteredAtLineIndex < 0) GameModeEnteredAtLineIndex = Lines.Count; GameModeEnteredCount++; };
        Parser.GameModeExited     += () => GameModeExitedCount++;
        Parser.OutgoingBytes      += b => Outgoing.Add(b);
        Parser.DreamwordChanged   += w => Dreamwords.Add(w);
        Parser.ClientModeReceived += d => ClientModeData.Add(d);
        Parser.SoundRequested     += s => Sounds.Add(s);
        Parser.FewPlayerReady     += (n, _) => FewPlayers.Add(n);
        Parser.ProbeHintReceived  += k => ProbeHints.Add(k);
        Parser.PresenceNameSeen   += n => PresenceNames.Add(n);
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
        Outgoing.Clear();
        Dreamwords.Clear();
        ClientModeData.Clear();
        Sounds.Clear();
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
        ConfirmedWidths.Clear();
    }

    /// <summary>Bytes helper: concatenate multiple byte arrays.</summary>
    public static byte[] Bytes(params byte[] b) => b;
}
