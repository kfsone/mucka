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

## 8. Current tooling

Mapping is operation-driven from the client's mapping console (`$map`, Windows): the
user explicitly probes a room (all the section-5 verbs plus FEX in one command
interrupt) or runs move-and-capture in a direction from the compass. Each session
appends to one walk file; edge outcomes -- including refusals, which are data --
are annotated `edge: {from} |{dir}> {to}` / `edge: {from} |{dir}! {reason}`.
Nothing infers movement from what the player types: the console knows what it sent.
`tools/mapping/decode_probe.py` decodes captures for analysis;
`tools/mapping/README.md` covers formats and how to hand data to sub-agents.

Not yet captured: out-of-map rooms reached by magical teleports (the sorcerer's
room, the wizards' homes, ...) -- expect `appear`-style transitions with no edge.
