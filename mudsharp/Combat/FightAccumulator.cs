namespace MudSharp.Combat;

/// <summary>
/// How a single per-NPC fight ended. Mirrors combat_fights.outcome in tools/combat/schema.sql so
/// live and offline rows are directly comparable - and since 2026-08-19 the two use the IDENTICAL
/// spellings, where they previously disagreed in case and separator ("Killed" live vs "killed"
/// offline) for no reason but drift.
///
/// <para><b>The ends, per the owner (2026-08-19), plus one found since.</b> Every end prints inside a
/// single frame (one prompt to the next) - always. That guarantee is why nothing here needs a timer or
/// a "lull" window to decide a fight is over: the evidence is never split across frames, so the
/// terminator line is the whole answer.</para>
///
/// <para>Five are per-creature and leave any other fights running (<see cref="Kill"/>,
/// <see cref="CFled"/>, <see cref="CFledFail"/>, <see cref="Withdraw"/>, <see cref="NoMore"/>); three
/// are changes to the PLAYER's own combat state and therefore zero the fight count, ending every open
/// fight at once (<see cref="UFled"/>, <see cref="UFledFail"/>, <see cref="Died"/>). "U" is the
/// player, "C" the creature - the player is itself a creature in MUD2's model, so the pairing is
/// deliberate rather than cosmetic.</para>
///
/// <para><see cref="NoMore"/> is the eighth, added 2026-08-26 after a wyvern died of poison and the
/// client, having no line for it, stayed "in combat" for the rest of the session. Take "exactly
/// seven" as "the seven observed by then", not as a closed set - see tools/combat/FIGHT-ENDS.md.</para>
///
/// <para><see cref="Interrupted"/> is not one of the game's ends at all - it is the client saying the
/// world the fight was in has gone (reset, logout, room change, app exit). Kept in this enum because
/// the display has to give every fight it stops drawing SOME outcome, and giving it one of the game's
/// would be a claim about a frame that was never printed.</para>
/// </summary>
public enum FightOutcome
{
    /// <summary>Still going (or the encounter ended without ever resolving this one).</summary>
    Unresolved,

    /// <summary>Per-creature. "You have killed the X." (+ "The X has just passed on."). Verbatim,
    /// one frame: <c>You hit the banshee (6). / You have killed the banshee. / (Persona saved on
    /// +143 = 343). / The banshee has just passed on.</c></summary>
    Kill,

    /// <summary>All fights. "You have fled by going &lt;dir&gt;." Costs points.</summary>
    UFled,

    /// <summary>
    /// All fights. "You have fled by trying to go &lt;dir&gt;." - the player's flee FAILED, so they
    /// are still in the room, but MUD2 has still zeroed the fight count.
    ///
    /// <para>Nothing parsed this line until 2026-08-19; it was invisible to the client. Verbatim from
    /// session-rec.mud2.co.uk.20260819-000137, all one frame:
    /// <c>flee n / You cannot go north from here. / You have changed experience level from protector
    /// to novice. / (Persona saved on -102 = 98). / You have fled by trying to go north.</c></para>
    ///
    /// <para>Note what that capture proves: a FAILED flee is not free. It charged 102 points and
    /// demoted the persona a whole experience level, for no escape at all.</para>
    /// </summary>
    UFledFail,

    /// <summary>Per-creature. "The X has fled by going &lt;dir&gt;." It really left the room, so a
    /// chase is meaningful.</summary>
    CFled,

    /// <summary>
    /// Per-creature. "The X has fled by trying to go &lt;dir&gt;." The creature's flee FAILED: it is
    /// still standing in the room - but it is NOT still fighting, and the player must attack again to
    /// re-engage. The owner's description: snakes "often try to flee but almost never succeed, it just
    /// breaks the fight sequence"; and the rule behind it (2026-09-01), "fleeing ends combat with all
    /// creatures attacking you. so if a zombie flees, even if it fails, it is no-longer in combat with
    /// you." An earlier version of this comment said "still hostile", which is wrong in the way that
    /// matters: the creature has left combat, so anything that lands on it afterwards is a new
    /// engagement rather than a continuation. 128 NpcFleeFailed events in the clog corpus, not one
    /// followed by a swing from that creature before a fresh FightStart.
    ///
    /// <para>The distinction from <see cref="CFled"/> is one word ("trying to") and it must never
    /// blur: chasing a creature standing in front of you is nonsense, and counting a failed attempt
    /// as an escape corrupts the per-class flee rates.</para>
    ///
    /// <para>This outcome is also the direct fix for a real bug: the tracker used to treat the line
    /// as not-an-ending at all, so a fight the player never re-opened stayed "in combat" until
    /// logout. See CombatTracker's NpcFleeFailed handling.</para>
    /// </summary>
    CFledFail,

    /// <summary>Per-creature. "The X withdraws from your fight, and so do you." - the accepted mutual
    /// withdraw (the earlier "You offer to withdraw..." is an offer, not an end). Only the named
    /// creature's fight ends: the agreement is with that one creature, not a reset of the player's
    /// own combat state.</summary>
    Withdraw,

    /// <summary>All fights. The player died - permadeath, "Not updating persona." Verbatim, one frame:
    /// <c>The rat18 hits you. / You feel your life concluding... / The rat18 has killed you. / Unlit
    /// brand40 dropped. / Not updating persona.</c></summary>
    Died,

    /// <summary>
    /// Per-creature. <b>You lost the creature.</b> It stopped being available to fight and to kill,
    /// and not by your hand - so MUD2 printed no "You have killed the X." and, on the evidence so
    /// far, credited nothing.
    ///
    /// <para><b>The name is the owner's</b> (2026-08-26), and the reason for it is worth keeping:
    /// this is not losing a fight. "Died" would be too narrow for the family and would collide with
    /// <see cref="Died"/>, which is the PLAYER dying; "Lost" alone reads as having lost the fight,
    /// which is the opposite of what happened. The creature is no more, and neither is your claim on
    /// it. Not to be confused with <see cref="EndOther"/>, which comes from the same "no longer"
    /// sentence but means only that MUD2 stopped a fight without saying why - there, the creature may
    /// be standing right in front of you.</para>
    ///
    /// <para><b>An open family, one member observed.</b> The observed member is poison: captured
    /// 2026-08-26 in session-rec.mud2.co.uk.20260826-134435.jsonl as <c>The wyvern drops dead,
    /// poisoned... / The wyvern has just passed on. / {c08.12}You can fight the wyvern no
    /// longer.</c>, the first two lines carrying no C1 code at all. The owner expects others - his
    /// example is poisoning the ogre with alcohol, "a lot of juggling and luck" to reproduce - and
    /// they are likely to be worded differently. That is why this outcome is named for what the
    /// player lost rather than for how it happened, and why the pattern behind it
    /// (<c>The X drops dead, &lt;cause&gt;...</c>) matches any cause rather than the word "poisoned".
    /// A new wording is a new <see cref="CombatEventKind"/> at most; it maps to this same outcome.</para>
    ///
    /// <para><b>Event kind vs outcome, deliberately not one thing.</b>
    /// <see cref="CombatEventKind.NpcDied"/> describes an observed LINE - a creature died. This
    /// describes what the fight was worth. Keeping them separate is what lets an unfamiliar death
    /// line be added later without either renaming an outcome or pretending the new line is the old
    /// one.</para>
    ///
    /// <para>Kept apart from <see cref="Kill"/> on purpose, and not merely for bookkeeping: the line
    /// states a cause but never an agent, so a kill claim would be inference. It also protects the
    /// corpus - StaminaPoolEstimator reads the damage brackets of fights that ended in a Kill to
    /// bracket a creature's pool from above, and the damage that finished one of these was never on
    /// the wire, so counting it as a kill would drag every estimate for that creature down.</para>
    ///
    /// <para>This is the EIGHTH end. The seven the rest of this file documents came from the owner in
    /// 2026-08-19 and were complete as far as anything then observed; this one arrived as a stuck
    /// "in combat" readout a week later. See tools/combat/FIGHT-ENDS.md.</para>
    /// </summary>
    NoMore,

    /// <summary>
    /// Per-creature. MUD2 said this fight ended and did not say why: a C08.12 "fight ends - other"
    /// line that identified its creature - "You can fight the wyvern no longer." - reached the tracker
    /// with the fight still open, so nothing else had resolved it.
    ///
    /// <para>Exists to keep <see cref="Unresolved"/> meaning ONE thing. Unresolved is "we lost track
    /// of this fight" - the encounter ended without any terminator we could attribute, which is a bug
    /// report, and the rows to go hunting through when looking for the next unmatched wording. This is
    /// "the game closed it and offered no reason", which is not a bug and must not sit in the same
    /// bucket diluting it. The distinction is the same one <see cref="NoMore"/> exists for.</para>
    ///
    /// <para>Expected to be RARE, and if it is not, that is itself the finding: in every frame observed
    /// so far the 08.12 line trails a real terminator (a kill, a flee, a failed flee, a poison death)
    /// which resolved the fight first, and a resolved fight keeps its first outcome. A run of these
    /// means a terminator upstream is going unmatched - see tools/combat/FIGHT-ENDS.md.</para>
    /// </summary>
    EndOther,

    /// <summary>
    /// All fights. <b>Not an ending MUD2 stated - the client ended it.</b> The world the fight was
    /// happening in stopped existing or stopped being reachable: a world reset, a logout/relog, the
    /// player standing in a different room, or the app exiting. Comes from
    /// <see cref="CombatEventKind.EncounterForceEnded"/>, which names no creature because it means
    /// all of them.
    ///
    /// <para><b>Why not <see cref="Unresolved"/>, which is what these fights honestly are.</b>
    /// <see cref="FightAccumulator.IsResolved"/> is derived from this enum, and the roster's
    /// <c>IsLive</c> from that, so leaving a force-ended fight Unresolved is the same statement as
    /// "still swinging" - which is the bug: after a reset the rail went on drawing the whole pack as
    /// live opponents in a world that had already been rebuilt. Something has to say the fight is
    /// over.</para>
    ///
    /// <para><b>Why not <see cref="EndOther"/>, the nearest existing fit.</b> EndOther is
    /// specifically "MUD2 closed this fight and gave no reason", and its value is diagnostic - a run
    /// of them means a terminator upstream is going unmatched and should be hunted down. A reset is
    /// not an unmatched terminator, and filing resets there would fill that bucket with false
    /// alarms.</para>
    ///
    /// <para><b>Not won, not lost, and it must not read as either.</b> The player was interrupted;
    /// nothing here says who was ahead. So it is not <see cref="Kill"/>, and it is not any of the
    /// flee outcomes, whose whole point is that a flee was attempted and cost something.</para>
    ///
    /// <para><b>Display-side only, deliberately.</b> The live aggregator resolves to this so the
    /// panel stops drawing the fight; FightHistoryRecorder still persists the same fights as
    /// <see cref="Unresolved"/>, because for the RECORD "we never saw this fight end" is exactly
    /// true and the history's Unresolved bucket is what the corpus is searched on. The two are
    /// separate <see cref="FightAccumulator"/> instances fed by the same events and answering
    /// different questions - see FightHistoryRecorder's EncounterForceEnded case. Consequently this
    /// never appears in combat_fights and needs no schema.sql entry.</para>
    /// </summary>
    Interrupted,
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

    /// <summary>When the health reading landed. The ladder only updates on a landed blow, and the
    /// player misses often enough that age is what separates "this is current" from "this is what it
    /// looked like four swings ago" - an unknown reading must never be drawn as a measured one.
    /// <para>This used to say "the player's hit rate is 0.57". Corrected 2026-09-03: 0.57 is not a
    /// measured player hit rate, it is <c>100/175</c> - the published guide's <c>Dy/(Dy+Do)</c> for a
    /// player at dexterity 100 against the bestiary's rat. The measured rate over the swing ledger is
    /// 0.6275 (5,118 of 8,156 player swings, 2026-08-14 to 2026-09-03), and it varies from 0.41 to
    /// 0.74 by species. No exact figure is quoted here now because the argument does not need one:
    /// roughly a third of swings miss whatever the opponent, so gaps are ordinary.</para></summary>
    public DateTime? HealthReadUtc { get; private set; }

    public int YouHits { get; private set; }
    public int YouMisses { get; private set; }
    public int TheyHits { get; private set; }
    public int TheyMisses { get; private set; }
    public double ApproxDamageDone { get; private set; }
    public double ApproxDamageTaken { get; private set; }

    /// <summary>The worst single blow this creature has landed in THIS fight. Kept alongside the
    /// running total because they answer different questions: the total says how much trouble you are
    /// in, the worst blow says how much trouble one more tick could put you in - which is the figure a
    /// flee decision actually turns on when stamina is low.</summary>
    public double MaxDamageTaken { get; private set; }

    /// <summary>Landed blows whose magnitude was actually resolved, which is the honest denominator
    /// for a mean. Distinct from <see cref="TheyHits"/>: a hit with no stamina baseline to diff
    /// against still counts as a hit (it happened, and the swing rhythm is real evidence) but
    /// contributes nothing to <see cref="ApproxDamageTaken"/>, so dividing the total by TheyHits would
    /// quietly understate every average in proportion to how many baselines were missed.</summary>
    public int TheyHitsMeasured { get; private set; }

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

    /// <summary>
    /// Records a health-descriptor reading for this NPC. Always overwrites - see
    /// <see cref="HealthRung"/> on why the latest reading wins over the worst.
    ///
    /// <para><b>A reading that is LOWER than the last one is a boundary crossing</b>, and a crossing is
    /// worth far more than the reading itself. The damage behind it localises where the boundary sat:
    /// carry the creature over a rung line with 1-4 and it is now within 4 stamina of that line; carry
    /// it over with 20-29 and almost nothing has been learned. See <see cref="NpcRungCrossing"/>, which
    /// is what turns "many small blows" into a sharper ladder reading than "few large ones" at equal
    /// total damage.</para>
    ///
    /// <para><b>The damage recorded is everything since the PREVIOUS reading, not the last blow.</b>
    /// Attributing a drop to one blow is only correct if no landed blow between the two readings went
    /// unreported, and while MUD2 prints a descriptor after every non-killing landed hit (3,559 against
    /// 3,561 such hits across 1,197 fights, instrumented window from 2026-08-11), rare is not never: a
    /// killing blow prints no descriptor, a narrative-mode blow carries no bracket for this class to
    /// add, and a parser miss stays possible. Understating the damage behind a drop makes the pool
    /// ceiling it implies too TIGHT, which is the unsafe direction - it would make a creature read as
    /// smaller, and the fight as easier to win, than the evidence supports. The span form degrades
    /// safely instead: an unseen reading simply leaves more damage inside the span.
    /// <c>StaminaPoolEstimator.MultiRungCeiling</c> takes the same form over the corpus, and the two
    /// must not diverge.</para>
    ///
    /// <para>Only a strict DROP counts. Repeats at the same rung say nothing new about the boundary -
    /// though they still re-anchor the span, which TIGHTENS the next crossing - and improvements happen,
    /// since creatures regenerate and the corpus has a zombie oscillating four times in one fight, so a
    /// rise is not a crossing of anything.</para>
    /// </summary>
    public void NoteHealth(int rung, string? phrase, DateTime timestampUtc)
    {
        if (HealthRung is int previous && rung < previous && !_bracketGapSinceHealthRead)
        {
            // Everything dealt since the previous descriptor. _dealtAtHealthRead is still the PREVIOUS
            // reading's snapshot at this point - it is advanced at the bottom of this method.
            var since = Since(_dealtAtHealthRead);
            if (since.High > 0)
            {
                _crossingRung = rung;
                _crossingDamage = since;
                _crossingRungsDropped = previous - rung;
                _dealtAtCrossing = DamageDealt;
            }
        }

        HealthRung = rung;
        HealthPhrase = phrase;
        HealthReadUtc = timestampUtc;
        _dealtAtHealthRead = DamageDealt;
        _bracketGapSinceHealthRead = false;
    }

    /// <summary>
    /// Records a <c>diagnose</c> reading against this creature, exactly as MUD2 printed it.
    ///
    /// <para>Nothing is rounded or snapped to a grid: whether the game's bracket aligns to tens, to a
    /// tenth of the pool, or to something else is unresolved at four observations in the whole corpus.
    /// The damage dealt so far is snapshotted with it, so the reading can be aged forward as the fight
    /// continues rather than going quietly stale.</para>
    /// </summary>
    public void NoteStaminaRead(int printedLow, int printedHigh)
    {
        _staminaReadLow = printedLow;
        _staminaReadHigh = printedHigh;
        _dealtAtStaminaRead = DamageDealt;
    }

    /// <summary>The player's cumulative damage this fight as a BRACKET - the sum of the lows and the
    /// sum of the highs of every landed blow, never a midpoint.</summary>
    public DamageBracket DamageDealt { get; private set; } = DamageBracket.Zero;

    private DamageBracket _dealtAtHealthRead = DamageBracket.Zero;
    private DamageBracket _dealtAtStaminaRead = DamageBracket.Zero;

    /// <summary>Set when a blow landed since the last descriptor whose bracket never reached
    /// <see cref="DamageDealt"/> - narrative mode. The span since that reading then understates the
    /// damage behind any drop, which would make the implied pool ceiling too tight, so the crossing is
    /// suppressed until the next clean reading rather than recorded from a short measurement.</summary>
    private bool _bracketGapSinceHealthRead;

    private int? _crossingRung;
    private DamageBracket _crossingDamage;
    private int _crossingRungsDropped = 1;
    private DamageBracket _dealtAtCrossing = DamageBracket.Zero;
    private int? _staminaReadLow;
    private int? _staminaReadHigh;

    /// <summary>The latest health descriptor with the damage dealt since it was printed, ready for
    /// <see cref="NpcRemainingStamina.Compute"/>. Null until one has landed.</summary>
    public NpcRungAnchor? RungAnchor => HealthRung is int rung
        ? new NpcRungAnchor(rung, Since(_dealtAtHealthRead))
        : null;

    /// <summary>The latest rung boundary this creature was driven across, with the blow that did it and
    /// the damage dealt since. Null until a descriptor has actually dropped. See
    /// <see cref="NpcRungCrossing"/>.</summary>
    public NpcRungCrossing? RungCrossing => _crossingRung is int rung
        ? new NpcRungCrossing(rung, _crossingDamage, Since(_dealtAtCrossing), _crossingRungsDropped)
        : null;

    /// <summary>The latest <c>diagnose</c> reading with the damage dealt since it, ready for
    /// <see cref="NpcRemainingStamina.Compute"/>. Null until one has landed.</summary>
    public NpcStaminaReading? StaminaReading => _staminaReadLow is int low && _staminaReadHigh is int high
        ? new NpcStaminaReading(low, high, Since(_dealtAtStaminaRead))
        : null;

    private DamageBracket Since(DamageBracket anchor)
        => new(DamageDealt.Low - anchor.Low, DamageDealt.High - anchor.High);

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
    public void NoteNpcWeapon(string? weapon)
    {
        if (!string.IsNullOrWhiteSpace(weapon))
            NpcWeapon = weapon;
    }

    /// <summary>The points a `value &lt;name&gt;` probe reported for killing this creature (operator,
    /// 2026-09-02). Null means "never asked/answered" OR "unattributable" (see
    /// <see cref="ValueIsAmbiguous"/>) - not zero, which is itself a legal value (the ox). See
    /// MudSession's creature-value probe for how this is learned.</summary>
    public int? Value { get; private set; }

    // Whether NoteValue has already recorded a reading THIS encounter. Unnumbered mobs (thief,
    // banshee, coot, fox - see NpcPoolKey's own remarks) have no instance number, so two distinct
    // live creatures sharing a name can both answer the SAME `value <name>` probe with different
    // points, and this bucket has no way to tell whose is whose (see MudSession's per-batch
    // resolution - both replies are attributed to this same NpcName-keyed bucket).
    private bool _valueNoted;

    /// <summary>True once a second (or later) reading has arrived for this bucket this encounter -
    /// the unnumbered-mob name collision above. A coin-flip last-writer-wins value is worse than an
    /// honest unknown, so once ambiguous, <see cref="Value"/> is nulled and stays null.</summary>
    public bool ValueIsAmbiguous { get; private set; }

    public void NoteValue(int value)
    {
        if (ValueIsAmbiguous)
            return;   // already given up on this bucket; a further reading changes nothing
        if (_valueNoted)
        {
            // Two readings for one name this encounter - cannot tell which live creature either
            // belongs to. Retract the first reading rather than keep whichever arrived last.
            ValueIsAmbiguous = true;
            Value = null;
            return;
        }
        _valueNoted = true;
        Value = value;
    }

    public void AddYouHit(int? rangeLow, int? rangeHigh)
    {
        YouHits++;
        if (rangeLow is int low && rangeHigh is int high)
        {
            var midpoint = (low + high) / 2.0;
            ApproxDamageDone += midpoint;
            // Kept alongside the midpoint total rather than instead of it: ApproxDamageDone answers
            // "how much have I done" for the outlook and the history rows, and a bracket sum answers
            // "what could the creature have left", which is a different question and the only one a
            // remaining-stamina band can be built from. Collapsing to the midpoint here is the one-way
            // door CombatDb's own remarks warn about.
            DamageDealt = DamageDealt.Plus(new DamageBracket(low, high));
            RecordSwing(_yourRecent, ref _yourRecentHead, ref _yourRecentCount, SwingOutcome.Hit(midpoint));
        }
        else
        {
            // No range means no parsed swing detail (narrative mode). The blow landed and did damage,
            // but none of it reached DamageDealt - so the span since the last descriptor now understates
            // what the creature absorbed, and any crossing measured across it would imply a pool ceiling
            // that is too tight. Flagged rather than ignored; see NoteHealth.
            _bracketGapSinceHealthRead = true;
        }
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

        // Counted for every RESOLVED magnitude including zero (see TheyHitsMeasured): a blow that
        // landed and took nothing off is a real observation about this creature, and excluding it
        // would bias the mean upward - the average would then answer "how hard does it hit when it
        // hurts", which is not the question. Adding a zero to the total above is a no-op, so the
        // guard there and the counter here deliberately disagree about zero.
        if (damage is double measured && measured >= 0)
        {
            TheyHitsMeasured++;
            if (measured > MaxDamageTaken)
                MaxDamageTaken = measured;
        }

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
