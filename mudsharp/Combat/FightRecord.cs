namespace MudSharp.Combat;

/// <summary>
/// One completed per-NPC fight, as stored in the <c>fights</c> table of the combat database (see
/// Mucka.Core.CombatDb), written when the fight closes. This is a compact rollup, deliberately
/// separate from the detailed per-encounter clogs, which stay as they are: those are the evidence,
/// this is what the client can cheaply load and query at runtime.
///
/// <para>Properties map one-to-one onto that table's columns, whose names in turn follow
/// tools/combat/schema.sql - so a query written against the offline reducer's database mostly
/// transfers.</para>
///
/// <para>The context fields (room/weather/stats/afflictions) are snapshotted at ENCOUNTER start,
/// not fight start — a fight that joins mid-encounter inherits the encounter's opening context.
/// That is the honest thing to record: we do not re-probe stats mid-fight, so pretending we have
/// fight-start values for a joiner would be fabricating them.</para>
/// </summary>
public sealed record FightRecord
{

    public long StartedAtMs { get; init; }
    public long EndedAtMs { get; init; }
    public long DurationMs { get; init; }

    /// <summary>The persona fighting this fight (MudSession.CharacterIdentified, from the
    /// post-login "score" reply). Null only for rows recorded before the character was identified
    /// (a fight resolving in the brief window right after game-mode entry) - see
    /// FightHistoryRecorder.OnCharacterIdentified. Format v2+ only.
    /// <para>Why this matters: every alt previously pooled into one fights.jsonl, silently
    /// contaminating medians across characters with very different stats/gear. Filtering/grouping
    /// by this field is left to callers (Foundation adds the capture, not the UI that reads it).</para>
    /// </summary>
    public string? CharacterName { get; init; }

    /// <summary>Unix-ms timestamp of the ENCOUNTER this fight belongs to (the instant
    /// CombatTracker.InCombatChanged flipped true) - shared by every fight opened within the same
    /// encounter, so a multi-NPC pack fight's rows can be regrouped by this value. Distinct from
    /// StartedAtMs, which is this specific fight's own start (a joiner's fight starts later than
    /// its encounter). Format v2+ only.</summary>
    public long? EncounterStartedAtMs { get; init; }

    public string NpcName { get; init; } = string.Empty;
    public string NpcGroup { get; init; } = string.Empty;
    public string? WeaponUsed { get; init; }
    public string Outcome { get; init; } = nameof(FightOutcome.Unresolved);

    public int YouHits { get; init; }
    public int YouMisses { get; init; }
    public int TheyHits { get; init; }
    public int TheyMisses { get; init; }
    public double ApproxDamageDone { get; init; }
    public double ApproxDamageTaken { get; init; }

    /// <summary>True when this fight produced a resolution but no per-swing hit/miss lines at all,
    /// which is the signature of a character without MUD2's <c>fightbrief</c> enabled: narrative
    /// mode replaces the fixed "You hit the X (A-B)." forms with a large flavour-text template set
    /// we do not parse (see MECHANICS_NOTES.md). Such rows must be EXCLUDED from hit-rate and
    /// damage aggregates or they drag every average toward zero. Kept rather than discarded
    /// because the outcome and duration are still real evidence.</summary>
    public bool NarrativeMode { get; init; }

    public string? Room { get; init; }
    public string? Weather { get; init; }
    public int? Strength { get; init; }
    public int? RawStrength { get; init; }
    public int? Dexterity { get; init; }
    public int? RawDexterity { get; init; }
    public int? StaminaAtStart { get; init; }
    public int? MaxStamina { get; init; }

    /// <summary>Lowest player stamina observed while THIS fight was open - "how close did I come to
    /// dying" fighting this specific opponent. Unlike StaminaAtStart (the ENCOUNTER's opening
    /// snapshot, shared by every fight in it), this is per-fight: each concurrent NPC in a pack
    /// fight shares the same READINGS but can have a different minimum if it resolved earlier/later
    /// than its packmates. Format v2+ only; null on older rows and on any row where no reading ever
    /// landed before resolution (see FightAccumulator.MinStamina).</summary>
    public int? MinStamina { get; init; }

    /// <summary>Player stamina as of the last reading observed while THIS fight was still open - see
    /// FightAccumulator.StaminaAtEnd for why this is never re-probed after the fight closes. Format
    /// v2+ only.</summary>
    public int? StaminaAtEnd { get; init; }

    /// <summary>Player score at the instant this fight began. Captured for the combat-log research
    /// tooling under tools/combat/ - score-at-risk per fight is exactly the kind of variable that
    /// tooling needs to surface (e.g. a specific weapon/creature pairing costing more than usual).
    /// Not currently read by any shipped UI. Format v2+ only.</summary>
    public int? ScoreAtStart { get; init; }

    /// <summary>Player score as of the last reading observed while this fight was still open. Format
    /// v2+ only.</summary>
    public int? ScoreAtEnd { get; init; }
    public int? ObjectsCarried { get; init; }
    public int? Level { get; init; }
    public bool IsBlind { get; init; }
    public bool IsDeaf { get; init; }
    public bool IsCrippled { get; init; }
    public bool IsDumb { get; init; }

    /// <summary>Active buff/debuff slot names at encounter start (e.g. "StrengthBuff"), so a
    /// later analysis pass can ask whether a spell was up without reparsing the clog.</summary>
    public string[] Effects { get; init; } = [];

    /// <summary>Whether the fight ended with the NPC dead. Only these fights bound an NPC's
    /// stamina pool from ABOVE — a survivor only tells us its pool exceeds what we dealt, so
    /// including non-kills in a pool estimate biases it downward (see STATS_DESIGN.md).</summary>
    public bool IsKill => Outcome == nameof(FightOutcome.Kill);

    /// <summary>Whether this row carries usable per-swing detail. False for narrative-mode rows
    /// and for fights that resolved without a single parsed swing either way.</summary>
    public bool HasSwingDetail => !NarrativeMode && (YouHits + YouMisses + TheyHits + TheyMisses) > 0;
}
