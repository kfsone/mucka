using Mucka.Commands;
using World = Mucka.Commands.DreamwordCarry.World;

namespace Mucka.Util.Tests;

/// <summary>
/// <see cref="DreamwordCarry"/>: the dreamword survives a relog into the same world - one server, one
/// reset number - and nothing else. The numbers are from the wire table: runs 115 and 116 both read
/// "This reset is number 127228.", the next reset read 127229, and the other server read 48784.
/// </summary>
public sealed class DreamwordCarryTests
{
    private static readonly World Reset127228 = World.Of("mudii.co.uk", 23, 127228);

    private static DreamwordCarry Holding(World world, string word)
    {
        var carry = new DreamwordCarry();
        carry.Enter(world);
        carry.Note(world, word);
        return carry;
    }

    [Fact]
    public void ARelogIntoTheSameWorld_RestoresTheWord()
    {
        var carry = Holding(Reset127228, "zanzibar");

        Assert.Equal("zanzibar", carry.Enter(World.Of(" MUDII.co.uk ", 23, 127228)));
    }

    [Fact]
    public void ANewResetNumber_DropsTheWord()
    {
        var carry = Holding(Reset127228, "zanzibar");

        Assert.Null(carry.Enter(World.Of("mudii.co.uk", 23, 127229)));
    }

    [Fact]
    public void AnotherServer_DropsTheWord_AndComingBackDoesNotRestoreIt()
    {
        var carry = Holding(Reset127228, "zanzibar");

        Assert.Null(carry.Enter(World.Of("mud2.com", 23, 48784)));
        Assert.Null(carry.Enter(Reset127228));
    }

    [Fact]
    public void AnotherPortOnTheSameHost_IsAnotherServer()
    {
        var carry = Holding(Reset127228, "zanzibar");

        Assert.Null(carry.Enter(World.Of("mudii.co.uk", 2323, 127228)));
    }

    [Fact]
    public void AClearedWord_IsNotRestored()
    {
        var carry = Holding(Reset127228, "zanzibar");

        carry.Note(Reset127228, null);

        Assert.Null(carry.Enter(Reset127228));
    }

    [Fact]
    public void ALateWriteFromAnotherWorld_IsNotRestoredIntoThisOne()
    {
        var carry = new DreamwordCarry();
        var next = World.Of("mudii.co.uk", 23, 127229);

        // A closing connection still in the old reset delivers its last word after the new login's
        // banner has been read.
        carry.Enter(next);
        carry.Note(Reset127228, "zanzibar");

        Assert.Null(carry.Enter(next));
    }
}
