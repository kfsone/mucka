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
    byte? StaminaColor = null,
    // ── `score` sheet only (no FES equivalent) ────────────────────────────────
    // The sheet is the sole source for these, so they are appended (never reordered):
    // every construction site names its arguments, and appending keeps that safe.
    /// <summary>Persona sex as the sheet words it ("male"/"female"). Never changes within a persona.</summary>
    string? Sex = null,
    /// <summary>Points earned in the current game ("this game: N points"). Legitimately 0 — null means
    /// "not reported", 0 means "reported as zero", per the nullable convention above.</summary>
    int? ScoreThisGame = null,
    /// <summary>The persona's own point value ("value: N points"). This is what an attacker collects
    /// when we flee or die, so it is the size of the transfer, not a vanity figure.</summary>
    int? PlayerValue = null
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
