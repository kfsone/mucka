-- Two things the wire log needs for the clean-up passes that run over a run after it has ended.
--
-- wire_healed_ms: when WireHeal last made one pass over the run's rx rows, so a run is healed once
-- and not rescanned at every start. NULL means not yet. A run that has not ended is never healed:
-- another client may still be writing it.
--
-- trimmed: where $trimdb puts what it takes out of wire, row for row. The first six columns are
-- wire's own, with the same ids, so a revert puts a row back exactly where it was. trimmed_ms is when
-- the row was taken out, which is what $trimdb expire ages by: the row's own ts_ms is the age of the
-- recording, and every row trim picks is already old by definition. reason says which rule took it.

ALTER TABLE mucka_runs ADD COLUMN wire_healed_ms INTEGER;

CREATE TABLE IF NOT EXISTS trimmed (
    id           INTEGER PRIMARY KEY,
    mucka_run_id INTEGER NOT NULL REFERENCES "mucka_runs"(id),
    seq          INTEGER NOT NULL,
    ts_ms        INTEGER NOT NULL,
    direction    INTEGER NOT NULL,
    data         BLOB    NOT NULL,
    trimmed_ms   INTEGER NOT NULL,
    reason       TEXT    NOT NULL
);
