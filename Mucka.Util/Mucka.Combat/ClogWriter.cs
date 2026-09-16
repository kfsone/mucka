using System.Linq;
using MudSharp.Combat;
using MudSharp.Models;
using Mucka.Store;

namespace Mucka.Combat;

/// <summary>
/// Records one encounter's combat log into the <c>encounter*</c> tables (see <see cref="MuckaDb"/>),
/// so real fights can be analyzed offline. Driven entirely by MudSession/MuckaConnection events - see
/// MuckaConnection's wiring for CombatTracker's InCombatChanged/CombatEventOccurred.
///
/// <para>An encounter is one <c>encounters</c> row plus the rows keyed to it: the previous
/// <see cref="PreBufferLines"/> non-combat lines and the trailing tail (<c>encounter_lines</c>), one
/// row per classified CombatEvent (<c>encounter_events</c>), the stats and room-contents snapshots
/// below, and the resolved creature values.</para>
///
/// <para>Two row types record state that MOVES during a fight, because a start-of-encounter reading
/// of either was found to be useless for the questions the corpus is kept to answer:</para>
/// <list type="bullet">
/// <item><b>contents</b> - the structured FEI list (room contents + carried inventory), written
/// whenever it differs from the last one written. The pre-roll holds the room description as prose,
/// which is not a substitute: it cannot be diffed, it omits the pack entirely, and it is silent about
/// anything that walked in afterwards. Re-writing on change is what makes a chase across rooms
/// reconstructible.</item>
/// <item><b>stats</b> - an absolute stats snapshot, written whenever a load-relevant value changes
/// (see <see cref="OnStatsUpdated"/>), so a fight that drops several items still carries a reading
/// after each one. Each drop or take with a fresh reading either side yields that object's dexterity
/// cost (keyed on item COUNT) and its strength cost (keyed on WEIGHT) - two numbers per object, from
/// observation alone.</item>
/// </list>
///
/// <para><b>Tail capture.</b> CombatTracker closes an encounter the instant its last active NPC
/// dies/flees - a decisive combat-state fact. But the server's own cleanup for that (score,
/// "The X has just passed on.", dropped items, level-up) routinely PRINTS after the death line
/// that closed it, and always before the next prompt. Rather than end the encounter's log exactly on
/// InCombatChanged(false), <see cref="Stop"/> only marks the entry "closing": every subsequent plain
/// line is still appended to it (phase 'tail') until the next <c>IsPartial</c> prompt line arrives
/// (the frame boundary - see MudSession's setup-swallow remarks for the same signal used the same
/// way), at which point <see cref="OnLineReady"/> stamps the encounter's end. This is a LOGGING
/// concern only - MUD2 has no such mechanic, and it never delays
/// InCombatChanged/IsInCombatGracePeriod's UI-visible flip.</para>
///
/// <para><b>Overlapping encounters.</b> A new encounter can legitimately start (Start()) while the
/// previous one is still draining its tail - e.g. one rat dies and a second, unrelated rat
/// attacks before the next prompt (this is a NEW encounter, not a continuation - see
/// CombatTracker's remarks). Both are kept open simultaneously in <see cref="_open"/>; classified
/// CombatEvents route to whichever entry is still actively live (there is at most one), while a plain
/// line during the overlap is appended to every entry still draining its tail.</para>
///
/// <para>Deliberately partial: this is not a raw capture (the wire log covers that). This is reduced
/// to the classified facts, which is what makes it queryable.</para>
///
/// <para>Always on: arming after the fact is too late, since the evidence would be missing
/// precisely when something interesting had just happened - the same argument the fight history
/// and the swing ledger already settle the same way. The pre-roll buffer costs a bounded queue per
/// line.</para>
///
/// <para>Threading: all On* methods are called from MudSession's Feed thread (same contract as
/// EffectTracker/CombatTracker - see MudSession's class doc comment). Does not touch UI types, and
/// does no I/O: every method here builds a row and hands it to <see cref="MuckaStore"/>, whose single
/// background task does the write. A synchronous write on the thread that parses incoming combat text
/// would stall that thread and delay the combat text itself, which no UI-side throttle can fix.</para>
/// </summary>
public sealed class ClogWriter : IDisposable
{
    private const int PreBufferLines = 30;

    /// <summary>One still-open encounter. "Closing" means the encounter itself has ended (Stop() was
    /// called) but it is still draining its tail, waiting for the next prompt (see the class
    /// remarks).</summary>
    private sealed class OpenEncounter
    {
        /// <summary>The encounter key - <c>encounter_started_at_ms</c>, as MuckaConnection stamped it,
        /// which is what joins these rows to swings and fights.</summary>
        public required long Key;
        public bool Closing;
        public DateTime EndedUtc;
        /// <summary>Next ordinal for a tail line in this encounter.</summary>
        public int TailOrd;
        // Names a creature_values row has already been written for this encounter. Unnumbered
        // mobs (thief, banshee, coot, fox) have no instance number, so a single `value <name>`
        // probe can draw one reply per live creature sharing that name - a second row for a name
        // already seen is flagged ambiguous rather than silently overwriting the first (see
        // OnCreatureValueResolved).
        public readonly HashSet<string> ValuedNames = new(StringComparer.OrdinalIgnoreCase);
    }

    // Supplied by the caller so this class stays free of MAUI references and can be tested from
    // Mucka.Util.Tests against a temp path - the same split FightHistoryStore and SwingLedger use.
    private readonly MuckaStore _store;

    private readonly object _lock = new();
    private readonly Queue<string> _recentLines = new();

    // Normally 0 or 1 entries; briefly 2 while a new encounter is live and the previous one is
    // still draining its tail (see the class remarks on overlapping encounters).
    private readonly List<OpenEncounter> _open = [];

    private GameStatsSnapshot _lastStats = GameStatsSnapshot.Empty;

    // The login these encounters belong to - persona_sessions.id. Null at the shell.
    private long? _personaSessionId;
    private StatusEffectState _lastEffects = StatusEffectState.Empty;
    private string? _lastRoom;

    // -- FEI room contents / carried inventory ---------------------------------
    // The structured room-contents list the Here panel is built from. The pre-roll captures the
    // room description as PROSE, which is not the same thing: it names nothing that arrived after
    // the description was printed, it cannot be diffed, and it does not carry the carried-items
    // half at all. Same split the side panel makes (items before "========" are in the room, items
    // after it are in the pack) - deliberately re-derived here from the session's own FEI events
    // rather than read off the view model, so this stays a Feed-thread, MAUI-free class.
    private const string FeiSeparator = "========";
    private readonly List<string> _pendingRoomItems = [];
    private readonly List<string> _pendingCarried = [];
    private bool _feiPastSeparator;
    // Creature-vs-object classification, from the game's own C04-coded presence sentences. Only the
    // sentence-derived half of the side panel's rule: the panel also unions "currently being
    // fought", which is NOT re-derived here because this same file already records every
    // CombatEvent by NPC name, so merging the two sources at write time would only destroy the
    // provenance a later pass can reconstruct for itself.
    private readonly RoomCreatures _roomCreatures = new();
    private string[]? _roomItems;
    private bool[]? _roomItemIsCreature;
    private string[]? _carried;
    private string? _contentsRoom;

    // -- Stat-change snapshots -------------------------------------------------
    // Last values actually WRITTEN (encounter_start counts as a write - see Start), so a "stats"
    // row is only emitted when something moved. See OnStatsUpdated for the trigger rules and
    // StatsBlock for why these are absolute snapshots rather than deltas.
    private GameStatsSnapshot _lastWrittenStats = GameStatsSnapshot.Empty;
    private StatusEffectState _lastWrittenEffects = StatusEffectState.Empty;
    // Set when the loadout is known to have just changed (an ItemDropped line, or a carried list
    // that differs from the last one). Forces the NEXT stats reading to be written even if nothing
    // moved, so "this object costs nothing" is recorded as a measurement rather than as a gap.
    private bool _statsRowDue;

    /// <summary>True while an encounter is being actively recorded (CombatEvents still arriving).
    /// False during tail-only draining - see <see cref="IsTailOnly"/> for that state.</summary>
    public bool IsRecording { get; private set; }
    /// <summary>The actively-recording encounter's key (<c>encounter_started_at_ms</c>), or null when
    /// none is live - even if a previous encounter's tail is still draining (see
    /// <see cref="IsTailOnly"/>).</summary>
    public long? CurrentEncounterKey { get; private set; }

    /// <summary>True while at least one encounter's tail is still draining (waiting for the next
    /// prompt to finalize) and no encounter is actively live. Drives the UI's "winding down"
    /// cosmetic (see MuckaConnection.IsInCombatGracePeriod) - the fight really is over
    /// (InCombat is already false), but the clog for it has not been finalized yet.</summary>
    public bool IsTailOnly { get; private set; }
    /// <summary>Fires whenever <see cref="IsTailOnly"/> flips.</summary>
    public event Action<bool>? TailOnlyChanged;

    public ClogWriter(MuckaStore store) => _store = store;

    /// <summary>Feed every line - prompts included - so the pre-roll buffer stays fresh and any
    /// encounter still draining its tail gets its trailing prose captured (see the class
    /// remarks).</summary>
    public void OnLineReady(StyledLine line)
    {
        lock (_lock)
        {
            if (line.IsPartial)
            {
                // The frame's closing prompt: nothing printed after it can belong to a fight that
                // already ended before it, so every encounter still draining its tail is done.
                foreach (var entry in _open.Where(e => e.Closing).ToList())
                    FinalizeLocked(entry);
                return;
            }

            var text = line.PlainText;
            if (string.IsNullOrEmpty(text))
                return;

            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var entry in _open)
            {
                if (!entry.Closing)
                    continue;   // combat lines from an active fight are recorded via OnCombatEvent
                _store.Enqueue(new EncounterLineRow(entry.Key, "tail", entry.TailOrd++, ts, text));
            }

            if (_open.Any(e => !e.Closing))
                return;   // an actively-recording encounter already covers its own text via events

            _recentLines.Enqueue(text);
            while (_recentLines.Count > PreBufferLines)
                _recentLines.Dequeue();
        }
    }

    /// <summary>
    /// A fresh stats reading. Writes a "stats" row to every open encounter when it carries
    /// something new, and otherwise only updates the cached value.
    ///
    /// <para><b>Snapshots, not deltas.</b> Two reasons, and neither is about which is prettier.
    /// SIZE: a row is written only when a tracked value actually moved, so the count is bounded by
    /// how often the game's numbers change, not by how often it is asked - a full absolute row
    /// costs a few hundred bytes and a fight produces a handful of them, against a ~7 KB mean clog.
    /// CONSUMPTION: the measurement these exist for is "reading immediately before the drop,
    /// reading immediately after", which against absolute rows is a scan to the nearest row either
    /// side. Against deltas it is a replay from the header, where one row lost to a mid-fight app
    /// exit silently corrupts every value after it - and the header's own stats block is already
    /// absolute, so deltas would also mean two representations of one thing in one file.</para>
    ///
    /// <para><b>Stamina alone does not trigger a row</b> (it rides along in every row that is
    /// written). It changes on essentially every incoming blow, and it is already recorded blow by
    /// blow in the "event" rows' own text - a row per swing would roughly double the file to
    /// restate what is on the line above it. Everything else that FES reports does trigger:
    /// strength and dexterity (effective, raw and max - the gap between them IS the burden being
    /// measured), the object counts, magic, level, max stamina, the four afflictions, and the
    /// weather.</para>
    ///
    /// <para>The <c>reason</c> field says which rule fired, so a consumer can tell a reading that
    /// was forced after a loadout change (and may show no movement at all - a genuine result) from
    /// one that was written because something moved.</para>
    /// </summary>
    public void OnStatsUpdated(GameStatsSnapshot stats)
    {
        lock (_lock)
        {
            _lastStats = stats;
            if (_open.Count == 0)
                return;
            var due = _statsRowDue;
            if (!due && !LoadRelevantChange(_lastWrittenStats, stats))
                return;
            WriteStatsLocked(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), due ? "post_inventory" : "change");
        }
    }

    /// <summary>Which FES fields warrant a new "stats" row. Stamina is deliberately absent - see
    /// <see cref="OnStatsUpdated"/>.</summary>
    private static bool LoadRelevantChange(GameStatsSnapshot a, GameStatsSnapshot b)
        => a.Strength != b.Strength || a.RawStrength != b.RawStrength || a.MaxStrength != b.MaxStrength
        || a.Dexterity != b.Dexterity || a.RawDexterity != b.RawDexterity || a.MaxDexterity != b.MaxDexterity
        || a.ObjectsCarried != b.ObjectsCarried || a.MaxObjectsCarried != b.MaxObjectsCarried
        || a.CurrentMagic != b.CurrentMagic || a.MaxMagic != b.MaxMagic
        // Not level: it is a function of score, and the only thing it changes that a reader of this
        // file could act on is the stat maxima, which are already three of the tests above.
        || a.MaxStamina != b.MaxStamina
        || a.IsBlind != b.IsBlind || a.IsDeaf != b.IsDeaf
        || a.IsCrippled != b.IsCrippled || a.IsDumb != b.IsDumb
        || a.Weather != b.Weather;

    /// <summary>The seven independent buff/debuff/glow slots, without the tooltip messages that
    /// ride with them - a boolean flip is the whole of what a stats row needs.</summary>
    private static bool EffectFlagsEqual(StatusEffectState a, StatusEffectState b)
        => a.StrengthBuff == b.StrengthBuff && a.StrengthDebuff == b.StrengthDebuff
        && a.DexterityBuff == b.DexterityBuff && a.DexterityDebuff == b.DexterityDebuff
        && a.StaminaBuff == b.StaminaBuff && a.StaminaDebuff == b.StaminaDebuff
        && a.Glow == b.Glow;

    /// <summary>Writes one "stats" row to every open encounter - active or still draining its tail
    /// - and re-baselines what counts as "changed". Caller holds <see cref="_lock"/>.</summary>
    private void WriteStatsLocked(long ts, string reason)
    {
        foreach (var entry in _open)
            _store.Enqueue(StatsRow(entry.Key, ts, reason));
        RebaselineStatsLocked();
    }

    private void RebaselineStatsLocked()
    {
        _lastWrittenStats = _lastStats;
        _lastWrittenEffects = _lastEffects;
        _statsRowDue = false;
    }

    /// <summary>
    /// One absolute stats snapshot, for <c>encounter_stats</c>. Caller holds <see cref="_lock"/>.
    ///
    /// <para><c>CarriedCount</c> is NOT <c>ObjectsCarried</c> and the two are both here on purpose.
    /// FES carries no object count at all, so <c>ObjectsCarried</c> reaches
    /// <see cref="GameStatsSnapshot"/> only by parsing a <c>score</c> sheet - typically the
    /// automatic one at character select - and then sits frozen for the rest of the session however
    /// much the player picks up or puts down. <c>CarriedCount</c> is the length of the live FEI
    /// carry list, which is the figure the dexterity burden is actually keyed on. Null until a FEI
    /// list has completed, which is a real distinction from an empty pack.</para>
    /// </summary>
    private EncounterStatsRow StatsRow(long key, long ts, string reason) => new()
    {
        Key = key,
        TimestampMs = ts,
        Reason = reason,
        Stamina = _lastStats.Stamina,
        MaxStamina = _lastStats.MaxStamina,
        Strength = _lastStats.Strength,
        RawStrength = _lastStats.RawStrength,
        MaxStrength = _lastStats.MaxStrength,
        Dexterity = _lastStats.Dexterity,
        RawDexterity = _lastStats.RawDexterity,
        MaxDexterity = _lastStats.MaxDexterity,
        Magic = _lastStats.CurrentMagic,
        MaxMagic = _lastStats.MaxMagic,
        ObjectsCarried = _lastStats.ObjectsCarried,
        MaxObjectsCarried = _lastStats.MaxObjectsCarried,
        CarriedCount = _carried?.Length,
        GamesPlayed = _lastStats.GamesPlayed,
        Weather = _lastStats.Weather.ToString(),
        IsBlind = _lastStats.IsBlind,
        IsDeaf = _lastStats.IsDeaf,
        IsCrippled = _lastStats.IsCrippled,
        IsDumb = _lastStats.IsDumb,
        StrengthBuff = _lastEffects.StrengthBuff,
        StrengthDebuff = _lastEffects.StrengthDebuff,
        DexterityBuff = _lastEffects.DexterityBuff,
        DexterityDebuff = _lastEffects.DexterityDebuff,
        StaminaBuff = _lastEffects.StaminaBuff,
        StaminaDebuff = _lastEffects.StaminaDebuff,
        Glow = _lastEffects.Glow,
    };

    /// <summary>
    /// Supplies the current reset's identity, for the header's <c>reset</c> block. Set by
    /// MuckaConnection at wire-up; null-safe, since a clog written before the clock has locked on is
    /// still worth having.
    ///
    /// <para><b>Why a delegate rather than a pushed value.</b> The estimate is polled, not evented at
    /// the granularity a clog needs - it refines continuously as ResetClock narrows its lock - and a
    /// value pushed on stats updates would be whatever the last FES heartbeat happened to see. Asking at
    /// the moment an encounter opens gets the best answer available then.</para>
    /// </summary>
    public Func<MudSharp.Session.ResetEstimate>? ResetEstimateProvider { get; set; }
    /// <summary>A buff/debuff/glow slot flipped. Worth a "stats" row of its own: a strength or
    /// dexterity buff landing mid-fight moves the exact numbers an object-cost measurement is
    /// differencing, and without a row saying so the movement would be attributed to the loadout.
    /// </summary>
    /// <summary>A persona session opened or closed - one login. Every encounter opened from here on
    /// belongs to it; null at the shell.</summary>
    public void OnPersonaSessionChanged(long? personaSessionId)
    {
        lock (_lock)
            _personaSessionId = personaSessionId;
    }

    public void OnStatusEffectsChanged(StatusEffectState effects)
    {
        lock (_lock)
        {
            _lastEffects = effects;
            if (_open.Count == 0 || EffectFlagsEqual(_lastWrittenEffects, effects))
                return;
            WriteStatsLocked(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "effects");
        }
    }

    public void OnRoomShortReady(string room)
    {
        lock (_lock)
            _lastRoom = room;
    }

    // -- FEI room contents / carried inventory ---------------------------------

    /// <summary>A new room. Forgets the previous room's creature sentences, exactly as the side
    /// panel's own index does - a name that was a creature there says nothing about here.</summary>
    public void OnRoomEntered()
    {
        lock (_lock)
            _roomCreatures.Clear();
    }

    /// <summary>One creature-presence sentence, in the game's own words. The only evidence MUD2
    /// gives about which FEI names are alive - see <see cref="RoomCreatures"/>.</summary>
    public void OnCreatureTextReady(string text)
    {
        lock (_lock)
            _roomCreatures.Observe(text);
    }

    /// <summary>The FEI list is starting. Clears the pending buffers.</summary>
    public void OnFeiListStarting()
    {
        lock (_lock)
        {
            _pendingRoomItems.Clear();
            _pendingCarried.Clear();
            _feiPastSeparator = false;
        }
    }

    /// <summary>One FEI entry. Everything before the "========" separator is in the room;
    /// everything after it is in the pack.</summary>
    public void OnFeiItemReady(string item)
    {
        lock (_lock)
        {
            if (item == FeiSeparator)
                _feiPastSeparator = true;
            else if (_feiPastSeparator)
                _pendingCarried.Add(item);
            else
                _pendingRoomItems.Add(item);
        }
    }

    /// <summary>
    /// The FEI list is complete. Writes a "contents" row to every open encounter when it differs
    /// from the last one - by room, by the room's names, by their creature classification, or by
    /// the pack - and nothing at all when it does not, which is the common case at the ~1 Hz rate
    /// FEI actually arrives at.
    ///
    /// <para>When the CARRIED half changed, a "stats" row follows immediately. FES leads every
    /// probe, so the reading for a drop lands before the FEI list that reflects it; without this
    /// second row the freshest strength/dexterity figures in the file would be paired with the
    /// pre-drop carry count, which is precisely the pairing the measurement must not get wrong.
    /// </para>
    /// </summary>
    public void OnFeiListComplete()
    {
        lock (_lock)
        {
            var room = _pendingRoomItems.ToArray();
            var carried = _pendingCarried.ToArray();
            _pendingRoomItems.Clear();
            _pendingCarried.Clear();

            var creature = new bool[room.Length];
            for (var i = 0; i < room.Length; i++)
                creature[i] = _roomCreatures.IsCreature(room[i]);

            var carriedChanged = _carried is null || !_carried.AsSpan().SequenceEqual(carried);
            var changed = carriedChanged
                || _roomItems is null
                || !_roomItems.AsSpan().SequenceEqual(room)
                || !_roomItemIsCreature!.AsSpan().SequenceEqual(creature)
                || !string.Equals(_contentsRoom, _lastRoom, StringComparison.Ordinal);

            _roomItems = room;
            _roomItemIsCreature = creature;
            _carried = carried;
            _contentsRoom = _lastRoom;

            if (carriedChanged)
                _statsRowDue = true;
            if (!changed || _open.Count == 0)
                return;

            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            WriteContentsLocked(ts);
            if (carriedChanged)
                WriteStatsLocked(ts, "post_inventory");
        }
    }

    /// <summary>Writes one contents row - parent plus its items - to every open encounter. No-ops
    /// before a FEI list has completed, which is the honest state when the client has attached
    /// mid-room and the game has not yet said what is here. Caller holds <see cref="_lock"/>.</summary>
    private void WriteContentsLocked(long ts)
    {
        if (_roomItems is null)
            return;
        var items = _roomItems
            .Select((name, i) => new EncounterContentsItem(name, _roomItemIsCreature![i], IsCarried: false))
            .Concat(_carried!.Select(name => new EncounterContentsItem(name, IsCreature: false, IsCarried: true)))
            .ToArray();
        foreach (var entry in _open)
            _store.Enqueue(new EncounterContentsRow(entry.Key, ts, _contentsRoom, _carried!.Length, items));
    }

    /// <summary>Wire directly to MudSession/MuckaConnection's InCombatChanged.
    /// <paramref name="encounterStartedAtMs"/> is the shared encounter key - see
    /// <see cref="Start"/>.</summary>
    public void OnInCombatChanged(bool inCombat, long? encounterStartedAtMs = null)
    {
        if (inCombat)
            Start(encounterStartedAtMs);
        else
            Stop();
    }

    /// <summary>Wire directly to MudSession/MuckaConnection's CombatEventOccurred.</summary>
    public void OnCombatEvent(CombatEvent e)
    {
        lock (_lock)
        {
            // At most one entry is ever actively recording - a closing (tail-only) entry gets no
            // more CombatEvents, since its own NPC(s) are already resolved by the time it closes.
            // Any of the four loadout lines changes what the player is carrying, so the next stats
            // reading must be written even if it turns out identical - "this object cost nothing" is
            // a result, and it is indistinguishable from a missing reading unless the row is there.
            // Set before the active-entry check because the move is a fact about the player, not the
            // fight.
            if (e.Kind is CombatEventKind.ItemDropped or CombatEventKind.ItemTaken
                       or CombatEventKind.ItemStowed or CombatEventKind.ItemRetrieved)
                _statsRowDue = true;
            // At most one entry is ever actively recording - a closing (tail-only) entry gets no
            // more CombatEvents, since its own NPC(s) are already resolved by the time it closes.
            var active = _open.FirstOrDefault(entry => !entry.Closing);
            if (active is null)
                return;
            _store.Enqueue(new EncounterEventRow(
                active.Key,
                new DateTimeOffset(e.TimestampUtc).ToUnixTimeMilliseconds(),
                e.Kind.ToString(),
                e.Actor?.ToString(),
                e.NpcName,
                e.Weapon,
                e.Container,
                e.RangeLow,
                e.RangeHigh,
                e.HealthRung,
                e.HealthPhrase,
                e.RawText));
        }
    }

    /// <summary>Wire directly to MudSession/MuckaConnection's CreatureValueResolved. Records the
    /// (creature, points) pair as its own fact line alongside the other per-creature CombatEvent
    /// facts, so the corpus starts accumulating these directly instead of only inferring them from
    /// kill awards (see the score sheet's "this game" delta, which conflates every kill in the
    /// window rather than crediting one creature).
    ///
    /// A second row for a name already written this encounter is flagged <c>ambiguous</c>: unnumbered
    /// mobs (thief, banshee, coot, fox) have no instance number, so one `value &lt;name&gt;` probe can
    /// draw a reply from more than one live creature sharing that name, and nothing here has any way
    /// to tell whose is whose - see FightAccumulator.NoteValue for the matching correction on the
    /// roster row itself.</summary>
    public void OnCreatureValueResolved(string name, int value)
    {
        lock (_lock)
        {
            var active = _open.FirstOrDefault(entry => !entry.Closing);
            if (active is null)
                return;
            var ambiguous = !active.ValuedNames.Add(name);
            _store.Enqueue(new CreatureValueRow(
                active.Key, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), name, value, ambiguous));
        }
    }

    /// <summary>
    /// Opens an encounter. <paramref name="encounterStartedAtMs"/> is the key MuckaConnection stamps
    /// ONCE and hands to every consumer; it is what joins these rows to <c>swings</c> and
    /// <c>fights</c>. A local reading is taken only when nobody supplied one, which is the unit-test
    /// and design-time path - the same fallback FightHistoryRecorder uses, and for the same reason: a
    /// row keyed to itself is still better than an unkeyed one.
    /// </summary>
    private void Start(long? encounterStartedAtMs)
    {
        lock (_lock)
        {
            // Defensive: CombatTracker only fires InCombatChanged(true) on a false->true
            // transition, so a second Start() while one is already active should never happen.
            if (_open.Any(e => !e.Closing))
                return;

            var startedMs = encounterStartedAtMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var entry = new OpenEncounter { Key = startedMs };
            _open.Add(entry);
            IsRecording = true;
            CurrentEncounterKey = startedMs;
            UpdateTailOnlyLocked();

            _store.Enqueue(new EncounterRow(
                startedMs,
                _lastRoom,
                _lastStats.Weather.ToString(),
                _lastStats.TimeToReset,
                _personaSessionId));

            var ord = 0;
            foreach (var text in _recentLines)
                _store.Enqueue(new EncounterLineRow(startedMs, "preroll", ord++, null, text));

            // The encounter's own first stats and contents rows, so its opening state is read back
            // exactly where every later reading is read from rather than out of a second shape.
            _store.Enqueue(StatsRow(startedMs, startedMs, "start"));
            WriteContentsLocked(startedMs);
            // Those rows ARE this encounter's baseline for change detection, so the next stats row is
            // written only if something has actually moved since.
            RebaselineStatsLocked();
        }
    }

    private void Stop()
    {
        lock (_lock)
        {
            var active = _open.FirstOrDefault(e => !e.Closing);
            if (active is null)
                return;
            active.Closing = true;
            active.EndedUtc = DateTime.UtcNow;
            IsRecording = false;
            CurrentEncounterKey = null;
            UpdateTailOnlyLocked();
            // Deliberately does NOT stamp the encounter's end yet: the server's own cleanup for this
            // close (score, "has just passed on", dropped items) routinely prints AFTER the line that
            // triggered this Stop() and BEFORE the next prompt, and it still belongs to this
            // encounter even if a brand new one starts in the meantime (see the class remarks on tail
            // capture / overlapping encounters). OnLineReady's IsPartial branch is what actually
            // finalizes this entry, once that prompt arrives.
        }
    }

    /// <summary>Stamps the encounter's end and drops it. Called once per entry, either from the next
    /// prompt after Stop() (the normal path) or from Dispose (best-effort, for whatever is still open
    /// at shutdown).</summary>
    private void FinalizeLocked(OpenEncounter entry)
    {
        _store.Enqueue(new EncounterEndRow(
            entry.Key, new DateTimeOffset(entry.EndedUtc, TimeSpan.Zero).ToUnixTimeMilliseconds()));
        _open.Remove(entry);
        UpdateTailOnlyLocked();
    }

    private void UpdateTailOnlyLocked()
    {
        var tailOnly = _open.Count > 0 && _open.All(e => e.Closing);
        if (tailOnly == IsTailOnly)
            return;
        IsTailOnly = tailOnly;
        TailOnlyChanged?.Invoke(tailOnly);
    }

    /// <summary>Stamps the end of whatever is still open - active or mid-tail - so an app exit
    /// mid-fight still leaves a closed encounter. The rows themselves are drained by the store, which
    /// MuckaConnection disposes after this.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            foreach (var entry in _open.ToList())
            {
                if (!entry.Closing)
                    entry.EndedUtc = now;
                FinalizeLocked(entry);
            }
            IsRecording = false;
            CurrentEncounterKey = null;
        }
    }
}
