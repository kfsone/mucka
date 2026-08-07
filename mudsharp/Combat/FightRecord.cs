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
    /// <summary>Schema version of THIS row, bumped whenever a breaking field is added/removed/
    /// renamed. v1 files (no such field at all - <see cref="System.Text.Json.JsonSerializer"/>
    /// deserializes a missing int property to its default, 0) predate character/encounter/
    /// min-stamina/score capture entirely; <see cref="Mucka.Core.FightHistoryStore"/> detects that
    /// on load and renames the old file aside rather than silently mixing formats. Bump this again
    /// the next time a field is added or removed.</summary>
    [JsonPropertyName("format_version")] public int FormatVersion { get; init; } = CurrentFormatVersion;

    /// <summary>The version every row written by the CURRENT build carries. A file whose rows carry
    /// anything less (including the implicit 0 of a v1 file with no field at all) is stale.</summary>
    [JsonIgnore] public const int CurrentFormatVersion = 2;

    [JsonPropertyName("started_at_ms")] public long StartedAtMs { get; init; }
    [JsonPropertyName("ended_at_ms")] public long EndedAtMs { get; init; }
    [JsonPropertyName("duration_ms")] public long DurationMs { get; init; }

    /// <summary>The persona fighting this fight (MudSession.CharacterIdentified, from the
    /// post-login "score" reply). Null only for rows recorded before the character was identified
    /// (a fight resolving in the brief window right after game-mode entry) - see
    /// FightHistoryRecorder.OnCharacterIdentified. Format v2+ only.
    /// <para>Why this matters: every alt previously pooled into one fights.jsonl, silently
    /// contaminating medians across characters with very different stats/gear. Filtering/grouping
    /// by this field is left to callers (Foundation adds the capture, not the UI that reads it).</para>
    /// </summary>
    [JsonPropertyName("character_name")] public string? CharacterName { get; init; }

    /// <summary>Unix-ms timestamp of the ENCOUNTER this fight belongs to (the instant
    /// CombatTracker.InCombatChanged flipped true) - shared by every fight opened within the same
    /// encounter, so a multi-NPC pack fight's rows can be regrouped by this value. Distinct from
    /// StartedAtMs, which is this specific fight's own start (a joiner's fight starts later than
    /// its encounter). Format v2+ only.</summary>
    [JsonPropertyName("encounter_started_at_ms")] public long? EncounterStartedAtMs { get; init; }

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

    /// <summary>Lowest player stamina observed while THIS fight was open - "how close did I come to
    /// dying" fighting this specific opponent. Unlike StaminaAtStart (the ENCOUNTER's opening
    /// snapshot, shared by every fight in it), this is per-fight: each concurrent NPC in a pack
    /// fight shares the same READINGS but can have a different minimum if it resolved earlier/later
    /// than its packmates. Format v2+ only; null on older rows and on any row where no reading ever
    /// landed before resolution (see FightAccumulator.MinStamina).</summary>
    [JsonPropertyName("min_stamina")] public int? MinStamina { get; init; }

    /// <summary>Player stamina as of the last reading observed while THIS fight was still open - see
    /// FightAccumulator.StaminaAtEnd for why this is never re-probed after the fight closes. Format
    /// v2+ only.</summary>
    [JsonPropertyName("stamina_at_end")] public int? StaminaAtEnd { get; init; }

    /// <summary>Player score at the instant this fight began. Needed for the flee-cost ladder's
    /// economics work (DESIGN_FINAL.md section 5) - dropped entirely before format v2. Format v2+
    /// only.</summary>
    [JsonPropertyName("score_at_start")] public int? ScoreAtStart { get; init; }

    /// <summary>Player score as of the last reading observed while this fight was still open. Format
    /// v2+ only.</summary>
    [JsonPropertyName("score_at_end")] public int? ScoreAtEnd { get; init; }
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
