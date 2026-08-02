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

Run the merged mechanics analysis pass and refresh `MECHANICS_NOTES.md`:

```bash
uv run tools/combat/analyze_mechanics.py
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
