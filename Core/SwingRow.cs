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
/// <summary>
/// A row this ledger can write. Three shapes go down the one queue - the per-swing stream, the rare
/// <c>diagnose</c> reading and the score announcements - and one ordered channel is what keeps them on
/// a single writer thread and a single connection. See <see cref="SwingLedger"/>.
/// </summary>
public interface ICombatLedgerRow;

/// <summary>
/// One <c>(Persona saved on +38 = 19,214).</c> line, as stored in the <c>score_events</c> table. See
/// <see cref="MudSharp.Models.ScoreSave"/> for the three forms the game prints, and the table's own
/// comment in <see cref="CombatDb"/> for why this is a row rather than a column on a swing.
/// </summary>
public sealed record ScoreEventRow : ICombatLedgerRow
{
    public long TimestampMs { get; init; }

    /// <summary>The open encounter, or the one that had just closed - <see cref="EncounterOpen"/>
    /// says which. Null only when this session has not seen a fight yet.</summary>
    public long? EncounterStartedAtMs { get; init; }

    /// <summary>Whether a fight was still in progress when the line arrived. False for every kill
    /// award that finished a room, because the kill line closes the encounter and the award is
    /// printed on the line after it.</summary>
    public bool EncounterOpen { get; init; }

    public string? Persona { get; init; }

    /// <summary>Signed, exactly as printed; null when the line carried no delta.</summary>
    public int? Delta { get; init; }

    /// <summary>The score after the change, as the game states it.</summary>
    public int Total { get; init; }

    /// <summary>Whether a <c>You have completed a Task.</c> line was printed immediately before this
    /// one, with no score line between. An observation about the wire's ordering, not a claim that
    /// this delta is the task's payout - see SwingLedger.OnTaskCompleted.</summary>
    public bool AfterTaskLine { get; init; }

    /// <summary>The line verbatim, so a later pass can re-read the wording instead of trusting this
    /// row's parse.</summary>
    public string? RawText { get; init; }
}

/// <summary>
/// One <c>diagnose</c> reading, as stored in the <c>npc_stamina_reads</c> table: "The water-snake5 has
/// a stamina lying between 90 and 99."
///
/// <para>The two numbers are stored EXACTLY as printed. Whether MUD2's bracket is aligned to tens, to
/// a tenth of the pool, or to something else is unresolved at four observations, and snapping them to
/// an assumed grid would turn those four into a rule.</para>
/// </summary>
public sealed record NpcStaminaReadRow : ICombatLedgerRow
{
    public long TimestampMs { get; init; }
    public long? EncounterStartedAtMs { get; init; }
    public string? Persona { get; init; }

    /// <summary>Instance name as the game gave it ("water-snake5").</summary>
    public string NpcName { get; init; } = string.Empty;

    /// <summary>NpcGroups.Normalize, matching every other table's grouping.</summary>
    public string NpcGroup { get; init; } = string.Empty;

    /// <summary>NpcPoolKey.For - species AND size adjective, which the group name drops.</summary>
    public string PoolKey { get; init; } = string.Empty;

    public int PrintedLow { get; init; }
    public int PrintedHigh { get; init; }

    /// <summary>The line verbatim, so a later pass can re-read the wording instead of trusting this
    /// row's parse.</summary>
    public string? RawText { get; init; }
}

public sealed record SwingRow : ICombatLedgerRow
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

    /// <summary>
    /// The player's running score at this swing.
    ///
    /// <para><b>No reader, and a successor.</b> Score is not a combat stat - it is not earned per
    /// swing, and one blow can finish several creatures with the game scoring them a line at a time -
    /// so this column can only ever say what the total happened to be, never what anything was worth.
    /// The <c>score_events</c> table now carries the real fact, signed delta and all. Nothing reads
    /// this column; dropping it is a separate change from the one that added its replacement, and
    /// this note is here so that change knows it is safe to make.</para>
    /// </summary>
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

    /// <summary>The game's own countdown to the next reset, in MINUTES, as reported on the FES
    /// heartbeat (field [13] - see Mud2C1Decoder.ParseAndEmitFes).</summary>
    public int? TimeToReset { get; init; }

    /// <summary>An ESTIMATE of when the reset this swing happened in will END - <see
    /// cref="TimestampMs"/> plus the countdown in minutes (<c>ttr * 60_000</c>). Derived rather than
    /// raw because the countdown changes on every swing while this stays put - but only to within a
    /// minute: the reading is whole minutes, so successive swings in one reset scatter across a 60s
    /// bucket. <b>Bucket before grouping</b> (ResetClock's MinuteUncertaintySec is the same +/-30s);
    /// raw equality is not an identity and splits one reset into many.
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

    /// <summary>The game's own damage bracket, <c>dir=out</c> hits only. MUD2 almost always brackets
    /// the player's own blows, but it does occasionally print an exact figure and CombatTracker parses
    /// that form too - roughly 6 such lines against 820 bracketed ones in the corpus, with the selector
    /// still undetermined. An exact hit is stored here as a zero-width range so this stays one shape.
    ///
    /// <para>BOTH ENDS, never collapsed, and that is a one-way door: a later pass that can constrain
    /// these ranges (a <c>diagnose</c> reading giving a known hitpoint band, or kill-total arithmetic
    /// across a fight) can only narrow a bracket that is still stored as a bracket.</para>
    ///
    /// <para>That is a rule about STORAGE. What a display derives from these two numbers - a mean, a
    /// midpoint, a shape - is the display's own call; see MudSharp.Combat.ExchangeLine's remarks
    /// for why the blanket "never draw a midpoint" that used to sit here was not the owner's rule.</para></summary>
    public int? DamageLow { get; init; }
    public int? DamageHigh { get; init; }

    /// <summary>Exact damage, <c>dir=in</c> hits only, from the stamina delta. Null when no baseline
    /// was available - see <see cref="SwingLedger"/>'s stamina relay, which exists because the naive
    /// delta computes to zero every time.</summary>
    public int? Damage { get; init; }
}
