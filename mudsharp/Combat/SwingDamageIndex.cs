namespace MudSharp.Combat;

/// <summary>
/// How hard something hits, over some set of landed blows: the count, the worst one, and enough to
/// derive the mean. A value type with no identity of its own, so the same shape describes "this rat,
/// this fight" and "every rat ever" without two near-identical records.
///
/// <para>This is the INCOMING side, where MUD2 prints the player an exact stamina figure for every
/// blow. The outgoing side gets <see cref="BracketProfile"/> instead, because the game only ever
/// gives a range for the player's own blows and collapsing that to a midpoint here would be a
/// one-way loss.</para>
///
/// <para>Zero-damage hits count. A blow that lands and takes nothing off is a real observation about
/// how dangerous a creature is, and dropping it would bias the mean upward - the average would then
/// answer "how hard does it hit when it hurts", which is a different and less useful question than
/// "how hard does it hit".</para>
/// </summary>
/// <param name="Max">Widened to double only so ONE profile type serves both the historical index
/// (whole stamina points, read straight out of the database) and the live per-fight tally, whose
/// damage already flows as double through FightAccumulator. Every value that actually reaches it is
/// integral - MUD2 reports stamina in whole points - so nothing here is ever fractional in practice.</param>
public readonly record struct DamageProfile(int Samples, double Max, double Sum)
{
    public static readonly DamageProfile Empty = new(0, 0, 0);

    public bool HasSamples => Samples > 0;

    /// <summary>Mean damage per landed blow. Zero when there is nothing to average - callers must
    /// test <see cref="HasSamples"/> rather than treat that zero as a measurement (the rail's rule 5:
    /// unknown must never render as a measured state).</summary>
    public double Average => Samples == 0 ? 0.0 : Sum / Samples;

    public DamageProfile Add(double damage)
        => new(Samples + 1, damage > Max ? damage : Max, Sum + damage);

    /// <summary>The live per-fight profile for one opponent, from the tallies FightAccumulator already
    /// keeps. Here rather than on the accumulator so the historical and in-fight halves of the rail's
    /// damage column are literally the same type, and cannot drift in how they define a mean.</summary>
    public static DamageProfile ForFight(int measuredHits, double maxTaken, double totalTaken)
        => measuredHits <= 0 ? Empty : new(measuredHits, maxTaken, totalTaken);
}

/// <summary>
/// The player's own output against something, kept as RANGES throughout - MUD2 reports a blow as
/// "15-19" and never as a number.
///
/// <para>The two ends are summed separately so the average comes out as a range too
/// (<see cref="AverageLow"/>..<see cref="AverageHigh"/>). Averaging the midpoints instead would
/// produce a single confident-looking figure whose error bars had been thrown away at the moment they
/// could still have been narrowed - and narrowing them is the plan: a <c>diagnose</c> reading gives a
/// known hitpoint band, and kill-total arithmetic across a whole fight constrains the sum of its
/// blows. Both work on stored ranges and neither can recover a midpoint.</para>
/// </summary>
public readonly record struct BracketProfile(int Samples, double SumLow, double SumHigh, double MaxHigh)
{
    public static readonly BracketProfile Empty = new(0, 0, 0, 0);

    public bool HasSamples => Samples > 0;

    public double AverageLow => Samples == 0 ? 0.0 : SumLow / Samples;
    public double AverageHigh => Samples == 0 ? 0.0 : SumHigh / Samples;

    /// <summary>The highest UPPER bound seen. Deliberately the pessimistic end of the worst bracket:
    /// "the most this could have been" is the honest reading of a range, and it is also the one that
    /// stays true if the ranges are later narrowed from above.</summary>
    public double Max => MaxHigh;

    public BracketProfile Add(double low, double high)
        => new(Samples + 1, SumLow + low, SumHigh + high, high > MaxHigh ? high : MaxHigh);
}

/// <summary>Both sides of the exchange with one creature. Kept together because every question worth
/// asking about a fight is a ratio of the two.</summary>
/// <param name="IncomingArmedAsNow">The incoming history narrowed to blows this creature's SPECIES
/// landed while holding the weapon it is holding right now. Empty when the creature's weapon is
/// unknown, or when too few blows have been seen through that particular weapon to say anything -
/// callers must fall back to <paramref name="Incoming"/> rather than read an empty profile as
/// "harmless". Instance is deliberately not a scope here: splitting an already-thin sample by weapon
/// as well as by instance leaves nothing to average.</param>
public readonly record struct OpponentDamage(
    DamageProfile Incoming, BracketProfile Outgoing, DamageProfile IncomingArmedAsNow)
{
    public static readonly OpponentDamage Empty =
        new(DamageProfile.Empty, BracketProfile.Empty, DamageProfile.Empty);

    /// <summary>The sharpest incoming answer available: the weapon-specific profile when it has
    /// enough blows behind it, the species-wide one otherwise.</summary>
    public DamageProfile BestIncoming
        => IncomingArmedAsNow.Samples >= MinimumIncomingSamples ? IncomingArmedAsNow : Incoming;

    /// <summary>Mirrors <see cref="SwingDamageIndex.MinimumSamples"/>; duplicated here only so this
    /// value type does not have to reach into the index to answer its own question.</summary>
    public const int MinimumIncomingSamples = 3;
}

/// <summary>
/// The accumulated "how hard does this thing hit, and how hard do I hit it" record, keyed by creature.
/// An in-memory CACHE in front of the <c>swings</c> table, not a store: it is warmed by a handful of
/// GROUP BY queries and thereafter updated incrementally, so the live rail never touches SQL.
///
/// <para>That split is Invariant #1. The rail's per-participant facts are assembled on the UI thread,
/// and a query there - however fast - is I/O on the typing hot path. Warming happens off-thread; the
/// UI thread only ever probes a dictionary.</para>
///
/// <para><b>Instance and group are both kept</b>, for the reason CombatHistoryContext already records
/// for the fight-level index: difficulty is per-instance (rat0 really is nastier than its siblings)
/// but samples only accumulate per-group. <see cref="Lookup"/> therefore prefers the instance once it
/// has enough blows of its own to be a distribution rather than an anecdote, and borrows the group's
/// numbers until then.</para>
///
/// <para><b>A live encounter can never enter its own baseline.</b> Nothing is folded in until the
/// encounter that produced it has closed - see SwingLedger, which buffers the current encounter's
/// blows and merges them at the close. Without that, the very first stickleback fight would show its
/// own "now" figures back as "ever", at n=1, and the comparison the two rows exist to support would be
/// a comparison of a number with itself. Same guarantee HistoryIndex and CombatHistoryCache establish
/// for the fight-level history, reached the same way: by construction, not by filtering afterwards.</para>
///
/// <para><b>What this deliberately does NOT model.</b> A creature's output is not stationary - MUD2
/// creatures level up within a reset, and can be buffed, debuffed or drunk. A single lifetime average
/// blends all of that. The dimensions needed to separate it are all stored per swing (reset epoch,
/// level, effect flags), so a richer baseline is a query away; this cache is the cheap always-there
/// answer, not the last word. Anything doing real risk assessment should slice the table.</para>
///
/// <para><b>Threading.</b> <see cref="Fold"/> and the load methods run on the session Feed thread or a
/// background warm task; <see cref="Lookup"/> runs on the UI thread on every panel refresh. The lock
/// is only ever held for a dictionary probe or a single insert, never across I/O - the same contract,
/// for the same reason, as FightHistoryStore's own lock.</para>
/// </summary>
public sealed class SwingDamageIndex
{
    /// <summary>Landed blows an instance needs before it describes itself rather than borrowing its
    /// group's numbers. Higher than CombatHistoryContext.InstanceSampleFloor (which counts whole
    /// FIGHTS, where two is already a real signal) because this counts individual swings, and a
    /// handful of blows from one creature says more about the dice than about the creature.</summary>
    public const int InstanceSampleFloor = 8;

    /// <summary>Below this, nothing is shown at all. Two blows are a pair of numbers, not a
    /// distribution, and a "max" drawn from them is just the larger of the two dressed up as a worst
    /// case.</summary>
    public const int MinimumSamples = 3;

    private readonly object _lock = new();
    private readonly Dictionary<string, DamageProfile> _inByInstance = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DamageProfile> _inByGroup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BracketProfile> _outByInstance = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BracketProfile> _outByGroup = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Incoming blows keyed by species AND the creature's own weapon. The key is assembled by
    /// <see cref="GroupWeaponKey"/>, whose shape must match v_incoming_by_group_weapon's - the live
    /// fold and the warm query write into the same dictionary and a disagreement would show up as
    /// history that silently resets every restart.</summary>
    private readonly Dictionary<string, DamageProfile> _inByGroupWeapon = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Group and weapon joined by a tab, which cannot occur inside a MUD2 object name.
    ///
    /// <para><b>The weapon has its instance digits stripped</b>, which is the whole reason this is a
    /// method rather than a concatenation. MUD2 numbers object instances the same way it numbers
    /// creatures - across the live database's own arming lines, 24 of 41 distinct weapon names carry a
    /// trailing digit and 8 base names appear BOTH ways (<c>axe</c>/<c>axe0</c>,
    /// <c>fork</c>/<c>fork1</c>, <c>staff</c>/<c>staff0</c>), with multi-digit forms
    /// (<c>unlit brand40</c>) and digits attached to the last word of a multi-word name
    /// (<c>glass bottle0</c>). Keyed raw, "zombies with an axe" splits into as many buckets as there
    /// are axes in the world, none of them reaching <see cref="MinimumSamples"/> - and that would
    /// reintroduce per-instance scope through the back door, which
    /// <see cref="OpponentDamage.IncomingArmedAsNow"/> explicitly says it does not want.</para>
    ///
    /// <para><b>The SQL view must strip the same digits, and does</b> -
    /// <c>lower(rtrim(npc_weapon, '0123456789'))</c> in v_incoming_by_group_weapon. The lower() is not
    /// cosmetic: SQLite's GROUP BY is BINARY-collated where this dictionary is OrdinalIgnoreCase, so
    /// two rows differing only in case would arrive as separate profiles and the second would
    /// OVERWRITE the first rather than merge with it. Change either side and you must change both.</para>
    /// </summary>
    public static string GroupWeaponKey(string group, string weapon)
        => group + "\t" + StripInstanceDigits(weapon);

    /// <summary>Removes a trailing run of ASCII digits - <c>axe0</c> to <c>axe</c>,
    /// <c>unlit brand40</c> to <c>unlit brand</c>. Trailing only: a digit anywhere else is part of the
    /// name.</summary>
    public static string StripInstanceDigits(string name)
    {
        var end = name.Length;
        while (end > 0 && char.IsAsciiDigit(name[end - 1]))
            end--;
        // All digits, or nothing to strip: hand back what came in rather than an empty key.
        return end == 0 || end == name.Length ? name : name[..end];
    }

    /// <summary>Folds in one blow the player TOOK from <paramref name="npcName"/>. The group key is
    /// derived here rather than passed in, so a caller cannot key the two halves inconsistently -
    /// NpcGroups.Normalize is also what reduce_combat.py applies, which is what keeps the live and
    /// offline halves of the pipeline bucketing identically.</summary>
    public void FoldIncoming(string? npcName, double damage, string? npcWeapon = null)
    {
        if (string.IsNullOrWhiteSpace(npcName) || damage < 0)
            return;
        lock (_lock)
            FoldIncomingLocked(npcName, damage, npcWeapon);
    }

    /// <summary>Folds in one blow the player LANDED on <paramref name="npcName"/>, as the bracket the
    /// game reported.</summary>
    public void FoldOutgoing(string? npcName, double low, double high)
    {
        if (string.IsNullOrWhiteSpace(npcName) || low < 0 || high < low)
            return;
        lock (_lock)
            FoldOutgoingLocked(npcName, low, high);
    }

    /// <summary>Folds in a whole encounter under one lock acquisition. Taking the lock per blow would
    /// be a few thousand acquisitions across a long session for no gain.</summary>
    public void FoldAll(
        IEnumerable<(string NpcName, string? NpcWeapon, double Damage)> taken,
        IEnumerable<(string NpcName, double Low, double High)> dealt)
    {
        lock (_lock)
        {
            foreach (var (npcName, npcWeapon, damage) in taken)
            {
                if (!string.IsNullOrWhiteSpace(npcName) && damage >= 0)
                    FoldIncomingLocked(npcName, damage, npcWeapon);
            }
            foreach (var (npcName, low, high) in dealt)
            {
                if (!string.IsNullOrWhiteSpace(npcName) && low >= 0 && high >= low)
                    FoldOutgoingLocked(npcName, low, high);
            }
        }
    }

    /// <summary>Replaces everything with pre-aggregated profiles, as produced by the database's own
    /// GROUP BY. Assignment rather than accumulation so a re-warm can never double-count, and
    /// pre-aggregated rather than row-by-row because pushing the arithmetic into SQL is the entire
    /// reason the corpus can grow without the warm-up cost growing with it.</summary>
    public void LoadProfiles(
        IEnumerable<(string Name, DamageProfile Profile)> incomingByInstance,
        IEnumerable<(string Name, DamageProfile Profile)> incomingByGroup,
        IEnumerable<(string Name, BracketProfile Profile)> outgoingByInstance,
        IEnumerable<(string Name, BracketProfile Profile)> outgoingByGroup,
        IEnumerable<(string Name, DamageProfile Profile)>? incomingByGroupWeapon = null)
    {
        lock (_lock)
        {
            _inByInstance.Clear();
            _inByGroup.Clear();
            _outByInstance.Clear();
            _outByGroup.Clear();
            _inByGroupWeapon.Clear();

            foreach (var (name, profile) in incomingByInstance)
                _inByInstance[name] = profile;
            foreach (var (name, profile) in incomingByGroup)
                _inByGroup[name] = profile;
            foreach (var (name, profile) in incomingByGroupWeapon ?? [])
                _inByGroupWeapon[name] = profile;
            foreach (var (name, profile) in outgoingByInstance)
                _outByInstance[name] = profile;
            foreach (var (name, profile) in outgoingByGroup)
                _outByGroup[name] = profile;
        }
    }

    /// <summary>Both sides of the exchange with this creature, historically. Each direction resolves
    /// its instance-vs-group preference independently: they accumulate at different rates (the player
    /// swings at things that never land a blow, and vice versa), so forcing one direction's scope onto
    /// the other would discard the better sample for no reason.</summary>
    public OpponentDamage Lookup(string? npcName, string? npcWeapon = null)
    {
        if (string.IsNullOrWhiteSpace(npcName))
            return OpponentDamage.Empty;

        var group = NpcGroups.Normalize(npcName);
        lock (_lock)
        {
            var armed = string.IsNullOrWhiteSpace(npcWeapon)
                ? DamageProfile.Empty
                : Get(_inByGroupWeapon, GroupWeaponKey(group, npcWeapon), DamageProfile.Empty);

            return new OpponentDamage(
                Resolve(_inByInstance, _inByGroup, npcName, group, DamageProfile.Empty, p => p.Samples),
                Resolve(_outByInstance, _outByGroup, npcName, group, BracketProfile.Empty, p => p.Samples),
                armed.Samples >= MinimumSamples ? armed : DamageProfile.Empty);
        }
    }

    private void FoldIncomingLocked(string npcName, double damage, string? npcWeapon)
    {
        var group = NpcGroups.Normalize(npcName);
        _inByInstance[npcName] = Get(_inByInstance, npcName, DamageProfile.Empty).Add(damage);
        _inByGroup[group] = Get(_inByGroup, group, DamageProfile.Empty).Add(damage);

        if (!string.IsNullOrWhiteSpace(npcWeapon))
        {
            var key = GroupWeaponKey(group, npcWeapon);
            _inByGroupWeapon[key] = Get(_inByGroupWeapon, key, DamageProfile.Empty).Add(damage);
        }
    }

    private void FoldOutgoingLocked(string npcName, double low, double high)
    {
        var group = NpcGroups.Normalize(npcName);
        _outByInstance[npcName] = Get(_outByInstance, npcName, BracketProfile.Empty).Add(low, high);
        _outByGroup[group] = Get(_outByGroup, group, BracketProfile.Empty).Add(low, high);
    }

    /// <summary>The instance-or-group choice, shared by both directions so the rule lives once.</summary>
    private static TProfile Resolve<TProfile>(
        Dictionary<string, TProfile> byInstance, Dictionary<string, TProfile> byGroup,
        string npcName, string group, TProfile empty, Func<TProfile, int> samples)
    {
        var instance = Get(byInstance, npcName, empty);
        if (samples(instance) >= InstanceSampleFloor)
            return instance;

        var groupProfile = Get(byGroup, group, empty);
        // The group is the fallback, but not automatically the better answer: a lone creature with no
        // siblings has an identical instance and group profile, and an instance that had somehow
        // outgrown its group should not be discarded in favour of a smaller sample.
        var best = samples(groupProfile) >= samples(instance) ? groupProfile : instance;
        return samples(best) >= MinimumSamples ? best : empty;
    }

    private static TProfile Get<TProfile>(Dictionary<string, TProfile> map, string key, TProfile empty)
        => map.TryGetValue(key, out var profile) ? profile : empty;
}
