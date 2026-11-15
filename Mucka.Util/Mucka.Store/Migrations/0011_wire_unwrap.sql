-- wire_unwrapped: the runs WireHeal has made its one pass over for the lines the server wrapped, so a
-- run is unwrapped once and not rescanned at every start. A run with no row here has not been. A run
-- this client records gets its row when it is written, because the client sends no width and the
-- server wraps nothing. A table rather than a column on mucka_runs, so the script can run twice.

CREATE TABLE IF NOT EXISTS wire_unwrapped (
    mucka_run_id INTEGER PRIMARY KEY REFERENCES mucka_runs(id),
    unwrapped_ms INTEGER NOT NULL
);
