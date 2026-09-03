namespace MudSharp.Combat;

/// <summary>One blow as MUD2 reported it: a bracket, never a number. An <c>identify</c>-on blow
/// arrives with <c>Low == High</c>, which needs no special case anywhere below.</summary>
public readonly record struct DamageBracket(double Low, double High)
{
    public static readonly DamageBracket Zero = new(0, 0);

    public DamageBracket Plus(DamageBracket other) => new(Low + other.Low, High + other.High);
}

/// <summary>
/// A health-descriptor reading, tied to the number of the player's own landed blows that preceded it
/// in this fight.
/// </summary>
/// <param name="Rung">1 (close to death) to <see cref="NpcHealthRungs.Rungs"/> (fit/strong).</param>
/// <param name="LandedBlowsBefore">How many of the player's blows had landed when this reading was
/// printed. The reading must be the one AFTER the blow, which is how MUD2 prints it - the descriptor
/// follows the hit line. A reading attributed to too many blows inflates the lower bound, which is
/// the direction that invents pool, so callers that cannot tell must under-count rather than
/// over-count.</param>
public readonly record struct RungReading(int Rung, int LandedBlowsBefore);

/// <summary>
/// One fight against one creature, reduced to exactly what constrains its stamina pool.
/// </summary>
/// <param name="NpcName">The instance name as the game gave it ("large rat0"), which is what the
/// re-engagement filter keys on.</param>
/// <param name="StartedAtMs">Unix ms, for the re-engagement filter.</param>
/// <param name="EndedAtMs">Unix ms, for the re-engagement filter.</param>
/// <param name="EndedInKill">Whether the player's own blow finished it. Deaths from other causes
/// (poison) do NOT count: the damage that finished those was never on the wire, so their totals
/// bound nothing.</param>
/// <param name="Blows">The player's LANDED blows, in order. Misses are not here - they constrain
/// nothing.</param>
/// <param name="Rungs">Health-descriptor readings observed during the fight.</param>
/// <param name="EncounterStartedAtMs">The encounter this fight belonged to, or null when unknown.</param>
/// <param name="PlayerState">What the player's own state looked like across this fight. The
/// discriminator between chasing one creature and coming back later to re-attempt it - see
/// <see cref="ChaseLinkPolicy"/>.</param>
public sealed record PoolFightObservation(
    string NpcName,
    long StartedAtMs,
    long EndedAtMs,
    bool EndedInKill,
    IReadOnlyList<DamageBracket> Blows,
    IReadOnlyList<RungReading> Rungs,
    long? EncounterStartedAtMs = null,
    PlayerFightState PlayerState = default);

/// <summary>
/// The player's own state across one fight, as the linkage test needs it.
/// </summary>
/// <param name="StaminaAtStart">Stamina on the fight's first swing.</param>
/// <param name="StaminaAtEnd">Stamina on its last.</param>
/// <param name="StaminaMaxAtEnd">Maximum on its last. A change between fights is a dreamword.</param>
/// <param name="Weapon">What was in hand, from the last outgoing swing that named one.</param>
public readonly record struct PlayerFightState(
    int? StaminaAtStart = null,
    int? StaminaAtEnd = null,
    int? StaminaMaxAtEnd = null,
    string? Weapon = null);

/// <summary>What kind of number the estimate is a number for.</summary>
public enum PoolQuantity
{
    /// <summary>The creature's stamina pool.</summary>
    StaminaPool,

    /// <summary>
    /// Damage required to kill from full, which for a REGENERATING creature exceeds its pool and is
    /// not a measurement of it.
    ///
    /// <para>Zombies are the measured case: median kill damage runs 46-48 in fights under 30 seconds
    /// and 65-92 in fights past 60. Both figures are real and neither is the pool - the longer fight
    /// simply had to out-damage the healing. Labelling it rather than silently reporting the larger
    /// number as a pool is the whole point of this enum.</para>
    /// </summary>
    DamageToKill,
}

/// <summary>How much the evidence supports.</summary>
public enum PoolEvidence
{
    /// <summary>Nothing on file. Not "zero" - render "--".</summary>
    None,

    /// <summary>Fights on file, but none of them a kill, so the quantity is bounded from below and
    /// not from above. <see cref="StaminaPoolEstimate.Interval"/>'s <c>AtMost</c> is null.</summary>
    LowerBoundOnly,

    /// <summary>A two-sided band, supported by a single run of agreeing fights.</summary>
    Band,

    /// <summary>
    /// Two or more DISJOINT bands are equally well supported, and nothing here can choose between
    /// them. <see cref="StaminaPoolEstimate.Interval"/> is their hull, which contains the truth but
    /// also contains a gap that no fight supports at all;
    /// <see cref="StaminaPoolEstimate.TiedRuns"/> holds the real bands.
    ///
    /// <para><b>A consumer must not draw the hull as an ordinary band.</b> That would claim support
    /// across the gap. Draw the runs separately, or say "two readings disagree" and draw neither -
    /// but do not present a hull spanning 0-110 as evidence that the pool might be 50 when every
    /// fight on file says it is either under 10 or over 100.</para>
    ///
    /// <para>Its own evidence state rather than a flag beside <see cref="Band"/>, so that any
    /// <c>switch</c> that words this estimate has to name the case. A boolean is something a caller
    /// can forget to read; a new enum member is something the compiler asks about.</para>
    /// </summary>
    Split,
}

/// <summary>
/// What is known about one pool key's stamina, and how well it is known.
/// </summary>
/// <param name="PoolKey">From <see cref="NpcPoolKey.For"/>.</param>
/// <param name="Quantity">Whether this is the pool or damage-to-kill - see <see cref="PoolQuantity"/>.</param>
/// <param name="Evidence">Whether there is a band, only a floor, or nothing.</param>
/// <param name="Interval">The band. <see cref="StaminaInterval.Unbounded"/> when
/// <paramref name="Evidence"/> is <see cref="PoolEvidence.None"/>.</param>
/// <param name="SupportingFights">Fights whose own constraint contains the whole reported band - the
/// depth of the run this came from. This is the number to show beside the figure; it is NOT the same
/// as <paramref name="ContributingFights"/> and is usually smaller, because contamination is real and
/// the estimator does not pretend a fight that contradicts the band voted for it.</param>
/// <param name="ContributingFights">Chains that produced a constraint at all, after linking.</param>
/// <param name="LinkedEngagements">Fights joined onto a predecessor as one continuing chase rather
/// than counted separately - see <see cref="ChaseLinker"/>. Not a loss: the whole chain becomes one
/// observation, and that is what makes a terminal kill usable as a ceiling.</param>
/// <param name="DroppedInFolds">Fights thrown away because several creatures shared one printed name
/// inside a single encounter, so no blow could be attributed to any one of them.</param>
/// <param name="ContradictoryFights">Kills whose rung readings demanded a larger pool than their own
/// kill bracket allows. The rung bounds were dropped for those and the kill bracket kept - see
/// <see cref="BoundFor"/>.</param>
/// <param name="TiedRuns">The disjoint bands that tied for maximum depth, low to high. Exactly one in
/// the ordinary case, and then it equals <paramref name="Interval"/>. More than one means
/// <paramref name="Evidence"/> is <see cref="PoolEvidence.Split"/> and
/// <paramref name="Interval"/> is only their hull. Empty when there is no evidence at all.</param>
public sealed record StaminaPoolEstimate(
    string PoolKey,
    PoolQuantity Quantity,
    PoolEvidence Evidence,
    StaminaInterval Interval,
    int SupportingFights,
    int ContributingFights,
    int LinkedEngagements,
    int DroppedInFolds,
    int ContradictoryFights,
    IReadOnlyList<StaminaInterval> TiedRuns)
{
    public static readonly StaminaPoolEstimate None = new(
        string.Empty, PoolQuantity.StaminaPool, PoolEvidence.None, StaminaInterval.Unbounded,
        0, 0, 0, 0, 0, []);

    public bool HasEvidence => Evidence != PoolEvidence.None;

    /// <summary>Whether <see cref="Interval"/> spans a gap no fight supports - see
    /// <see cref="PoolEvidence.Split"/>. Offered so a caller that only needs "can I draw this as one
    /// band" does not have to enumerate the enum, NOT as a substitute for handling the state.</summary>
    public bool IsSplit => Evidence == PoolEvidence.Split;

    /// <summary>
    /// The single number to use where a caller has already decided it must have one and cannot carry
    /// a band - specifically <see cref="CombatOutlook.Project"/>, whose "seconds to kill" needs a
    /// denominator.
    ///
    /// <para>It is the TOP of the band, not the middle. Overstating how much is left to chew through
    /// makes the fight read as slower to win and therefore closer to lost, which is the safe error in
    /// a permadeath game; the midpoint would split the difference between a measurement and an
    /// assumption. Null with no upper bound, because there is no pessimistic end of a half-line and
    /// substituting the floor would be optimism dressed as data.</para>
    ///
    /// <para>Still sound when <see cref="Evidence"/> is <see cref="PoolEvidence.Split"/>: the truth
    /// lies in one of the tied runs and every run is inside the hull, so the hull's top is still an
    /// upper bound. It is a looser one than usual, which is the correct consequence of the evidence
    /// disagreeing - unlike drawing the hull as a band, which would be a false claim rather than a
    /// weak one.</para>
    /// </summary>
    public double? PessimisticPool => Interval.AtMost;
}

/// <summary>
/// Estimates an NPC's stamina pool from the two things MUD2 actually tells the player about it: that
/// it was still standing after a known amount of damage, and that it stopped being so on a known
/// blow.
///
/// <para><b>What this replaced, and why.</b> The previous estimator took the MEDIAN TOTAL DAMAGE of
/// fights that ended in a kill. Every such sample includes the killing blow's overkill, so it reads
/// high. That much is a matter of arithmetic and is not in doubt; the old figure is not a worse
/// version of this one, it is a different quantity (how much damage a kill costs) wearing this one's
/// name.</para>
///
/// <para><b>What the validation is actually worth - read this before quoting it (audit,
/// 2026-09-03).</b> An earlier version of this comment said the old estimator was "biased high by
/// 21% - measured across 48 instances against an independently published figure", and that this one
/// "scores median 0.0% error and 4.3% mean absolute error, with the published value inside the
/// reported interval for 39 of 41 non-zombie instances". Two problems, both material.</para>
///
/// <para>First, the "independently published figure" is <c>tools/combat/bestiary.tsv</c> - the mobile
/// table transcribed from TheMudWiz's MUD2 Strategy Guide v0.42 on GameFAQs, which
/// <c>verify_mechanics.py</c> loads as its ground truth. <c>MUD2-PUBLISHED-MECHANICS.md:5</c>, the
/// header of the document that data belongs to, says of it: "These are HYPOTHESES, not ground truth.
/// The guide is player-derived and years old." Calling it "independently published" made a
/// player-derived FAQ sound like an authority. It is a second guess, and agreeing with it is
/// corroboration between two estimates, not accuracy against a measurement. There is no measurement
/// to be accurate against: MUD2 never prints a creature's stamina (see the rung item below).</para>
///
/// <para>Second, none of the four numbers is traceable. 21%, 48 instances, median 0.0%, 4.3% MAE and
/// 39-of-41 appear in no script, no document, and no stored result anywhere in the repo - only in
/// this comment and in the ones that cite it. The only stored run of this method is
/// <c>MECHANICS-VERIFICATION.md</c>, whose corpus was "two captures, about 69 wall-clock minutes …
/// 25 fights, 23 kills", which returned INCONCLUSIVE with "22 of 23 kills put the published STA
/// inside that bracket" - a different unit, a different n, and a corpus far too small to contain 48
/// instances. The live corpus today has 106 killed instances (94 non-zombie), which is not 48 or 41
/// either. The one stored figure that does corroborate the DIRECTION is in the same file: group
/// medians "are systematically high for small creatures - rats read 31.2 against a published 25",
/// i.e. 25% high by the overkill mechanism described above.</para>
///
/// <para>So: the mechanism is sound and the replacement is the right shape. The error figures are
/// not evidence and must not be requoted. Recovering them means writing the comparison as a script
/// under <c>tools/combat/</c> and storing its output beside it - the standing rule for findings in
/// this project, and precisely the rule whose absence is why they cannot be checked now.</para>
///
/// <para><b>The two constraint families.</b></para>
/// <list type="number">
/// <item><b>Kill.</b> Alive after blow n-1 and dead after blow n means the pool is more than the sum
/// of the first n-1 blows' LOW ends and at most the sum of all n blows' HIGH ends.</item>
/// <item><b>Rung.</b> Still reading rung k after cumulative damage D means the pool exceeds
/// <c>D x 7 / (8 - k)</c>. D is the LOW end of the cumulative bracket and the reading must be the one
/// printed AFTER the blow.
/// <para><b>Equal sevenths is an ASSUMPTION, not a measurement, and this whole bound rests on it
/// (audit, 2026-09-03).</b> There is no observation anywhere in the corpus mapping a rung to a
/// stamina number or a fraction, and there cannot be one from what MUD2 prints: a rung reading is a
/// bare adjectival phrase with no digit in it ("The zombie9 looks to have minor damage."). Counted:
/// 3,130 distinct rung readings across all 7 rungs in the swing ledger, 4,373 raw descriptor lines in
/// the clogs, and not one of them carries a number. The ONLY numeric NPC-stamina data MUD2 gives is
/// the stethoscope <c>diagnose</c> bracket ("The viper has a stamina lying between 18 and 27."), of
/// which the entire corpus holds FOUR - all pre-combat probes on undamaged creatures, none of them
/// beside a wound descriptor - and <c>npc_stamina_reads</c>, the table that would hold them, has 0
/// rows. The published guide is no help either: <c>MUD2-PUBLISHED-MECHANICS.md:359</c> records that
/// the wound descriptors are absent from it entirely, with "no ordering or percentage mapping between
/// them".</para>
/// <para>What IS measured is the rungs' ORDER - 1,980 transitions to a worse rung against 47 to a
/// better one - which fixes the ladder's sequence and says nothing about its spacing. Equal spacing
/// is the simplest choice consistent with that order, and it is chosen for that reason, not because
/// anything observed it.</para>
/// <para>An earlier version of this comment claimed the assumption "was tested against the
/// alternatives on 1,978 readings: equal sevenths is contradicted by 6.6% … against 9.6% … 17.7% …
/// 25.9%", and "violated in 3 of 199 rung-7 readings". Those figures are untraceable - no script,
/// document or stored result in the repo produces them. And even taken at face value they would not
/// be what they sound like: a "contradiction" here means the rung bound demands a pool larger than
/// the fight's own KILL BRACKET permits, so the test is a relative consistency ranking of four
/// candidate spacings against another estimate, never against a stamina value. It also inherits that
/// estimate's assumptions - that every printed damage band is right, that fight boundaries are
/// segmented correctly, and that the creature started undamaged - and the last of those is the same
/// confound the old comment used to explain the violations away ("all of them pre-damaged unnumbered
/// mobs"), which makes that part unfalsifiable as stated.</para>
/// <para>The practical consequence is already handled and should stay handled: where the rung bound
/// and the kill bracket disagree, the KILL BRACKET WINS and the rung bounds are dropped (see the
/// Contradicted path below). That ordering is right precisely because the kill bracket has no model
/// in it and this bound has one.</para></item>
/// </list>
///
/// <para><b>The rung's implied UPPER bound is deliberately not used.</b> Rung k also says the
/// remaining fraction is at most k/7, i.e. <c>pool &lt;= 7D/(7-k)</c>, and that bound is available
/// for free. It is left out because it is the constraint most exposed to the dominant contaminant: a
/// creature that was already hurt when the fight opened has taken more damage than the client saw, so
/// every D understates, and an upper bound built from an understated D is too low. The kill bracket
/// has no model in it at all and is the only thing here allowed to close the band from above. The
/// same rung arithmetic IS used against a live fight, where the pool band is the input rather than
/// the output - see <see cref="NpcRemainingStamina"/>.</para>
///
/// <para><b>Why the intersection is by maximum depth rather than by AND.</b> Contamination is
/// one-sided in both directions at once: a pre-damaged creature drags its fight's upper bound down, a
/// regenerating one drags its lower bound up. Either alone makes one fight disjoint from the rest,
/// and a hard intersection of everything is then empty - measured empty for 15 of 23 species groups.
/// Taking the interval covered by the most fights keeps the answer and reports how many fights agreed
/// with it.</para>
///
/// <para><b>Re-engagement is LINKED, not excluded.</b> Among kills that read as pre-damaged the
/// probability of a prior fight against the same name inside two minutes is 0.80 against a 0.05
/// baseline, so it is the mechanism rather than a subtle confound. It used to be handled by dropping
/// every later link and keeping the first, which cost the terminal kill: the opening fight is a floor
/// only, and the short fight that finished the creature carries the only two-sided constraint there is.
/// <see cref="ChaseLinker"/> joins the chain into one observation against one pool instead, which is
/// sound because the chain IS one creature - chain totals reproduce isolated kill totals within a few
/// points across eight species, and the rung does not move across a disengagement under ten
/// seconds.</para>
///
/// <para><b>Nothing is decayed.</b> The process is stationary across 2026-08-14 to 2026-08-30 on every
/// well-sampled species, so old observations are worth exactly as much as new ones and weighting them
/// down would just shrink the sample.</para>
///
/// <para>Pure and primitive-typed, so mudsharp.Tests exercises the arithmetic directly.</para>
/// </summary>
public static class StaminaPoolEstimator
{
    /// <summary>
    /// Species words whose creatures regenerate fast enough that "damage required to kill" and
    /// "stamina pool" are measurably different numbers, so their estimate is labelled
    /// <see cref="PoolQuantity.DamageToKill"/>.
    ///
    /// <para>One member, and it is the one that was measured: zombie kill damage has a median of 46-48
    /// under 30 seconds and 65-92 past 60. <b>This list is known to be incomplete</b> - MUD2 has no
    /// reason to confine regeneration to the undead, and nothing in the client detects it. Adding a
    /// species here requires the same evidence: kill totals that grow with fight duration for that
    /// species. Do not add one on the grounds that it "should" heal.</para>
    ///
    /// <para>Matched on the SPECIES WORD (see <see cref="NpcPoolKey.SpeciesWordOf"/>) rather than the
    /// whole key, so a variant nobody has fought yet ("large zombie") inherits it. Regeneration is a
    /// property of the animal, not of its size.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> RegeneratingSpecies =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "zombie" };

    /// <summary>Whether this pool key's estimate measures damage-to-kill rather than the pool.</summary>
    public static PoolQuantity QuantityFor(string poolKey)
        => RegeneratingSpecies.Contains(NpcPoolKey.SpeciesWordOf(poolKey))
            ? PoolQuantity.DamageToKill
            : PoolQuantity.StaminaPool;

    /// <summary>
    /// The whole estimate for one pool key, from every observation of it.
    ///
    /// <para><paramref name="observations"/> need not be sorted; the re-engagement filter sorts its
    /// own copy. Observations whose <see cref="PoolFightObservation.NpcName"/> maps to a different
    /// pool key are NOT filtered out here - the caller owns bucketing, and silently dropping rows
    /// would hide a mis-keyed feed.</para>
    /// </summary>
    public static StaminaPoolEstimate Estimate(string poolKey, IEnumerable<PoolFightObservation> observations)
    {
        var quantity = QuantityFor(poolKey);
        var (kept, linked, droppedInFolds) = ChaseLinker.Link(observations);

        var bounds = new List<StaminaInterval>(kept.Count);
        var contradictions = 0;
        foreach (var observation in kept)
        {
            var (bound, contradicted) = BoundFor(observation);
            if (contradicted)
                contradictions++;
            if (bound is StaminaInterval constraint)
                bounds.Add(constraint);
        }

        if (bounds.Count == 0)
        {
            return StaminaPoolEstimate.None with
            {
                PoolKey = poolKey,
                Quantity = quantity,
                ContributingFights = 0,
                LinkedEngagements = linked,
                DroppedInFolds = droppedInFolds,
                ContradictoryFights = contradictions,
            };
        }

        var (interval, depth, runs) = MaxDepthRun(bounds);

        var evidence = runs.Count > 1
            ? PoolEvidence.Split
            : interval.AtMost is null ? PoolEvidence.LowerBoundOnly : PoolEvidence.Band;

        return new StaminaPoolEstimate(
            poolKey,
            quantity,
            evidence,
            interval,
            SupportingFights: depth,
            ContributingFights: bounds.Count,
            LinkedEngagements: linked,
            DroppedInFolds: droppedInFolds,
            ContradictoryFights: contradictions,
            TiedRuns: runs);
    }

    /// <summary>
    /// The pool ceiling implied by any stretch of the fight that drove the creature down TWO OR MORE
    /// rungs at once, or null when no stretch did.
    ///
    /// <para><b>Why this is not the rung upper bound the estimator refuses.</b> That one is
    /// <c>pool &lt;= 7D/(7-k)</c>, taken from a single reading and the CUMULATIVE damage behind it, and
    /// it is refused because a creature that walked in pre-damaged has absorbed more than the client
    /// ever saw - so every D understates and a ceiling built from one comes out too low. This bound
    /// uses no cumulative figure at all. It compares two readings and the damage BETWEEN them, and what
    /// it measures is how many rung-widths that damage was worth. A rung is <c>pool/7</c> wide however
    /// injured the creature already was: pre-damage moves where on the ladder the stretch lands, it
    /// cannot change how wide the rungs are. The contaminant that rules the other bound out does not
    /// reach this one. <b>Do not delete this as a duplicate of a refused constraint - it is the
    /// opposite case.</b></para>
    ///
    /// <para><b>The derivation.</b> Rung k means the remaining fraction lies in <c>((k-1)/7, k/7]</c>.
    /// Take reading A at rung <c>ka</c>, reading B at rung <c>kb = ka - N</c>, and damage <c>d</c>
    /// dealt between them:</para>
    /// <list type="bullet">
    /// <item>d is smallest with A at its floor and B at its ceiling: <c>d &gt; (N-1)P/7</c>.</item>
    /// <item>d is largest with A at its ceiling and B at its floor: <c>d &lt; (N+1)P/7</c>.</item>
    /// </list>
    /// <para>The first rearranges to <c>P &lt; 7d/(N-1)</c> for N of 2 or more, and d is at most the
    /// sum of the intervening blows' HIGH ends, giving <c>P &lt; 7 x dHigh / (N-1)</c>. N=1 yields
    /// nothing here - the term vanishes - which is the whole shape of the thing: a blow that crosses
    /// one boundary tells you where the creature is, and a blow that crosses three tells you how big it
    /// is.</para>
    ///
    /// <para><b>Regeneration cannot break it either.</b> If the creature healed R between the two
    /// readings the constraint bounds <c>d - R</c> rather than d, and R is never negative, so
    /// <c>d - R &lt;= dHigh</c> and the ceiling stands. The matching LOWER bound
    /// (<c>P &gt; 7 x dLow / (N+1)</c>) is deliberately NOT taken, because that one does break: healing
    /// makes the effective damage smaller than dLow and the floor would be invented. Families 1 and 2
    /// already bound from below with no such exposure.</para>
    ///
    /// <para><b>How much evidence this family actually has.</b> Nearly all of it. MUD2 prints a wound
    /// descriptor after every landed blow that does not kill - 3,559 descriptors against 3,561 such
    /// hits across 1,197 fights in the instrumented window from 2026-08-11, with no species deviating
    /// (36 tested, all ratios 0.99-1.03). So rung crossings are near-universally observable and this
    /// family fires on essentially every fight that drove a creature down two or more rungs, rather
    /// than on the handful an earlier reading of the corpus suggested. That earlier reading - "39% of
    /// fights print no descriptor" - was an instrumentation artefact: the fight table predates the
    /// health logger, so every pre-instrumentation fight joined to nothing and scored as silent.</para>
    ///
    /// <para><b>Nothing here was loosened on the strength of that correction.</b> Better coverage makes
    /// the constraint fire more often; it does not make a short measurement safe. The span form below
    /// is kept precisely because it still degrades correctly on the cases that remain - a killing blow
    /// prints no descriptor at all, and a parser miss is always possible.</para>
    ///
    /// <para><b>A missed descriptor cannot break it.</b> The span runs between two OBSERVED readings
    /// and uses the damage between those two points, so a reading the client never saw simply makes
    /// both N and d larger, consistently. This is the property the live path in
    /// <c>FightAccumulator.NoteHealth</c> also has to preserve, and the reason both take the span form
    /// rather than attributing a drop to one blow. What it does share with the kill family is exposure to a
    /// landed blow whose bracket never reached the ledger: that understates d and tightens the ceiling.
    /// That is the same exposure <c>killAtMost</c> already carries, not a new one.</para>
    /// </summary>
    private static double? MultiRungCeiling(
        IReadOnlyList<RungReading> readings, double[] prefixHigh, int blowCount)
    {
        if (readings.Count < 2)
            return null;

        // Sorted defensively: PoolObservationBuilder emits these in stream order, but BoundFor is
        // public and a caller assembling an observation by hand need not.
        var ordered = readings
            .Where(r => r.Rung >= 1 && r.Rung <= NpcHealthRungs.Rungs
                && r.LandedBlowsBefore >= 0 && r.LandedBlowsBefore <= blowCount)
            .OrderBy(r => r.LandedBlowsBefore)
            .ToList();

        // EVERY ordered pair, not just adjacent ones. An earlier version swept adjacent readings only,
        // on the reasoning that a wider pair spans the same drop over more damage and so can never
        // bind. That is false whenever a single-rung reading interrupts a longer fall: a 5-damage step
        // down one rung followed by a 50-damage step down three gives 7 x 50 / 2 = 175 adjacent, where
        // the pair straddling both is four rungs over 55 and gives 7 x 55 / 3 = 128.3 - strictly
        // tighter. Both are sound, so nothing was wrong on screen; the tightening was simply left
        // unclaimed.
        //
        // Quadratic, and deliberately so: readings are recorded only when the rung CHANGES, and the
        // rung has seven values, so a fight holds a handful of them and a hundred pairs is the
        // pessimistic case. This runs at load and at encounter close, never on the UI thread.
        //
        // Pairs that straddle a regeneration episode are included and are still sound: the derivation
        // needs only that each reading's rung bounds the remaining fraction at that moment and that the
        // damage between them is at most dealtHigh. Healing makes the effective damage smaller, which
        // the ceiling tolerates - see the remarks above.
        double? ceiling = null;
        for (var from = 0; from < ordered.Count - 1; from++)
        {
            for (var to = from + 1; to < ordered.Count; to++)
            {
                // One rung localises the creature, not its size; a rise is regeneration and bounds
                // nothing from above.
                var dropped = ordered[from].Rung - ordered[to].Rung;
                if (dropped < 2)
                    continue;

                var dealtHigh = prefixHigh[ordered[to].LandedBlowsBefore]
                    - prefixHigh[ordered[from].LandedBlowsBefore];
                if (dealtHigh <= 0)
                    continue;

                var bound = dealtHigh * NpcHealthRungs.Rungs / (double)(dropped - 1);
                if (ceiling is null || bound < ceiling)
                    ceiling = bound;
            }
        }

        return ceiling;
    }

    /// <summary>
    /// One fight's own constraint on the pool, or null when the fight constrains nothing.
    ///
    /// <para>Returns <c>Contradicted</c> when this fight's rung readings demand a pool larger than its
    /// own kill bracket permits. That happens - 3 of 199 rung-7 readings, all pre-damaged unnumbered
    /// mobs - and when it does the KILL BRACKET IS KEPT and the rung bounds are dropped. The kill
    /// bracket is arithmetic over what the game printed; the rung bound additionally assumes the
    /// seven-rung scale is uniform and that the creature started this fight undamaged, which is
    /// exactly the assumption a pre-damaged creature breaks.</para>
    /// </summary>
    public static (StaminaInterval? Bound, bool Contradicted) BoundFor(PoolFightObservation observation)
    {
        var blows = observation.Blows;
        if (blows.Count == 0)
            return (null, false);

        // Running prefix sums of the brackets: prefixLow[i] / prefixHigh[i] are the totals after the
        // first i landed blows.
        var count = blows.Count;
        var prefixLow = new double[count + 1];
        var prefixHigh = new double[count + 1];
        for (var i = 0; i < count; i++)
        {
            prefixLow[i + 1] = prefixLow[i] + blows[i].Low;
            prefixHigh[i + 1] = prefixHigh[i] + blows[i].High;
        }

        // Family 1. A kill brackets the pool from both sides; a survivor only floors it.
        var killAbove = observation.EndedInKill ? prefixLow[count - 1] : prefixLow[count];
        double? killAtMost = observation.EndedInKill ? prefixHigh[count] : null;

        // Family 2. The strongest rung reading wins; they are all lower bounds on one quantity.
        var rungAbove = 0.0;
        foreach (var (rung, landedBefore) in observation.Rungs)
        {
            if (rung < 1 || rung > NpcHealthRungs.Rungs)
                continue;
            if (landedBefore <= 0 || landedBefore > count)
                continue;

            var bound = prefixLow[landedBefore] * NpcHealthRungs.Rungs / (double)(NpcHealthRungs.Rungs + 1 - rung);
            if (bound > rungAbove)
                rungAbove = bound;
        }

        // Family 3. A MULTI-RUNG DROP bounds the pool from above.
        var spanAtMost = MultiRungCeiling(observation.Rungs, prefixHigh, count);

        // Order of trust when the families disagree, and it is the amount of MODEL in each that sets
        // it. The kill bracket is arithmetic over what the game printed and assumes nothing. The span
        // assumes one thing - that the seven rungs are equal sevenths. The static rung bound assumes
        // that AND that the creature began this fight undamaged, which is the assumption a
        // re-engagement breaks and the dominant contaminant in this corpus.
        //
        // So a span below the kill floor is the span's problem (a landed blow whose bracket never
        // reached the ledger would understate its damage), and a rung floor above the resulting
        // ceiling is the rung's.
        if (spanAtMost is double span && span <= killAbove)
            spanAtMost = null;

        var atMost = (killAtMost, spanAtMost) switch
        {
            (double kill, double sp) => Math.Min(kill, sp),
            (double kill, null) => kill,
            (null, double sp) => sp,
            _ => (double?)null,
        };

        var contradicted = atMost is double top && rungAbove >= top;
        var above = contradicted ? killAbove : Math.Max(killAbove, rungAbove);

        var interval = new StaminaInterval(above, atMost);
        // A degenerate bracket (every blow reported as 0-0) can produce ends that meet. It satisfies
        // nothing, so it is dropped rather than left to add a boundary the sweep would score at zero.
        return (interval.IsUnbounded || interval.IsEmpty ? null : interval, contradicted);
    }

    /// <summary>
    /// The sub-interval covered by the greatest number of the supplied constraints, its depth, and how
    /// many disjoint runs tied for that depth.
    ///
    /// <para>A sweep over the distinct boundary values. Between two adjacent boundaries the coverage
    /// is constant, so it is enough to score each gap and merge adjacent gaps that score the same.
    /// Only gaps with a FINITE top are candidates: half-infinite constraints all overlap out at
    /// infinity, so an unbounded run would always win on depth while saying nothing.</para>
    ///
    /// <para><b>When nothing has ever been killed</b> there is no finite top anywhere, and the answer
    /// is the hard intersection of the half-lines - the highest floor, which cannot be empty and which
    /// every constraint supports. Checked up front rather than fallen into: half-lines DO cover finite
    /// gaps between each other's floors, so a sweep left to itself would happily report the second-
    /// highest floor as a band with a ceiling nobody has evidence for.</para>
    ///
    /// <para><b>A survivor's floor above every kill's ceiling loses.</b> Only the bounded runs compete,
    /// so however many fights insist the pool exceeds 50, two kills capping it at 28 win the run. That
    /// is deliberate: the two readings cannot both be right, and a kill total is arithmetic over what
    /// the game printed where a survivor's floor is inflated by any regeneration the client cannot see.
    /// The losing fights still show in the gap between <c>SupportingFights</c> and
    /// <c>ContributingFights</c>.</para>
    ///
    /// <para>Ties are NOT resolved. One-sided contamination pushes in both directions here, so
    /// preferring the lower run would systematically pick the pre-damaged reading and preferring the
    /// higher one would pick the regenerating reading - an arbitrary pick is a claim about which
    /// contaminant to believe. Every tied run is returned, and the hull is returned alongside them as
    /// a conservative envelope, NOT as a band anyone should draw: the gap between two tied runs has
    /// zero support. See <see cref="PoolEvidence.Split"/>.</para>
    /// </summary>
    internal static (StaminaInterval Interval, int Depth, IReadOnlyList<StaminaInterval> Runs) MaxDepthRun(
        IReadOnlyList<StaminaInterval> bounds)
    {
        var points = new SortedSet<double>();
        var highestFloor = 0.0;
        var anyBounded = false;
        foreach (var bound in bounds)
        {
            points.Add(bound.Above);
            if (bound.Above > highestFloor)
                highestFloor = bound.Above;
            if (bound.AtMost is double top)
            {
                points.Add(top);
                anyBounded = true;
            }
        }

        if (!anyBounded)
        {
            var halfLine = new StaminaInterval(highestFloor, null);
            return (halfLine, bounds.Count, [halfLine]);
        }

        var ordered = points.ToArray();

        // Gap j is the half-open interval (ordered[j], ordered[j+1]].
        var gapCount = ordered.Length - 1;
        if (gapCount <= 0)
        {
            var floor = ordered.Length == 0 ? 0 : ordered[0];
            var degenerate = new StaminaInterval(floor, null);
            return (degenerate, bounds.Count, [degenerate]);
        }

        var depths = new int[gapCount];
        var best = 0;
        for (var j = 0; j < gapCount; j++)
        {
            var low = ordered[j];
            var high = ordered[j + 1];
            var depth = 0;
            foreach (var bound in bounds)
            {
                if (bound.Above <= low && (bound.AtMost is not double top || top >= high))
                    depth++;
            }
            depths[j] = depth;
            if (depth > best)
                best = depth;
        }

        if (best == 0)
        {
            // Every constraint is degenerate (its ends meet), so no gap is covered by anything. BoundFor
            // filters those out, so this is unreachable from Estimate; it is here because this method is
            // also called directly and must not return a run it never found.
            var unsupported = new StaminaInterval(highestFloor, null);
            return (unsupported, bounds.Count, [unsupported]);
        }

        // Adjacent gaps at the same depth are one contiguous run; a gap at a lower depth breaks it.
        var runs = new List<StaminaInterval>();
        var runLow = 0.0;
        var inRun = false;
        for (var j = 0; j < gapCount; j++)
        {
            if (depths[j] == best)
            {
                if (!inRun)
                {
                    runLow = ordered[j];
                    inRun = true;
                }
                // Extend (or close, on the next iteration) this run to cover gap j.
                if (j + 1 == gapCount || depths[j + 1] != best)
                {
                    runs.Add(new StaminaInterval(runLow, ordered[j + 1]));
                    inRun = false;
                }
            }
            else
            {
                inRun = false;
            }
        }

        var hull = new StaminaInterval(runs[0].Above, runs[^1].AtMost);
        return (hull, best, runs);
    }
}
