# MUD2 Mapping: Design Proposal (preliminary)

Status: DRAFT for discussion. Domain model lives in `MUD-Cartography.md` — this
document does not restate it; it proposes the data model, analysis pipeline, and
$map console UX built on top of it. Capture formats are in `tools/mapping/README.md`.

## 1. End artifact

A reconstruction of MUD's original travel tables, at room-instance level:

    jetty
        ne    -               beach1
        w     !carrying boat  "It's too rough to swim"
        w     rain            "With all the rain it's too rough"
        w     -               sea1

i.e. per room: `(verb(s), condition, destination | refusal-message)` rows. This is
a **derived, defeasible view** — never authored directly, always recomputed from
observations. Completion = every room visited AND every (room, direction) pair
resolved under at least one condition context, with same-name ambiguities settled.

## 2. Architecture: two layers

**Layer 1 — observation log (exists).** The walk files. Append-only, raw,
timestamped. The console (`MappingSession`) stays a dumb recorder: it sends
operations, captures responses, annotates outcomes. It never interprets.

**Layer 2 — derived model (new).** A pure function over the mapping directory:

    MapModel.Build(directory) -> { instances, edges, decision tables,
                                   work queue, inbox, stats }

Recomputed on load/reload, no persisted state of its own (derived caches are
disposable). Everything below describes this layer. The current `MapGraph` is a
proto-version of it (name+fingerprint keyed); `MapModel` replaces it.

## 3. Room identity

### 3.1 Instances and clusters

A **room instance** is a cluster of observations the model currently believes are
the same place. Clusters must be cheap to split and merge; every merge records the
evidence tier that justified it, every split records its witness (see 3.3).

Evidence tiers, weakest first:

| Tier | Evidence | Revocable? |
|------|----------|------------|
| T1 `name-unique` | short description is corpus-unique (so far) | yes — auto-demoted the moment a second instance claims the name |
| T2 `name+long` | short + long description match | yes |
| T3 `signature` | T2 + majority subset of exit fingerprint | yes |
| T4 `traversal` | walked in sequence (console knows what it sent) | no, barring maze/magic edges |
| T5 `instance` | breadcrumb sighting or player-inclusion probe | no |

**Ambiguity demotes agreement.** A `Name` reference binds to an instance at T1
while the name is unique in the corpus. Discovery of a second same-named instance
retroactively unbinds every T1 binding of that name (they fall back to unresolved
and re-enter the work queue). This keeps T1 usable for the large unique majority
without poisoning the "Pine forest" cases. T1–T3 bindings are bookkept separately
from T4/T5 so revocation is cheap.

### 3.2 Edge destinations: Name vs Instance

An edge's destination is a sum type:

- `Name("Trail")` — reported by `exits`/`look around`/`quickscan`. Dangling.
- `Instance(#42)` — bound by traversal arrival, breadcrumb, or (revocably) T1–T3.

Reported destinations are NEVER unified by name alone when the name is ambiguous.
"Potential rooms" need no representation: an unbound `Name` ref IS the frontier
entry, and an unanticipated room (the trailmaster's hideout) costs nothing because
nothing was speculated.

### 3.3 Comparing candidate instances

Three-valued comparison over decision tables (4.2), not exit snapshots:

1. **Distinguished** — some `(direction, context)` has conflicting outcomes under
   *matched* context. Split, storing the witness pair (splits are defeasible too).
2. **Confounded** — outcomes differ but contexts differ (raining in one capture).
   Zero evidence either way; emits a work-queue task: "re-probe X dir under ¬rain".
3. **Identical-so-far** — matching rows discriminate nothing and never accumulate
   into a merge. Flag the cluster `candidates: N`; only T5 evidence (or T1
   uniqueness) merges.

**Divergence distinguishes; agreement never merges above its tier.**

## 4. Edges and conditions

### 4.1 Outcome kinds

| Kind | Meaning | Handling |
|------|---------|----------|
| `Arrived(instance)` | traversed, arrival identified | normal binding |
| `DarkDestination` | traversed, arrival unlit | auto: edge stays open with requirement `bring-light` |
| `Refused(message)` | server rejected the move | rain-pattern messages auto-annotate `retry-when: ¬rain`; all others → inbox |
| `Transient(message)` | movable blocker ("blocked by the ox") | recorded, edge stays wanted (exists today) |
| `Artifact(timeout/no-output)` | op failure | recorded, edge stays wanted (exists today) |

### 4.2 Decision tables

Per `(instance, direction)`: the set of observed `(context, outcome)` rows. A
contradiction (same direction, different outcome) **adds a row**, never replaces
one — the contradiction is what *creates* the conditional annotation. This is the
travel-table format reconstructed: a refusal row `(¬boat, "too rough")` is not a
failed observation, it is the `w !carrying boat "..."` line.

### 4.3 Context snapshots

Every operation records the context the console can see at op time:

- **weather**: rain/storm state (ambient `20 xx` codes; weather FE events — needs
  a survey of what the protocol exposes, see §8 experiments)
- **lighting**: lit/unlit/unknown (carrying light source; too-dark outcomes)
- **inventory**: FEI is already in the probe battery — capture the item list
- **doors**: open/closed where observable

Proposed record (new `extra` line, emitted by the console per op):

    {"extra":"context","weather":"rain","light":"lit","carrying":["brand47","boat"]}

Unknown fields are omitted; absence means "not observed", never "false".

### 4.4 Discriminator analysis

For an edge with mixed outcomes, diff the context sets:

    refused  at {¬rain, ¬boat}, {rain, ¬boat}
    arrived  at {¬rain, boat}

→ `boat` is the only feature separating success from failure → surface
"hypothesis: requires boat (2 refusals, 1 success consistent)" in the inbox.
Human confirms with one tap; confirmation is an annotation. A later contradicting
row *reopens* the hypothesis rather than fighting it. A later consistent row
(boat+rain → arrived) strengthens it. No grid/geometry assumptions, no deduction
beyond set logic over recorded contexts.

Two patterns are pre-confirmed (auto-rules): `DarkDestination` → bring-light, and
rain-message refusals → retry-when-¬rain. Everything else waits for a human.

### 4.5 Hand-authored rules (implemented 2026-07-09)

The console can now write decision-table rows directly, so section 4.2's tables are seeded
by hand while the discriminator analysis (4.4) stays future work. A "mark" is one row:

    (from-room + fex + dir)   guard  ->  outcome

**Guard** (the "when"): `carrying <item>` (item in the FEI carried inventory; optional `!`
negate, plus an optional free `class` tag e.g. "boat" -- stored, never resolved into a
synonym/object database); `door(<ref?>) <state>` (ref = discriminator, or the room's sole
door; state open|closed|locked|absent -- observable in the room long description);
`weather <state>`; `count ...` (deferred); and `else` (the default when no other guard for
this direction matched).

**Outcome** (the "then"): `arrive <dest>` (traverses; dest may differ by guard -> a
conditional/forked destination, and multiple observed dests = random); `refuse "<text>"`
(fixed message, no transit); or `absent` (the exit is not offered at all under this guard
-- it vanishes from the fex, as if there were no edge). `absent` is what collapses
door-driven fex fragmentation: an Ingresso `door(oaken) -> absent` row explains the
disappearing `out/s/swamp` exits as one conditional room, not two instances.

**Storage**: one append-only walk-file record per row --
`{"extra":"edge-rule","edge":{from,fex,dir},"guard":{...},"outcome":{...},
"evidence":{"fei":[...]},"note":...,"ts":...}`. A carrying rule snapshots current inventory
as raw evidence beside the human-authored guard (human writes "boat"; machine keeps
"coracle"). Rules accumulate; contradictions add rows, never replace. Read back on load into
`MapGraph`, rendered on each edge (ROOM-panel table + compass tooltips). `carrying` / `weather`
(free state token e.g. "rain") / `else` are wired in the UI; `door` is storage-ready. A rule can
be added to a direction even when the game currently HIDES the exit (a rain-gated exit absent
from the fex -- its existence demonstrable by a custom refusal like "Rain has swollen the
river"): the direction picker offers all directions and the edge table shows any direction with a
recorded refusal/rule/reported-dest, not just enabled ones. Doors need `LongDescLineReady`
plumbed from the parser up to the mapping layer -- the next slice.

**Known limitation (fex-keying).** Rules key on `{room}|{fex}|{dir}`, like resolved edges. A
weather-gated exit changes the fex (north present when clear, absent when raining), so a rule
authored in one weather state attaches to that state's fex and does not show in the other. Full
cross-state unification (one room, weather-conditional edge) is the same-name/multi-fex
de-fragmentation work (see the Ingresso note in MUD-Cartography.md).

Human-authored only, deliberately: edge conditions are game puzzles, so the console never
auto-decides one (4.4's hypotheses are suggestions for a human to confirm, not silent facts).

## 5. Work queue

The unit of work is the **open exit**: `(instance, direction)` lacking a resolved
outcome under the *current* condition context. Pure function over the log — no
persisted queue state. Contents:

- never-attempted enabled exits (what the compass shows today)
- unlisted directions worth testing (grey compass buttons today)
- `DarkDestination` edges, surfaced only when carrying a light
- `retry-when` edges, surfaced only when their condition is currently met
- generated disambiguation tasks: "re-probe under ¬rain" (3.3.2), "breadcrumb
  needed to split candidates" (3.3.3), "revoked T1 binding — re-verify"
- **blocks**: user-declared `never-traverse(edge | room | direction)` flags;
  planner input, not deletions (the swamp eats brands; some edges are deathtraps)

**Closure is a claim with a context**: a room is closed *under {¬rain, lit,
carrying: X}*. Standing in it under an uncovered context silently reopens it.
Overview shows "closed (2 contexts)".

## 6. Planner

One routing engine, used three ways:

- **Return** (close-room mode): cheapest path back to the focus room
- **Frontier**: rank open exits by routable cost from here
- **Guidance**: replaces `MapGraph.BfsFirstHop`

Rules:

- routes only over **T4/T5-bound edges** (reported names are not routable)
- conditional edges cost ∞ unless the current context satisfies the condition
- maze rooms (self-loop signature) cost ∞ unless the known route is recorded
- blocked edges/rooms excluded
- execution is **move-by-move with arrival verification** after each step,
  aborting loudly on mismatch — never fire-and-forget (mazes, variable exits,
  oxen)

This subsumes and retires the u-turn button. Today's `PickReturnLocked` routes by
name-level destination match with no multi-hop fallback — exactly why it
"sometimes works": when the reciprocal isn't listed, isn't the right instance, or
the way back is two hops (the hut/field case: D→C→A), it stops or guesses.

## 7. $map console UX

Three tabs:

**Overview**
- counts: instances, uncertain identities (`candidates: N` clusters), open exits,
  conditional edges, inbox size
- "Here" panel: current room, identity confidence tier, open exits remaining,
  closure contexts
- **Resample** button → *delta report*: "no new data" / "new exit `sw`" /
  "long desc differs — possible conflation"
- the **contradiction inbox**: pending hypotheses awaiting confirm/reject/annotate

**Room** (current compass console, evolved)
- compass with per-direction state (today's colours, driven by MapModel)
- **Close this room** toggle: arms the focus loop. Take an open exit → console
  records traversal + chained probe → planner computes return path → one button
  "Return (n→C, s→A)" executes it stepwise → repeat until no open exits remain
  under the current context. Replaces u-turn.
- breadcrumb helper: "drop brand47 here" writes the breadcrumbs `extra` record so
  the sighting is admissible evidence (policy: undeclared items are meaningless)

**Frontier**
- open exits + uncertain rooms ranked by path cost from here; tap → show route

## 8. Staging

Each stage lands independently and is useful without the later ones.

- **Stage 0 — capture additions** (console): emit the `context` extra record per
  op (weather/light/inventory as observable); emit `breadcrumbs` records from the
  breadcrumb helper. Costs little, makes every future capture analysis-grade.
  *Do this first: data captured without context can't be retrofitted.*
- **Stage 1 — MapModel** (`Core/Mapping/MapModel.cs`): pure derive over the
  mapping dir. Instances + tiered bindings, decision tables, work queue, stats.
  No UI. Unit-tested against existing walk files (they lack context records —
  rows get `context: unknown`, which the model must tolerate anyway).
- **Stage 2 — Overview tab**: render MapModel aggregates + delta-resample + inbox.
- **Stage 3 — planner + close-room mode**: routing engine, Return execution,
  retire u-turn. Frontier tab falls out of the same engine.
- **Stage 4 — discriminator analysis + identity comparison**: hypothesis
  generation, confound-driven task emission, T1 revocation.

### Experiments to run in-game (cheap, high-value)

1. **Quickscan dedup hypothesis** (`MUD-Cartography.md` §5): if confirmed, one
   command distinguishes aliased exits from distinct same-name neighbors — free
   T5-adjacent evidence. Verify before Stage 4 relies on it.
2. **Weather observability survey**: what does the protocol expose that lets the
   console *know* it's raining at op time (ambient codes? FE weather events? a
   `weather`-ish verb worth adding to the probe battery)?
3. **Rain-refusal message catalogue**: collect refusal texts during rain to seed
   the auto-rule's pattern list. (Open question: do ¬rain-conditioned refusals
   exist? If unknown, the auto-rule only fires on messages that mention rain.)
4. **Player-inclusion probe reliability**: how consistently does the
   self-sighting trick distinguish self-loops across room types?

## 9. Open questions

- Condition vocabulary: closed set (rain, light, carrying-X, door-state) or
  open tags? Proposal: open tags, with the closed set as the only ones the
  auto-rules and planner understand initially.
- Does the derived layer live only in C# (needed live for queue/planner), with
  `decode_probe.py` remaining the offline/sub-agent path? Proposal: yes —
  python stays read-only tooling; MapModel is the single live implementation.
- Teleports (`appear`-style transitions, no edge): out of scope until observed;
  the model tolerates rooms with no inbound edges.
- The fex fingerprint in edge keys (`{room}|{fex}|{dir}`) conflates conditional
  exits with identity (same room, door now closed → different fingerprint →
  "different room"). MapModel's majority-subset matching (T3) supersedes it;
  the console's resolved-edge keying can stay as-is short-term since it only
  errs toward re-capturing.
