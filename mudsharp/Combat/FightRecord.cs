namespace MudSharp.Combat;

/// <summary>
/// One completed per-NPC fight, as stored in the <c>fights</c> table (see <c>Mucka.Store.MuckaDb</c>),
/// written when the fight closes. This is a compact rollup, deliberately separate from the detailed
/// per-encounter rows, which stay as they are: those are the evidence, this is what the client can
/// cheaply load and query at runtime.
///
///
/// <para>The context fields (room/weather/stats/afflictions) are snapshotted at ENCOUNTER start,
/// not fight start - a fight that joins mid-encounter inherits the encounter's opening context.
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
    /// we do not parse. Such rows must be EXCLUDED from hit-rate and
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

    /// <summary>
    /// The player's score when this fight began - score-at-risk, which is what a flee stands to cost.
    /// This is the last total MUD2 itself stated in a <c>(Persona saved on ...)</c>
    /// line, not a sample off the FES heartbeat, so it and <see cref="ScoreAtEnd"/> are the same kind
    /// of number and are comparable at all. Format v2+ only.
    ///
    /// <para><b>Why this duplicates score_events.</b> The value is derivable from that table - the
    /// last <c>total</c> before <c>started_at_ms</c> - but only for fights recorded after that table
    /// existed, and 1,972 rows predate it. This column keeps those rows answerable.</para>
    /// </summary>
    public int? ScoreAtStart { get; init; }

    /// <summary>
    /// The last score total MUD2 stated while this fight was still open.
    ///
    /// <para><b>This is NOT what the fight earned, and the difference from
    /// <see cref="ScoreAtStart"/> is not either.</b> A kill's award is printed on the line AFTER the
    /// kill, and the kill line is what closes the fight, so a kill's own award is by construction not
    /// in here. What the fight was worth lives in <c>score_events</c>, as a signed delta the game
    /// stated outright, and that is the only honest source for it. Format v2+ only.</para>
    /// </summary>
    public int? ScoreAtEnd { get; init; }

    /// <summary>
    /// When the previous engagement against this exact instance name ended, if this recorder saw one
    /// in this session. Unix ms, matching <see cref="EndedAtMs"/> on that earlier row.
    ///
    /// <para><b>Why the row needs this.</b> MUD2 ends combat with a creature every time it tries to
    /// flee, succeeds or not, and a cornered creature tries every round; a creature that gets away is
    /// chased into another room. Each of those is its own engagement with its own attack command, so
    /// the recorder writes each as its own row - deliberately, because <c>ChaseLinker</c> can only
    /// rejoin them into one observation against one stamina pool if they are separate rows in the
    /// first place. The cost is that the LAST row of such a run holds only the killing blow, and read
    /// on its own it says a banshee died to one hit. 128 of the 206 one-hit kills in the corpus at the
    /// time of writing are that row; the rest are genuine (foxes, fireflies, goblins).</para>
    ///
    /// <para><b>Deliberately raw.</b> No time bound and no policy is applied here: this is the
    /// observation, and whether the gap is short enough to be one creature is
    /// <c>ChaseLinkPolicy</c>'s question, asked at read time where it can be changed without
    /// rewriting history. The earlier row's outcome is recoverable by joining on
    /// (npc_name, ended_at_ms).</para>
    /// </summary>
    public long? PrevSameNameEndedMs { get; init; }
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
    /// stamina pool from ABOVE - a survivor only tells us its pool exceeds what we dealt, so
    /// including non-kills in a pool estimate biases it downward.</summary>
    public bool IsKill => Outcome == nameof(FightOutcome.Kill);

    /// <summary>Whether this row carries usable per-swing detail. False for narrative-mode rows
    /// and for fights that resolved without a single parsed swing either way.</summary>
    public bool HasSwingDetail => !NarrativeMode && (YouHits + YouMisses + TheyHits + TheyMisses) > 0;
}
