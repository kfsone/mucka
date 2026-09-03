using MudSharp.Models;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// The four wordings that mean the player's loadout just changed. Every positive here is a verbatim
/// line from the session recordings under %LOCALAPPDATA%\Temp\mucka (swept 2026-09-02: 368
/// "dropped", 370 "taken", ~70 each of "inserted in" / "removed from"); every negative is either a
/// real line from the same corpus that must NOT fire a probe, or the specific near-miss the anchors
/// exist to reject.
/// </summary>
public sealed class InventoryChangeLinesTests
{
    [Theory]
    [InlineData("Axe0 dropped.")]
    [InlineData("Staff dropped.")]
    [InlineData("Well-maintained pick2 dropped.")]
    [InlineData("Glass bottle3 dropped.")]
    [InlineData("Nightcap taken.")]
    [InlineData("Lobster pot0 taken.")]
    [InlineData("Golden statue0 taken.")]
    [InlineData("Cache of farthings taken.")]
    // The container pair: these move an object between the pack and a container, which changes the
    // item COUNT (the dexterity burden) without changing the WEIGHT carried (the strength burden).
    [InlineData("Starfish removed from lobster pot0.")]
    [InlineData("Baton inserted in glass bottle6.")]
    [InlineData("Beautiful painting1 removed from coracle.")]
    [InlineData("Piece of jet removed from potty.")]
    public void RealInventoryLines_AreChanges(string line)
        => Assert.True(InventoryChangeLines.IsChange(line));

    [Theory]
    // Ordinary combat and movement output from the same captures.
    [InlineData("The evil, black rat17 hits you (84/90).")]
    [InlineData("You cannot go north from here.")]
    [InlineData("The rat17 has just passed on.")]
    [InlineData("Your guard drops momentarily in your confusion.")]
    [InlineData("You drop everything you're carrying!")]   // covered by the per-item lines that follow
    [InlineData("The starfish is embroiled in combat and can't be dropped.")]
    [InlineData("Well-maintained pick2 kept.")]
    [InlineData("Etude removed from")]                     // truncated: no container, no full stop
    [InlineData("axe0 dropped.")]                          // server capitalises an object at sentence start
    [InlineData("")]
    [InlineData(null)]
    public void EverythingElse_IsNotAChange(string? line)
        => Assert.False(InventoryChangeLines.IsChange(line));

    [Theory]
    [InlineData("Cache of farthings dropped.", InventoryChangeKind.Dropped, "Cache of farthings", null)]
    [InlineData("Well-maintained pick2 taken.", InventoryChangeKind.Taken, "Well-maintained pick2", null)]
    [InlineData("Baton inserted in glass bottle6.", InventoryChangeKind.Stowed, "Baton", "glass bottle6")]
    [InlineData("Starfish removed from lobster pot0.", InventoryChangeKind.Retrieved, "Starfish", "lobster pot0")]
    public void TryParse_SeparatesTheDirectionTheItemAndTheContainer(
        string line, InventoryChangeKind expectedKind, string expectedItem, string? expectedContainer)
    {
        Assert.True(InventoryChangeLines.TryParse(line, out var kind, out var item, out var container));
        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedItem, item);
        Assert.Equal(expectedContainer, container);
    }
}
