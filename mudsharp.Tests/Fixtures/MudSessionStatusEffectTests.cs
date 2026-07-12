using System.Text;
using MudSharp.Models;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// End-to-end wiring of the status-effect feature through MudSession: a decoded C11 bracket
/// flows parser → EffectTracker.Apply → EffectTracker.Changed → MudSession.StatusEffectsChanged,
/// and exiting game mode resets the tracker (a relog/logout carries no effects). A slow FES
/// heartbeat keeps the probe timer out of the way; no network or real timing is involved.
/// </summary>
public class MudSessionStatusEffectTests : IDisposable
{
    // C02+C01 game-mode prompt variant — the post-character-select entry trigger.
    private static readonly byte[] GameModeEntry = [0x9D, 0x9C, 0xFF, 0xFF];
    // C95+C03 account-logout → exit game mode.
    private static readonly byte[] AccountLogout = [0xFA, 0x9E, 0xFF, 0xFF];

    // A C11 enhancing-start bracket (11 02): 0xA6 0x9D <FF FF> phrase <FF FF>.
    private static byte[] Start(string phrase)
        => [0xA6, 0x9D, 0xFF, 0xFF, .. Encoding.Latin1.GetBytes(phrase), 0xFF, 0xFF];

    private const string StrongerLine = "You have suddenly and magically become stronger!";

    private readonly MudSession _session;
    private readonly List<StatusEffectState> _states = new();

    public MudSessionStatusEffectTests()
    {
        _session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval = TimeSpan.FromSeconds(60),   // keep the heartbeat out of the way
        });
        _session.StatusEffectsChanged += s => _states.Add(s);
    }

    public void Dispose() => _session.Dispose();

    [Fact]
    public void StatusBracket_RaisesStatusEffectsChanged_WithRightState()
    {
        _session.Feed(GameModeEntry);
        Assert.Empty(_states);   // entry resets an already-empty tracker → no publish

        _session.Feed(Start(StrongerLine));

        var s = Assert.Single(_states);
        Assert.True(s.StrengthBuff);
        Assert.True(s.AnyActive);
        Assert.Equal(StrongerLine, s.StrengthBuffMsg);   // tooltip line survives the whole path
    }

    [Fact]
    public void GameModeExit_ResetsEffects()
    {
        _session.Feed(GameModeEntry);
        _session.Feed(Start(StrongerLine));
        _states.Clear();

        _session.Feed(AccountLogout);   // exit game mode → EffectTracker.Reset publishes Empty

        var s = Assert.Single(_states);
        Assert.False(s.AnyActive);
        Assert.Null(s.StrengthBuffMsg);
    }
}
