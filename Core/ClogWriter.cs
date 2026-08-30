using System.Linq;
using System.Text.Json;
using System.Threading.Channels;
using MudSharp.Combat;
using MudSharp.Models;

namespace Mucka.Core;

/// <summary>
/// Records one JSONL "clog" (combat log) per encounter under ~/.mucka/clogs/, so real fights can
/// be analyzed offline with the same tooling used on RESEARCH/mud2-multi-combat.jsonl
/// (tools/combat/). Driven entirely by MudSession/MuckaConnection events — see MuckaConnection's
/// wiring for CombatTracker's InCombatChanged/CombatEventOccurred.
///
/// <para>Each file has one header line (type "encounter_start": the previous ~30 non-combat
/// lines plus a snapshot of stats/status-effects/room at the moment combat began — everything
/// a later analysis pass needs to answer "was the player invisible / what was the weather / what
/// were their stats" without replaying the whole session), one line per classified CombatEvent
/// (type "event"), zero or more trailing plain lines (type "line" — see the tail-capture remarks
/// below), and one footer line (type "encounter_end").</para>
///
/// <para><b>Tail capture.</b> CombatTracker closes an encounter the instant its last active NPC
/// dies/flees — a decisive combat-state fact. But the server's own cleanup for that (score,
/// "The X has just passed on.", dropped items, level-up) routinely PRINTS after the death line
/// that closed it, and always before the next prompt. Rather than end the clog exactly on
/// InCombatChanged(false), <see cref="Stop"/> only marks the encounter's entry "closing": every
/// subsequent plain line is still appended to it (type "line") until the next <c>IsPartial</c>
/// prompt line arrives (the frame boundary — see MudSession's setup-swallow remarks for the same
/// signal used the same way), at which point <see cref="OnLineReady"/> writes the real
/// "encounter_end" footer and finalizes the file. This is a LOGGING concern only — MUD2 has no
/// such mechanic, and it never delays InCombatChanged/IsInCombatGracePeriod's UI-visible flip.</para>
///
/// <para><b>Overlapping clogs.</b> A new encounter can legitimately start (Start()) while the
/// previous one is still draining its tail — e.g. one rat dies and a second, unrelated rat
/// attacks before the next prompt (this is a NEW encounter, not a continuation — see
/// CombatTracker's remarks). Both are kept open simultaneously in <see cref="_open"/>, each with
/// its own file, queue, and drain task; classified CombatEvents route to whichever entry is still
/// actively live (there is at most one), while a plain line during the overlap is appended to
/// every entry still draining its tail.</para>
///
/// <para>Deliberately partial: this is not a full raw capture (SessionCapture already covers
/// that, opt-in, for debugging). A clog is intentionally reduced to what tools/combat's analysis
/// needs, per the user's request to keep these lightweight enough to accumulate over many
/// sessions.</para>
///
/// <para>Always on. It used to be opt-in behind a "$clog on" command, which meant the evidence was
/// missing precisely when something interesting had just happened and the player had not thought to
/// arm it beforehand - the same argument the fight history and the swing ledger already settle the
/// same way. A clog is small and the pre-roll buffer costs a bounded queue per line.</para>
///
/// <para>Threading: all On* methods are called from MudSession's Feed thread (same contract as
/// EffectTracker/CombatTracker — see MudSession's class doc comment). Does not touch UI types.</para>
///
/// <para>File I/O runs off the Feed thread entirely: <see cref="WriteEntryLocked"/> only serializes
/// the entry (cheap, in-memory) and enqueues the line; a per-encounter background task
/// (<see cref="DrainAsync"/>) owns the actual <see cref="StreamWriter"/> and does the blocking disk
/// write. Before this, every clog line paid a synchronous open/flush on the SAME thread that parses
/// incoming combat text - stalling that thread delays the combat text itself, which no UI-side
/// throttle can fix (DESIGN_FINAL.md section 7.5). <see cref="Dispose"/> waits (briefly - just
/// draining whatever is already queued in memory) for every still-open encounter's drain to finish,
/// so an app exit mid-fight (or mid-tail) cannot lose the encounter_end line or anything queued just
/// before it.</para>
/// </summary>
public sealed class ClogWriter : IDisposable
{
    private const int PreBufferLines = 30;

    /// <summary>One still-open clog. "Closing" means the encounter itself has ended (Stop() was
    /// called) but the file is not finalized yet — it is still draining its tail, waiting for the
    /// next prompt (see the class remarks). Owns its StreamWriter's lifetime via WriterTask, the
    /// same handoff the single-encounter version used to do with a bare Task field.</summary>
    private sealed class OpenEncounter
    {
        public required string FilePath;
        public required Channel<string> Queue;
        public required Task WriterTask;
        public bool Closing;
        public DateTime EndedUtc;
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

    /// <summary>True while an encounter is being actively recorded (CombatEvents still arriving).
    /// False during tail-only draining — see <see cref="IsTailOnly"/> for that state.</summary>
    public bool IsRecording { get; private set; }
    /// <summary>The actively-recording encounter's file, or null when none is live (even if a
    /// previous encounter's tail is still draining — see <see cref="IsTailOnly"/>).</summary>
    public string? FilePath { get; private set; }

    /// <summary>True while at least one encounter's tail is still draining (waiting for the next
    /// prompt to finalize) and no encounter is actively live. Drives the UI's "winding down"
    /// cosmetic (see MuckaConnection.IsInCombatGracePeriod) — the fight really is over
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

    /// <summary>Feed every line — prompts included — so the pre-roll buffer stays fresh and any
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

    public void OnStatsUpdated(GameStatsSnapshot stats) => _lastStats = stats;

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
    public void OnStatusEffectsChanged(StatusEffectState effects) => _lastEffects = effects;
    public void OnRoomShortReady(string room) => _lastRoom = room;

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
            // At most one entry is ever actively recording — a closing (tail-only) entry gets no
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
                weapon = e.Weapon,
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

    private void Start()
    {
        lock (_lock)
        {
            // Defensive: CombatTracker only fires InCombatChanged(true) on a false→true
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
                    stats = new
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
                        level = _lastStats.Level,
                        gamesPlayed = _lastStats.GamesPlayed,
                        isBlind = _lastStats.IsBlind,
                        isDeaf = _lastStats.IsDeaf,
                        isCrippled = _lastStats.IsCrippled,
                        isDumb = _lastStats.IsDumb,
                    },
                    effects = _lastEffects,
                });
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
    /// Added 2026-08-28 at the owner's request, prompted by a measurement that could not be finished
    /// without it: 2.4% of encounters have a tick phase more than half a second off the session's
    /// best-fit lattice, and the leading explanation is a session that SPANS a reset, where the server's
    /// lattice genuinely moves and one estimate cannot describe both halves. Untestable while nothing
    /// records which side of a reset an encounter sat on.</para>
    ///
    /// <para><b>Two fields, because they are different in kind and the better one can be absent.</b></para>
    /// <list type="bullet">
    /// <item><c>targetUtcMs</c> - ResetClock's locked estimate of the reset instant. This is the one to
    /// group on: it is a single converged figure, so it stays put across every encounter in a reset.
    /// Carries <c>uncertaintySec</c> and <c>phase</c> so a consumer can tell a locked reading from a
    /// guess, and is null before the clock has locked.</item>
    /// <item><c>timeToReset</c> - the raw FES seconds-remaining reading, always available, and the only
    /// thing present in an early clog. <b>Do not group on <c>ts + timeToReset * 1000</c> without
    /// bucketing it first.</b> That expression is what <c>swings.reset_epoch_ms</c> holds, and
    /// CombatDb's comment calling it "constant across every swing of one reset" is wrong as written: the
    /// reading is whole seconds, so the derived instant jitters by up to a second between observations
    /// and grouping on it raw splits one reset into many.</item>
    /// </list>
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
            derivedEpochMs = _lastStats.TimeToReset is int ttr ? startedMs + (ttr * 1000L) : (long?)null,
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
    /// off the Feed thread (Task.Run from Start()) - this is the actual fix for AutoFlush-per-line
    /// blocking the thread that parses incoming combat text (DESIGN_FINAL.md section 7.5).
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

    /// <summary>Finalizes whatever is still open — active or mid-tail — then blocks (briefly -
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
