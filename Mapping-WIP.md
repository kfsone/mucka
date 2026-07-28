# Mapping work-in-progress: context handoff

Prompt for a fresh session picking up the MUD2 mapping effort. State as of 2026-06-12.

**2026-07-09 update:** `mapdb.sqlite` is confirmed dormant -- the live model is `MapGraph`
(in-memory, rebuilt from walk files each load), never persisted; `ingest_walk.py` was never
written (see the MUD-Cartography.md correction). New: hand-authored **edge rules** (guard ->
outcome decision-table rows) ship in the `$map` console -- `carrying`/`else` guards,
`arrive`/`refuse`/`absent` outcomes, persisted as `{"extra":"edge-rule"}` records and rendered
per edge (ROOM-panel table + compass tooltips + live FEI inventory capture). Grammar + record:
MUD-Mapping-Design.md §4.5. Also fixed: the close-room / u-turn return picker now uses
**reported** exit destinations (the `exits` verb, via a new `ExitLineReady` forward
parser -> MudSession -> MuckaConnection -> MappingSession) -- it prefers an exit reported to
lead home over an unconfirmed reciprocal reported elsewhere (the Cedar-forest miss; see the
MUD-Cartography.md close-room note). Next: `door` guards -- which need `LongDescLineReady` plumbed
parser -> MudSession -> MuckaConnection -> MappingSession so the room long description (where
door state lives) reaches the mapping layer; then the same-name/multi-fex "what changed"
door-detection view (the Ingresso over-splitting fix).

## Read first, in order

1. `MUD-Cartography.md` — domain model (room identity, edges, conditions, breadcrumbs).
   Every rule below derives from it; do not act on map semantics before reading it.
2. `MUD-Mapping-Design.md` — the design proposal (DRAFT, discussed and accepted as
   direction): two layers (append-only walk files + derived `MapModel`), five-tier
   evidence ladder, Name-vs-Instance edge destinations, decision tables, work queue,
   planner, three-tab $map UX, staging plan in §8.
3. `tools/mapping/README.md` — capture formats, decode/ingest tooling, sub-agent policy.

## Current state

- **Capture layer works** (Windows-only, `#if WINDOWS`): `Core/Mapping/MappingSession.cs`
  (op console: probe / move-and-capture, edge annotations with fex fingerprints),
  `MappingStore.cs` (walk-file scanning, resolved-edge keys), `MapGraph.cs`
  (name-keyed proto-graph + BFS guidance — superseded by the design's MapModel, still
  live). UI in `Pages/MappingPage.cs`.
- The **u-turn button is known-flaky by design**: `PickReturnLocked` matches names only,
  no multi-hop. Design §6 retires it in favor of the planner. Don't patch it.
- **First real walk ingested**: `~/.mucka/mapping/walk.www.mud2.com.20260612-140621.jsonl`
  (swamp → cottage perimeter sweep → swamp → western forestry, ~5 min, 71 ops).
  Summary: `~/.mucka/mapping/summary.walk.www.mud2.com.20260612-140621.md`
  (44 provisional rooms, 68 distinct traversed edges, 1 refusal, same-name candidate
  sets kept split).
- **New tool**: `tools/mapping/reduce_walk.py` — collapses a walk jsonl to a ~350-line
  digest; the standard ingestion path is now jsonl → reduce → analyze. Agent-written
  in a hurry; needs review and a README mention (next step 1).

## Findings that shape the work

- **Decoder bug**: async server events mid-probe shift `decode_probe.py`'s positional
  segment labels (seen twice in the walk: fex slot empty, qscan text in the fei slot).
  **Fixed**: `label_probe_segments` now pre-classifies known-async C1 codes (06–09,
  11, 13–14, 16, 19) before doing fex-anchor labeling. Codes 03/04/05 (item/creature
  lists) deferred — need nested-awareness to distinguish from FEI content.
- **qscan is not a subset of exits**: it emitted an `over=rocky beach` line at the
  cliffs with no corresponding exit. Views must not mint edges, and the §5 quickscan
  dedup hypothesis needs re-testing with corrected expectations.
- **The entire walk was during a storm** (ambient `20 13` on every exposed outdoor
  arrival) and predates context records — permanently `context: unknown`. This is why
  Stage 0 is urgent. Note: indoor rooms report `20 00` silence, so weather is
  unobservable indoors; context records must distinguish "observed ¬rain" from
  "can't tell from in here" (carry last outdoor observation + staleness).
- **River R18/R19** (see summary): identical short+long+fex, divergent exits tables,
  traversed `R18 |w> R19`. Cleanest live ambiguity; first breadcrumb target.
- **Compass "?" flicker is fex-keyed** (root cause of the "inconsistent for unknown
  reasons" feel): exit-resolution keys on `room|fex|dir` (`EdgeKey`), and the same room
  legitimately reports different `fex` over time (door opens, weather, cliff crumble —
  see the conditional-exit taxonomy below). When fex changes, a *different* EdgeKey is
  tested, so an already-walked exit can flip back to "interesting" though nothing about
  it changed; closing out a distant room likewise drops a near exit's "?" via
  `ExitLeadsToOpenRoom` (name-keyed, so same-name rooms can mislead it). This is by
  design until Stage 1 MapModel folds same-room/differing-fex into conditional edges —
  do NOT patch it ad hoc. 2026-06-19: the compass icon set (9 PNGs + size slider) was
  retired for a single amber "?" on interesting exits; the determination logic above is
  unchanged, so the flicker is calmed (binary present/absent) but not fixed. The "?" is a
  deliberate placeholder for richer per-exit state later (fanout / there / there-and-back
  / here), so keep `InterestingExits` and its caller forward-friendly.
- **Heartbeat focus mode is shared-session state — restore it on every teardown.**
  `SetMappingFocus(true)` reduces the periodic heartbeat to FES+FEW (online list for PKer
  watch) while the mapping window has focus. The `MudSession` is reused across
  reconnects/relogs, so focus state MUST be cleared on disconnect or a relog resumes
  FES+FEW and silently starves the main window of inventory (FEI) — the
  desktop sibling of the known FES-leak-on-relog bug. Fixed 2026-06-19: `MudSession.Reset()`
  and `OnGameModeExited()` now drop `_mappingFocus` so FEI resumes;
  `MappingSession.Dispose()` restores it as a safety net.
- **Focus mode currently forces FEW even if the user disabled the online list** (`_includeFew
  == false`). This is intentional for now — focus mode exists to keep the PKer list live
  mid-survey — but it overrides a user opt-out. Open question: gate it (`focused && _includeFew`)
  or leave it. Low stakes; revisit if it surprises anyone.
- **Two windows, one connection → current-room can briefly desync.** `_currentRoom` is
  shared; a manual move in the game window and a mapping op completing race to update it.
  Walk-file edges key off `_moveFrom`/`_moveFromFex` captured at op start, so a wrong edge
  is unlikely, but Seek/close-room re-planning reads `_currentRoom` and could plan a hop
  from a room the player just manually left. Mitigated by re-probing after each hop and
  the home-verification mismatch blocking (rather than looping). Inherent to the
  two-window design; not a bug to fix, a constraint to remember.

## Conditional exit taxonomy (from live captures)

Exits can appear/disappear from fex for several distinct reasons. MapModel must track
these differently; capture layer records them all the same way (just fex + edges).

| Type | Observed example | Notes |
|---|---|---|
| **Door state** | Small bedroom sw→Fitted cupboard (appears after opening door); Cellar w→Coal bunker (same) | A door *object* gates the edge. Door closed/locked = edge absent from fex; door open/absent = edge present. One room, one edge, one condition. The fex fingerprint reflects door state at probe time. Two observations of the same room with different fex are the *same* room — not two room instances. |
| **Puzzle-locked** | Mausoleum tomb doors | Exits absent until puzzle solved. Indistinguishable from door-state at capture time; only context distinguishes them. |
| **Encumbrance** | Badly-paved road narrow gap: "gap too narrow without dropping everything" | Carrying-capacity gated. Refusal message is the signal. |
| **Environmental damage** | Beaten track near cliff: crumble auto-retreat, fex loses `w` | Refusal modifies available exits. May be permanent or session-scoped. |
| **Hostile NPCs / fight** | Coal bunker (rats), path refusal during fight | Transient blocker; edge left unresolved (already handled by `IsTransientRefusal`). |
| **One-way / squeeze** | Cellar south crack: "might not be able to turn round" | Not yet traversed; description warns of no-return. Treat as unresolved until walked. |
| **Light** | Dark rooms (cellar north/east) | Absence of light makes room unidentifiable; edges stay unresolved until re-walked lit. |

**MapModel implication**: when two observations of the same room differ only in fex
(same short + long), they are the *same room* — not different instances. Exits that
appear in one fex but not the other are **conditional edges**: gated by a door object,
puzzle state, or other world condition. The condition type is not inferrable from
capture data alone. The presence of the edge in fex is the observable; the cause
requires a note or subsequent observation with context.

**C# capture implication**: none yet. The capture layer correctly records fex as
observed. Re-probing after a state change produces a new observation automatically.

## Data model: event-sourcing architecture

Decided 2026-06-13. The mapping DB (`~/.mucka/mapping/mapdb.sqlite`) uses a
full event-sourcing model. Key principle: **observations and edge_events are
immutable; only assignment linkage is revised (by supersession, never deletion)**.

### Layers

```
raw_captures          immutable JSONL records verbatim (SHA256 ID)
    ↓ decoded into
observations          immutable room states (SHA256(short|long|fex|exits) ID)
edge_events           immutable raw traversals (UUID, outcome enum)
    ↓ assigned via
observation_assignments   append-only; observation → impression
impression_assignments    append-only; impression → location
    ↓
impressions           immutable hypotheses ('observed'|'synthesized'|'canonical')
locations             identity anchors only (UUID)
    ↓ derived into
edges                 canonical edges (from/to: impression until resolved to location)
derivations           algorithm decision log (supports full replay)
```

**Impression kinds:**
- `observed` — 1:1 with one observation; auto-created on ingest
- `synthesized` — derived from multiple observations (dark+lit subsumption, door-state merge)
- `canonical` — current best model of a location; what map renders; superseded when
  evidence improves (new canonical impression created, old assignment superseded)

**Why full event-sourcing over un-merge-only:**
Fix `decode_probe.py`, re-feed raw captures, recompute derivations — bad observations
orphan automatically. Un-merge-only can correct output; full replay can correct input.
Migration full→un-merge is trivial (stop logging derivation events); reverse is lossy.

**Observation ID is content-hash** — same room seen identically in two walks yields
the same observation ID. Naturally idempotent across captures.

**One capture, multiple observations** — a move record yields both origin and
destination observations. `capture_observations` is a many-to-many join with a `role`
column (`origin` | `destination` | `probe`).

Schema: `tools/mapping/schema.sql`. Init: `uv run tools/mapping/init_db.py [path]`.

## Agreed next steps, in order

1. ~~**Fix decode_probe segment alignment**~~ **Done.** fex-anchor + async pre-classification
   landed. C# capture-side filtering (strip known-async C1 containers from rx before
   logging) is a follow-on; deferred until the Python filter proves sufficient.
2. **Stage 0 — context records**: `MappingSession` emits `{"extra":"context",...}`
   per op — weather (from ambient codes), light, FEI inventory (fei is already in the
   probe battery). Handle the indoor-unobservable case per above. Captures without
   context cannot be retrofitted — this precedes any new walking.
3. **In-game experiment A**: breadcrumb the R18/R19 pair (drop a distinct expendable
   object, qscan from the neighbor; emit the first real `breadcrumbs` extra record).
4. **In-game experiment B**: qscan semantics — what is `over`? does qscan suppress
   duplicate instances (dedup hypothesis) given its line set isn't exits-subset?
5. **Stage 1 — `Core/Mapping/MapModel.cs`**: the pure derive (instances + tiered
   bindings, decision tables, open-exit work queue, stats). Pure function over the
   mapping dir, no persisted state, unit-tested against the existing walk (which
   doubles as the `context: unknown` tolerance test). Only after 1–2 land.

Steps 3–4 need the operator in-game; 2, 5 are code.

## Hard rules (poison the dataset if violated)

- Names are evidence, not keys: NEVER merge rooms on name match; agreement never
  merges above its evidence tier; ambiguity retroactively demotes T1 name bindings.
- NEVER synthesize reverse edges; record only observed directions.
- Items seen in captures are meaningless for *positive / cross-capture* identity
  without a `breadcrumbs` record (things move) -- BUT within a *single* `look around` /
  `quickscan`, a content *difference* between two same-named neighbors is valid
  *distinguishing* evidence they are distinct instances (split-only; never a merge,
  never across captures). See MUD-Cartography.md §6.1.
- Refusals are outcomes (travel-table rows), not errors; contradictions ADD decision-
  table rows, never replace.
- Multiple directions A→B is containment/scale signal, never redundancy.
- Never paste raw walk jsonl into an agent prompt (C1 escapes); use
  `reduce_walk.py` / `decode_probe.py` output.

## Build notes

- Windows build: target `net10.0-windows10.0.19041.0` when building `Mucka.csproj`.
- Mapping code is `#if WINDOWS`; mapping dir defaults to `~/.mucka/mapping`
  (`mappingdir` in mucka.ini, hand-edited key).
- Python tooling runs via `uv run tools/mapping/<script>.py`.
