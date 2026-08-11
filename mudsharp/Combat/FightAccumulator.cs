namespace MudSharp.Combat;

/// <summary>How a single per-NPC fight ended. Mirrors combat_fights.outcome in
/// tools/combat/schema.sql so live and offline rows are directly comparable.</summary>
public enum FightOutcome
{
    /// <summary>Still going (or the encounter ended without ever resolving this one).</summary>
    Unresolved,
    Killed,
    KilledByNpc,
    NpcFled,
    YouFled,
    Withdrawn,
}

/// <summary>
/// One swing's outcome, for the clog window's recent-hits strip: a landed blow's damage
/// magnitude, or a miss. Deliberately NOT nullable-double - a null "hit" and a "miss" read the
/// same to a caller that forgets to check, and the two are different information (a miss tells
/// you the swing rhythm, a null tells you nothing was observed).
/// </summary>
public readonly record struct SwingOutcome(bool IsHit, double Damage)
{
    public static readonly SwingOutcome Miss = new(false, 0);
    public static SwingOutcome Hit(double damage) => new(true, damage);
}

/// <summary>
/// Accumulates one NPC's fight within an encounter: the counters, the weapon actually used, and
/// how it ended.
///
/// <para>Exists because <see cref="CombatEvent"/> names its NPC on every kind that has one, but
/// nothing was bucketing by it — encounter-wide totals cannot answer "how did this rat fight
/// compare to previous rat fights" when a goat was also in the room. The offline pipeline already
/// models this split (combat_sessions holding N combat_fights); this is the live half.</para>
///
/// <para>Pure and thread-agnostic: one instance is driven from the UI thread for display, another
/// from the session Feed thread for history persistence. They never share state — see
/// CombatStatsAggregator and FightHistoryRecorder respectively.</para>
/// </summary>
public sealed class FightAccumulator
{
    public FightAccumulator(
        string npcName, DateTime startedUtc, string? weaponAtStart,
        int? staminaAtStart = null, int? scoreAtStart = null)
    {
        NpcName = npcName;
        NpcGroup = NpcGroups.Normalize(npcName);
        StartedUtc = startedUtc;
        WeaponUsed = weaponAtStart;
        // Seed the min/end trackers from whatever the caller already knew at the instant this NPC
        // joined the fight (FightHistoryRecorder passes its running "last known" stamina/score) -
        // without this, a one-sided kill that never triggers an inline "(cur/max)" stamina line or
        // a FES heartbeat before resolving would leave MinStamina/StaminaAtEnd/ScoreAtStart null
        // despite the value being perfectly knowable. NoteStamina/NoteScore then refine these as
        // real readings arrive over the fight's lifetime.
        if (staminaAtStart is int sta) { MinStamina = sta; StaminaAtEnd = sta; }
        ScoreAtStart = scoreAtStart;
    }

    public string NpcName { get; }
    public string NpcGroup { get; }
    public DateTime StartedUtc { get; }
    public DateTime? EndedUtc { get; private set; }
    public FightOutcome Outcome { get; private set; } = FightOutcome.Unresolved;

    /// <summary>The weapon in use for THIS fight. Seeded from the encounter's current weapon at
    /// fight start rather than left null, because MUD2 does not re-arm you for a second
    /// attacker: a weapon equipped for fight A silently extends to fight B when B joins
    /// mid-encounter, and there is no equip line for B. reduce_combat.py does the same.</summary>
    public string? WeaponUsed { get; private set; }

    /// <summary>The NPC's own weapon, once it has equipped one - e.g. a zombie that picked up a
    /// fork mid-fight. Distinct from <see cref="WeaponUsed"/> (the PLAYER's weapon for this fight):
    /// NPCs arm themselves independently and it can materially change their damage output, so this
    /// needs its own field rather than overloading the player's. Null is the common case - most
    /// NPCs fight with fists/claws/bite and never announce a weapon at all.</summary>
    public string? NpcWeapon { get; private set; }

    /// <summary>UTC time the NPC's own weapon was last confirmed via <see cref="NoteNpcWeapon"/>  -
    /// i.e. when the "The X has started to use the Y to fight!" line landed. Drives the "why" line's
    /// priority-5 rule and the panel's E2 weapon-pickup alert (DESIGN_FINAL.md 3.8/4.3): both need
    /// "how long ago did this NPC arm itself", not just "is it armed".</summary>
    public DateTime? NpcWeaponEquippedUtc { get; private set; }

    /// <summary>The NPC's health rung as last reported by the game, 1 (about to die) to 7 (unhurt), or
    /// null while nothing has been reported yet. See <see cref="NpcHealthRungs"/>.
    ///
    /// <para>Latest reading, never the worst seen: creatures regenerate, and the corpus has a zombie
    /// oscillating between "strong" and "superficially damaged" four times in one fight. Latching to
    /// the worst would keep telling the player a target was nearly dead after it had healed - the exact
    /// "one more hit will do it" gamble that costs characters.</para></summary>
    public int? HealthRung { get; private set; }

    /// <summary>The descriptor as the game worded it ("covered in wounds"), so the panel can echo the
    /// player's own scroll rather than paraphrase it. Null until first reported.</summary>
    public string? HealthPhrase { get; private set; }

    /// <summary>When the health reading landed. The ladder only updates on a landed blow and the
    /// player's hit rate is 0.57, so age is what separates "this is current" from "this is what it
    /// looked like four swings ago" - and an unknown reading must never be drawn as a measured
    /// one.</summary>
    public DateTime? HealthReadUtc { get; private set; }

    public int YouHits { get; private set; }
    public int YouMisses { get; private set; }
    public int TheyHits { get; private set; }
    public int TheyMisses { get; private set; }
    public double ApproxDamageDone { get; private set; }
    public double ApproxDamageTaken { get; private set; }

    /// <summary>Lowest player stamina observed while this fight was open - "how close did I come
    /// to dying" fighting THIS npc specifically. Null only when no reading was ever available (no
    /// FES heartbeat and no inline "(cur/max)" line landed before the fight resolved).</summary>
    public int? MinStamina { get; private set; }

    /// <summary>Player stamina as of the last reading observed WHILE this fight was still open -
    /// the honest "stamina at end of fight" figure. Deliberately NOT re-probed after resolution
    /// (same non-fabrication rule FightRecord's remarks already apply to room/weather/stats: we do
    /// not chase a fresher value once the fight we are attributing it to has already closed).</summary>
    public int? StaminaAtEnd { get; private set; }

    /// <summary>Player score at the instant this fight began (seeded once at construction, never
    /// revised) - the baseline the flee-economics work (DESIGN_FINAL.md 5.6) will diff against.</summary>
    public int? ScoreAtStart { get; private set; }

    /// <summary>Player score as of the last reading observed while this fight was still open. Same
    /// "no re-probe after close" honesty rule as <see cref="StaminaAtEnd"/>.</summary>
    public int? ScoreAtEnd { get; private set; }

    /// <summary>How many of each side's most recent swings the clog window's recent-hits strip
    /// shows. A fixed-size ring, not a growing list: one fight can run to hundreds of swings and
    /// the display only ever wants the last handful, so unbounded growth would be pure churn on a
    /// path (AddYouHit/AddTheyHit/etc) that runs on every combat line (Invariant #1).</summary>
    public const int RecentSwingCapacity = 6;

    private readonly SwingOutcome[] _yourRecent = new SwingOutcome[RecentSwingCapacity];
    private readonly SwingOutcome[] _theirRecent = new SwingOutcome[RecentSwingCapacity];
    private int _yourRecentHead;
    private int _yourRecentCount;
    private int _theirRecentHead;
    private int _theirRecentCount;

    public bool IsResolved => Outcome != FightOutcome.Unresolved;

    public TimeSpan DurationAt(DateTime nowUtc)
    {
        var end = EndedUtc ?? nowUtc;
        var duration = end - StartedUtc;
        return duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
    }

    public void NoteWeapon(string? weapon)
    {
        if (!string.IsNullOrWhiteSpace(weapon))
            WeaponUsed = weapon;
    }

    /// <summary>
    /// True once the weapon left the player's hands during this fight - broken, refused, or dropped.
    /// Records that it happened WITHOUT erasing what the fight was fought with.
    ///
    /// <para>This used to null <see cref="WeaponUsed"/>, which destroyed the one durable fact the
    /// fight had to offer. MUD2 auto-drops your weapon when you flee and prints the drop in the same
    /// tick, immediately BEFORE the flee line - so an 83-second fight, armed with an axe0 throughout
    /// and 7 hits into it, was written to history as having been fought bare-handed. That silently
    /// poisons <c>FightHistory.SummarizeByWeapon</c>, which is the table the alternate-weapon offer
    /// and the whole weapon-vs-creature comparison are built on: the weapon gets no credit for its
    /// own fight, and the unarmed bucket gets a fight it never had.</para>
    ///
    /// <para>The LIVE "what is in my hands right now" answer is not this field's job - that is the
    /// encounter-level current weapon, which is cleared as it always was.</para>
    /// </summary>
    public bool WasDisarmed { get; private set; }

    public void NoteDisarmed() => WasDisarmed = true;

    /// <summary>Failed flee attempts by this creature - it tried to run and could not. Distinct from
    /// the fight ending in a flee, which is an outcome; this is a creature that is still standing in
    /// front of you. Water snakes attempt this repeatedly (7 times in 13 seconds in one captured
    /// fight) and almost never get away.</summary>
    public int FleeAttempts { get; private set; }

    public void NoteFleeAttempt() => FleeAttempts++;

    /// <summary>Records a health-descriptor reading for this NPC. Always overwrites - see
    /// <see cref="HealthRung"/> on why the latest reading wins over the worst.</summary>
    public void NoteHealth(int rung, string? phrase, DateTime timestampUtc)
    {
        HealthRung = rung;
        HealthPhrase = phrase;
        HealthReadUtc = timestampUtc;
    }

    /// <summary>Folds in one more player-stamina reading. Callers broadcast this to every
    /// UNRESOLVED fight on every stats update (mirroring the existing WeaponEquip broadcast) -
    /// stamina is a player-scoped stat, not per-NPC, so every concurrent pack-fight row shares the
    /// same readings. A no-op once the fight has resolved simply by the caller no longer calling
    /// it (see FightHistoryRecorder.OnStatsUpdated), which is what freezes StaminaAtEnd/MinStamina
    /// at "last known while still open" rather than drifting into post-fight regen.</summary>
    public void NoteStamina(int? stamina)
    {
        if (stamina is not int value)
            return;
        StaminaAtEnd = value;
        MinStamina = MinStamina is null ? value : Math.Min(MinStamina.Value, value);
    }

    /// <summary>Folds in one more player-score reading. Same broadcast/freeze contract as
    /// <see cref="NoteStamina"/>; ScoreAtStart is deliberately untouched here - it is seeded once at
    /// construction and never revised.</summary>
    public void NoteScore(int? score)
    {
        if (score is int value)
            ScoreAtEnd = value;
    }

    /// <summary>Records the NPC's own weapon once a "The X has started to use the Y to fight!"
    /// line confirms one. Never cleared by <see cref="NoteDisarmed"/> - that line is about the
    /// PLAYER'S weapon breaking, and MUD2 gives no equivalent "NPC weapon broke" line to react to,
    /// so the last-known NPC weapon is the honest thing to keep showing.</summary>
    public void NoteNpcWeapon(string? weapon, DateTime timestampUtc)
    {
        if (!string.IsNullOrWhiteSpace(weapon))
        {
            NpcWeapon = weapon;
            NpcWeaponEquippedUtc = timestampUtc;
        }
    }

    public void AddYouHit(int? rangeLow, int? rangeHigh)
    {
        YouHits++;
        if (rangeLow is int low && rangeHigh is int high)
        {
            var midpoint = (low + high) / 2.0;
            ApproxDamageDone += midpoint;
            RecordSwing(_yourRecent, ref _yourRecentHead, ref _yourRecentCount, SwingOutcome.Hit(midpoint));
        }
        // No range means no parsed swing detail (narrative mode) - nothing to put in the ring
        // buffer either, since there is no magnitude to show and a placeholder would be a guess.
    }

    public void AddYouMiss()
    {
        YouMisses++;
        RecordSwing(_yourRecent, ref _yourRecentHead, ref _yourRecentCount, SwingOutcome.Miss);
    }

    /// <summary>Records an incoming hit. <paramref name="damage"/> is the already-resolved stamina
    /// delta for this blow (the caller owns baseline tracking — see
    /// CombatStatsAggregator.ObserveDamageTaken for why the baseline cannot simply be read off the
    /// hit line itself), or null when it could not be determined.</summary>
    public void AddTheyHit(double? damage)
    {
        TheyHits++;
        if (damage is double value && value > 0)
            ApproxDamageTaken += value;

        // The ring buffer records the swing whenever a magnitude was resolved at all, even a zero
        // delta (armour soaking a blow is still a landed hit) - only a genuinely unresolvable
        // baseline (damage null) is skipped, since there is nothing honest to show for it.
        if (damage is double resolved)
            RecordSwing(_theirRecent, ref _theirRecentHead, ref _theirRecentCount, SwingOutcome.Hit(Math.Max(resolved, 0)));
    }

    public void AddTheyMiss()
    {
        TheyMisses++;
        RecordSwing(_theirRecent, ref _theirRecentHead, ref _theirRecentCount, SwingOutcome.Miss);
    }

    /// <summary>Oldest-to-newest snapshot of the player's last <see cref="RecentSwingCapacity"/>
    /// swings against this NPC, so the clog window reads it left-to-right as a timeline.</summary>
    public IReadOnlyList<SwingOutcome> RecentYourSwings
        => OrderedRingCopy(_yourRecent, _yourRecentHead, _yourRecentCount);

    /// <summary>Oldest-to-newest snapshot of this NPC's last <see cref="RecentSwingCapacity"/>
    /// swings against the player.</summary>
    public IReadOnlyList<SwingOutcome> RecentTheirSwings
        => OrderedRingCopy(_theirRecent, _theirRecentHead, _theirRecentCount);

    /// <summary>Writes into a fixed-capacity ring: <paramref name="head"/> is the next write index,
    /// wrapping at capacity, and <paramref name="count"/> saturates at capacity once the ring has
    /// filled at least once (it never needs to count past that).</summary>
    private static void RecordSwing(SwingOutcome[] ring, ref int head, ref int count, SwingOutcome outcome)
    {
        ring[head] = outcome;
        head = (head + 1) % ring.Length;
        if (count < ring.Length)
            count++;
    }

    /// <summary>Copies a ring buffer out in chronological (oldest-first) order. While the ring has
    /// not yet filled, the oldest entry is always index 0 (writes started there and have not
    /// wrapped); once full, <paramref name="head"/> itself points at the oldest entry, because that
    /// is exactly the slot the NEXT write is about to overwrite.</summary>
    private static SwingOutcome[] OrderedRingCopy(SwingOutcome[] ring, int head, int count)
    {
        if (count == 0)
            return Array.Empty<SwingOutcome>();

        var result = new SwingOutcome[count];
        var oldest = count < ring.Length ? 0 : head;
        for (var i = 0; i < count; i++)
            result[i] = ring[(oldest + i) % ring.Length];
        return result;
    }

    /// <summary>First resolution wins: a Kill followed by a trailing FightEndOther, or a player
    /// death that also force-closes the encounter, must not overwrite the real outcome.</summary>
    public void Resolve(FightOutcome outcome, DateTime endedUtc)
    {
        if (IsResolved || outcome == FightOutcome.Unresolved)
            return;
        Outcome = outcome;
        EndedUtc = endedUtc;
    }
}
