namespace MudSharp.Models;

/// <summary>
/// Player-state categories that a C1 code can hint may have changed ("gone stale").
/// Individual stats map to the FES probe; <see cref="Inventory"/> maps to FEI and
/// <see cref="WhoList"/> to FEW. The granularity matters on the clearing side: an
/// inline combat line like "(84/90)" refreshes only <see cref="Stamina"/>, while a
/// full FES snapshot refreshes all of <see cref="AllStats"/>.
/// </summary>
[Flags]
public enum StaleStats
{
    None      = 0,
    Stamina   = 1 << 0,
    Strength  = 1 << 1,
    Dexterity = 1 << 2,
    Magic     = 1 << 3,
    Score     = 1 << 4,
    /// <summary>Room/carried items (FEI probe).</summary>
    Inventory = 1 << 5,
    /// <summary>Online player list (FEW probe).</summary>
    WhoList   = 1 << 6,

    /// <summary>Everything the FES probe refreshes.</summary>
    AllStats  = Stamina | Strength | Dexterity | Magic | Score,
    All       = AllStats | Inventory | WhoList,
}
