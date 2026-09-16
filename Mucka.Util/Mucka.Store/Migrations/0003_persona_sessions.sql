-- A persona session is one login of one persona to one MUD2, and it is the unit every recorded fact
-- belongs to. It replaces a timestamp that was standing in for this table.
--
-- What was there before: swings carried reset_epoch_ms, ts + time_to_reset*60000, an estimate of when
-- the world would end. That is a server-relative figure derived from a countdown wizards move, it was
-- never an identity (one 113-second encounter produced 118 distinct values), and it cannot tell two
-- clients apart - a player may run several Muckas side by side, on different MUD2s or on different
-- personas of the same one, whose resets have nothing to do with each other.
--
-- The old `sessions` table was never a session either. Its row opened when the client's store opened
-- and closed when the process went away, so it spanned every login, logout and world reset in
-- between - one such row has been seen holding three world resets 107 minutes apart. That is a RUN of
-- the client, so it is now named one.
--
-- A world reset terminates the server and logs everyone out, so a persona session can never span one.
-- That falls out of the definition and needs no column to enforce.

-- ---------------------------------------------------------------- the run, renamed ----

ALTER TABLE sessions RENAME TO mucka_runs;

-- SQLite rewrites the references in dependent views and foreign keys, so wire and v_session_sizes
-- follow the rename automatically - but they follow it still SAYING "session", which is the exact
-- confusion this migration exists to remove. Rename them too.
ALTER TABLE wire RENAME COLUMN session_id TO mucka_run_id;
DROP VIEW IF EXISTS v_session_sizes;
CREATE VIEW v_mucka_run_sizes AS
SELECT r.id, r.started_ms, r.ended_ms, r.host,
       COUNT(w.id)          AS records,
       SUM(LENGTH(w.data))  AS payload_bytes
FROM mucka_runs r LEFT JOIN wire w ON w.mucka_run_id = r.id
GROUP BY r.id;

-- ------------------------------------------------------------- the session, new ----

CREATE TABLE IF NOT EXISTS persona_sessions (
    id              INTEGER PRIMARY KEY,
    mucka_run_id    INTEGER NOT NULL REFERENCES mucka_runs(id),

    -- Which character, and where. Host is carried rather than joined because it is what separates two
    -- clients playing the same persona name on different MUD2s, and every query about a session wants
    -- it. Persona is null until the post-login score reply names the character.
    persona         TEXT,
    host            TEXT,

    started_ms      INTEGER NOT NULL,   -- game mode entered (C1-sourced, not prose)
    ended_ms        INTEGER,            -- game mode exited; NULL if the client died without one

    -- Free text, advisory, and NEVER load-bearing: 'reset', 'quit', 'died', 'permadeath' - the
    -- vocabulary is Mucka.Store.PersonaSessionEnd, which is where it is documented. A missing note
    -- does NOT mean none of those happened - a crash leaves both this and ended_ms empty, and a
    -- deliberate logout shortly before a reset looks identical to one unrelated to it. Do not
    -- reconstruct world boundaries from this column.
    ended_note      TEXT
);

CREATE INDEX IF NOT EXISTS ix_persona_sessions_run ON persona_sessions(mucka_run_id);
CREATE INDEX IF NOT EXISTS ix_persona_sessions_started ON persona_sessions(started_ms);

-- --------------------------------------------------- the fact tables point at it ----

-- Only the top-level facts carry the key. The encounter children (encounter_lines, encounter_stats,
-- encounter_contents, encounter_contents_items, encounter_events) reach it through encounters, and a
-- second copy on each of them would be a second thing to keep true.
ALTER TABLE swings            ADD COLUMN persona_session_id INTEGER REFERENCES persona_sessions(id);
ALTER TABLE fights            ADD COLUMN persona_session_id INTEGER REFERENCES persona_sessions(id);
ALTER TABLE score_events      ADD COLUMN persona_session_id INTEGER REFERENCES persona_sessions(id);
ALTER TABLE npc_stamina_reads ADD COLUMN persona_session_id INTEGER REFERENCES persona_sessions(id);
ALTER TABLE encounters        ADD COLUMN persona_session_id INTEGER REFERENCES persona_sessions(id);

CREATE INDEX IF NOT EXISTS ix_swings_session       ON swings(persona_session_id);
CREATE INDEX IF NOT EXISTS ix_fights_session       ON fights(persona_session_id);
CREATE INDEX IF NOT EXISTS ix_score_events_session ON score_events(persona_session_id);
CREATE INDEX IF NOT EXISTS ix_encounters_session   ON encounters(persona_session_id);
CREATE INDEX IF NOT EXISTS ix_stamina_reads_session ON npc_stamina_reads(persona_session_id);

-- ------------------------------------------------------- rows that cannot be placed ----

-- Rows recorded before there was a session to attribute them to keep a NULL key here, and are
-- pruned later by PersonaSessionBackfill - AFTER it has replayed the wire log and claimed everything
-- the bytes can account for.
--
-- This script deliberately deletes nothing. A migration is the wrong place to decide what is
-- unattributable: it runs before anything has attempted attribution, and it is frozen, so whatever
-- it destroys it destroys on every machine for ever. An earlier draft cut at
-- `(SELECT MIN(ts_ms) FROM wire)`, which made the amount of history destroyed depend on the state of
-- a table this project's own documentation tells people to clear by hand to reclaim space. On a
-- stranger's machine that is unrecoverable and there is no way to reach them.

-- --------------------------------------------------------------------- dead weight ----

-- The lab tool this served was rolled back and nothing reads it.
DROP TABLE IF EXISTS lab_flees;

-- ------------------------------------------------------------ what it replaces ----

-- The index first: SQLite refuses to drop a column an index still names.
DROP INDEX IF EXISTS ix_swings_reset;
ALTER TABLE swings DROP COLUMN reset_epoch_ms;

-- Persona now lives on the session, once, instead of on every row of every fact table.
ALTER TABLE swings            DROP COLUMN persona;
ALTER TABLE score_events      DROP COLUMN persona;
ALTER TABLE npc_stamina_reads DROP COLUMN persona;
ALTER TABLE fights            DROP COLUMN character_name;

-- Estimates of the reset instant, all derived from the countdown. time_to_reset itself stays on both
-- tables: it is what the game printed, a reading rather than a key, and nothing is derived from it.
ALTER TABLE encounters DROP COLUMN reset_target_utc_ms;
ALTER TABLE encounters DROP COLUMN reset_uncertainty_sec;
ALTER TABLE encounters DROP COLUMN reset_phase;
ALTER TABLE encounters DROP COLUMN reset_derived_epoch_ms;
