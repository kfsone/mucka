#if WINDOWS
using System.Text;
using System.Text.Json;
using MudSharp.Models;

namespace Mucka.Core.Mapping;

/// <summary>
/// Operation console for map capture. Mapping is explicit: nothing is recorded until
/// the user runs an operation (probe the current room, or move-and-capture in a
/// direction); between operations the session only tracks state passively (current
/// room from RoomShortReady, enabled exits from the FE EXITS events every arrival
/// fires) so the compass stays live during manual play without polluting the record.
///
/// All operations append to one walk file per session: session-rec jsonl
/// (an/tx/rx triples). Edge outcomes get a grep-friendly annotation:
///   an "edge: {from} |{dir}> {to} [{exits}]"      -- traversed
///   an "edge: {from} |{dir}! {reason} [{exits}]"  -- refused (failed edges are data too)
/// [{exits}] is the from-room's enabled-exit fingerprint at move time: short
/// descriptions are not unique, so resolved-edge state keys on name+fingerprint
/// (same name AND same exits still collide; true identity is analysis-side).
/// A move that arrives somewhere chains an automatic probe of the new room.
///
/// THREADING: conn events fire on the read-loop thread, Start* on the UI thread;
/// all state is guarded by _lock. StateChanged/Status fire on arbitrary threads --
/// subscribers marshal.
/// </summary>
public sealed class MappingSession : IDisposable
{
    public const string ProbeCommands = "longlook,superlook,exits,look around,qscan,fei,fex,no";
    private const string EndMarker = "Don't then.";
    private static readonly TimeSpan OpTimeout = TimeSpan.FromSeconds(10);
    // How long after a FES/FEW/FEI heartbeat tx the wire counts as contended when the
    // response is not observed sooner (FeiListComplete closes the window early).
    private static readonly TimeSpan HeartbeatWindow = TimeSpan.FromSeconds(2);

    /// <summary>Canonical direction commands, as sent to the server.</summary>
    public static readonly string[] Directions =
        { "n", "ne", "e", "se", "s", "sw", "w", "nw", "up", "down", "in", "out", "swamp" };

    private enum Op { None, Probe, Move }

    // ── Close-room state ───────────────────────────────────────────────────────
    // Set while a "close room" cycle is running (visit every unresolved exit from
    // home, return after each one). Cleared on completion, failure, or cancel.
    private string? _closeHomeRoom;            // home room name at cycle start
    private string  _closeHomeFex = string.Empty;  // home fex fingerprint at cycle start
    private Queue<string>? _closePendingExits; // exits still to visit (dequeued before each outbound)
    private bool _closeReturning;              // true while on the return leg of one iteration

    private readonly MuckaConnection _conn;
    private readonly string _directory;
    private readonly string _host;
    private readonly object _lock = new();
    private readonly Timer _opTimer;
    private readonly Timer _windowTimer;          // repaint when the heartbeat window lapses
    private DateTime _probeWindowUntil = DateTime.MinValue;

    private StreamWriter? _writer;
    private Op _op;
    private string _moveDir = string.Empty;
    private string _moveFrom = string.Empty;
    private string? _returnDir;        // u-turn: direction to attempt after the arrival probe
    private string? _arrival;          // room short seen during the current op
    private bool _entered;             // RoomEntered fired (covers too-dark arrivals)
    private bool _echoSeen;            // our command's echo line arrived -- the response follows it
    private string? _failLine;         // first post-echo line of a move response
    private readonly StringBuilder _opRx = new();
    private readonly List<string> _fexBuffer = new();
    private bool _fexCollecting;

    private string _currentRoom = string.Empty;
    private HashSet<string> _enabledExits = new(StringComparer.OrdinalIgnoreCase);
    private string _moveFromFex = string.Empty;   // from-room exit fingerprint at move start
    private readonly HashSet<string> _resolved;   // "{room}|{fingerprint}|{dir}" keys, seeded from disk
    private readonly MapGraph _graph;             // name-level edge evidence for u-turn return choice

    /// <summary>Room/exits/busy state changed -- refresh the compass.</summary>
    public event Action? StateChanged;
    /// <summary>Human-readable operation progress for status lines / system echo.</summary>
    public event Action<string>? Status;
    /// <summary>A u-turn exhausted all return options and stopped. Fires on the TCP thread;
    /// subscribers must marshal to the UI thread.</summary>
    public event Action? ReturnBlocked;
    /// <summary>Close-room cycle finished: every previously-unresolved exit was visited.
    /// Fires on the TCP thread; subscribers must marshal to the UI thread.</summary>
    public event Action? CloseRoomComplete;
    /// <summary>Close-room cycle was interrupted (dark room, move failure, no return route,
    /// or home-verification mismatch). Carries a short reason string.
    /// Fires on the TCP thread; subscribers must marshal to the UI thread.</summary>
    public event Action<string>? CloseRoomBlocked;

    public string CurrentRoom { get { lock (_lock) return _currentRoom; } }
    public bool Busy { get { lock (_lock) return _op != Op.None; } }
    /// <summary>True while a FES/FEW/FEI heartbeat response may be in flight.</summary>
    public bool HeartbeatBlocked { get { lock (_lock) return DateTime.UtcNow < _probeWindowUntil; } }
    /// <summary>Bumps when an operation finishes -- lets the panel reload only when the walk file grew.</summary>
    public int OpsCompleted { get; private set; }
    public string? WalkFilePath { get; private set; }

    public bool IsExitEnabled(string dir) { lock (_lock) return _enabledExits.Contains(dir); }
    public bool IsResolved(string dir)
    {
        lock (_lock) return _resolved.Contains(EdgeKey(_currentRoom, FexKeyLocked(), dir));
    }
    public bool IsClosingRoom { get { lock (_lock) return _closePendingExits is not null; } }
    /// <summary>Number of exits still to visit in the current close-room cycle (0 when not closing).</summary>
    public int CloseRoomRemainingExits { get { lock (_lock) return _closePendingExits?.Count ?? 0; } }

    /// <summary>Snapshot of the current room's enabled exits (for graph guidance queries).</summary>
    public IReadOnlySet<string> EnabledExits
    {
        get { lock (_lock) return _enabledExits.ToHashSet(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>Snapshot of resolved-edge keys for the current room (live, overrides stale graph).</summary>
    public IReadOnlySet<string> CurrentRoomResolvedDirs
    {
        get
        {
            lock (_lock)
            {
                var fex = FexKeyLocked();
                var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var dir in Directions)
                    if (_resolved.Contains(EdgeKey(_currentRoom, fex, dir)))
                        dirs.Add(dir);
                return dirs;
            }
        }
    }

    public string? SuggestedNextExit()
    {
        lock (_lock)
        {
            var room = _currentRoom;
            var enabledExits = _enabledExits.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fex = FexKeyLocked();
            var resolvedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directions)
                if (_resolved.Contains(EdgeKey(room, fex, dir)))
                    resolvedDirs.Add(dir);
            return _graph.SuggestedNextExit(room, enabledExits, resolvedDirs);
        }
    }

    public MappingSession(MuckaConnection conn, string directory, string host)
    {
        _conn = conn;
        _directory = directory;
        _host = host;
        var edgeState = MappingStore.LoadEdgeState(directory);
        _resolved = edgeState.Resolved;
        _graph = edgeState.Graph;
        _opTimer = new Timer(_ => OnOpTimeout(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _windowTimer = new Timer(_ => StateChanged?.Invoke(), null,
                                 Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _conn.RawBytesReceived += OnRawBytesReceived;
        _conn.LineReady        += OnLineReady;
        _conn.RoomShortReady   += OnRoomShortReady;
        _conn.RoomEntered      += OnRoomEntered;
        _conn.FexListStarting  += OnFexListStarting;
        _conn.FexItemReady     += OnFexItemReady;
        _conn.FexListComplete  += OnFexListComplete;
        _conn.FesProbeSent     += OnFesProbeSent;
        _conn.FeiListComplete  += OnFeiListComplete;
    }

    // ── Operations ─────────────────────────────────────────────────────────────

    /// <summary>Probe the current room (full verb battery, one command interrupt).</summary>
    public bool TryStartProbe(out string? error)
    {
        lock (_lock)
        {
            if (!CanStartLocked(out error)) return false;
            StartProbeLocked();
            return true;
        }
    }

    /// <summary>Move-and-capture: send a direction, record the outcome either way,
    /// and chain a probe of the destination on arrival.</summary>
    public bool TryStartMove(string dir, out string? error)
        => TryStartMoveCore(dir, returnDir: null, out error);

    /// <summary>There-and-back: move-and-capture, then attempt the reciprocal return.
    /// Whether a return is possible cannot be known until arrival -- if the destination
    /// does not list the reciprocal exit (or the outbound leg fails), the op stops there.</summary>
    public bool TryStartUturn(string dir, out string? error)
    {
        var reciprocal = MapGraph.Reciprocal(dir);
        if (reciprocal is null) { error = $"{dir} has no reciprocal direction"; return false; }
        return TryStartMoveCore(dir, reciprocal, out error);
    }

    /// <summary>Systematic close-room cycle: visit every unresolved enabled exit from the
    /// current room, probing each destination, and return home after each one.
    /// The return uses only the reciprocal or a name-confirmed route (no last-resort
    /// wandering). If any return fails, the cycle stops and <see cref="CloseRoomBlocked"/>
    /// fires. When all exits are done, <see cref="CloseRoomComplete"/> fires.</summary>
    public bool TryStartCloseRoom(out string? error)
    {
        lock (_lock)
        {
            if (!CanStartLocked(out error)) return false;
            if (_currentRoom.Length == 0) { error = "not in a known room"; return false; }

            var fex = FexKeyLocked();
            var pending = new Queue<string>(
                Directions.Where(d =>
                    _enabledExits.Contains(d) &&
                    !_resolved.Contains(EdgeKey(_currentRoom, fex, d))));
            if (pending.Count == 0) { error = "all exits in this room are already resolved"; return false; }

            _closeHomeRoom = _currentRoom;
            _closeHomeFex  = fex;
            _closePendingExits = pending;
            _closeReturning    = false;
            var total = pending.Count;
            var first = pending.Dequeue();
            WriteEntryLocked("an", $"close-room: starting from {_currentRoom}, {total} exit(s) to visit");
            StartMoveLocked(first);
        }
        Status?.Invoke($"close room: visiting {CloseRoomRemainingExits + 1} exit(s)...");
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>Abort an in-progress close-room cycle. Safe to call at any time.</summary>
    public void CancelCloseRoom()
    {
        bool wasClosing;
        lock (_lock)
        {
            wasClosing = _closePendingExits is not null;
            CancelCloseRoomLocked(reason: null);
            if (_op == Op.None)
                _conn.SetProbeHold(false);
        }
        if (wasClosing)
        {
            Status?.Invoke("close room: cancelled");
            StateChanged?.Invoke();
        }
    }

    private bool TryStartMoveCore(string dir, string? returnDir, out string? error)
    {
        lock (_lock)
        {
            if (!CanStartLocked(out error)) return false;
            _returnDir = returnDir;
            StartMoveLocked(dir);
        }
        Status?.Invoke(returnDir is null ? $"moving {dir}..." : $"u-turn: {dir}, then {returnDir} back...");
        StateChanged?.Invoke();
        return true;
    }

    private void StartMoveLocked(string dir)
    {
        _conn.SetProbeHold(true);
        _op = Op.Move;
        _moveDir = dir;
        _moveFrom = _currentRoom;
        _moveFromFex = FexKeyLocked();
        ResetOpStateLocked();
        WriteEntryLocked("an", $"op: move {dir} from {(_moveFrom.Length > 0 ? _moveFrom : "(unknown)")}");
        WriteEntryLocked("tx", dir + "\r\n");
        _conn.SendLine(dir);
        _opTimer.Change(OpTimeout, Timeout.InfiniteTimeSpan);
    }

    private bool CanStartLocked(out string? error)
    {
        if (!_conn.IsConnected || !_conn.InGameMode) { error = "not connected to a game"; return false; }
        if (_op != Op.None) { error = "operation already in progress"; return false; }
        if (DateTime.UtcNow < _probeWindowUntil)
        {
            error = "stats/who refresh in flight -- try again in a moment";
            return false;
        }
        error = null;
        return true;
    }

    private void StartProbeLocked()
    {
        // Own the wire: the FES/FEW/FEI heartbeat's response ends in a prompt redraw
        // and must not interleave with the capture. Released when the op chain ends.
        _conn.SetProbeHold(true);
        _op = Op.Probe;
        ResetOpStateLocked();
        WriteEntryLocked("an", "op: probe");
        var probe = "\x1b-[" + ProbeCommands + "\x1b-]";
        WriteEntryLocked("tx", probe);
        _conn.SendBytes(Encoding.ASCII.GetBytes(probe));
        _opTimer.Change(OpTimeout, Timeout.InfiniteTimeSpan);
    }

    private void ResetOpStateLocked()
    {
        _arrival = null;
        _entered = false;
        _echoSeen = false;
        _failLine = null;
        _opRx.Clear();
    }

    // ── Connection event taps ──────────────────────────────────────────────────

    private void OnRawBytesReceived(byte[] bytes)
    {
        string? status = null;
        bool changed = false;
        lock (_lock)
        {
            if (_op == Op.None) return;
            var text = Encoding.Latin1.GetString(bytes);
            WriteEntryLocked("rx", text);
            _opRx.Append(text);
            if (_op == Op.Probe && _opRx.ToString().Contains(EndMarker, StringComparison.Ordinal))
                (status, changed) = CompleteProbeLocked(timedOut: false);
        }
        Notify(status, changed);
    }

    private void OnLineReady(StyledLine line)
    {
        string? status = null;
        bool changed = false;
        lock (_lock)
        {
            if (_op == Op.Move)
            {
                if (line.IsPartial)   // in game mode a partial line is the prompt
                {
                    // Asynchronous server blocks (the periodic FEW who-refresh, NPC events,
                    // chat) also end in a prompt redraw. Our command's response always starts
                    // with its echo line, so a prompt before the echo is not ours -- ignore it.
                    if (_echoSeen || _arrival is not null)
                        (status, changed) = CompleteMoveLocked(timedOut: false);
                }
                else
                {
                    var plain = line.PlainText.Trim();
                    if (!_echoSeen && plain.Equals(_moveDir, StringComparison.OrdinalIgnoreCase))
                        _echoSeen = true;
                    else if (_echoSeen && _failLine is null && plain.Length > 0)
                        _failLine = plain;
                }
            }
        }
        Notify(status, changed);
    }

    private void OnRoomShortReady(string name)
    {
        bool changed = false;
        lock (_lock)
        {
            var room = name.TrimEnd('.', ' ');
            if (_op != Op.None)
                _arrival = room;
            else if (!room.Equals(_currentRoom, StringComparison.Ordinal))
            {
                // Manual movement (typed in the game window): track, don't record.
                _currentRoom = room;
                changed = true;
            }
        }
        Notify(null, changed);
    }

    private void OnRoomEntered()
    {
        lock (_lock) _entered = true;
    }

    private void OnFexListStarting()
    {
        lock (_lock)
        {
            _fexCollecting = true;
            _fexBuffer.Clear();
        }
    }

    private void OnFexItemReady(string item)
    {
        // One event per FEX line; each line is space-separated exit keywords.
        lock (_lock)
        {
            if (!_fexCollecting) return;
            foreach (var word in item.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                _fexBuffer.Add(word);
        }
    }

    private void OnFesProbeSent()
    {
        lock (_lock)
        {
            _probeWindowUntil = DateTime.UtcNow + HeartbeatWindow;
            _windowTimer.Change(HeartbeatWindow, Timeout.InfiniteTimeSpan);
        }
        StateChanged?.Invoke();
    }

    private void OnFeiListComplete()
    {
        bool wasOpen;
        lock (_lock)
        {
            // The FEI part is the tail of the heartbeat response -- the wire is free again.
            wasOpen = DateTime.UtcNow < _probeWindowUntil;
            _probeWindowUntil = DateTime.MinValue;
        }
        if (wasOpen) StateChanged?.Invoke();
    }

    private void OnFexListComplete()
    {
        bool changed;
        lock (_lock)
        {
            _fexCollecting = false;
            // FE EXITS fires on every arrival (and our probe's fex verb) -- always fresh.
            var exits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _fexBuffer)
                if (NormalizeDirection(item) is { } dir)
                    exits.Add(dir);
            changed = !exits.SetEquals(_enabledExits);
            _enabledExits = exits;
        }
        Notify(null, changed);
    }

    // ── Completion ─────────────────────────────────────────────────────────────

    private (string?, bool) CompleteProbeLocked(bool timedOut)
    {
        _op = Op.None;
        _opTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (_arrival is { } room)
        {
            _currentRoom = room;
            WriteEntryLocked("an", $"room: {room}");
        }
        WriteEntryLocked("an", timedOut ? "probe timeout" : "probe complete");
        OpsCompleted++;

        // U-turn: the outbound leg landed and its probe just refreshed the exits --
        // now we can pick the return leg. The goal is to get BACK to the origin while
        // capturing something: the reciprocal if still unconfirmed; then any exit known
        // to lead to origin; then as a last resort any unconfirmed exit with no recorded
        // destination at all (unknown territory -- captures the edge and may return).
        if (_returnDir is { } back)
        {
            _returnDir = null;
            // _moveFrom still holds the outbound leg's from-room: the chained probe
            // never touches it, and StartMoveLocked below is what overwrites it.
            var origin = _moveFrom;
            if (!timedOut && PickReturnLocked(back, origin, out bool lastResort) is { } ret)
            {
                // Analysis can compare the arrival against this expectation: a u-turn
                // return that lands as expected is loop evidence for instance identity.
                var note = lastResort
                    ? $" (low confidence, no known route to {origin})"
                    : $", expecting {origin}";
                WriteEntryLocked("an", $"u-turn: returning {ret}{note}");
                StartMoveLocked(ret);   // hold stays ours through the return leg
                return ($"u-turn: returning {ret}...", true);
            }
            _conn.SetProbeHold(false);
            if (!timedOut) ReturnBlocked?.Invoke();
            return (timedOut
                ? "u-turn abandoned -- probe timed out"
                : $"u-turn: nothing here seems to lead back to {origin} -- stopping", true);
        }

            if (_closePendingExits is not null)
                return CompleteCloseRoomProbeLocked(timedOut);

            _conn.SetProbeHold(false);
        return (timedOut ? "probe timed out" : $"probed {_currentRoom}", true);
    }

    private (string?, bool) CompleteCloseRoomProbeLocked(bool timedOut)
    {
        // Probe after the return leg: verify we landed at home.
        if (_closeReturning)
        {
            _closeReturning = false;
            var home = _closeHomeRoom!;
            var homeFex = _closeHomeFex;
            if (!timedOut && _currentRoom == home && FexKeyLocked() == homeFex)
            {
                // Home confirmed -- move to the next exit or finish.
                if (_closePendingExits!.TryDequeue(out var nextExit))
                {
                    var remaining = _closePendingExits.Count + 1; // +1 for the one we just dequeued
                    WriteEntryLocked("an", $"close-room: back home, visiting {nextExit} ({remaining} left)");
                    StartMoveLocked(nextExit);
                    return ($"close room: {remaining} exits left, moving {nextExit}...", true);
                }
                // All exits visited successfully.
                CancelCloseRoomLocked(reason: null);
                _conn.SetProbeHold(false);
                CloseRoomComplete?.Invoke();
                return ($"close room: done -- all exits of {home} visited", true);
            }
            // Home mismatch or timeout.
            var reason = timedOut
                ? $"probe timed out on return leg"
                : $"arrived at {_currentRoom} but expected {home}";
            CancelCloseRoomLocked(reason);
            _conn.SetProbeHold(false);
            return (timedOut
                ? "close room: abandoned -- probe timed out on return"
                : $"close room: blocked -- {reason}", true);
        }

        // Probe after the outbound leg: pick return direction (no last resort).
        var origin    = _closeHomeRoom!;
        var reciprocal = MapGraph.Reciprocal(_moveDir) ?? string.Empty;
        if (!timedOut && PickReturnLocked(reciprocal, origin, out _, useLastResort: false) is { } ret)
        {
            _closeReturning = true;
            WriteEntryLocked("an", $"close-room: returning {ret}, expecting {origin}");
            StartMoveLocked(ret);
            return ($"close room: returning {ret}...", true);
        }
        var blockReason = timedOut
            ? "probe timed out on outbound leg"
            : $"no route back to {origin} from {_currentRoom}";
        CancelCloseRoomLocked(blockReason);
        _conn.SetProbeHold(false);
        return (timedOut
            ? "close room: abandoned -- probe timed out"
            : $"close room: blocked -- {blockReason}", true);
    }

    private (string?, bool) CompleteMoveLocked(bool timedOut)
    {
        // Leave _op set if the move succeeded -- ChainProbe below replaces it.
        _opTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var from = _moveFrom.Length > 0 ? _moveFrom : "(unknown)";
        // The from-room's exit fingerprint disambiguates same-named rooms (short
        // descriptions are not unique); it travels in the annotation so resolved
        // state can be rebuilt from disk.
        var suffix = _moveFromFex.Length > 0 ? $" [{_moveFromFex}]" : string.Empty;
        string status;

        if (_arrival is { } room)
        {
            WriteEntryLocked("an", $"edge: {from} |{_moveDir}> {room}{suffix}");
            _resolved.Add(EdgeKey(_moveFrom, _moveFromFex, _moveDir));
            _graph.RecordTraversal(_moveFrom, _moveDir, room);
            _currentRoom = room;
            status = $"{from} |{_moveDir}> {room}";
            StartProbeLocked();   // chain: capture the room we just entered
        }
        else if (_entered)
        {
            // Moved somewhere we cannot see (no light). The edge is real but the far end
            // is unidentified -- deliberately NOT marked resolved, so the compass keeps
            // offering it until it is re-walked with a light source.
            WriteEntryLocked("an", $"edge: {from} |{_moveDir}> (dark){suffix}");
            _currentRoom = string.Empty;
            _op = Op.None;
            _returnDir = null;   // a u-turn cannot navigate back from a room it cannot see
            CancelCloseRoomLocked("arrived in a dark room");
            status = $"{from} |{_moveDir}> (dark room)";
        }
        else
        {
            var reason = timedOut ? "(timeout)" : _failLine ?? "(no output)";
            WriteEntryLocked("an", $"edge: {from} |{_moveDir}! {reason}{suffix}");
            // Structural refusals ("You can't go that way") resolve the edge; transient
            // ones (an ox or player in the way) and op artifacts (timeout, no output)
            // leave it wanted so it gets retried.
            var transient = MappingStore.IsTransientRefusal(reason);
            if (!timedOut && _failLine is not null && !transient)
                _resolved.Add(EdgeKey(_moveFrom, _moveFromFex, _moveDir));
            _op = Op.None;
            _returnDir = null;   // the outbound leg failed -- no return to attempt
            CancelCloseRoomLocked(reason);
            status = $"{from} |{_moveDir}! {reason}";
            if (transient)
            {
                // Something movable is in the way -- wait a moment and click again.
                if (Mucka.Audio.SoundService.MasterEnabled)
                    Mucka.Audio.SoundService.Play("sounds/clio.0801.wav");
                status += " (transient -- wait and retry)";
            }
        }
        OpsCompleted++;
        if (_op == Op.None)               // not chaining into a probe -- the wire is free
            _conn.SetProbeHold(false);
        return (status, true);
    }

    /// <summary>Picks the return direction from the just-probed room, or null to stop here.
    /// A recorded destination NAME matching the origin is not identity (five
    /// "Badly-paved road"s) -- whether the edge returns to THIS instance is only ever
    /// evidenced by walking it in sequence, so a name-matching reciprocal is taken even
    /// when its edge record is confirmed.
    ///
    /// Priority:
    ///   1. Reciprocal: unconfirmed, or confirmed to lead to origin.
    ///   2. Any other exit: unconfirmed first, then confirmed, whose destination is origin.
    ///   3. Last resort (only when <paramref name="useLastResort"/> is true): any unconfirmed
    ///      exit with no recorded destination at all (truly unknown -- captures the edge and
    ///      may happen to return). Disabled for close-room cycles.
    /// Sets <paramref name="lastResort"/> true when only tier 3 found a candidate.</summary>
    private string? PickReturnLocked(string reciprocal, string origin, out bool lastResort,
                                     bool useLastResort = true)
    {
        lastResort = false;
        var fex = FexKeyLocked();
        bool Unconfirmed(string dir) => !_resolved.Contains(EdgeKey(_currentRoom, fex, dir));

        if (_enabledExits.Contains(reciprocal))
        {
            var dest = _graph.KnownDestination(_currentRoom, reciprocal);
            // Take it when unwalked (geometry says it probably returns) or when its
            // recorded destination is named like the origin (walk to test the loop).
            // Only a reciprocal KNOWN to lead somewhere else is passed over.
            if (Unconfirmed(reciprocal) || string.Equals(dest, origin, StringComparison.Ordinal))
                return reciprocal;
        }

        // Reciprocal leads elsewhere: any other enabled exit that seems to return to
        // the origin -- unconfirmed ones first (they close an edge AND test the loop).
        foreach (var unconfirmedFirst in new[] { true, false })
            foreach (var dir in Directions)
            {
                if (dir == reciprocal || !_enabledExits.Contains(dir)) continue;
                if (Unconfirmed(dir) != unconfirmedFirst) continue;
                if (string.Equals(_graph.KnownDestination(_currentRoom, dir), origin, StringComparison.Ordinal))
                    return dir;
            }

        if (!useLastResort) return null;

        // Last resort: any unconfirmed exit with no recorded destination -- we have no
        // evidence it returns, but we also have no evidence it doesn't; walking it
        // captures the edge regardless.
        foreach (var dir in Directions)
        {
            if (!_enabledExits.Contains(dir) || !Unconfirmed(dir)) continue;
            if (_graph.KnownDestination(_currentRoom, dir) is null)
            {
                lastResort = true;
                return dir;
            }
        }

        return null;
    }

    private void CancelCloseRoomLocked(string? reason)
    {
        if (_closePendingExits is null) return;
        _closePendingExits = null;
        _closeHomeRoom     = null;
        _closeHomeFex      = string.Empty;
        _closeReturning    = false;
        if (reason is not null) CloseRoomBlocked?.Invoke(reason);
    }

    private void OnOpTimeout()
    {
        string? status = null;
        bool changed = false;
        lock (_lock)
        {
            if (_op == Op.Probe) (status, changed) = CompleteProbeLocked(timedOut: true);
            else if (_op == Op.Move) (status, changed) = CompleteMoveLocked(timedOut: true);
        }
        Notify(status, changed);
    }

    private void Notify(string? status, bool changed)
    {
        if (status is not null) Status?.Invoke(status);
        if (changed) StateChanged?.Invoke();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    internal static string EdgeKey(string room, string fex, string dir) => $"{room}|{fex}|{dir}";

    /// <summary>Canonical fingerprint of the current room's enabled exits, e.g. "e n out s".</summary>
    private string FexKeyLocked()
        => string.Join(' ', _enabledExits.OrderBy(d => d, StringComparer.Ordinal));

    /// <summary>Maps a FE EXITS keyword (or user input) to a canonical direction command.</summary>
    public static string? NormalizeDirection(string word) => word.Trim().ToLowerInvariant() switch
    {
        "n" or "north"          => "n",
        "ne" or "northeast"     => "ne",
        "e" or "east"           => "e",
        "se" or "southeast"     => "se",
        "s" or "south"          => "s",
        "sw" or "southwest"     => "sw",
        "w" or "west"           => "w",
        "nw" or "northwest"     => "nw",
        "u" or "up"             => "up",
        "d" or "down"           => "down",
        "in"                    => "in",
        "o" or "out"            => "out",
        "swamp" or "swampward"  => "swamp",
        _ => null,
    };

    private void WriteEntryLocked(string mode, string data)
    {
        if (_writer is null)
        {
            Directory.CreateDirectory(_directory);
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            var safeHost = new string(_host.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            WalkFilePath = Path.Combine(_directory, $"walk.{safeHost}.{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
            _writer = new StreamWriter(WalkFilePath, append: false, Encoding.UTF8) { AutoFlush = true };
            var ms0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _writer.WriteLine($"[{ms0},\"an\",{JsonSerializer.Serialize($"map walk: {_host}")}]");
        }
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _writer.WriteLine($"[{ms},{JsonSerializer.Serialize(mode)},{JsonSerializer.Serialize(data)}]");
    }

    public void Dispose()
    {
        _conn.RawBytesReceived -= OnRawBytesReceived;
        _conn.LineReady        -= OnLineReady;
        _conn.RoomShortReady   -= OnRoomShortReady;
        _conn.RoomEntered      -= OnRoomEntered;
        _conn.FexListStarting  -= OnFexListStarting;
        _conn.FexItemReady     -= OnFexItemReady;
        _conn.FexListComplete  -= OnFexListComplete;
        _conn.FesProbeSent     -= OnFesProbeSent;
        _conn.FeiListComplete  -= OnFeiListComplete;
        _conn.SetProbeHold(false);
        _opTimer.Dispose();
        _windowTimer.Dispose();
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
#endif
