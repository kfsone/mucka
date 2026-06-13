# Mapping work-in-progress: context handoff

Prompt for a fresh session picking up the MUD2 mapping effort. State as of 2026-06-12.

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
  Fix: validate the fex segment against its `{c12.08.02}` marker, not prompt position.
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

## Agreed next steps, in order

1. **Fix decode_probe segment alignment** (marker-validated, not positional) and
   review/document `reduce_walk.py`. Cheap; blocks data quality for everything later.
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

Steps 3–4 need the operator in-game; 1, 2, 5 are code.

## Hard rules (poison the dataset if violated)

- Names are evidence, not keys: NEVER merge rooms on name match; agreement never
  merges above its evidence tier; ambiguity retroactively demotes T1 name bindings.
- NEVER synthesize reverse edges; record only observed directions.
- Items seen in captures are meaningless for identity without a `breadcrumbs` record.
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
