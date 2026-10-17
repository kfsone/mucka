namespace Mucka.ViewModels;

/// <summary>One row of the Carrying list: a name, and the arrival time the glow is drawn from.</summary>
public sealed class InventoryEntry
{
    public Mucka.Ui.ArrivalSlot Slot { get; }

    public string Name => Slot.Name;

    /// <summary>When this object arrived in the list; null if it was already there when the list
    /// was read.</summary>
    public DateTime? GlowStartedUtc => Slot.ArrivedUtc;

    public bool IsGlowing => Slot.ArrivedUtc is not null;

    public InventoryEntry(Mucka.Ui.ArrivalSlot slot) => Slot = slot;
}
