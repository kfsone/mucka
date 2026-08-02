namespace MudSharp.Models;

/// <summary>Immutable snapshot of FES (Fast Event Stream) game stats.</summary>
public sealed record GameStatsSnapshot(
    int? Stamina = null,
    int? MaxStamina = null,
    int? Score = null,
    int? Strength = null,
    int? RawStrength = null,
    int? MaxStrength = null,
    int? Dexterity = null,
    int? RawDexterity = null,
    int? MaxDexterity = null,
    int? CurrentMagic = null,
    int? MaxMagic = null,
    int? WeightCarriedGrams = null,
    int? MaxWeightGrams = null,
    int? ObjectsCarried = null,
    int? MaxObjectsCarried = null,
    int? Level = null,
    int? GamesPlayed = null,
    bool IsBlind = false,
    bool IsDeaf = false,
    bool IsCrippled = false,
    bool IsDumb = false,
    char Weather = ' ',
    int? TimeToReset = null,
    string? DreamWord = null,
    bool PersonaSaved = false,
    string? AccountId = null,
    int? Privs = null,
    byte? StaminaColor = null
)
{
    public static readonly GameStatsSnapshot Empty = new();

    /// <summary>
    /// True when this snapshot originates from a FES binary data packet.
    /// When true, boolean flags (IsBlind, IsDeaf, IsCrippled, IsDumb, PersonaSaved)
    /// represent authoritative server state and replace — not OR — the current values
    /// in MergeStats. Text-analysis snapshots leave this false and are OR-merged.
    /// </summary>
    public bool HasFesStats { get; init; }
}
