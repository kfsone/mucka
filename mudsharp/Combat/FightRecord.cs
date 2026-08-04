using System.Text.Json.Serialization;

namespace MudSharp.Combat;

/// <summary>
/// One completed per-NPC fight, as persisted to ~/.mucka/clogs/fights.jsonl (one JSON object per
/// line, appended when the fight closes). This is a compact rollup INDEX, deliberately separate
/// from the detailed per-encounter clogs, which stay as they are: those are the evidence, this is
/// what the client can cheaply load and query at runtime.
///
/// <para>Property names are snake_case to match tools/combat/schema.sql's column naming, so
/// ingest_clogs.py can read these rows without a translation layer.</para>
///
/// <para>The context fields (room/weather/stats/afflictions) are snapshotted at ENCOUNTER start,
/// not fight start — a fight that joins mid-encounter inherits the encounter's opening context.
/// That is the honest thing to record: we do not re-probe stats mid-fight, so pretending we have
/// fight-start values for a joiner would be fabricating them.</para>
/// </summary>
public sealed record FightRecord
{
    [JsonPropertyName("started_at_ms")] public long StartedAtMs { get; init; }
    [JsonPropertyName("ended_at_ms")] public long EndedAtMs { get; init; }
    [JsonPropertyName("duration_ms")] public long DurationMs { get; init; }

    [JsonPropertyName("npc_name")] public string NpcName { get; init; } = string.Empty;
    [JsonPropertyName("npc_group")] public string NpcGroup { get; init; } = string.Empty;
    [JsonPropertyName("weapon_used")] public string? WeaponUsed { get; init; }
    [JsonPropertyName("outcome")] public string Outcome { get; init; } = nameof(FightOutcome.Unresolved);

    [JsonPropertyName("you_hits")] public int YouHits { get; init; }
    [JsonPropertyName("you_misses")] public int YouMisses { get; init; }
    [JsonPropertyName("they_hits")] public int TheyHits { get; init; }
    [JsonPropertyName("they_misses")] public int TheyMisses { get; init; }
    [JsonPropertyName("approx_damage_done")] public double ApproxDamageDone { get; init; }
    [JsonPropertyName("approx_damage_taken")] public double ApproxDamageTaken { get; init; }

    /// <summary>True when this fight produced a resolution but no per-swing hit/miss lines at all,
    /// which is the signature of a character without MUD2's <c>fightbrief</c> enabled: narrative
    /// mode replaces the fixed "You hit the X (A-B)." forms with a large flavour-text template set
    /// we do not parse (see MECHANICS_NOTES.md). Such rows must be EXCLUDED from hit-rate and
    /// damage aggregates or they drag every average toward zero. Kept rather than discarded
    /// because the outcome and duration are still real evidence.</summary>
    [JsonPropertyName("narrative_mode")] public bool NarrativeMode { get; init; }

    [JsonPropertyName("room")] public string? Room { get; init; }
    [JsonPropertyName("weather")] public string? Weather { get; init; }
    [JsonPropertyName("strength")] public int? Strength { get; init; }
    [JsonPropertyName("raw_strength")] public int? RawStrength { get; init; }
    [JsonPropertyName("dexterity")] public int? Dexterity { get; init; }
    [JsonPropertyName("raw_dexterity")] public int? RawDexterity { get; init; }
    [JsonPropertyName("stamina_at_start")] public int? StaminaAtStart { get; init; }
    [JsonPropertyName("max_stamina")] public int? MaxStamina { get; init; }
    [JsonPropertyName("weight_carried_grams")] public int? WeightCarriedGrams { get; init; }
    [JsonPropertyName("objects_carried")] public int? ObjectsCarried { get; init; }
    [JsonPropertyName("level")] public int? Level { get; init; }
    [JsonPropertyName("is_blind")] public bool IsBlind { get; init; }
    [JsonPropertyName("is_deaf")] public bool IsDeaf { get; init; }
    [JsonPropertyName("is_crippled")] public bool IsCrippled { get; init; }
    [JsonPropertyName("is_dumb")] public bool IsDumb { get; init; }

    /// <summary>Active buff/debuff slot names at encounter start (e.g. "StrengthBuff"), so a
    /// later analysis pass can ask whether a spell was up without reparsing the clog.</summary>
    [JsonPropertyName("effects")] public string[] Effects { get; init; } = [];

    /// <summary>Whether the fight ended with the NPC dead. Only these fights bound an NPC's
    /// stamina pool from ABOVE — a survivor only tells us its pool exceeds what we dealt, so
    /// including non-kills in a pool estimate biases it downward (see STATS_DESIGN.md).</summary>
    [JsonIgnore] public bool IsKill => Outcome == nameof(FightOutcome.Killed);

    /// <summary>Whether this row carries usable per-swing detail. False for narrative-mode rows
    /// and for fights that resolved without a single parsed swing either way.</summary>
    [JsonIgnore] public bool HasSwingDetail => !NarrativeMode && (YouHits + YouMisses + TheyHits + TheyMisses) > 0;
}
