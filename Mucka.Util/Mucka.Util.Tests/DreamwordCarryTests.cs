using Mucka.Commands;

namespace Mucka.Util.Tests;

/// <summary>
/// <see cref="DreamwordCarry"/>: the dreamword survives a relog to the same server inside one reset
/// cycle, and nothing else.
/// </summary>
public sealed class DreamwordCarryTests
{
    private static readonly DateTime T0 = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    private static DreamwordCarry HoldingOn(string host, string word, DateTime? resetDue = null)
    {
        var carry = new DreamwordCarry();
        carry.Connect(host, T0);
        carry.Note(word);
        carry.NoteResetDue(resetDue);
        return carry;
    }

    [Fact]
    public void ARelogToTheSameServerBeforeTheReset_RestoresTheWord()
    {
        var carry = HoldingOn("mud2.co.uk", "zanzibar", T0.AddMinutes(30));

        Assert.Equal("zanzibar", carry.Connect("MUD2.co.uk", T0.AddMinutes(5)));
    }

    [Fact]
    public void AnotherServer_DropsTheWord_AndComingBackDoesNotRestoreIt()
    {
        var carry = HoldingOn("mud2.co.uk", "zanzibar", T0.AddMinutes(30));

        Assert.Null(carry.Connect("mud2.com", T0.AddMinutes(1)));
        Assert.Null(carry.Connect("mud2.co.uk", T0.AddMinutes(2)));
    }

    [Fact]
    public void AResetWhileAway_DropsTheWord()
    {
        var carry = HoldingOn("mud2.co.uk", "zanzibar", T0.AddMinutes(30));

        Assert.Null(carry.Connect("mud2.co.uk", T0.AddMinutes(31)));
    }

    [Fact]
    public void AResetSeenLive_DropsTheWord()
    {
        var carry = HoldingOn("mud2.co.uk", "zanzibar", T0.AddMinutes(30));

        carry.Clear();

        Assert.Null(carry.Connect("mud2.co.uk", T0.AddMinutes(1)));
    }

    [Fact]
    public void AClearedWord_IsNotRestored()
    {
        var carry = HoldingOn("mud2.co.uk", "zanzibar", T0.AddMinutes(30));

        carry.Note(null);

        Assert.Null(carry.Connect("mud2.co.uk", T0.AddMinutes(1)));
    }

    [Fact]
    public void ADroppedProjection_DoesNotForgetTheLastKnownReset()
    {
        var carry = HoldingOn("mud2.co.uk", "zanzibar", T0.AddMinutes(30));

        carry.NoteResetDue(null);

        Assert.Null(carry.Connect("mud2.co.uk", T0.AddMinutes(31)));
    }

    [Fact]
    public void NoProjectionEver_KeepsTheWord()
    {
        var carry = HoldingOn("mud2.co.uk", "zanzibar");

        Assert.False(carry.IsPastReset(T0.AddHours(3)));
        Assert.Equal("zanzibar", carry.Connect("mud2.co.uk", T0.AddHours(3)));
    }

    [Fact]
    public void IsPastReset_TracksTheLatestProjection()
    {
        var carry = HoldingOn("mud2.co.uk", "zanzibar", T0.AddMinutes(30));

        carry.NoteResetDue(T0.AddMinutes(40));

        Assert.False(carry.IsPastReset(T0.AddMinutes(35)));
        Assert.True(carry.IsPastReset(T0.AddMinutes(40)));
    }
}
