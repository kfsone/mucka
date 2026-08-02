# Combat mechanics notes

## Current observables in the merged database

- Per-fight outcome, duration, weapon, npc instance, and npc group.
- Per-hit player damage ranges from combat prose, stored as replayable event text.
- Approximate damage taken inferred from stamina-before minus stamina-after when a hit line reports it.
- Encounter-start room, weather, and status/effect snapshot from live clogs.
- Effective strength/dexterity from the older research capture, plus new live-capture fields for raw strength, raw dexterity, carried weight, carried object count, level, and games played.

## Hidden weapon modifier methodology

The cleanest way to isolate a hidden per-weapon damage modifier is controlled A/B sampling:

1. Hold the target constant: same npc_group, ideally same room/light/weather where possible.
2. Hold the player state constant: same raw/effective strength, raw/effective dexterity, similar stamina, same afflictions, same carried weight, same carried object count.
3. Vary only the weapon.
4. Collect enough swings per condition to compare both hit rate and damage-per-hit distribution, not just one kill time.
5. Prefer repeated single-target fights over pack fights, since joins and retargets muddy fight duration and weapon provenance.

Suggested analysis sequence:

- First compare average hit midpoint and hit rate for the same weapon against the same npc_group.
- Then compare two weapons against that same npc_group under matching raw/effective stats buckets.
- If a weapon shows consistently higher damage at the same raw strength and same target, the residual is a candidate hidden modifier.
- If hit rate changes but damage-per-hit does not, the hidden property may be accuracy or timing rather than raw damage.

## What is still missing for rigorous proof

- Most existing rows do not yet have raw_strength, raw_dexterity, weight_carried_grams, or objects_carried because the older captures predate the new scorecard parsing.
- We still do not know the exact in-game formula mapping strength, weight, and dexterity to hit chance or damage.
- We do not have direct npc stats; we only see outcomes.
- We do not persist explicit room lighting state, only room prose and weather.
- Current live clogs snapshot stats at encounter start, not every weapon switch or every joiner start inside a long encounter.

## Highest-value next data improvements

- Keep collecting live clogs after the new scorecard fields land so raw/effective stat deltas become queryable.
- Add inventory parsing so carried item identities can be correlated with weight and dex penalties.
- Capture nearest scorecard snapshot after weapon-equip or weapon-break events when practical.
- If any command or prose reveals weapon weight directly, record that verbatim alongside the equipped weapon.
- Consider an explicit light/darkness flag if the protocol exposes one; some user hypotheses depend on visibility.
- **NPC-carried weapons are now observed and tracked (previously a known gap).** Confirmed live:
  `"The zombie has started to use the fork to fight!"` — distinct from the per-tick "The X hits you
  (N/M)." line, which never names a weapon. `CombatTracker` now has a dedicated
  `CombatEventKind.NpcWeaponEquip` regex/event (`"^The (?<npc>.+?) has started to use the
  (?<weapon>.+?) to fight!$"`), `ClogWriter` logs it automatically (it logs every `CombatEvent`
  generically, no plumbing needed), and `CombatStatsAggregator` now tracks each active NPC's
  last-known weapon in a per-name dictionary, surfacing it in the live HUD's active-NPC list as
  `"zombie (fork)"` once observed (most NPCs never announce a weapon at all — presumably
  fists/claws/bite — so the common case is still just the bare name). Not yet confirmed: whether
  any other text (a join/description line, "wields"/"brandishes", or `look <npc>`) also reveals an
  NPC's *starting* weapon before any mid-fight switch.

- **Live-HUD "damage taken" was reported as not visibly increasing during one fight.** A pass over
  this session's freshly captured clogs shows the underlying data is actually fine (`HitByNpc`
  events' stamina readings decrease consistently within an encounter, e.g. rat 58→55→54→51→48→47),
  and `CombatStatsAggregator`/`SidePanelViewModel` wiring looks correct end-to-end, so this may have
  been a momentary/visual observation rather than a data bug — flagged here to re-check against the
  next live session rather than "fixed" on unverified suspicion.

## Search result: direct weapon-weight evidence

A repository search over the research capture and current clogs found scorecard "weight carried" lines, but no direct prose reporting a weapon's own weight next to its weapon name. That means the current best path is still indirect inference: use controlled same-target comparisons while holding carried weight and effective stats as constant as possible.

## `$clog eval` sequencing fix: "chokes on qs/score data"

Following the identify-resolution/`CurrentStats` fixes (see below), the user still reported eval
"chokes on qs/score data" in a subsequent live test. First fix attempt: made
`SendAndAwaitStatsAsync` wait for the action command's own confirmation line before sending a
separate `"sc"`, to avoid both landing in the same MUD2 game turn — this worked but still paid for
a client/server round trip between every step (look, weigh, drop, sc, get, sc — six round trips).

**Superseded by batching**: the user pointed out MUD2 already supports sending several
comma-separated sub-commands in one input line, each still executed on its own sequential game
turn server-side (confirmed by the user's own example: `"cripple thief,e,e,e,e,e,e,e,kill
thief"`). So `RunAsync` now sends the *entire* `look/weigh/drop/sc/get/sc` sequence as ONE combined
line (`"look X,weigh X,drop X,sc,get X,sc"`), eliminating all 6 round trips down to 1 while keeping
the exact per-turn ordering guarantee that made the previous confirmation-wait fix work — the
server itself, not our client, now guarantees `sc` never runs before the preceding drop/get lands.
Replies are captured with two independent listeners running concurrently: a small line-based FSM
(`CaptureLookAndWeighAsync`) that anchors on the `look`/`weigh` echo lines to grab their reply text
(ignoring intervening `sc`/`drop`/`get` echoes and stats blocks, since it only recognises an anchor
while actively waiting for it), and a `StatsUpdated` collector (`CollectStatsAsync`) that takes the
first two snapshots reporting both Strength and Dexterity, in arrival order (afterDrop, then
afterGet). Verified via build (Windows + Android) + `mudsharp.Tests` (473/473); not yet
re-verified against a live session.

## Score-to-level thresholds (todo, unverified)

Per the user, MUD2's level-up thresholds on the `sc`/`qs` score value appear to be simple
fixed-point breakpoints: level 0 = 0-199, level 1 = 200-399, level 2 = 400-799, level 3 = 800-1599,
continuing (roughly doubling) up through rank 11 ("wizard"). Not yet independently verified against
more data points. Tracked as a todo (`score-level-thresholds` in the session's todo DB) — potential
use: predict an approaching level crossing from the last known score + gain rate, and only send a
targeted `sc` probe near that crossing instead of polling constantly. A distinct sound per direction
(ding up / dong down) was suggested but explicitly deferred by the user — do not implement yet.

## Combat indicator lingering after the last kill until an unrelated line arrives

Reported live: after `You have killed the zombie0.`/`The zombie0 has expired.`, the combat icon
stayed fully lit for ~2-3s — coincidentally until the next unrelated server line (`You can hear the
sound of rain on the trees.`) arrived. Root cause: `CombatTracker`'s post-kill grace window
(`KillGrace`, 5s — lets a pack-fight NPC that hasn't traded blows yet keep the SAME encounter open)
only re-evaluated via `ExpireKillGrace`, which was called exclusively from inside `Observe()`. A
solo kill with total server silence afterwards left `InCombat`/the grace state stale indefinitely —
correct only by accident whenever *some* unrelated line happened to show up soon after.

Fix: `CombatTracker` now exposes `IsInGracePeriod` (true once the last tracked NPC is dead/gone but
the encounter is still open pending `KillGrace`) and a `GracePeriodChanged` event, plus a public
`Tick(DateTime nowUtc)` that just re-runs `ExpireKillGrace` — callable independently of any new
line. `MudSession.TickCombat()` / `MuckaConnection.TickCombat()` plumb this down to the existing
1 Hz UI tick (`GamePage.OnAntiIdleTick` → `GameViewModel.TickCombatDisplay`), so the grace window
now expires on real wall-clock time instead of waiting for the next line of *any* kind. The combat
icon (`SidePanelViewModel.CombatIconOpacity`) also dims to 0.4 opacity while `IsCombatGracePeriod`
is true, so "actively fighting" and "last kill just landed, winding down" are now visually distinct
instead of looking identical. See `CombatTrackerTests.Kill_SetsGracePeriodUntilTickExpiresIt_*`.

## "Damage taken" always showing 0.0

Reported live. Root cause: an NPC hit line like `The zombie0 hits you (95/100).` gets parsed
TWICE by two independent regexes — once by `CombatTracker`'s `HitByNpc` (feeds
`CombatEventKind.HitByNpc` with `RangeLow=95`), and once generically by `GameLineAnalyzer`'s
`CombatStaminaRegex` (any embedded `(N/M)` — fires `StatsUpdated` with `Stamina=95`, purely so the
live "Sta" HUD readout stays fresh without needing an explicit `qs`). Crucially,
`MudStreamParser` fires `StatsUpdated` for a line strictly BEFORE `LineReady` (and therefore
`CombatTracker.Observe`) for that SAME line. `CombatStatsAggregator.ObserveDamageTaken` used to
diff the hit's own value directly against `_lastKnownStamina` — but by the time it ran,
`_lastKnownStamina` had ALREADY been overwritten with that exact hit's own post-hit value by the
`StatsUpdated` path moments earlier, so every delta computed to exactly 0. Most visible on a
single-hit fight (the common case — NPCs miss often), which matches the reported symptom exactly.

First attempt (superseded): a dedicated `_combatBaselineStamina`, seeded once from
`_lastKnownStamina` at `BeginEncounter` and thereafter updated only by `ObserveDamageTaken`
itself. That fixed the always-0 symptom but was wrong in a subtler way — it made the whole
encounter's damage tally hang off a *fixed* pre-fight snapshot, deliberately deaf to every
mid-fight stamina change that isn't an NPC hit. Stamina genuinely rises during a fight: the
dreamword recovers it, the temporary-heal spell tops it up, eating a wafer heals, and an
unhit combatant regenerates ~1 point periodically (NPCs regen too). Any of those would be
silently absorbed into the running tally, so a heal mid-fight would understate the NPC's real
output on every subsequent blow.

Fix (current): keep `_lastKnownStamina` as the single continuously-revised source of truth,
fed by *every* stat reading (qs/heartbeat, regen ticks, heals, wafers, dreamword), and defeat
the same-line race with a one-shot relay instead. `ObserveStamina` stashes the value
`_lastKnownStamina` held immediately before its own update into `_pendingPreUpdateStamina`;
the `ObserveDamageTaken` that follows on the same line consumes that relay as its baseline, so
the delta reflects only that hit's own effect while all *other* lines' changes still revise the
baseline normally. This is the "one event parser relays details to the next parser" pattern.

The relay is trusted only when `_lastKnownStamina == currentStamina` — i.e. `ObserveStamina`
really did fire for this same line. When it didn't (notably a hit that drops the player to
exactly 0, which `GameLineAnalyzer`'s compact-stamina scan skips because it requires `sta > 0`),
`_lastKnownStamina` was never touched by this line and already holds the correct pre-hit value,
so we diff against it directly. The relay is nulled after use so a stale, never-consumed one
can't outrank a fresher reading later.

Known residual (unfixable — intentional MUD2 fog of war): the automatic regen tick is not
reported just before an incoming hit, so a round where you gained 1 and lost 5–10 reads as one
point light. Accepted; do not try to model it away.

See `CombatStatsAggregatorTests.Snapshot_SingleHitFight_StillComputesDamageDespiteSameLineStatsRace`
and `Snapshot_RegenerationBetweenHits_RevisesBaselineSoNextHitIsNotOverOrUnderCounted`.


Confirmed live (2026-08-01, `session-rec.mud2.co.uk.20260801-234914.jsonl` / `clog.20260801-234954.jsonl`): a
character that never toggled MUD2's `fightbrief` setting produces a completely different combat message
format. A vampire aggro'd, cast blindness mid-fight, then cast sleep, then killed the player while asleep.

**What still works without fightbrief:** `FightStart` classification (NPC aggro verb-phrase list matches
regardless of fightbrief), and both death lines (`"The X has killed you."` and, newly added, the narrative
`"You have been killed by the X/someone."`).

**What is completely lost without fightbrief:** every per-swing `Hit`/`Miss`/`HitByNpc`/`MissByNpc` line.
Narrative mode uses a large, flavourful message-template library instead of the fixed `"You hit the X
(A-B)."` / `"The X hits you (C/M)."` forms, e.g.:

```
You are grieved by the violence of a crafty lunge from the vampire.
Stamina=24/67.
The vampire is cut by the effort of your well-aimed bite.
Damage: 3.
```

No regex was added for these yet — the verb/adjective combinations look like a sizeable enumerated
template set (`grieved`/`winded`/`numbed`/`only just injured`/etc. crossed with `violence`/`effort`/
`impetus`/`force`/`strength`/`brutality`/etc.), not a small fixed handful. **Recommendation: always enable
`fightbrief` for any character used for combat-mechanics data collection** — the fixed-format lines are
what the whole offline pipeline (weapon×npc effectiveness, hit-rate correlation) depends on. Narrative-mode
clogs will still get an accurate encounter/fight boundary and death outcome, but will show near-zero
hit/miss counts, which would badly skew aggregate hit-rate stats if mixed in without flagging the capture as
narrative-mode.

**Death-detection bug fixed while investigating this**: even in fightbrief mode, `NpcKilledYou` was
previously routed through `End()` with the same 5-second post-kill grace window used for NPC deaths
(waiting for other pack participants). A player death is unconditionally the end of the whole encounter —
using the grace path meant a death immediately followed by a disconnect (the common real case: the player
quits from the death/respawn menu) never got a chance to expire the grace window, so the encounter stayed
open until `ForceEnd`'s generic `"(forced end: reset/disconnect)"` closed it — exactly what the original
2026-08-01 clog shows, with **no `KilledByNpc` event recorded at all**. Player deaths (both fightbrief and
narrative forms) now call `EndAll()` immediately. See `CombatTracker.cs` (`NpcKilledYou`,
`NpcKilledYouNarrative`) and the regression tests in `CombatTrackerTests.cs`.

**Blindness anonymizes the killer's name.** Narrative death text says `"You have been killed by someone."`
instead of naming the NPC whenever the player is blind at the moment of death (confirmed: the vampire cast
blindness on the player before the killing blow). `CombatTracker` best-effort resolves `"someone"` back to
the sole currently-active NPC when there is exactly one; with multiple active participants it keeps the
literal `"someone"` rather than guessing wrong. This same blind-hides-identity behavior likely applies to
ordinary narrative hit/miss lines too (not yet parsed) and to fightbrief mode's own NPC-name text if MUD2
applies the same anonymization there — not yet confirmed either way.

**Caution: even the "sole active NPC" resolution of "someone" can be wrong.** A blind player cannot see
room arrivals, departures, or (per the user) other NPCs fleeing — so "you are fighting exactly one NPC"
according to `CombatTracker`'s bookkeeping does not guarantee only that NPC could have landed the hit.
Concretely: attack rat0, cast blind on rat0 but the spell fails and blinds you instead, then "Someone hits
you" arrives — this could genuinely be rat0, but it could equally be a second rat that wandered in and
joined the fight unseen, an unrelated NPC that entered and attacked, or another player attacking you, none
of which a blind player is told about. `CombatTracker`'s resolution is a best-effort label for display and
clogging, **not a verified fact** — any analysis pass consuming `KilledByNpc`/anonymized-hit attributions
should treat the resolved name as "most likely, unverifiable" whenever blindness was active, and this
caveat should be repeated in any future narrative hit/miss parsing work (see the deferred item above).

**New hidden mechanic worth tracking later: sleep suppresses ALL client-visible combat resolution.** From
the raw session recording, once `"You have fallen into a magically-induced sleep..."` lands, no combat
text of any kind appears for either side — not even the periodic stamina-bar prompt updates — until
`"You have just been woken up!"`, immediately followed by the fatal hit. This means a sleeping player can
take real, unreported damage/hits with zero client-visible signal in between, which would show as an
implausible instantaneous stamina drop if naively diffed. Sleep/wake are not yet threaded through
`EffectTracker`/`ClogWriter` as a tracked state (unlike blind/deaf/dumb/crippled, which already come from
the periodic scorecard status-line flags, not text) — this is a good candidate for a future dedicated pass
given the user's specific interest in the hit/miss/pass hidden-tick mechanic.

## In-client data collection: "$clog" (opt-in) and "$clog eval"

Clogging is now **opt-in**, toggled from the game input box:

- `$clog on` — starts recording every subsequent combat encounter to `~/.mucka/clogs/clog.*.jsonl`
  (see `ClogWriter`), and opens a small floating "Clog" window (see `ClogPage`/`GamePage`'s
  `OnOpenClogWindowRequested`) showing the live combat-stats readout that used to live in the
  extras side panel. Closing that window (its native ✕) turns clogging back off — the window
  itself is the on/off indicator, so there is exactly one place to check.
- `$clog off` — stops recording (closing any in-progress encounter cleanly) and closes the window.
- `$clog` / `$clog status` — prints the current on/off + recording state without changing anything.

**`$clog eval <itemid>`** (only available while clogging is on) automates the manual item-inspection
sequence used to reverse-engineer an item's hidden strength/dexterity cost:

1. `GameViewModel` does a cheap local sanity check against the last FEI carried-items snapshot
   (`SidePanel.InventoryList`) before starting, but it's only a heuristic (warns, doesn't block) —
   FEI shows an item's display **name/label** (e.g. `"croquet mallet"`), which need not equal the
   short **id** a player can type for it (e.g. `"mallet"`), so a strict string-equality check
   against FEI produced false "not carried" rejections in practice.
2. The authoritative resolution step is `identify <itemid>`: MUD2 replies
   `"The X is referred to as X when identification numbers are requested."` for a single
   carried/visible match, naming the item's canonical display text, which is what the rest of the
   sequence then uses (look/weigh/drop/get all accept it directly). Zero matches aborts the eval
   (not carried/visible, or an unknown id). If `<itemid>` instead names a whole **weapon class**
   (e.g. `"axe"` while carrying a falchion and a halberd), MUD2 replies once per matching carried
   item — eval detects that (more than one match) and aborts rather than guessing which one was
   meant, but logs the matches as a `type: "identify_class"` entry since class membership is
   independently useful research data (this is also a promising avenue for mapping the informal
   weapon classes mentioned elsewhere in this doc — `identify <classname>` against a full
   inventory is effectively a membership query).
3. `look <name>` for its description, `weigh <name>` for its reported weight (MUD2 always phrases
   this generically — `"The weight of the staff is 4kg."` — never by itemid, so the parser matches
   on the surrounding phrase).
4. Reads a **baseline** strength/dexterity from `MuckaConnection.CurrentStats`, which mirrors
   `MudSession`'s continuously-updated merged FES snapshot (kept fresh by the client's periodic FES
   heartbeat) — read directly, not awaited from a fresh event, since subscribing and waiting for
   the *next* `StatsUpdated` event races the heartbeat's own cadence and can time out into an empty
   snapshot right after subscribing (this was an earlier bug: baseline printed as `"str: ? -> 45"`).
5. `drop <name>` followed by an explicit `sc` (score/full-status) to force a fresh, parseable stats
   reply, then reads strength/dexterity again — the delta is that single item's str/dex cost. `sc`
   matters specifically because its `"strength: N  effective strength: M"` /
   `"dexterity: N  effective dexterity: M"` lines are what `GameLineAnalyzer` actually parses;
   MUD2's terser `qs` quick-stats reply (`"eff str 45  eff dex 61  ..."`) looks similar to a human
   eye but is a different format the parser doesn't recognise at all — an earlier version sent
   `qs` for this and always silently timed out.
6. `get <name>` to restore the original carried state (again followed by `sc`), reading
   strength/dexterity a third time to confirm the restoration. This get-back step always runs
   (`try`/`finally`), even if the drop step above timed out or threw, so a fumbled eval never
   leaves the item lying on the ground.

Results print to the terminal and append one JSON line per eval to `~/.mucka/clogs/items.jsonl`
(`type: "item_eval"`, recording both the original `itemId` typed and the `identify`-resolved
`resolvedName`), so item-cost data accumulates across sessions the same way combat clogs do.
This is exactly the workflow that surfaced the staff example: picking up a 4kg staff cost 6
effective strength and 1 effective dexterity in one live test — a cost not reported anywhere else
in the client, and (per the user) not necessarily proportional to weight alone for every item.

Not yet implemented: `inspect <itemid>` was tried live and produced no additional useful data over
`look`, so eval does not send it. Item *labels* (e.g. "shining falchion" vs the itemid "falchion")
are not queried either — there is no reliable command found so far to retrieve them, so eval only
ever reports/logs the itemid/resolved name, not a decorated label. NPCs' own carried
weapons/items are also not currently observable at all (we only ever `identify`/`look`/`weigh`
our own inventory) — their weight presumably still affects their effective str/dex the same way,
but we have no way to measure or even confirm what an NPC is carrying beyond the "weapon in use"
line the `sc` sheet reports while fighting one.


