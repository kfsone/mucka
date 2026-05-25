namespace MudSharp.Models;

/// <summary>Immutable snapshot of FES (Fast Event Stream) game stats.</summary>
public sealed record GameStatsSnapshot(
    int? Stamina = null,
    int? MaxStamina = null,
    int? Score = null,
    int? Strength = null,
    int? MaxStrength = null,
    int? Dexterity = null,
    int? MaxDexterity = null,
    int? CurrentMagic = null,
    int? MaxMagic = null,
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
    byte StaminaColor = 0
)
{
    public static readonly GameStatsSnapshot Empty = new();
}
