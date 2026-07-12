using MudSharp.Models;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

public class EffectTrackerTests
{
    private static StatusEffectChange Start(StatusEffectKind k, EffectSign s)
        => new(k, s, EffectTransition.Started);
    private static StatusEffectChange Full(StatusEffectKind k, EffectSign s)
        => new(k, s, EffectTransition.FullyWoreOff);
    private static StatusEffectChange Partial(StatusEffectKind k, EffectSign s)
        => new(k, s, EffectTransition.PartiallyWoreOff);

    [Fact]
    public void Start_TurnsSlotOn()
    {
        var t = new EffectTracker();
        t.Apply(Start(StatusEffectKind.Strength, EffectSign.Buff));
        Assert.True(t.Current.StrengthBuff);
        Assert.False(t.Current.StrengthDebuff);
    }

    [Fact]
    public void BuffAndDebuff_CoexistIndependently()
    {
        var t = new EffectTracker();
        t.Apply(Start(StatusEffectKind.Strength, EffectSign.Buff));
        t.Apply(Start(StatusEffectKind.Strength, EffectSign.Debuff));
        Assert.True(t.Current.StrengthBuff);
        Assert.True(t.Current.StrengthDebuff);
    }

    [Fact]
    public void PartialWearOff_LeavesSlotOn()
    {
        var t = new EffectTracker();
        t.Apply(Start(StatusEffectKind.Strength, EffectSign.Debuff));
        t.Apply(Partial(StatusEffectKind.Strength, EffectSign.Debuff));
        Assert.True(t.Current.StrengthDebuff);
    }

    [Fact]
    public void StackedThenPartialsThenFull_MatchesRealWeakenSequence()
    {
        // 3× weaken, two partials, one full clear → on the whole time, off only at the end.
        var t = new EffectTracker();
        t.Apply(Start(StatusEffectKind.Strength, EffectSign.Debuff));
        t.Apply(Start(StatusEffectKind.Strength, EffectSign.Debuff));
        t.Apply(Start(StatusEffectKind.Strength, EffectSign.Debuff));
        t.Apply(Partial(StatusEffectKind.Strength, EffectSign.Debuff));
        Assert.True(t.Current.StrengthDebuff);
        t.Apply(Partial(StatusEffectKind.Strength, EffectSign.Debuff));
        Assert.True(t.Current.StrengthDebuff);
        t.Apply(Full(StatusEffectKind.Strength, EffectSign.Debuff));
        Assert.False(t.Current.StrengthDebuff);
    }

    [Fact]
    public void Changed_FiresOnlyOnActualStateChange()
    {
        var t = new EffectTracker();
        int fires = 0;
        t.Changed += _ => fires++;
        t.Apply(Start(StatusEffectKind.Glow, EffectSign.Buff));       // on  → fire
        t.Apply(Start(StatusEffectKind.Glow, EffectSign.Buff));       // still on → no fire
        t.Apply(Partial(StatusEffectKind.Stamina, EffectSign.Buff));  // ignored → no fire
        t.Apply(Full(StatusEffectKind.Glow, EffectSign.Buff));        // off → fire
        Assert.Equal(2, fires);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var t = new EffectTracker();
        t.Apply(Start(StatusEffectKind.Dexterity, EffectSign.Buff));
        t.Apply(Start(StatusEffectKind.Glow, EffectSign.Buff));
        t.Reset();
        Assert.False(t.Current.AnyActive);
    }

    [Fact]
    public void Reset_ClearsCachedMessages()
    {
        // AnyActive excludes the cached-message fields, so Reset_ClearsEverything cannot see them.
        // Reset must also wipe the buff/glow tooltip lines AND the affliction message cache
        // (which AnyActive never reflects, so it needs explicit coverage).
        var t = new EffectTracker();
        t.Apply(new StatusEffectChange(StatusEffectKind.Strength, EffectSign.Buff, EffectTransition.Started, "stronger"));
        t.Apply(new StatusEffectChange(StatusEffectKind.Glow, EffectSign.Buff, EffectTransition.Started, "glowing"));
        t.Apply(new StatusEffectChange(StatusEffectKind.Deaf, EffectSign.Buff, EffectTransition.Started, "gone deaf"));
        t.Apply(new StatusEffectChange(StatusEffectKind.Blind, EffectSign.Buff, EffectTransition.Started, "gone blind"));
        t.Apply(new StatusEffectChange(StatusEffectKind.Dumb, EffectSign.Buff, EffectTransition.Started, "gone dumb"));
        t.Apply(new StatusEffectChange(StatusEffectKind.Crippled, EffectSign.Buff, EffectTransition.Started, "crippled"));
        // Sanity: messages are cached while active.
        Assert.Equal("stronger", t.Current.StrengthBuffMsg);
        Assert.Equal("gone deaf", t.Current.DeafMsg);

        t.Reset();

        Assert.Null(t.Current.StrengthBuffMsg);
        Assert.Null(t.Current.GlowMsg);
        Assert.Null(t.Current.DeafMsg);
        Assert.Null(t.Current.BlindMsg);
        Assert.Null(t.Current.DumbMsg);
        Assert.Null(t.Current.CrippledMsg);
    }
}
