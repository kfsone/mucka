namespace MudSharp.Combat;

/// <summary>One row of the swing ledger, reduced to what constrains a pool.</summary>
/// <param name="NpcName">Instance name as the game gave it.</param>
/// <param name="TimestampMs">Unix ms, the tracker's own stamp for the line.</param>
/// <param name="IsOutgoing">True for the player's own swing.</param>
/// <param name="Hit">Whether it landed.</param>
/// <param name="DamageLow">Bracket low, for an outgoing hit. Null in narrative mode, where MUD2
/// prints no figure at all.</param>
/// <param name="DamageHigh">Bracket high.</param>
/// <param name="Rung">The creature's health rung as it stood BEFORE this swing - which is how the
/// ledger stores it, because MUD2 prints the descriptor on the line after a landed blow.</param>
/// <param name="PlayerStamina">The player's stamina at this swing. Carried so consecutive fights
/// against one name can be tested for player-state continuity - see <see cref="ChaseLinkPolicy"/>.</param>
/// <param name="PlayerStaminaMax">The player's maximum at this swing. A change in it is a dreamword
/// boost, which is a deliberate re-attempt rather than a chase.</param>
/// <param name="PlayerWeapon">What the player had in hand. Only stamped on outgoing rows.</param>
public readonly record struct PoolSwingRow(
    string NpcName,
    long TimestampMs,
    bool IsOutgoing,
    bool Hit,
    double? DamageLow,
    double? DamageHigh,
    int? Rung,
    int? PlayerStamina = null,
    int? PlayerStaminaMax = null,
    string? PlayerWeapon = null);

/// <summary>One row of the fight rollup, reduced to what a pool observation needs.</summary>
/// <param name="EndedInKill">Only a kill by the player's own blow. A creature that dropped dead of
/// poison bounds nothing - the damage that finished it was never on the wire.</param>
/// <param name="EncounterStartedAtMs">The encounter this fight belonged to. Two same-name fights
/// inside ONE encounter cannot be a re-engagement with a respawn; they are either a chase after the
/// creature broke off, or a FOLD of several creatures sharing one printed name. See
/// <see cref="ChaseLinker"/>.</param>
public readonly record struct PoolFightRow(
    string NpcName,
    long StartedAtMs,
    long EndedAtMs,
    bool EndedInKill,
    long? EncounterStartedAtMs = null);

/// <summary>
/// Assembles <see cref="PoolFightObservation"/>s from the two stored streams: the per-fight rollup,
/// which knows how each fight ENDED, and the per-swing ledger, which knows what was dealt and what
/// the creature looked like along the way.
///
/// <para>Pure, so the attribution rule below is testable rather than buried in a query.</para>
///
/// <para><b>The rung attribution rule, and the evidence it deliberately throws away.</b> The ledger
/// stamps every swing row with the creature's rung as it stood before that swing, which means a rung
/// that does not change is indistinguishable from a rung that was re-read and came back the same. A
/// reading is therefore attributed ONLY at the row where the stored value CHANGES, and it is credited
/// with the blows landed strictly before that row. The alternative - crediting the last row carrying a
/// value - would attribute a stale reading to a larger cumulative damage total and inflate the lower
/// bound, which is the direction that invents pool. Under-using the evidence is the safe error and
/// this makes it deliberately.</para>
///
/// <para><b>A second, unavoidable loss:</b> the reading printed after the LAST blow of a fight has no
/// following swing row to be stamped on, so it is never seen here at all. That costs a survivor its
/// strongest rung bound. Nothing in the stored corpus can recover it; a future ledger row for the
/// descriptor line itself could.</para>
/// </summary>
public static class PoolObservationBuilder
{
    /// <summary>
    /// One fight's observation, from its swings in time order. Returns null when the fight landed no
    /// blow with a parsed bracket - narrative mode, or a fight that resolved without the player
    /// connecting - since such a fight constrains nothing.
    /// </summary>
    public static PoolFightObservation? Build(PoolFightRow fight, IEnumerable<PoolSwingRow> swingsInOrder)
    {
        var ordered = swingsInOrder as IReadOnlyList<PoolSwingRow> ?? swingsInOrder.ToList();
        var blows = new List<DamageBracket>();
        var rungs = new List<RungReading>();
        int? previousRung = null;
        var seenAnyRung = false;

        foreach (var swing in ordered)
        {
            // Checked BEFORE the blow is counted: the stored rung is the state preceding this swing,
            // so it belongs to the blows that came before it.
            if (swing.Rung is int rung && rung >= 1 && rung <= NpcHealthRungs.Rungs
                && (!seenAnyRung || rung != previousRung))
            {
                rungs.Add(new RungReading(rung, blows.Count));
                previousRung = rung;
                seenAnyRung = true;
            }

            if (swing.IsOutgoing && swing.Hit
                && swing.DamageLow is double low && swing.DamageHigh is double high
                && high >= low && low >= 0)
            {
                blows.Add(new DamageBracket(low, high));
            }
        }

        if (blows.Count == 0)
            return null;

        // Player state across the fight, for the chase-linkage test. Taken from the swing rows rather
        // than the rollup because the rollup stores no weapon-at-end and no per-swing stamina.
        int? staStart = null, staEnd = null, maxEnd = null;
        string? weapon = null;
        foreach (var swing in ordered)
        {
            staStart ??= swing.PlayerStamina;
            if (swing.PlayerStamina is not null) staEnd = swing.PlayerStamina;
            if (swing.PlayerStaminaMax is not null) maxEnd = swing.PlayerStaminaMax;
            if (!string.IsNullOrWhiteSpace(swing.PlayerWeapon)) weapon = swing.PlayerWeapon;
        }

        return new PoolFightObservation(
            fight.NpcName, fight.StartedAtMs, fight.EndedAtMs, fight.EndedInKill, blows, rungs,
            fight.EncounterStartedAtMs,
            new PlayerFightState(staStart, staEnd, maxEnd, weapon));
    }

    /// <summary>
    /// Every observation the corpus supports, matching each fight to the swings that fall inside its
    /// own time window against the same instance name.
    ///
    /// <para>Matched on (name, time window) rather than on the encounter id, because a swing recorded
    /// before the client noticed the encounter carries no encounter id at all and would be dropped by
    /// a join. The window's end is INCLUSIVE - the killing blow's stamp is the fight's end
    /// stamp.</para>
    ///
    /// <para>Neither input needs sorting; both are sorted here.</para>
    /// </summary>
    public static IReadOnlyList<PoolFightObservation> BuildAll(
        IEnumerable<PoolFightRow> fights,
        IEnumerable<PoolSwingRow> swings)
    {
        var byName = new Dictionary<string, List<PoolSwingRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var swing in swings)
        {
            if (string.IsNullOrWhiteSpace(swing.NpcName))
                continue;
            if (!byName.TryGetValue(swing.NpcName, out var list))
                byName[swing.NpcName] = list = [];
            list.Add(swing);
        }

        foreach (var list in byName.Values)
            list.Sort((a, b) => a.TimestampMs.CompareTo(b.TimestampMs));

        var result = new List<PoolFightObservation>();
        foreach (var fight in fights)
        {
            if (string.IsNullOrWhiteSpace(fight.NpcName)
                || !byName.TryGetValue(fight.NpcName, out var list))
                continue;

            var window = new List<PoolSwingRow>();
            foreach (var swing in list)
            {
                if (swing.TimestampMs < fight.StartedAtMs)
                    continue;
                if (swing.TimestampMs > fight.EndedAtMs)
                    break;
                window.Add(swing);
            }

            if (Build(fight, window) is PoolFightObservation observation)
                result.Add(observation);
        }

        return result;
    }
}
