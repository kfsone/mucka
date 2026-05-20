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

    public ParserHarness()
    {
        Parser.LineReady          += l => Lines.Add(l);
        Parser.StatsUpdated       += s => Stats.Add(s);
        Parser.GameModeEntered    += () => { if (GameModeEnteredAtLineIndex < 0) GameModeEnteredAtLineIndex = Lines.Count; GameModeEnteredCount++; };
        Parser.GameModeExited     += () => GameModeExitedCount++;
        Parser.OutgoingBytes      += b => Outgoing.Add(b);
        Parser.DreamwordChanged   += w => Dreamwords.Add(w);
        Parser.ClientModeReceived += d => ClientModeData.Add(d);
    }

    public void Feed(params byte[] data) => Parser.Feed(data);
    public void Feed(string ascii) => Feed(System.Text.Encoding.Latin1.GetBytes(ascii));
    public void Reset() => Parser.Reset();

    /// <summary>Bytes helper: concatenate multiple byte arrays.</summary>
    public static byte[] Bytes(params byte[] b) => b;
}
