# Combat analysis tools

External (python/uv) processing for MUD2 combat-session research.

The reducer reuses `tools/mapping/decode_probe.py` for telnet stripping
and C1 tag decoding, then stores a replayable evidence trail in SQLite:

- `raw_events` keeps decoded combat/protocol events plus tx/an records.
- `stats_snapshots`, `inventory_snapshots`, `room_snapshots`, and
  `status_effect_windows` keep ancillary state.
- `combat_sessions` holds encounter-level rows.
- `combat_fights` holds one row per per-NPC fight inside an encounter.
- `combat_events` holds the replayable combat event stream, including plain-text
  weapon-switch / weapon-break / guard-drop events that were not wrapped in
  literal `08.05` / `08.06` tags in the research capture.

## Not tracked: carried weight

**Carried weight is deliberately not captured, stored or displayed anywhere in this project.** The
`weight_carried_grams` / `max_weight_grams` columns survive in `schema.sql` only because existing
`combat.db` files have them; nothing populates them, and nothing should start.

The reasons, so this does not get "fixed" by someone completing the score-sheet parser:

- **It is only ever as fresh as the last `score`.** The FES heartbeat does not carry it, so the sole
  source is the sheet - a figure that is minutes old by construction.
- **It changes on every pick-up and drop**, and the client cannot see those. So the stored value is
  not merely stale, it is stale in a way nothing can detect.
- **It is insufficient for the one thing it would feed.** The published effective-strength formula
  needs a PER-OBJECT weight breakdown (its third step sums half of each object's weight, rounded down
  individually), which this line does not give. A total cannot reconstruct it.

Stale, undetectably so, and insufficient - and worse than nothing, because a number invites
arithmetic. Anyone who wants it can type `sc` and read it.

And it cannot be fixed by asking more often. There is deliberately no periodic `score` injection: the
sheet is a dozen-plus lines, MUD2's link is not fat, and pushing housekeeping down it delays the
combat text and the flee acknowledgement coming back the other way. (The cost is bandwidth, not a
game turn - MUD2 turns are short server slices that exist to stop action spam, not combat rounds.)

**Objects carried IS kept**: it has a live source in the FEI inventory list, so it can be trusted
between sheets.

## Usage

Initialize the default database:

```bash
uv run tools/combat/init_db.py
```

Reduce one or more captures into the database:

```bash
uv run tools/combat/reduce_combat.py G:\Source\mucka\RESEARCH\mud2-multi-combat.jsonl
```

Write to a custom database path:

```bash
uv run tools/combat/reduce_combat.py --db path\to\combat.db capture1.jsonl capture2.jsonl
```

If `uv` is unavailable locally, plain `python` also works:

```bash
python tools/combat/reduce_combat.py --db path\to\combat.db capture.jsonl
```

Generate the markdown fight summary from the populated database:

```bash
uv run tools/combat/summarize.py
```

Ingest live per-encounter clog files from `~/.mucka/clogs` into the same database:

```bash
uv run tools/combat/ingest_clogs.py
```

Run the merged mechanics analysis pass and print a coverage/effectiveness report:

```bash
uv run tools/combat/analyze_mechanics.py
```

This does NOT touch `MECHANICS_NOTES.md` by default: that file accumulates hand-written
live-session research findings on top of a small fixed methodology template, so refreshing it is
a separate, deliberate action:

```bash
uv run tools/combat/analyze_mechanics.py --write-notes
```

`--write-notes` itself refuses to shrink an existing `MECHANICS_NOTES.md` (i.e. it will not
overwrite a file bigger than the template), since a bigger file almost certainly holds
hand-written notes the template does not reproduce. Pass `--force-notes-overwrite` in addition
only if you are certain you want to discard that content.

Test every claim in `MUD2-PUBLISHED-MECHANICS.md` against the capture corpus and print a
SUPPORTED/REFUTED/INCONCLUSIVE/INSUFFICIENT DATA verdict per claim, with sample sizes so it is
obvious when a verdict has earned an upgrade. Meant to be re-run as more sessions accumulate, not
a one-off:

```bash
uv run tools/combat/verify_mechanics.py
uv run tools/combat/verify_mechanics.py --db path/to/combat.db
uv run tools/combat/verify_mechanics.py --db verify.db --claim knees --claim damage
uv run tools/combat/verify_mechanics.py --list
```

## Current detection rules

- Combat starts on `08` family protocol events, never on room text or NPC presence.
- A bare `08` while already in combat is treated as a joiner / escalation inside
  the current session, not a new session.
- Bare `08` start text is classified as either player-initiated (`You attack...`)
  or NPC-initiated (`The X is ...`).
- Plain decoded prose is also scanned for combat-only weapon/guard transitions:
  `You are now using...`, switch/drop-guard text, weapon-break text, and the
  confusion guard-drop text.
- `08 10`, `08 11`, and `08 12` are explicit combat ends.
- `08 08` / `08 09` are only treated as implicit session ends if no further combat
  activity follows within a short window.
- `06 03` / `06 04` force-close any open combat as a reset boundary.

See `tools/combat/NOTES.md` for the observed behavior of the provided
`mud2-multi-combat.jsonl` research capture, and `tools/combat/SUMMARY.md`
for roll-up totals by weapon and NPC.
