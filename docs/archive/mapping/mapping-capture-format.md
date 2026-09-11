# Mapping capture format

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
writes it). The directory is the source of truth: external tooling may add or
rewrite derived files; the client appends walk files and rescans on Reload.

## File format

Session-rec jsonl: `[timestamp_ms,"tx"|"rx"|"an",latin1-text]`.
Files may also contain extra object records, one JSON object per line, reserved for
structured facts. Defined so far (not yet emitted):

    {"extra": "breadcrumbs", "items": ["brand47", "key52"]}

declaring objects deliberately placed as breadcrumbs. **Policy: items seen in
captures are meaningless for room identity unless declared in a breadcrumbs record**
-- NPCs, players, and objects all move on their own. Absence of the record means no
item in that capture is interesting, which is factually correct.
