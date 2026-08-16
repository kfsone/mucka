using MudSharp.Combat;
using MudSharp.Models;

namespace Mucka.Core;

/// <summary>
/// One swing, either direction, as stored in the <c>swings</c> table (see <see cref="CombatDb"/>).
/// Property names map one-to-one onto columns; the table is the schema of record now, so adding a
/// property means adding a column - see CombatDb.ApplySchema on how schema changes are made.
///
/// <para><b>Everything knowable is recorded, not just what today's reader wants.</b> That is not
/// hoarding: MUD2's creatures level up within a reset, take buffs and debuffs, get drunk, and respond
/// differently to different weapons - so any "this fight is going worse than usual" judgement is a
/// comparison against a baseline, and a baseline is only as good as the dimensions it can be sliced
/// by. Those dimensions cannot be added to rows that already happened. Every field here is already
/// sitting on the stats snapshot the ledger holds at swing time, so the cost is a column; the cost of
/// omitting one is permanent.</para>
///
/// <para>Nulls are stored rather than defaulted. Half the damage fields are direction-specific by
/// construction (a bracket going out, an exact figure coming in - see <see cref="Damage"/>), and
/// every stat can genuinely be unknown for a swing that landed before the first heartbeat. A zero
/// standing in for "not reported" would be a fabricated measurement that outlives the session that
/// invented it.</para>
/// </summary>
public sealed record SwingRow
{
    /// <summary>"out" - the player swinging.</summary>
    public const string DirectionOut = "out";
    /// <summary>"in" - the creature swinging at the player.</summary>
    public const string DirectionIn = "in";

    /// <summary>Unix ms, taken from <see cref="CombatEvent.TimestampUtc"/> - the instant the line
    /// completed on the Feed thread. Never re-stamped: a consumer's own clock reading would be later
    /// than the event by however long the fan-out took, and the whole point of an ordered stream is
    /// that "what was happening around this swing" survives.</summary>
    public long TimestampMs { get; init; }

    public string Direction { get; init; } = DirectionOut;

    /// <summary>The encounter this swing belongs to, as the shared encounter id (see
    /// MuckaConnection, which stamps ONE id and hands it to every consumer). Joins to
    /// <c>fights.encounter_started_at_ms</c>, which is the whole reason it is stamped centrally
    /// rather than computed here: two consumers each calling UtcNow would produce two ids a few
    /// microseconds apart and the join would silently match nothing.</summary>
    public long? EncounterStartedAtMs { get; init; }

    /// <summary>The character swinging/being swung at (MudSession.CharacterIdentified). Null only for
    /// swings landing in the window between game-mode entry and the setup <c>score</c> reply - the
    /// same gap FightRecord.CharacterName documents.</summary>
    public string? Persona { get; init; }

    /// <summary>Persona sex, as the <c>score</c> sheet words it. SWING-LEDGER-SPEC.md section 3 says
    /// this is unobtainable for an existing character and to ship it null; that note is now stale -
    /// <see cref="GameStatsSnapshot.Sex"/> parses it straight off the sheet, so it is populated.</summary>
    public string? Sex { get; init; }

    /// <summary>Player stamina from the most recent stats snapshot. For <c>dir=in</c> this is the
    /// POST-hit reading: MUD2 embeds "(cur/max)" in the hit line itself and the generic stats scan
    /// consumes it before the combat classifier sees the line.</summary>
    public int? Stamina { get; init; }

    /// <summary>Player stamina immediately BEFORE this blow landed - <c>dir=in</c> hits only.
    ///
    /// <para>Its own field rather than arithmetic on <see cref="Stamina"/> and <see cref="Damage"/>,
    /// even though the two agree whenever both are present. They are not always both present: when no
    /// baseline was available to diff against, <see cref="Damage"/> is null and the pre-hit figure is
    /// unrecoverable - so a consumer computing <c>sta + dmg</c> would silently produce nothing for
    /// exactly the rows where it mattered, with no way to tell that from an honest zero. Storing the
    /// baseline actually used also records what the client BELIEVED when it attributed the damage,
    /// which is the thing worth auditing when a delta looks wrong.</para></summary>
    public int? StaminaBefore { get; init; }

    public int? MaxStamina { get; init; }

    /// <summary>EFFECTIVE strength/dexterity - what the hit-chance and damage formulas actually
    /// consume, and what moves with stamina and carried load. The raw and max values ride alongside so
    /// the GAP between them (what the current load and afflictions are costing) is recoverable without
    /// a second source; recording only one of the three would throw away the variable under test.
    /// </summary>
    public int? Strength { get; init; }
    public int? RawStrength { get; init; }
    public int? MaxStrength { get; init; }
    public int? Dexterity { get; init; }
    public int? RawDexterity { get; init; }
    public int? MaxDexterity { get; init; }

    public int? Level { get; init; }
    public int? Score { get; init; }
    public int? ObjectsCarried { get; init; }
    public string? Weather { get; init; }

    public bool IsBlind { get; init; }
    public bool IsDeaf { get; init; }
    public bool IsCrippled { get; init; }
    public bool IsDumb { get; init; }

    /// <summary>The independent buff/debuff slots, present-or-absent (stack depth is not reliably
    /// knowable - see StatusEffectState). Both directions of the same stat can be active at once,
    /// which is why these are seven flags and not three signed values.</summary>
    public bool StrengthBuff { get; init; }
    public bool StrengthDebuff { get; init; }
    public bool DexterityBuff { get; init; }
    public bool DexterityDebuff { get; init; }
    public bool StaminaBuff { get; init; }
    public bool StaminaDebuff { get; init; }
    public bool Glow { get; init; }

    /// <summary>The game's own countdown to the next reset, as reported on the FES heartbeat.</summary>
    public int? TimeToReset { get; init; }

    /// <summary>When the reset this swing happened in will END - <see cref="TimestampMs"/> plus the
    /// countdown. Derived rather than raw because the countdown changes on every swing while THIS is
    /// constant across a whole reset, which makes it the key to group by.
    ///
    /// <para>It matters because MUD2's creatures are not constants: within a reset they earn points
    /// and level up, hitting harder and surviving longer. A lifetime average for "zombies" silently
    /// blends a freshly-spawned one with one that has been levelling for hours, and a risk assessment
    /// built on that baseline would be confidently wrong in both directions.</para></summary>
    public long? ResetEpochMs { get; init; }

    /// <summary>The instance name exactly as the game gave it ("rat0"), so a single unusually tough
    /// spawn stays distinguishable from its group.</summary>
    public string? NpcName { get; init; }

    /// <summary><see cref="NpcGroups.Normalize"/>d, the same normalisation reduce_combat.py applies -
    /// live and offline rows must bucket identically or the two halves of the pipeline silently
    /// disagree about history.</summary>
    public string NpcGroup { get; init; } = string.Empty;

    /// <summary>The creature's own weapon, which it arms independently of the player and which
    /// materially changes its output - see FightAccumulator.NpcWeapon.</summary>
    public string? NpcWeapon { get; init; }

    /// <summary>The creature's health rung BEFORE this swing, 1-7 on NpcHealthRungs' scale, with the
    /// game's own wording. "Before" is not a nicety: MUD2 prints the descriptor on the line AFTER a
    /// landed blow, so the reading in hand when a swing is classified is the state that swing was
    /// aimed at - exactly what "does a wounded creature hit softer" needs.</summary>
    public int? HealthRung { get; init; }
    public string? HealthPhrase { get; init; }

    /// <summary>What the player had in hand at this instant (<c>dir=out</c>).</summary>
    public string? Weapon { get; init; }

    public bool Hit { get; init; }

    /// <summary>The game's own damage bracket, <c>dir=out</c> hits only - MUD2 never gives the player
    /// an exact figure for their own blows. BOTH ENDS, never a midpoint, and that is a one-way door:
    /// a later pass that can constrain these ranges (a <c>diagnose</c> reading giving a known hitpoint
    /// band, or kill-total arithmetic across a fight) can only narrow a bracket that is still stored
    /// as a bracket.</summary>
    public int? DamageLow { get; init; }
    public int? DamageHigh { get; init; }

    /// <summary>Exact damage, <c>dir=in</c> hits only, from the stamina delta. Null when no baseline
    /// was available - see <see cref="SwingLedger"/>'s stamina relay, which exists because the naive
    /// delta computes to zero every time.</summary>
    public int? Damage { get; init; }
}
