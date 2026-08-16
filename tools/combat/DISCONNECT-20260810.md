# Disconnect investigation — 2026-08-10 sessions 15:26 / 16:14

Captures analyzed:
- `session-rec.mud2.co.uk.20260810-152631.jsonl` (session 1, the one that dropped)
- `session-rec.mud2.co.uk.20260810-161441.jsonl` (session 2, the reconnect)

Decoding done with a throwaway script reusing `tools/mapping/decode_probe.py`'s `decode_rx`
(same approach as `reduce_combat.py`). Script: `analyze_disconnect.py` (scratchpad, not
committed) — dumps tail records, all FES snapshots (tracking `reset_minutes`), all `an`
annotations, and the largest inter-record gaps for a given capture.

## Verdict: half-open TCP connection, undetected by the client (not server, not reset)

The server never sent a disconnect/logout/idle/reset notice. The byte stream simply stops.
The client's transport layer has no read timeout and no TCP keepalive, so a dropped/half-open
socket is invisible to it — the read loop just blocks forever with no error and no UI
notification. The gap ended only because the user manually intervened. This is a real,
fixable gap in `MuckaConnection`/`TcpMudConnection`.

## 1. Last ~100 events of session 1

Nothing unusual precedes the drop. In order: a stethoscope-diagnose of a `water-snake0`, two
buff spells (`str`, `dex`), more fighting, the kill (`You have killed the water-snake0`,
persona auto-saved at score 44,824), then:

```
[1786403308419] tx: 'resite\r\n'
[1786403308744] rx: "Your spell works!\n... Room 13 ... bloodstained ...
                      The power of the magic has put you to sleep!\n\n[PROMPT]"
[1786403308760] tx: FES,FEI probe
[1786403308901] rx: [PROMPT]   (empty — normal: probes no-op while asleep)
[1786403309085] rx: [PROMPT]
[1786403311652] tx: FES,FEW probe
[1786403311793] rx: [PROMPT]
[1786403311993] rx: [PROMPT]   ← LAST rx in the capture
[1786403316651] tx: FES,FEW probe   (unanswered)
[1786403321648] tx: FES,FEW probe   (unanswered)
[1786403326653] tx: FES,FEW probe   (unanswered)
[1786403331652] tx: FES,FEW probe   (unanswered)
[1786403336659] tx: FES,FEW probe   (unanswered)
[1786403341649] tx: FES,FEW probe   (unanswered)  ← LAST tx
[1786403659535] an: 'capture stopped'
```

No logout line, no idle warning, no "you have been disconnected," no reset banner, no error
annotation. The rx stream ends on an ordinary empty prompt reply — consistent with the sleep
spell suppressing FES/FEW content, not with any kind of ejection message.

## 2. Server or client?

- **Gap between last rx and end of capture: 347.5 s** (≈5.8 min).
- **tx continued past the last rx**: yes — 6 more `FES,FEW` heartbeat probes went out every
  ~5 s (the configured heartbeat cadence), all unanswered, ending at `1786403341649`.
- After that 7th silent beat, the heartbeat itself stopped. There is then a **317.9 s
  (≈5.3 min) gap with no tx and no rx at all**, ending in an `an: capture stopped` — which
  `SessionCapture.Stop()`/`Dispose()` only writes on an explicit, user/app-driven action, never
  automatically from a socket error. This means the read loop's `Disconnected` event never
  fired during the whole outage — nothing in the code path that produces a `Disconnected`
  callback ran (see below); the user had to force it by acting.
- No client-side error or exception is recorded anywhere in the `an` annotation stream for
  either file (checked with a regex over "linkdead/rescue/idle/left the game" plus a scan of
  every annotation — only dreamword tracking, the two capture start/stop markers, and nothing
  else).

Reading `TcpMudConnection.ReadLoopAsync` / `MuckaConnection.ReadLoopAsync`
(`G:\Source\mucka\combat\mudsharp\Transport\TcpMudConnection.cs:81-106`,
`G:\Source\mucka\combat\Core\MuckaConnection.cs:383-412`): the loop is
`await stream.ReadAsync(...)` with no timeout and no cancellation source tied to inactivity.
On a genuinely half-open connection (server vanished without sending FIN — dropped Wi-Fi,
NAT/idle-timeout on a router or the ISP, a hung/reset middlebox) `ReadAsync` simply never
completes: no exception, no `read == 0`, nothing. `Disconnected` (and therefore the
"Disconnected — the server closed the connection" dialog in `GamePage.xaml.cs:2000-2018`)
never fires. This matches the data exactly: rx stops cold with no server message and no
client error.

The 6 heartbeats that still went out after the last rx are also explained by the code: they're
independent local writes queued through a channel (`WriteLoopAsync`,
`Core/MuckaConnection.cs:414-436`) — a `stream.WriteAsync` on a half-open socket typically
still succeeds locally (into the OS send buffer) for a while even though nothing is arriving
back, so the app keeps firing its 5 s heartbeat timer regardless of whether replies come back.
The heartbeat stopping entirely afterward (no more tx for 5+ minutes) means either the local
TCP send window filled and a later `WriteAsync` blocked, or (more likely given nothing at all
happened for over 5 minutes, not even a blocked-write hang being silently retried) the user
had already noticed the freeze and was in the process of manually restarting the connection
when `capture stopped` was written.

**Conclusion: this looks like a genuine network-layer half-open connection (client or server
side network path, not the MUD2 server logic) that the client had no mechanism to detect.**
There is no evidence implicating the MUD2 server itself (no reset, no kick, no error) — but
also no proof of exactly *where* the link broke (home network, ISP, MUD2's host) since nothing
on either side logged the actual failure.

## 3. Was RESET involved? No.

`reset_minutes` (FES field 14) ticked down steadily and undramatically across the whole
session, in line with real elapsed time, with no discontinuity around the drop:

| ts | reset_minutes |
|---|---|
| session start | 72 |
| ~end of session 1 (last real FES, ts 1786403306791) | **30** |
| first FES of session 2 (ts 1786403684934) | **24** |

30 → 24 over the ~6.3-minute real-world gap between the last real FES and the reconnect is
exactly the expected ~1/min decay — the reset clock is server-side and keeps counting
regardless of the client's link state. No `{c06.03}`/`{c06.04}` reset tag appears anywhere in
either capture (grepped for it explicitly), and the countdown never jumped back up or reset to
a large value. **A periodic RESET did not cause this disconnect and was not imminent (30–24
minutes out) when it happened.**

## 4. Client bug?

Files inspected: `Core/MuckaConnection.cs`, `mudsharp/Transport/TcpMudConnection.cs`,
`mudsharp/Session/MudSession.cs`, `ViewModels/GameViewModel.cs`, `Pages/GamePage.xaml.cs`.

Findings:
- **No TCP keepalive is configured anywhere in the repo** (`grep -i "KeepAlive\|SetSocketOption\|ReceiveTimeout\|SendTimeout"` over the whole tree returns nothing).
- **No application-level read/heartbeat-reply timeout exists.** `MudSession` already tracks
  `_lastProbeReplyUtc` / `_lastProbeSentUtc` for a different purpose (detecting when the
  character is *asleep* so it can fire an eager "wake" probe — see
  `mudsharp/Session/MudSession.cs:22-46`, `961-983`), but nothing turns "N heartbeats sent with
  zero replies" into a "the link is dead" signal. The pieces to detect this already half-exist
  in that timestamp bookkeeping; they're just never checked against the elapsed time to declare
  the connection dead.
- `GamePage.xaml.cs:2000-2018` *does* show a "Disconnected — the server closed the connection"
  dialog, but only when `TcpMudConnection`/`MuckaConnection`'s `Disconnected` event actually
  fires (`read == 0`, an exception, or explicit cancellation). None of those happen on a
  half-open socket, so **the client would not notice this kind of drop at all** and would sit
  showing nothing new, with the command box apparently accepting input while nothing ever
  returns — exactly what the user experienced ("spontaneous disconnect" from their point of
  view, but really an indefinitely silent hang).

**Recommendation (minimal):** give the heartbeat a dead-link detector — e.g. in `MudSession`,
if a routine FES probe goes unanswered for N consecutive beats (or M seconds) while in game
mode, raise a new "probe timeout" signal that `MuckaConnection`/`TcpMudConnection` uses to
force-close the socket and fire `Disconnected` with a real error, so the existing dialog in
`GamePage.xaml.cs` surfaces promptly instead of the app hanging silently. (Alternatively,
enable `SocketOptionName.KeepAlive` plus `TcpKeepAliveTime/Interval/RetryCount` on the
underlying `Socket` at connect time — cheaper, but OS-default keepalive intervals are typically
far longer than a 5 s heartbeat cadence, so the application-level check is the more precise
fix.) Do not implement this without discussing scope/threshold with the user — this report is
investigation-only per instructions.

## 5. What state was lost

- **Score/persona value: none lost.** Last auto-save before the freeze was 44,824 (from the
  water-snake kill); session 2's first FES snapshot also reads 44,824. The persistent score is
  saved incrementally server-side and survived intact.
- **Position: lost.** Session 1 ends with the character asleep in "Room 13" (a bloodstained
  room, reached via the `resite` spell). Session 2's first captured activity (recording was
  manually re-armed after reconnecting, so this is not necessarily the very first moment back)
  shows the character awake and moving through "Dense forest" / "Ruin" / "Paddock" — a
  different location entirely.
- **Inventory: lost.** Session 1's last FEI snapshot shows carried items `stethoscope` and
  `axe0` (their weapon) with nothing on the ground. Session 2's first FEI snapshots show
  **zero carried items** (only ambient room items like an "umbrella"). The stethoscope and the
  axe0 weapon are gone.
- **Buffs: lost/re-cast.** The `str`/`dex` self-buffs applied late in session 1 are gone by
  session 2 (stats reset to base 100/100 then re-buffed by hand again after reconnecting) —
  expected/normal on a fresh login, not evidence of a bug by itself.

No reset event or logout message explains the inventory/position change — it happened
somewhere in the ~6.3-minute silent gap that neither capture recorded (the client-side log
went dark because the socket was hung; the server-side reason is not visible from these
captures). The measurable loss is: the weapon and tool the character was carrying, and their
map position; the numeric score/persona value was not affected.

## Caveat / what would settle the "server or client" question definitively

These captures show only the client's side. To know for certain whether the physical break was
in the home network/ISP path or on MUD2's host, you'd need either (a) a router/OS-level
connection log covering that time window, or (b) MUD2 server-side logs for that character's
socket around `reset_minutes` 30→24 (roughly 16:08–16:14 local time going by the file
timestamps). Absent that, "half-open TCP connection, cause unlocated" is as far as the evidence
goes — the important, well-supported finding is that whichever side broke it, **the client had
no way to notice**, and that part is fixable.
