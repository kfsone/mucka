using System.Linq;
using System.Text.Json;
using System.Threading.Channels;
using MudSharp.Combat;
using MudSharp.Models;

namespace Mucka.Core;

/// <summary>
/// Records one JSONL "clog" (combat log) per encounter under ~/.mucka/clogs/, so real fights can
/// be analyzed offline. Driven entirely by MudSession/MuckaConnection events - see MuckaConnection's
/// wiring for CombatTracker's InCombatChanged/CombatEventOccurred.
///
/// <para>Each file has one header line (type "encounter_start": the previous ~30 non-combat
/// lines plus a snapshot of stats/status-effects/room/room-contents at the moment combat began -
/// everything a later analysis pass needs to answer "was the player invisible / what was the
/// weather / what were their stats / what was in the room and in the pack" without replaying the
/// whole session), one line per classified CombatEvent (type "event"), zero or more trailing plain
/// lines (type "line" - see the tail-capture remarks below), and one footer line
/// (type "encounter_end").</para>
///
/// <para>Two further row types record state that MOVES during a fight, because a header-only
/// reading of either was found to be useless for the questions the corpus is kept to answer:</para>
/// <list type="bullet">
/// <item><b>"contents"</b> - the structured FEI list (room contents + carried inventory), emitted
/// whenever it differs from the last one written. The header's <c>preroll</c> holds the room
/// description as prose, which is not a substitute: it cannot be diffed, it omits the pack
/// entirely, and it is silent about anything that walked in afterwards. Re-emitting on change is
/// what makes a chase across rooms reconstructible.</item>
/// <item><b>"stats"</b> - an absolute stats snapshot, emitted whenever a load-relevant value
/// changes (see <see cref="OnStatsUpdated"/>), so a fight that drops several items still carries a
/// reading after each one. Each drop or take with a fresh reading either side yields that object's
/// dexterity cost (keyed on item COUNT) and its strength cost (keyed on WEIGHT) - two numbers per
/// object, from observation alone.</item>
/// </list>
///
/// <para><b>Tail capture.</b> CombatTracker closes an encounter the instant its last active NPC
/// dies/flees - a decisive combat-state fact. But the server's own cleanup for that (score,
/// "The X has just passed on.", dropped items, level-up) routinely PRINTS after the death line
/// that closed it, and always before the next prompt. Rather than end the clog exactly on
/// InCombatChanged(false), <see cref="Stop"/> only marks the encounter's entry "closing": every
/// subsequent plain line is still appended to it (type "line") until the next <c>IsPartial</c>
/// prompt line arrives (the frame boundary - see MudSession's setup-swallow remarks for the same
/// signal used the same way), at which point <see cref="OnLineReady"/> writes the real
/// "encounter_end" footer and finalizes the file. This is a LOGGING concern only - MUD2 has no
/// such mechanic, and it never delays InCombatChanged/IsInCombatGracePeriod's UI-visible flip.</para>
///
/// <para><b>Overlapping clogs.</b> A new encounter can legitimately start (Start()) while the
/// previous one is still draining its tail - e.g. one rat dies and a second, unrelated rat
/// attacks before the next prompt (this is a NEW encounter, not a continuation - see
/// CombatTracker's remarks). Both are kept open simultaneously in <see cref="_open"/>, each with
/// its own file, queue, and drain task; classified CombatEvents route to whichever entry is still
/// actively live (there is at most one), while a plain line during the overlap is appended to
/// every entry still draining its tail.</para>
///
/// <para>Deliberately partial: this is not a full raw capture (SessionCapture already covers
/// that, opt-in, for debugging). A clog is intentionally reduced to keep these lightweight enough
/// to accumulate over many sessions.</para>
///
/// <para>Always on: arming after the fact is too late, since the evidence would be missing
/// precisely when something interesting had just happened - the same argument the fight history
/// and the swing ledger already settle the same way. A clog is small and the pre-roll buffer costs
/// a bounded queue per line.</para>
///
/// <para>Threading: all On* methods are called from MudSession's Feed thread (same contract as
/// EffectTracker/CombatTracker - see MudSession's class doc comment). Does not touch UI types.</para>
///
/// <para>File I/O runs off the Feed thread entirely: <see cref="WriteEntryLocked"/> only serializes
/// the entry (cheap, in-memory) and enqueues the line; a per-encounter background task
/// (<see cref="DrainAsync"/>) owns the actual <see cref="StreamWriter"/> and does the blocking disk
/// write - a synchronous open/flush on the same thread that parses incoming combat text would stall
/// that thread and delay the combat text itself, which no UI-side throttle can fix.
/// <see cref="Dispose"/> waits (briefly - just draining whatever is already queued in memory) for
/// every still-open encounter's drain to finish,
/// so an app exit mid-fight (or mid-tail) cannot lose the encounter_end line or anything queued just
/// before it.</para>
/// </summary>
public sealed class ClogWriter : IDisposable
{
    private const int PreBufferLines = 30;

    /// <summary>One still-open clog. "Closing" means the encounter itself has ended (Stop() was
    /// called) but the file is not finalized yet - it is still draining its tail, waiting for the
    /// next prompt (see the class remarks). Owns its StreamWriter's lifetime via WriterTask.</summary>
    private sealed class OpenEncounter
    {
        public required string FilePath;
        public required Channel<string> Queue;
        public required Task WriterTask;
        public bool Closing;
        public DateTime EndedUtc;
        // Names a "creature_value" row has already been written for this encounter. Unnumbered
        // mobs (thief, banshee, coot, fox) have no instance number, so a single `value <name>`
        // probe can draw one reply per live creature sharing that name - a second row for a name
        // already seen is flagged ambiguous rather than silently overwriting the first (see
        // OnCreatureValueResolved).
        public readonly HashSet<string> ValuedNames = new(StringComparer.OrdinalIgnoreCase);
    }

    // Directory supplied by the caller (see ClogPaths.GetClogDirectory, which owns the platform
    // lookup) so this class stays free of MAUI references and can be linked into mudsharp.Tests
    // against a temp path - the same split CombatDb/FightHistoryStore/SwingLedger already use for
    // their own files.
    private readonly string _directory;

    private readonly object _lock = new();
    private readonly Queue<string> _recentLines = new();

    // Normally 0 or 1 entries; briefly 2 while a new encounter is live and the previous one is
    // still draining its tail (see the class remarks on overlapping clogs).
    private readonly List<OpenEncounter> _open = [];
    // Purely to keep filenames unique: two encounters can now legitimately start within the same
    // millisecond (a new fight opening the instant the previous one's last NPC dies is routine
    // under the current CombatTracker, not a rare race).
    private int _startSequence;

    private GameStatsSnapshot _lastStats = GameStatsSnapshot.Empty;
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
    /// <summary>The actively-recording encounter's file, or null when none is live (even if a
    /// previous encounter's tail is still draining - see <see cref="IsTailOnly"/>).</summary>
    public string? FilePath { get; private set; }

    /// <summary>True while at least one encounter's tail is still draining (waiting for the next
    /// prompt to finalize) and no encounter is actively live. Drives the UI's "winding down"
    /// cosmetic (see MuckaConnection.IsInCombatGracePeriod) - the fight really is over
    /// (InCombat is already false), but the clog for it has not been finalized yet.</summary>
    public bool IsTailOnly { get; private set; }
    /// <summary>Fires whenever <see cref="IsTailOnly"/> flips.</summary>
    public event Action<bool>? TailOnlyChanged;

    public ClogWriter(string directory) => _directory = directory;

    // Testability seam only: every writer task that MIGHT still be draining, so a test can
    // deterministically wait for a FINALIZED encounter's background drain to actually reach disk
    // before reading the file back. Production never needs this - Dispose() only waits on entries
    // still in _open, which is correct there (once OnLineReady's prompt branch finalizes and
    // removes an entry, nothing else in the app is waiting on its file).
    //
    // Pruned of already-completed tasks on every Start() (see there) rather than left to grow for
    // the process's whole lifetime: a completed Task has nothing left to wait on, so dropping it
    // costs the test seam nothing, and it is what keeps this list bounded by "how many encounters
    // are concurrently draining right now" (normally 0-2, per the class remarks on overlapping
    // clogs) instead of by "how many encounters this session has ever had" - the second was an
    // unbounded per-encounter growth for a field only tests ever read.
    private readonly List<Task> _writerTasksForTests = [];

    internal void WaitForDrainsToSettle_TestOnly(TimeSpan timeout)
    {
        Task[] tasks;
        lock (_lock)
            tasks = _writerTasksForTests.ToArray();
        Task.WaitAll(tasks, timeout);
    }

    /// <summary>How many writer tasks are currently tracked for <see cref="WaitForDrainsToSettle_TestOnly"/>.
    /// Test-only: exists so a test can assert the pruning in <see cref="Start"/> actually bounds this,
    /// rather than trusting it by inspection.</summary>
    internal int WriterTaskTrackingCount_TestOnly
    {
        get
        {
            lock (_lock)
                return _writerTasksForTests.Count;
        }
    }

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

            foreach (var entry in _open)
            {
                if (!entry.Closing)
                    continue;   // combat lines from an active fight are recorded via OnCombatEvent
                WriteEntryLocked(entry, new
                {
                    type = "line",
                    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    text,
                });
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
        || a.MaxStamina != b.MaxStamina || a.Level != b.Level
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
        var payload = new
        {
            type = "stats",
            ts,
            reason,
            stats = StatsBlock(),
            effectFlags = EffectFlagsBlock(),
        };
        foreach (var entry in _open)
            WriteEntryLocked(entry, payload);
        RebaselineStatsLocked();
    }

    private void RebaselineStatsLocked()
    {
        _lastWrittenStats = _lastStats;
        _lastWrittenEffects = _lastEffects;
        _statsRowDue = false;
    }

    /// <summary>
    /// The absolute stats block, shared by "encounter_start" and every "stats" row so a consumer
    /// reads one shape wherever it finds it.
    ///
    /// <para><c>carriedCount</c> is NOT <c>objectsCarried</c> and the two are both here on purpose.
    /// FES carries no object count at all, so <c>objectsCarried</c> reaches
    /// <see cref="GameStatsSnapshot"/> only by parsing a <c>score</c> sheet - typically the
    /// automatic one at character select - and then sits frozen for the rest of the session however
    /// much the player picks up or puts down. <c>carriedCount</c> is the length of the live FEI
    /// carry list, which is the figure the dexterity burden is actually keyed on. Null until a FEI
    /// list has completed, which is a real distinction from an empty pack.</para>
    /// </summary>
    private object StatsBlock() => new
    {
        stamina = _lastStats.Stamina,
        maxStamina = _lastStats.MaxStamina,
        strength = _lastStats.Strength,
        rawStrength = _lastStats.RawStrength,
        maxStrength = _lastStats.MaxStrength,
        dexterity = _lastStats.Dexterity,
        rawDexterity = _lastStats.RawDexterity,
        maxDexterity = _lastStats.MaxDexterity,
        magic = _lastStats.CurrentMagic,
        maxMagic = _lastStats.MaxMagic,
        objectsCarried = _lastStats.ObjectsCarried,
        maxObjectsCarried = _lastStats.MaxObjectsCarried,
        carriedCount = _carried?.Length,
        level = _lastStats.Level,
        gamesPlayed = _lastStats.GamesPlayed,
        weather = _lastStats.Weather.ToString(),
        isBlind = _lastStats.IsBlind,
        isDeaf = _lastStats.IsDeaf,
        isCrippled = _lastStats.IsCrippled,
        isDumb = _lastStats.IsDumb,
    };

    private object EffectFlagsBlock() => new
    {
        strBuff = _lastEffects.StrengthBuff,
        strDebuff = _lastEffects.StrengthDebuff,
        dexBuff = _lastEffects.DexterityBuff,
        dexDebuff = _lastEffects.DexterityDebuff,
        staBuff = _lastEffects.StaminaBuff,
        staDebuff = _lastEffects.StaminaDebuff,
        glow = _lastEffects.Glow,
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
            var payload = new { type = "contents", ts, contents = ContentsBlock() };
            foreach (var entry in _open)
                WriteEntryLocked(entry, payload);
            if (carriedChanged)
                WriteStatsLocked(ts, "post_inventory");
        }
    }

    /// <summary>The structured room-contents block, shared by "encounter_start" and every
    /// "contents" row. Null until a FEI list has completed - the honest state when the client has
    /// attached mid-room and the game has not yet said what is here.</summary>
    private object? ContentsBlock() => _roomItems is null ? null : new
    {
        room = _contentsRoom,
        here = _roomItems.Select((name, i) => new { name, creature = _roomItemIsCreature![i] }).ToArray(),
        carried = _carried,
        carriedCount = _carried!.Length,
    };

    /// <summary>Wire directly to MudSession/MuckaConnection's InCombatChanged.</summary>
    public void OnInCombatChanged(bool inCombat)
    {
        if (inCombat)
            Start();
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
            WriteEntryLocked(active, new
            {
                type = "event",
                ts = new DateTimeOffset(e.TimestampUtc).ToUnixTimeMilliseconds(),
                kind = e.Kind.ToString(),
                actor = e.Actor?.ToString(),
                npc = e.NpcName,
                // Also the moved object's name on the four loadout kinds - the field has carried it
                // since ItemDropped was the only one, and renaming it now would cost every existing
                // query in the corpus for nothing.
                weapon = e.Weapon,
                // ItemStowed/ItemRetrieved only: which container. Whether its weight left the player
                // depends on whether the container was itself carried, which the nearest "contents"
                // row in this same file answers.
                container = e.Container,
                rangeLow = e.RangeLow,
                rangeHigh = e.RangeHigh,
                // NpcHealth only. Written even though `raw` carries the same sentence, because the
                // rung is the interpretation and it is worth being able to re-read the corpus without
                // re-deriving it - the health ladder's ordering was itself established from records
                // like these.
                healthRung = e.HealthRung,
                healthPhrase = e.HealthPhrase,
                raw = e.RawText,
            });
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
    /// draw a reply from more than one live creature sharing that name, and this file has no way to
    /// tell whose is whose - see FightAccumulator.NoteValue for the matching correction on the roster
    /// row itself.</summary>
    public void OnCreatureValueResolved(string name, int value)
    {
        lock (_lock)
        {
            var active = _open.FirstOrDefault(entry => !entry.Closing);
            if (active is null)
                return;
            var ambiguous = !active.ValuedNames.Add(name);
            WriteEntryLocked(active, new
            {
                type = "creature_value",
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                npc = name,
                value,
                ambiguous,
            });
        }
    }

    private void Start()
    {
        lock (_lock)
        {
            // Defensive: CombatTracker only fires InCombatChanged(true) on a false->true
            // transition, so a second Start() while one is already active should never happen.
            if (_open.Any(e => !e.Closing))
                return;
            StreamWriter? writer = null;
            try
            {
                var dir = _directory;
                Directory.CreateDirectory(dir);
                var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
                var path = Path.Combine(dir, $"clog.{timestamp}-{_startSequence++}.jsonl");
                // No BOM: these files are meant to be read line-by-line by plain JSON parsers
                // (json.loads chokes on a BOM prefixed to the first line without utf-8-sig).
                // AutoFlush is deliberately OFF: DrainAsync below owns flushing, off this thread.
                writer = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(false)) { AutoFlush = false };
                var queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                });
                var entry = new OpenEncounter
                {
                    FilePath = path,
                    Queue = queue,
                    // One background task per encounter, owning `writer` for its whole lifetime -
                    // FinalizeLocked hands it off entirely (never touches `writer` or this queue
                    // again once this is running) rather than sharing mutable state across threads.
                    WriterTask = Task.Run(() => DrainAsync(queue, writer)),
                };
                _open.Add(entry);
                // Prune before adding, not after: this is the only place that ever grows the list,
                // so pruning here is enough to keep it bounded regardless of how many encounters the
                // process has seen (see the field's remarks).
                _writerTasksForTests.RemoveAll(t => t.IsCompleted);
                _writerTasksForTests.Add(entry.WriterTask);
                IsRecording = true;
                FilePath = path;
                UpdateTailOnlyLocked();

                var startedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                WriteEntryLocked(entry, new
                {
                    type = "encounter_start",
                    ts = startedMs,
                    reset = ResetBlock(startedMs),
                    preroll = _recentLines.ToArray(),
                    room = _lastRoom,
                    weather = _lastStats.Weather.ToString(),
                    stats = StatsBlock(),
                    contents = ContentsBlock(),
                    effects = _lastEffects,
                });
                // The header IS this encounter's first stats row for change-detection purposes, so
                // the next "stats" row is written only if something has actually moved since.
                RebaselineStatsLocked();
            }
            catch
            {
                // WriterTask is deliberately NOT started at this point in any failure path (it is
                // the last statement before WriteEntryLocked, itself after everything that can
                // throw), so there is nothing running to cancel - just clean up what did get created.
                writer?.Dispose();
                IsRecording = false;
                FilePath = null;
                UpdateTailOnlyLocked();
                // Best-effort feature: a clogging failure must never disrupt play.
            }
        }
    }

    /// <summary>
    /// Which reset this encounter happened in - the header's <c>reset</c> block.
    ///
    /// <para><b>Why this is here.</b> MUD2 creatures earn points and level up WITHIN a reset, so the
    /// same name is a different opponent at different points in the cycle; the <c>swings</c> table has
    /// carried <c>reset_epoch_ms</c> as its grouping key for exactly that reason (CombatDb), but the
    /// clogs - the corpus every offline query actually runs against - carried no reset context at all.
    /// 2.4% of encounters have a tick phase more than half a second off the session's best-fit lattice,
    /// which a session spanning a reset would explain but could not be tested for while nothing
    /// recorded which side of a reset an encounter sat on.</para>
    ///
    /// <para><b>Two fields, because they are different in kind and the better one can be absent.</b></para>
    /// <list type="bullet">
    /// <item><c>targetUtcMs</c> - ResetClock's locked estimate of the reset instant. This is the one to
    /// group on: it is a single converged figure, so it stays put across every encounter in a reset.
    /// Carries <c>uncertaintySec</c> and <c>phase</c> so a consumer can tell a locked reading from a
    /// guess, and is null before the clock has locked.</item>
    /// <item><c>timeToReset</c> - the raw FES minutes-remaining reading (field [13] is minutes, not
    /// seconds), always available, and the only thing present in an early clog. <b>Do not group on
    /// <c>ts + timeToReset * 60000</c> without bucketing it first.</b> That expression is what
    /// <c>swings.reset_epoch_ms</c> holds, and calling it "constant across every swing of one reset"
    /// is wrong as written: the reading is whole MINUTES, so the derived instant jitters by up to a
    /// minute between observations and grouping on it raw splits one reset into many. Bucket to
    /// ResetClock's +/-30s (its <c>MinuteUncertaintySec</c>) - or prefer <c>targetUtcMs</c>.</item>
    /// </list>
    /// </summary>
    private object ResetBlock(long startedMs)
    {
        var estimate = ResetEstimateProvider?.Invoke();
        return new
        {
            targetUtcMs = estimate?.TargetUtc is DateTime t
                ? new DateTimeOffset(t, TimeSpan.Zero).ToUnixTimeMilliseconds()
                : (long?)null,
            uncertaintySec = estimate?.UncertaintySec,
            phase = estimate?.Phase.ToString(),
            timeToReset = _lastStats.TimeToReset,
            // The raw derivation, recorded so an early clog with no lock is still groupable, and
            // deliberately NOT presented as an identity - see the remarks above on its quantisation.
            derivedEpochMs = _lastStats.TimeToReset is int ttr ? startedMs + (ttr * 60_000L) : (long?)null,
        };
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
            FilePath = null;
            UpdateTailOnlyLocked();
            // Deliberately does NOT write "encounter_end" or complete the queue yet: the server's
            // own cleanup for this close (score, "has just passed on", dropped items) routinely
            // prints AFTER the line that triggered this Stop() and BEFORE the next prompt, and it
            // still belongs in this encounter's clog even if a brand new encounter starts in the
            // meantime (see the class remarks on tail capture / overlapping clogs). OnLineReady's
            // IsPartial branch is what actually finalizes this entry, once that prompt arrives.
        }
    }

    /// <summary>Writes the real "encounter_end" footer and hands this entry's queue/writer off to
    /// its drain task for good. Called once per entry, either from the next prompt after Stop()
    /// (the normal path) or from Dispose (best-effort, for whatever is still open at shutdown).</summary>
    private void FinalizeLocked(OpenEncounter entry)
    {
        WriteEntryLocked(entry, new
        {
            type = "encounter_end",
            ts = new DateTimeOffset(entry.EndedUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
        });
        // Signals DrainAsync that no more lines are coming for THIS encounter; it drains whatever
        // is already queued (including the encounter_end line just above) and then flushes+
        // disposes the writer itself, off this thread.
        entry.Queue.Writer.TryComplete();
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

    /// <summary>Drains one encounter's queued lines to disk and closes its writer. Runs entirely
    /// off the Feed thread (Task.Run from Start()), so AutoFlush-per-line disk writes never block
    /// the thread that parses incoming combat text.
    /// ReadAllAsync completes normally (no exception) once the channel is both completed
    /// (FinalizeLocked's TryComplete) AND fully drained, so every queued line - including
    /// encounter_end - is written before the writer is flushed and disposed.</summary>
    private static async Task DrainAsync(Channel<string> queue, StreamWriter writer)
    {
        try
        {
            await foreach (var line in queue.Reader.ReadAllAsync().ConfigureAwait(false))
                writer.WriteLine(line);
        }
        catch
        {
            // Best-effort feature: a write fault here must never crash the drain loop or the
            // process - the try/finally below still flushes+closes whatever DID make it to disk.
        }
        finally
        {
            try { writer.Flush(); } catch { }
            try { writer.Dispose(); } catch { }
        }
    }

    /// <summary>Enqueues onto one specific entry's queue. Cheap, in-memory, stays on the Feed
    /// thread; the actual disk write happens in that entry's DrainAsync. TryWrite on an unbounded
    /// channel never blocks and only fails after Complete() - which only FinalizeLocked calls, on
    /// an entry already removed from <see cref="_open"/> and never written to again.</summary>
    private static void WriteEntryLocked(OpenEncounter entry, object payload)
        => entry.Queue.Writer.TryWrite(JsonSerializer.Serialize(payload));

    /// <summary>Finalizes whatever is still open - active or mid-tail - then blocks (briefly -
    /// just draining whatever is already queued in memory, typically a handful of lines) until
    /// every background writer has actually flushed to disk. This is the fix for fight/clog rows
    /// being lost when the app exits mid-fight or mid-tail: without this wait, FinalizeLocked's
    /// TryComplete only SIGNALS the drain to finish - it does not wait for it - so a process exit
    /// immediately after could beat DrainAsync to the punch.</summary>
    public void Dispose()
    {
        List<Task> pending;
        lock (_lock)
        {
            pending = _open.Select(e => e.WriterTask).ToList();
            var now = DateTime.UtcNow;
            foreach (var entry in _open.ToList())
            {
                if (!entry.Closing)
                    entry.EndedUtc = now;
                FinalizeLocked(entry);
            }
            IsRecording = false;
            FilePath = null;
        }
        foreach (var task in pending)
        {
            try
            {
                task.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Best-effort: Dispose must never throw during shutdown. Whatever did not get
                // written in time is lost, same as any other best-effort I/O failure in this class.
            }
        }
    }
}
