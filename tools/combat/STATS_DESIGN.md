# Combat stats: historical comparison and outcome projection

Design doc. Written 2026-08-03.

**Status: steps 1-6 implemented. Step 7 (projection) is planning only and deliberately NOT built.**

Implemented map:
- Per-fight layer: `mudsharp/Combat/FightAccumulator.cs`, consumed by `ViewModels/CombatStatsAggregator.cs`
  (display) and `Core/FightHistoryRecorder.cs` (persistence).
- npc_group port: `mudsharp/Combat/NpcGroups.cs`, pinned to the Python by
  `tools/combat/npc_group_fixture.txt` (regenerate with `gen_npc_group_fixture.py`).
- Storage: `mudsharp/Combat/FightRecord.cs` + `Core/FightHistoryStore.cs` -> `~/.mucka/clogs/fights.jsonl`.
- Query: `mudsharp/Combat/FightHistory.cs`. Display: `ViewModels/CombatHistoryFormatter.cs` -> `Pages/ClogPage.cs`.
- Offline: `live_fights` table plus `v_live_by_npc_group` / `v_live_by_weapon_npc_group` in
  `schema.sql`, loaded by `ingest_clogs.py`.

One deliberate deviation from the plan below: `normalize_npc_group` diverges from the Python for
degenerate names (blank/all-digits), where the C# yields `""` and the Python yields `"s"`. Empty is
rejected downstream; a literal `"s"` group would accumulate a junk bucket. Unreachable for real NPC
names. Pinned by `NpcGroupsTests`.

Goal, in the user's words: "contrasting previous fights with the same npc, perhaps comparing
with current/previous weapons, and perhaps some kind of 'are you winning' projection".
The projection is explicitly **plan-only for now** — see the last section.

## Where we are

`CombatStatsAggregator` produces a live `CombatEncounterSnapshot` (hits, misses, hit rates,
approx damage done/taken, duration, dps) which `ClogPage` renders. `ClogWriter` appends a
per-encounter JSONL clog to `~/.mucka/clogs/`. The offline pipeline (`ingest_clogs.py` ->
sqlite) already computes the exact cross-fight rollups we want, via `v_summary_by_npc_group`
and `v_summary_by_weapon_npc_group`.

So the analysis shapes are known-good and proven against real data. The two things missing are
per-NPC granularity in the *live* tracker, and any way for the client to read its own history.

## Blocker: the live aggregator has no per-fight layer

`CombatStatsAggregator` buckets everything per *encounter*. Every `_youHits++`,
`_approxDamageDone +=`, etc. is encounter-wide, ignoring `combatEvent.NpcName` (which is
populated on every event). In a multi-NPC encounter — goat plus ram, or anything that joins
mid-fight — the stats are one undifferentiated lump.

That makes "how did this rat fight compare to previous rat fights" unanswerable, because the
current fight's numbers may include damage dealt to something else entirely. The offline
schema already models this correctly with two layers (`combat_sessions` holding N
`combat_fights`); the live tracker needs the same split.

**This has to land first.** Everything below depends on it.

### Shape

Keep `CombatStatsAggregator` as the encounter-level owner, but hold a
`Dictionary<string, FightAccumulator>` keyed by NPC name. Each `FightAccumulator` carries the
same counters the encounter currently carries, plus its own start time, weapon-at-start,
weapon-last-used, and outcome. Route every event with an `NpcName` into its bucket as well as
the encounter total (so the existing HUD numbers do not change meaning).

Two attribution caveats to encode, not paper over:

- **Damage taken cannot be split across simultaneous attackers.** The stamina delta on a hit
  line names the attacker, so `HitByNpc` attributes cleanly. But the unreported-regen fog (see
  MECHANICS_NOTES) applies per-fight now, so a 3-NPC pile-on will have per-fight damage-taken
  that sums to slightly less than the encounter total. Expected; do not "fix" by rescaling.
- **Weapon provenance.** A weapon equipped for fight A extends to fight B when B joins
  mid-encounter (MUD2 does not re-arm you, and there is no equip line for the second fight).
  So a joining fight's `weapon_used` must inherit the encounter's current weapon, not be left
  null. This is already handled correctly in `reduce_combat.py` — mirror that behaviour.

## Persistence: a per-fight history index

Add `~/.mucka/clogs/fights.jsonl` — append-only, **one compact line per fight** (not per
encounter), written when a fight closes. This is a rollup index, deliberately separate from the
detailed per-encounter clogs, which stay as they are.

Why a flat file rather than SQLite in the client:

- No new dependency, no MAUI/Android packaging question.
- Tiny. 44 clogs so far is on the order of 60 fights; even 10k fights is a couple of MB.
  Loadable into memory at startup in one off-thread read.
- The Python pipeline can ingest it directly, so offline analysis and the live HUD agree by
  construction instead of by reimplementation.

Per-line fields (superset of what the HUD needs, so we do not have to re-collect later):
timestamp, npc_name, npc_group, weapon_used, outcome, duration_ms, you_hits/misses,
they_hits/misses, approx_damage_done, approx_damage_taken, and the encounter-start context we
already snapshot (room, weather, raw/effective str+dex, carried weight, blind/deaf/crippled,
status effects). Also a `narrative_mode` flag — see below.

**`narrative_mode` matters.** A character without `fightbrief` records near-zero hit/miss counts
(MECHANICS_NOTES documents this). Mixing those rows into aggregate hit-rate stats would badly
skew them. Flag at write time and exclude from rate comparisons by default. This is a concrete
reason task #1 (auto-enable fightbrief) should land before serious collection.

### npc_group must be shared logic

`normalize_npc_group` currently lives only in `reduce_combat.py` (strip trailing digits, take
the last token, irregular-plural table, else pluralize: `rat0` -> `rats`). The client needs a
C# twin. Two implementations of the same rule *will* drift. Mitigate by porting it with the
irregulars table as data and adding a test that asserts the C# output matches a checked-in
fixture of name/group pairs generated from the Python side.

## The comparison surface

In `ClogPage`, below the live block, a "vs history" section for the current primary target:

- Sample size `n` shown **first and always**. With n=3 the honest presentation is "3 prior
  fights", not a confident-looking average. This is the single most important honesty guard.
- **Median, not mean**, for damage/duration/hit-rate. One fight where you wandered off mid-
  encounter destroys a mean and barely moves a median.
- Current fight's live value against the historical band, so you can see "hitting harder than
  usual" at a glance.
- Keyed on `npc_group` by default (all rats), with the specific instance (`rat0`) available —
  group is what gives usable sample sizes, instance is what answers "is this particular one
  tougher".
- A weapon breakdown for that npc_group: per-weapon n, median damage-per-hit, hit rate,
  kill rate. This is the axis your hypotheses live on (picks vs dwarves, axes vs the ogre).

Deliberately not doing: significance tests, confidence intervals, any single "this weapon is
better" verdict. At these sample sizes that would be false precision. Show n and the spread;
let the reader judge. Revisit once some npc_group has n in the hundreds.

## Projection: "are you winning" — PLANNING ONLY, DO NOT BUILD YET

Recorded now because it changes what history needs to store.

**The key realisation: history is what makes projection possible at all.** We never observe NPC
stamina — MUD2 gives no readout for it. But if previous `rats` kills each took roughly 35 points
of cumulative damage-done, that is an empirical estimate of the rat stamina pool. Without the
history store there is no denominator and no projection can exist. This is the strongest reason
to build the store first, independent of the display.

Sketch:

- Estimate NPC pool = median cumulative `approx_damage_done` across historical fights against
  that npc_group **that ended in a kill** (non-kills are censored observations — the NPC
  survived, so its pool is only bounded below. Including them biases the estimate down).
- Remaining pool = estimate minus this fight's damage-done so far.
- Their time-to-kill-me = my current stamina / their observed damage rate this fight.
- My time-to-kill-it = remaining pool / my observed damage rate this fight.
- "Winning" = my TTK meaningfully shorter than theirs.

Why this is harder than it looks, and what to respect:

- **The pass tick.** Three outcomes per participant per tick (pass, miss, hit), and a pass emits
  *no text at all* — the starfish fight sat silent for 90 seconds. So a short observation window
  can show an inferred rate far above the true one. Rate estimates must be over wall-clock
  elapsed, not over observed ticks, or a lucky opening burst reads as a guaranteed win.
- **Both sides regen**, and NPC regen is entirely unobserved. A long grindy fight against a
  regenerating NPC may be unwinnable even with a healthy-looking rate.
- **Early-fight rates are near-worthless.** Suppress the projection entirely below some minimum
  observed exchange count; showing "you're winning" off one lucky hit is worse than showing
  nothing.
- **Pool estimates vary by weapon.** `approx_damage_done` is derived from the reported damage
  range midpoint, so it should be roughly weapon-independent — but if the hidden per-weapon
  modifier we are hunting turns out to apply *after* the reported range, this assumption breaks.
  Treat the pool estimate as weapon-agnostic initially and check that assumption once there is
  enough data to compare per-weapon pool estimates for the same npc_group.
- Present as a coarse three-state (winning / even / losing) with the sample size behind it, not
  a percentage. A number implies precision we do not have.

## Suggested order

1. Per-fight layer in `CombatStatsAggregator` (+ tests). No visible change.
2. `normalize_npc_group` C# port with cross-language fixture test.
3. `fights.jsonl` writer, appended on fight close.
4. Loader (off-thread at startup) plus the in-memory history query.
5. "vs history" block in `ClogPage`.
6. `ingest_clogs.py` support for `fights.jsonl` so offline and live agree.
7. Projection — only after there is enough real data to sanity-check pool estimates.
