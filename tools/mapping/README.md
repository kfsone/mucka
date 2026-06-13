# Mapping tools

External (python/uv) processing for the MUD2 mapping effort. Read `MUD-Cartography.md`
at the repo root before touching map semantics.

## Data flow

Mapping is **operation-driven** from the client's mapping console (`$map` on Windows,
its own window beside the game). Nothing is recorded between operations; the console
tracks the current room and enabled exits passively (FE EXITS fires on every arrival)
so its compass stays live during manual play.

Operations, all appended to one walk file per session
(`walk.{host}.{timestamp}.jsonl` in the mapping directory):

- **Probe** (`Probe here` button, or `$map probe`): one command interrupt --
  `ESC-[longlook,superlook,exits,look around,qscan,fei,fex,no ESC-]` -- capturing
  the full room observation. The trailing `no` draws "Don't then.", the end marker.
- **Move-and-capture** (compass click): sends the direction, records the raw
  response, and annotates the outcome either way:

      an "edge: {from} |{dir}> {to} [{exits}]"      traversed
      an "edge: {from} |{dir}> (dark) [{exits}]"    traversed into an unlit room
      an "edge: {from} |{dir}! {reason} [{exits}]"  refused -- failed edges are data too

  `[{exits}]` is the from-room's enabled-exit fingerprint (sorted FEX keywords) at
  move time. Short descriptions are not unique (five "Badly-paved road"s), so the
  console keys captured-edge state on name+fingerprint; same name AND same exits
  still collide, and true instance identity stays an analysis-side problem -- the
  console only errs toward re-capturing.

  Refusals caused by something movable ("Your way is blocked by the ox.") and op
  artifacts ("(timeout)", "(no output)") are recorded but do NOT mark the edge
  captured -- analysis should treat them as behavioral observations, not topology.

  Arrival chains an automatic probe of the destination, so a mapping walk is
  click-click-click. Compass colours: bold light green = enabled exit not yet
  captured from this room; dark green = already captured; grey = not listed by
  FE EXITS (still clickable -- that is how refusals and unlisted exits get
  recorded).

The mapping directory defaults to `~/.mucka/mapping` (`mappingdir=` in mucka.ini
`[settings]` or `[settings:Profile]`; hand-edited key, the settings dialog never
writes it). The directory is the source of truth: tools here may add or rewrite
derived files; the client appends walk files and rescans on Reload.

## File format

Session-rec jsonl (see INTERNAL.md): `[timestamp_ms,"tx"|"rx"|"an",latin1-text]`.
Files may also contain extra object records, one JSON object per line, reserved for
structured facts. Defined so far (not yet emitted):

    {"extra": "breadcrumbs", "items": ["brand47", "key52"]}

declaring objects deliberately placed as breadcrumbs. **Policy: items seen in
captures are meaningless for room identity unless declared in a breadcrumbs record**
-- NPCs, players, and objects all move on their own. Absence of the record means no
item in that capture is interesting, which is factually correct.

## Handing captures to sub-agents

Do NOT paste raw .jsonl rx data into an agent prompt: it is full of C1 escape bytes
and doubled telnet IACs that waste context and confuse tokenization (see the prompt
hazard note in INTERNAL.md). Instead run the decoder and hand the agent its output:

    uv run tools/mapping/decode_probe.py <capture.jsonl>            # tagged
    uv run tools/mapping/decode_probe.py --plain <capture.jsonl>    # text only

Tagged output preserves the protocol context codes as `{c02.01}...{/c02.01}` markers
(02.01 = room short description, 02.02 = long description, 12.09 = exits listing --
full table in MUD-ClientProto.md), so an agent can tell a room name from narrative
text without seeing raw bytes. Probe responses get their prompt-delimited segments
labeled (`=== exits ===` etc.); `edge:` annotations are already grep-friendly.

### reduce_walk.py — compact analysis digest

Probe-faithful output is verbose: every revisit repeats the full probe, and
superlook / look-around / qscan largely restate exits.  For bulk analysis (many
rooms, cross-referencing) hand agents the reduced form instead:

    uv run tools/mapping/reduce_walk.py <capture.jsonl>

Output: one `R<n>` block per distinct `(short, long, fex, exits-table)` observation;
revisits collapse to a one-liner `R<n> revisit: <name>`.  Each block includes
known-as name, qscan direction→name pairs, fei item list, and the arrival ambient
code (`[20.xx]`) when present.  `edge:` / `u-turn:` console annotations and extra
object records pass through verbatim.  The standard ingestion path for analysis
agents is:

    jsonl → reduce_walk.py → agent prompt

Never hand a raw jsonl to an agent (see prompt hazard note above).  `decode_probe.py`
remains useful for human inspection of individual probes or debugging the decoder.

Bulk analysis (many walk files, cross-referencing rooms/edges) should be delegated
to sub-agents rather than done in the main conversation -- Haiku or Sonnet-class
models are sufficient for decoded-capture work. Give an analysis sub-agent: the
decoded output plus `MUD-Cartography.md` for the domain model. Give a tooling
sub-agent: this README, one decoded sample, and one raw sample line so it knows
both layers.
