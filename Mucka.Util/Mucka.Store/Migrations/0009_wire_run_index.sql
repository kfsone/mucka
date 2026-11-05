-- An index on wire(mucka_run_id), for the passes that read one run's rows at a time: WireHeal reads
-- each run it heals, and without an index each read walks the whole table.
--
-- Measured on a vacuumed copy of a 1,324,557-row, 257,499,136-byte file with 513 runs: the lookup of
-- one small run took about 480 ms unindexed and about 140 ms indexed, both figures including the
-- start-up of the sqlite3 shell that ran them. dbstat puts the index at 14,139,392 bytes against
-- 179,253,248 for the table, after a vacuum: 7.9% on top of the table. A first heal of a file with a
-- run per play session does one read per run.
--
-- The baseline's "no indexes on wire" names this case: CREATE INDEX is there the day something needs
-- one. (mucka_run_id) alone, because a run's rows are read in id order and an index entry carries the
-- row's id.

CREATE INDEX IF NOT EXISTS ix_wire_run ON wire(mucka_run_id);
