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

    -- Free text, advisory, and NEVER load-bearing: 'reset', 'logout', 'died', 'drop'. A missing note
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

-- ------------------------------------------------------- rows that cannot be placed ----

-- Everything before the wire log begins cannot be attributed to a session: the wire bytes it would be
-- reconstructed from were deleted, and mucka_runs starts at the same instant, so there is no coarser
-- attribution either. These rows are dropped rather than left with a null key - a table where the
-- session is sometimes absent is a table every query has to special-case forever, and the operator's
-- judgement is that well-structured data beats more of the badly-structured kind.
--
-- The cut is the first wire record. Stated as a subquery rather than a literal so the script is the
-- same statement on every database it runs against.

DELETE FROM encounter_contents_items WHERE contents_id IN (
    SELECT id FROM encounter_contents WHERE ts < (SELECT MIN(ts_ms) FROM wire));
DELETE FROM encounter_contents  WHERE ts < (SELECT MIN(ts_ms) FROM wire);
DELETE FROM encounter_lines     WHERE encounter_started_at_ms < (SELECT MIN(ts_ms) FROM wire);
DELETE FROM encounter_events    WHERE ts < (SELECT MIN(ts_ms) FROM wire);
DELETE FROM encounter_stats     WHERE ts < (SELECT MIN(ts_ms) FROM wire);
DELETE FROM creature_values     WHERE ts < (SELECT MIN(ts_ms) FROM wire);
DELETE FROM encounters          WHERE encounter_started_at_ms < (SELECT MIN(ts_ms) FROM wire);
DELETE FROM npc_stamina_reads   WHERE ts < (SELECT MIN(ts_ms) FROM wire);
DELETE FROM score_events        WHERE ts < (SELECT MIN(ts_ms) FROM wire);
DELETE FROM fights              WHERE started_at_ms < (SELECT MIN(ts_ms) FROM wire);
DELETE FROM swings              WHERE ts < (SELECT MIN(ts_ms) FROM wire);

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
