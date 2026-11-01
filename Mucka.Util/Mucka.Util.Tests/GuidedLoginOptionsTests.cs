using Mucka.Commands;

namespace Mucka.Util.Tests;

/// <summary>
/// The pass that follows a drop back to the Option menu. Operator rule: no automatic relog, under
/// any circumstance. What the controller then does with these options is in the MAUI assembly,
/// which nothing here reaches.
/// </summary>
public class GuidedLoginOptionsTests
{
    private static readonly PersonaSlot[] Slots =
    {
        new(1, "Ollie", false),
        new(2, "Derpster", false),
        new(3, null, true),
    };

    public static TheoryData<SessionDropReason> EveryReason()
    {
        var data = new TheoryData<SessionDropReason>();
        foreach (var reason in Enum.GetValues<SessionDropReason>())
            data.Add(reason);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryReason))]
    public void NoDropLogsAPersonaBackInOnItsOwn(SessionDropReason reason)
    {
        var options = GuidedLoginOptions.ForReentry(reason, Slots);

        Assert.Null(options.PreferredPersonaName);
        Assert.True(options.ForcePersonaChoice);
        Assert.False(options.AllowCreatePreferredPersona);
        Assert.True(options.StartAtOptionMenu);
    }

    [Fact]
    public void AResetOffersTheLastListAndWaitsOutTheRebuild()
    {
        var options = GuidedLoginOptions.ForReentry(SessionDropReason.Reset, Slots);

        Assert.Same(Slots, options.KnownSlots);
        Assert.Equal(GuidedLoginOptions.ResetRebuildWindow, options.PlayRetryWindow);
    }

    [Fact]
    public void AResetWithNoListStillWaitsOutTheRebuild()
    {
        var options = GuidedLoginOptions.ForReentry(SessionDropReason.Reset, null);

        Assert.Null(options.KnownSlots);
        Assert.Equal(GuidedLoginOptions.ResetRebuildWindow, options.PlayRetryWindow);
    }

    [Theory]
    [InlineData(SessionDropReason.Quit)]
    [InlineData(SessionDropReason.Died)]
    [InlineData(SessionDropReason.Permadeath)]
    [InlineData(SessionDropReason.Unknown)]
    public void OtherDropsAskTheShellForTheList(SessionDropReason reason)
    {
        var options = GuidedLoginOptions.ForReentry(reason, Slots);

        Assert.Null(options.KnownSlots);
        Assert.Null(options.PlayRetryWindow);
    }
}
