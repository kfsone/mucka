namespace MudSharp.Models;

/// <summary>Immutable snapshot of FES (Fast Event Stream) game stats.</summary>
public sealed record GameStatsSnapshot(
    int Stamina = 0,
    int MaxStamina = 0,
    int Score = 0,
    int Strength = 0,
    int MaxStrength = 0,
    int Dexterity = 0,
    int MaxDexterity = 0,
    int CurrentMagic = 0,
    int MaxMagic = 0,
    bool IsBlind = false,
    bool IsDeaf = false,
    bool IsCrippled = false,
    bool IsDumb = false,
    char Weather = ' ',
    int TimeToReset = 0,
    string? DreamWord = null,
    bool PersonaSaved = false,
    string? AccountId = null,
    int Privs = 0
)
{
    public static readonly GameStatsSnapshot Empty = new();
}
