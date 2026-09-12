namespace Mucka.ViewModels;

/// <summary>
/// What a damage float is reporting. The kind is not cosmetic - it decides the colour, and it is
/// what the motion budget sheds by (see <see cref="RailFloatBudget"/>).
/// </summary>
public enum RailFloatKind
{
    /// <summary>A blow the player landed. Its text is the game's own BRACKET ("5-9"), or the single
    /// figure MUD2 sometimes prints instead - never a fabricated midpoint. See
    /// <see cref="RailFloatText.Outgoing"/>.</summary>
    OutgoingHit,
    /// <summary>"You miss the X."</summary>
    OutgoingMiss,
    /// <summary>A blow the player took. Exact: MUD2 prints absolute stamina on the hit line, so the
    /// delta against the previous reading is a measurement rather than an estimate.</summary>
    IncomingHit,
    /// <summary>"The X misses you."</summary>
    IncomingMiss,
    /// <summary>Stamina observed going UP. Deduced, never announced - MUD2 has no "+3 health" line,
    /// so this is the sum of everything (regen, food, a spell) since the previous reading, reported
    /// at the moment of observation. See <see cref="RailFloatText"/>.</summary>
    StaminaGain,
}

/// <summary>
/// One float to draw: what it says, and which pane it belongs over.
/// </summary>
/// <param name="RosterIndex">Index into the roster the rail last published, or
/// <see cref="PlayerAnchor"/> for the player's own stamina seal. Resolved by the view model at the
/// moment the event lands, because the roster reorders as a pack fight resolves and a name looked
/// up later would find a different row.</param>
/// <param name="LiveCount">How many LIVE opponents that roster covered - RosterPlan.LiveCount,
/// including any past the roster's own row cap. Needed as well
/// as the index because the rail surrenders one slot to the overflow row once the opposition outgrows
/// the visible capacity, so whether a given index has a pane of its own depends on the total.</param>
public sealed record RailFloat(
    RailFloatKind Kind,
    string Text,
    int RosterIndex,
    int LiveCount,
    DateTime AtUtc)
{
    /// <summary>The roster index that means "the player's own stamina seal, not an opponent".</summary>
    public const int PlayerAnchor = -1;

    public bool IsPlayerAnchored => RosterIndex == PlayerAnchor;
}

/// <summary>
/// The words and numbers a float carries. Kept apart from the view model so the rules below are
/// stated once, in a file a test can link.
///
/// <para><b>The asymmetry between the two damage directions is the point, not an inconsistency.</b>
/// Outgoing damage is a BUCKET - MUD2 prints "You hit the rat (5-9)." - so the float prints the
/// bracket exactly as the game printed it, unsigned. Incoming damage is EXACT, because the wire
/// carries absolute stamina ("The rat hits you (58/62)."), so the float prints a signed "-5". A
/// player who sees a signed number on a float is looking at a measurement; an unsigned range is a
/// range. Giving the outgoing side a minus sign would launder a bracket into a value.</para>
///
/// <para>That is a rule about the FLOAT, whose whole job is to echo the one line the game just
/// printed. It is NOT a rule about what the rail may derive elsewhere - see
/// <see cref="MudSharp.Combat.ExchangeLine"/>, which owns that distinction.</para>
/// </summary>
public static class RailFloatText
{
    /// <summary>Both sides' miss. One word for both directions - which side missed is already said
    /// by WHERE the float appears.</summary>
    public const string Miss = "Miss";

    /// <summary>
    /// The player's own blow, from the parsed range: equal bounds print as one figure, anything else
    /// prints as the bracket.
    ///
    /// <para>MUD2 usually brackets a blow ("You hit the rat (5-9).") and occasionally prints a single
    /// figure instead ("You hit the banshee (6)."). What selects between the two is not known - see
    /// <c>CombatTracker.YouHitExact</c>, which holds the evidence. The corpus is roughly 6 single
    /// figures against 820 brackets.</para>
    ///
    /// <para>Nothing here depends on the answer: the tracker emits the single-figure form as a range
    /// of width zero, so "print the bounds, collapsed when they are equal" is correct whatever is
    /// doing the selecting.</para>
    ///
    /// <para>Null when the line carried no numbers at all, which no observed wording does - a float
    /// with nothing to say is not drawn rather than guessed at.</para>
    /// </summary>
    public static string? Outgoing(int? rangeLow, int? rangeHigh)
    {
        if (rangeLow is not int low)
            return null;
        if (rangeHigh is not int high || high == low)
            return low.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return string.Concat(
            low.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-",
            high.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Stamina lost to one blow, signed.</summary>
    public static string Incoming(int damage)
        => "-" + damage.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Stamina observed going up, signed. Deliberately NOT split into per-tick increments
    /// and NOT aligned to the combat tick: nothing on the wire says when within the gap it happened,
    /// and inventing a schedule for it would be the panel making up a mechanism.</summary>
    public static string Gain(int amount)
        => "+" + amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The pool slot a float was granted, plus the lane it sits in and the token that
/// identifies THIS use of the slot.</summary>
/// <param name="Lane">0 for the first float at an anchor, 1 for a second one still in flight -
/// offset upward so two blows in the same tick do not print on top of each other.</param>
/// <param name="Token">Increments on every grant. A completion callback carrying a stale token is
/// a float that was shed and its slot re-let; retiring on it would free the float now using the
/// slot. Same hazard, and the same fix, as TickSweep's generation counter.</param>
public readonly record struct RailFloatGrant(int PoolSlot, int Lane, int Token);

/// <summary>
/// The Combat Rail's motion budget: how many floats may be in the air at once, and what gets shed
/// when more arrive than that.
///
/// <para><b>The rule this serves.</b> The rail is a GLANCE instrument - the spec's whole premise is
/// that the player's eye lives on the terminal text and visits the panel briefly, which is why the
/// canvas draws indicators that change state in place and never move. Floats are the one deliberate
/// exception, and they are only affordable while they stay exceptional. MUD2's combat tick is 2000
/// ms and up to seven creatures have been observed engaged at once, so an unbudgeted feed is one
/// outgoing plus seven incoming floats every two seconds - permanent motion in the corner of the
/// eye, which is precisely the thing the rail was designed not to be. The cap is what keeps a float
/// meaning "something just happened" instead of "a fight is ongoing", which the rail already says
/// in four other ways.</para>
///
/// <para><b>Misses go first.</b> Shedding by kind rather than by age is what makes the cap survive
/// a pack fight with its meaning intact: NPCs miss often, and a miss is the least consequential
/// thing on the panel - losing one costs the player nothing, while losing the blow that took 14 off
/// them costs the fight's most important number. A miss is never shed in favour of another miss's
/// slot either; if everything in the air is a hit, an arriving miss is simply dropped.</para>
///
/// <para>Pure and MAUI-free so it is linked into mudsharp.Tests (RailFloatBudgetTests); the host
/// owns the actual elements and animations and asks this class only which slot to use.</para>
/// </summary>
public sealed class RailFloatBudget
{
    /// <summary>How many floats may be in the air at once - and therefore how many pooled elements
    /// the host creates, since a float that cannot be granted a slot is never drawn. Four: at the
    /// 2000 ms tick and a ~1500 ms lifetime, this is roughly two ticks' worth of the events a
    /// player can actually read, and it leaves the panel empty of motion for a visible beat between
    /// exchanges. See the class remarks for the rule.</summary>
    public const int MaxInFlight = 4;

    /// <summary>How many floats may share one anchor. Two, because every incoming blow in a pack
    /// fight lands on the SAME anchor (the stamina seal) in the same tick, and a third stacked
    /// number over one 92dp seal is a smear rather than a reading.</summary>
    public const int MaxPerAnchor = 2;

    /// <summary>How long one float lives (~1.5s). Also the expiry this class falls back on: the
    /// host retires a slot from the animation's completion callback, but a completion that never
    /// arrives (a torn-down compositor) must not strand a slot forever.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMilliseconds(1500);

    private readonly bool[] _busy = new bool[MaxInFlight];
    private readonly int[] _anchor = new int[MaxInFlight];
    private readonly int[] _lane = new int[MaxInFlight];
    private readonly bool[] _sheddable = new bool[MaxInFlight];
    private readonly DateTime[] _startedUtc = new DateTime[MaxInFlight];
    private readonly int[] _token = new int[MaxInFlight];
    private int _nextToken;

    /// <summary>Floats currently in the air, after expiring anything past its lifetime as of
    /// <paramref name="nowUtc"/>. For tests and diagnostics.</summary>
    public int InFlight(DateTime nowUtc)
    {
        Expire(nowUtc);
        var count = 0;
        for (var i = 0; i < MaxInFlight; i++)
            if (_busy[i]) count++;
        return count;
    }

    /// <summary>A miss is the cheap thing to lose; a landed blow is not. This is the whole shed
    /// policy in one predicate.</summary>
    private static bool IsSheddable(RailFloatKind kind)
        => kind is RailFloatKind.OutgoingMiss or RailFloatKind.IncomingMiss;

    /// <summary>
    /// Asks for a pool slot. Null means the budget refused - the float is not drawn at all, which
    /// is the correct outcome rather than a queued one: a float that appeared a second after the
    /// blow would be reporting the wrong tick.
    /// </summary>
    public RailFloatGrant? Admit(RailFloatKind kind, int anchor, DateTime nowUtc)
    {
        Expire(nowUtc);

        var arrivingIsSheddable = IsSheddable(kind);

        // Per-anchor cap first: it is the tighter of the two, and when it bites the eviction has to
        // come from THIS anchor - freeing a float over a different pane would leave the smear here
        // and blank the pane that had room.
        var atAnchor = 0;
        for (var i = 0; i < MaxInFlight; i++)
            if (_busy[i] && _anchor[i] == anchor) atAnchor++;

        int slot;
        int lane;
        if (atAnchor >= MaxPerAnchor)
        {
            var victim = PickVictim(arrivingIsSheddable, anchor);
            if (victim < 0)
                return null;
            lane = _lane[victim];
            slot = victim;
        }
        else
        {
            slot = FreeSlot();
            if (slot < 0)
            {
                var victim = PickVictim(arrivingIsSheddable, anchor: null);
                if (victim < 0)
                    return null;
                slot = victim;
            }
            lane = FreeLane(anchor, excluding: slot);
        }

        _busy[slot] = true;
        _anchor[slot] = anchor;
        _lane[slot] = lane;
        _sheddable[slot] = arrivingIsSheddable;
        _startedUtc[slot] = nowUtc;
        _token[slot] = ++_nextToken;
        return new RailFloatGrant(slot, lane, _token[slot]);
    }

    /// <summary>Frees a slot once its animation has finished. Ignored when the token is stale - the
    /// slot was shed and re-let while this completion was in flight.</summary>
    public void Retire(int poolSlot, int token)
    {
        if ((uint)poolSlot >= MaxInFlight || _token[poolSlot] != token)
            return;
        _busy[poolSlot] = false;
    }

    /// <summary>Drops everything. The host calls this whenever the geometry underneath the floats
    /// stops being the geometry they were placed against - the panel resized, or was hidden.</summary>
    public void Clear()
    {
        for (var i = 0; i < MaxInFlight; i++)
            _busy[i] = false;
    }

    /// <summary>
    /// Drops only the floats pinned to an opponent slot, leaving the player's own alone. Called when
    /// the roster reorders (something died, something joined): slot 2 is now a different creature,
    /// so a number still rising over it is attributing a blow to the wrong thing. The player's
    /// stamina seal has not moved and its floats are still true.
    /// </summary>
    /// <returns>A bitmask of the pool slots actually freed, so the host resets exactly those
    /// elements. Resetting all of them instead would cancel a player-anchored float this method just
    /// deliberately spared - and worse, would leave its slot held until the lifetime expiry, because
    /// cancelling an animation is what makes its completion callback stale.</returns>
    public int ClearOpponentAnchored()
    {
        var freed = 0;
        for (var i = 0; i < MaxInFlight; i++)
        {
            if (!_busy[i] || _anchor[i] == RailFloat.PlayerAnchor)
                continue;
            _busy[i] = false;
            freed |= 1 << i;
        }
        return freed;
    }

    private void Expire(DateTime nowUtc)
    {
        for (var i = 0; i < MaxInFlight; i++)
            if (_busy[i] && nowUtc - _startedUtc[i] >= Lifetime)
                _busy[i] = false;
    }

    private int FreeSlot()
    {
        for (var i = 0; i < MaxInFlight; i++)
            if (!_busy[i]) return i;
        return -1;
    }

    /// <summary>The lowest lane not already taken at this anchor. Lanes stack the float upward so
    /// two events at one anchor in one tick stay separately readable.</summary>
    private int FreeLane(int anchor, int excluding)
    {
        for (var lane = 0; lane < MaxPerAnchor; lane++)
        {
            var taken = false;
            for (var i = 0; i < MaxInFlight && !taken; i++)
                taken = i != excluding && _busy[i] && _anchor[i] == anchor && _lane[i] == lane;
            if (!taken)
                return lane;
        }
        return 0;
    }

    /// <summary>
    /// Which float to shed, or -1 for "shed nothing, drop the arriving one instead".
    ///
    /// <para>Oldest sheddable first - a miss that has been on screen longest has already been read.
    /// Only if there is no miss to take does a hit get evicted, and then never on behalf of a miss:
    /// that is the ordering the class remarks describe, and it is the reason the cap does not
    /// quietly turn a pack fight's display into a wall of "Miss".</para>
    /// </summary>
    private int PickVictim(bool arrivingIsSheddable, int? anchor)
    {
        var best = -1;
        for (var i = 0; i < MaxInFlight; i++)
        {
            if (!_busy[i] || !_sheddable[i]) continue;
            if (anchor is int a && _anchor[i] != a) continue;
            if (best < 0 || _startedUtc[i] < _startedUtc[best]) best = i;
        }
        if (best >= 0)
            return best;

        // Everything in range is a landed blow. A miss does not get to displace one.
        if (arrivingIsSheddable)
            return -1;

        for (var i = 0; i < MaxInFlight; i++)
        {
            if (!_busy[i]) continue;
            if (anchor is int a && _anchor[i] != a) continue;
            if (best < 0 || _startedUtc[i] < _startedUtc[best]) best = i;
        }
        return best;
    }
}
