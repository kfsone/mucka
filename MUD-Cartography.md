# MUD2 Cartography — Session Handoff

**Last updated: 2026-06-14. Read this first when resuming mapping work.**

## Current state

- Capture layer working (Windows): `Core/Mapping/MappingSession.cs` writes
  `~/.mucka/mapping/walk.*.jsonl`. 7 walk files captured to date covering
  ~120 distinct room observations across the Land, mine, graveyard, swamp, and
  cliff/coastal outer loop.
- Analysis tooling: `tools/mapping/reduce_walk.py` (JSONL → compact digest),
  `tools/mapping/decode_probe.py` (raw C1 decode + probe segment labeling).
  See `tools/mapping/README.md` for formats and sub-agent policy.
- **Database**: `tools/mapping/schema.sql` + `tools/mapping/init_db.py`.
  DB lives at `~/.mucka/mapping/mapdb.sqlite`. Schema is defined and initialized;
  **no ingest script exists yet** — walk files are not yet loaded into the DB.
  The ingest layer is the next concrete code task.
- Design doc: `MUD-Mapping-Design.md` (DRAFT, accepted as direction).
- This file: domain model reference + session state. Do not overwrite the domain
  model sections without good reason.

## Next concrete task: ingest script

Write `tools/mapping/ingest_walk.py`:
1. Read a walk JSONL file, insert each record into `raw_captures`.
2. Decode each record into `observations` (content-hash ID: SHA256 of
   `short|long|fex|exits_sorted`) + `edge_events`.
3. Auto-create `impressions` of kind `'observed'` for each new observation,
   and assign via `observation_assignments`.
4. Log each derivation step to `derivations`.
5. Idempotent: re-ingesting the same file must not create duplicate rows.

Observation kinds to handle: `full`, `dark`, `partial`, `mist_occluded`, `corrupt`.
`mist_occluded` = exits hidden by fog/fumes (room visible, exits not).
`dark` = no room info at all.
`sequence_context` on impressions: `"<predecessor_impression_id>/<direction>"` —
required for graveyard/maze rooms where content-hash is not unique.

## Key open questions

- **Subsumption**: same `short+long+fex`, exits A ⊂ exits B → same room, conditional
  edge. Ingest should flag the conflict, not silently merge. Neither observation is
  wrong; the door/condition state explains the difference.
- **Sequence-context impressions**: graveyard has 11 observation-identical rooms.
  The ingest script should create distinct impressions for each arrival when the
  content-hash collides with an existing impression in the same walk sequence.
- **`over=` exits**: recognized fex keyword (seen at Pond in swamp); treat identically
  to directional exits in all parsing and storage.
- **Dark-room capture**: `MappingSession` does not yet suppress `edge: (unknown)...`
  records during dark navigation. That C# work is deferred until after ingest lands.

## Client console: close-room return routing (2026-06-16)

The `$map` console's Close Room cycle visits every unresolved exit of the current room
and returns home after each. Its return picker (`MappingSession.PickReturnLocked`) twice
falsely reported "no route back" on edges that plainly returned. Both were the same bug
class: **using aggregate, name-keyed destination history to veto the reciprocal.**

- *Different-fex collision* (two "Flower garden"s, one with `sw` and one without):
  `MapGraph.KnownDestination` was name-only, so a sibling room's `ne` masqueraded as this
  room's. Fixed by a **fex-aware destination map** (`NeighborsByKey`, keyed `"{fex}|{dir}"`).
- *Same-fex collision* (the five "Badly-paved road"s, all fex
  `e in n ne nw out s se sw swamp up w` — one's `s`→Entrance hall, another's `s`→Briar
  patch): short+fex **still collides**, exactly the tier-3 identification limit in §3.
  Fex-awareness can't fix this. Fixed by a **geometric reciprocal fallback**: after the
  evidence-based tiers fail, trust the reciprocal of the move that just arrived and let
  the arrival/home-verification probe confirm. A wrong guess blocks safely; it never loops.

**Principle for any auto-navigation built on this graph**: aggregate edge history cannot
establish *instance* identity for same-name (±same-fex) rooms — only traversal-in-sequence
or breadcrumbs (§6) can. Routing must therefore (a) trust the just-walked reciprocal as the
canonical return, and (b) **re-plan from where you actually landed after every hop**, never
blindly follow a precomputed name-keyed path. Verify each arrival; stop on mismatch.

---

# MUD2 Cartography: Domain Model for Mapping Agents

This document defines how MUD2's world is actually structured, for any agent or tool
working on mapping features. Read it before designing schemas, parsers, or renderers
for map data. Protocol codes referenced here are defined in `MUD-ClientProto.md`.
All claims marked [capture] are demonstrated in real session transcripts.

## 1. What the map is -- and is not

MUD2's world is a **directed, labeled multigraph with self-loops and parallel edges**.
It is NOT:

- **a grid.** "Rooms" have no fixed size, shape, or unit. They are *place names* on a
  cartographic map: "Foothills" may span what a grid would call dozens of cells, while
  "Narrow road" is a sliver between two such regions.
- **a DAG.** Cycles are everywhere; self-loops are a deliberate device (mazes).
- **planar or Euclidean.** The world has height and depth (multi-floor buildings,
  caves below pastures), and magic permits topology that cannot embed consistently in
  2D or 3D. Any coordinate assignment is a *presentation* artifact, never ground truth.

Direction labels (`n ne e se s sw w nw`, plus `up down in out` and the special
`swampward`) are narrative geographic hints, not vectors of fixed length.

## 2. What edges encode: scale, containment, relative position

Multiple distinct directions from A to B is not an error or redundancy -- it encodes
geography:

- If several spread-out exits from A lead to B, then A is small relative to B, or A
  runs alongside B over a long shared border.
- If *all* exits from A lead to B, A is effectively a feature *within* B.

[capture] "Narrow road between lands" has `n`, `s`, `ne`, `se`, `up`, and `swampward`
all reporting "Foothills": the road is a thin feature threading between large foothill
regions. (Whether those are one Foothills room or several distinct ones is a separate
question -- see section 3.)

Further edge facts every schema must respect:

- **Reciprocity is not guaranteed.** [capture] "East pasture" has `up` -> Cave, but
  Cave has no `down` exit at all (its way back is `w`/`out`). Record each direction as
  its own observed edge; never synthesize the reverse.
- **`up`/`down`/`in`/`out` are sometimes aliases, sometimes sole routes.** [capture]
  At Cave, `in` and `e` both lead to "Before gate" (alias). Elsewhere `u` is a ladder
  or stair with no ordinal equivalent. An alias is only *proven* when both directions
  are shown to reach the same room instance (section 6), not merely the same name.
- **`swampward` is a landmark bearing**, pointing toward the swamp; its ordinal
  meaning varies room by room. [capture] Cave: swampward -> Rapids (south-ish);
  Gorse: swampward -> East pasture (southeast-ish). Aggregated over many rooms this
  is weak but free global-orientation evidence.

## 3. Room identity: names are evidence, not keys

The server never exposes a room ID. Every room presents:

- **Short description** (FE code `02 01`): title-case header on arrival, e.g.
  "Narrow road between lands". Stable. *Mostly* unique -- but duplicates exist.
- **Reference name**: the name other rooms know it by -- shown lowercase in
  `look around` ("a place known as \"foothills\"") and capitalized in `exits` output.
  Usually equal to the short description, but not always, and NOT unique.
- **Long description** (FE code `02 02`): optional; stable with rare mutable
  exceptions (mostly one known room).
- **Exit set**: mostly static, but conditional (section 4), so only a *majority
  subset* is reliable identity evidence.
- **Lighting**: an unlit room with no player light source yields "It is too dark to
  see" -- no identity evidence at all.

**Identification ladder** (try in order, cheapest first):

1. globally unique short description;
2. short + long description;
3. short + long + majority subset of the exit signature;
4. behavioral probes: breadcrumb objects and actual traversal (section 6).

Which rooms sit in tier 1 cannot be known until the whole map is built, so *every*
identification is defeasible: the data model must support splitting a room record in
two (we conflated distinct rooms) and merging two into one (we double-counted) as
later evidence arrives.

[capture] Real duplicate-name cases: "Narrow road between lands" lists both `n` ->
"Foothills" and `s` -> "Foothills"; "Pine forest" famously has `n` and `e` to "Pine
forest" -- distinct rooms with different long descriptions, not a loop.

## 4. Conditional and deceptive topology

About 99% of exits are static. The rest:

- **Doors** (portcullises, gates...) can block an exit temporarily.
- **Item/state forks**: holding a particular object can send you to a different
  destination through the same exit -- a fork in the path, keyed on player state.
- **Mazes**: the graveyard has a fixed route to its center (se, nw, s, n, ne, sw, w,
  e, n, s); while inside, every exit of an intermediate room loops back to that room
  itself, except `out` and the single correct next step. Self-loops are the signature.
- **Deceptive/variable exits**: rare, magic-driven; an observed edge can simply be
  wrong later.
- **Darkness**: hides everything, including whether you moved where you intended.

Consequence: an edge is an *observation with conditions and a timestamp*, never a
fact. Store `(from, direction, observed-destination, evidence-kind, conditions,
when)`. Repeated consistent observations raise confidence; contradictions mark the
edge conditional rather than overwriting it.

## 5. Evidence catalogue (what each verb yields)

| Source | FE codes | Yields |
|--------|----------|--------|
| Arrival (move/`l`) | `02 01`, `02 02`, `03 xx 01`, `12 08 02` | short desc, long desc, items here, brief exit-direction list (directions only, no destinations) |
| `exits` | `12 09` | one line per exit: direction -> destination *reference name*. Names only: two "Foothills" lines may be two rooms |
| `look around` | `12 ...` per line | per direction: "view blocked" / "too dark" / 'a place known as "x"' plus visible portable contents of that room |
| `quickscan` | as above | same line format as `look around`, fewer lines (see below) |
| Ambient sound | `20 xx` | terrain/situation hint on arrival (e.g. `20 05` meadows, `20 00` silence indoors). Weather-variable ([capture] `20 13` = storm over several outdoor rooms), so corroborating evidence only -- never identity |

**Quickscan dedup hypothesis** [capture, unverified]: at "Narrow road between lands",
`exits` listed 11 directions but `quickscan` printed only 6 lines (n, e, s, w, sw,
nw), omitting exactly the directions whose destinations duplicate a listed one (ne,
se, up, swampward, out). If quickscan suppresses repeat *instances*, it is free
same-instance evidence -- one command distinguishes aliased exits from genuinely
distinct same-name neighbors. Verify before relying on it.

**Object instance numbering**: when more than one of an object exists, each instance
gets a numeric suffix ([capture] "brand18", "spinach0"); singletons are unnumbered
([capture] "a bouncy-ball"). Numbered or singleton, an object visible via
`look around` identifies a neighbor room *instance*, not just a name.

## 6. Disambiguation playbook: breadcrumbs

`look around`/`quickscan` show the portable contents of adjacent rooms. Dropping a
distinctive object therefore lets you test room *instance* equality without leaving
your spot.

[capture] Live demonstration: drop a bouncy-ball (singleton) in Cave, return to East
pasture, `look around`:

    Looking upward ... "cave". It contains a bouncy-ball.
    Looking eastward ... "cave". It contains a bouncy-ball.

Both directions show the same singleton object: `up` and `e` from East pasture are
the SAME Cave instance. From "Before gate", `out`, `swampward`, and `w` all show the
ball -- all three reach that same Cave.

The negative case (the "Pine forest" problem -- current room has `n` and `e` both to
"Pine forest"):

1. Drop `brand47` (or any expendable numbered object) in the current Pine forest.
   `quickscan`: if neither neighbor shows `brand47`, neither exit loops back here.
2. Move `n`. Suppose `exits` here shows `s` and `se` -> "Pine forest". Drop a second
   distinct object (e.g. `wafer2`), `quickscan`: `s` shows `brand47` (that is the
   room we came from), `se` shows neither -- three Pine forest instances are now
   mutually discriminated.

Cautions:

- **Things move.** NPCs, players, and portable objects all relocate on their own.
  An object sighting is NEVER identity evidence unless that object was explicitly
  placed as a breadcrumb and recorded as such in the capture (an extra record:
  `{"extra":"breadcrumbs","items":["brand47","key52"]}`). Captures with no
  breadcrumb record contain no interesting items -- that absence is factual, not
  an omission. Tooling must not infer room identity from incidentally-seen items.
- Use expendable, distinct, low-value objects; never treasure (other players, and
  some mobiles, pick things up -- a moved breadcrumb silently poisons the inference).
- Lit brands explode in the swamp (FE `13 02`); pick breadcrumbs that survive the
  terrain.
- Treat breadcrumb evidence as timestamped: it decays. Re-verify if the session is
  interrupted or the object may have moved.
- One ambiguity at a time per object; record which instance the suffix (`brand47`)
  was dropped in, since suffixes are assigned by the server, not by us.

## 7. Data-model implications (summary for implementers)

1. **Observations, not assertions.** Persist raw sightings (room observation, exit
   report, traversal result, breadcrumb check) with timestamps. The "map" is a view
   derived by clustering observations into room instances via the identification
   ladder; clusters must be cheap to split and merge.
2. **Two edge strengths**: *reported* (seen in `exits`/`look around` -- destination
   is a name) vs *traversed* (we moved and identified the arrival room -- destination
   is an instance). Only traversal, or a breadcrumb sighting, binds an edge to an
   instance.
3. **Multigraph storage**: multiple labeled edges between the same pair, self-loops,
   and asymmetric pairs are all normal, meaningful data -- never "cleaned up".
4. **Layout is derived**: any 2D/3D positioning is best-effort rendering on top of
   the graph, informed by direction labels, the containment heuristic (section 2),
   and `swampward` bearings. It may be locally inconsistent (mazes, magic); the
   renderer must tolerate that rather than the data model lying to fix it.
5. **Coverage accounting**: completion means every room visited AND every edge
   traversed (an `a -ne-> b` and `a -n-> b` pair are two edges to walk), with
   ambiguous same-name destinations resolved by breadcrumb or quickscan-dedup
   evidence.

## 8. Current tooling and database

Mapping is operation-driven from the client's mapping console (`$map`, Windows): the
user explicitly probes a room (all the section-5 verbs plus FEX in one command
interrupt) or runs move-and-capture in a direction from the compass. Each session
appends to one walk file; edge outcomes -- including refusals, which are data --
are annotated `edge: {from} |{dir}> {to}` / `edge: {from} |{dir}! {reason}`.
Nothing infers movement from what the player types: the console knows what it sent.

`tools/mapping/decode_probe.py` decodes captures for analysis.
`tools/mapping/reduce_walk.py` collapses a walk JSONL to a compact digest (the
standard input for analysis agents -- never paste raw JSONL).
`tools/mapping/README.md` covers formats and sub-agent policy.

**Database** (`tools/mapping/schema.sql`): event-sourcing model.
- `raw_captures` → `observations` + `edge_events` (immutable source layer)
- `impressions` → `locations` (hypothesis + identity anchor layer)
- `observation_assignments` + `impression_assignments` (append-only, supersedable)
- `derivations` (algorithm decision log — enables full replay)
- `edges` (canonical, derived from edge_events via resolution)

The DB is at `~/.mucka/mapping/mapdb.sqlite`. The **ingest script does not exist yet**;
see the "Next concrete task" section at the top of this file.

Not yet captured: out-of-map rooms reached by magical teleports (the sorcerer's
room, the wizards' homes, ...) -- expect `appear`-style transitions with no edge.
