-- Identity that does not depend on the clock.
--
-- Until now an encounter was identified by the unix millisecond at which combat began
-- (encounter_started_at_ms), carried on swings, fights, score_events, npc_stamina_reads and on every
-- encounter table, and a run by (started_ms, host). A millisecond is not an identity: two Muckas on
-- one machine, or two machines whose files are merged, can open an encounter in the same
-- millisecond, and every row of one then belongs to both. The old index on the key was deliberately
-- not unique, so the ambiguity was kept rather than refused.
--
-- A run and an encounter now carry a uid minted by the client when it is created (a GUID rendered as
-- 32 lower-case hex digits). Every encounter child and every fact row refers to its encounter by
-- that uid. The time an encounter began survives as an ordinary column, encounters.started_at_ms,
-- which is what PersonaSessionBackfill windows on; nothing joins on it.

-- ------------------------------------------------------------------------- runs ----

ALTER TABLE mucka_runs ADD COLUMN uid TEXT;
UPDATE mucka_runs SET uid = lower(hex(randomblob(16)));
CREATE UNIQUE INDEX ix_mucka_runs_uid ON mucka_runs(uid);

-- -------------------------------------------------------------------- encounters ----

ALTER TABLE encounters ADD COLUMN uid TEXT;
ALTER TABLE encounters ADD COLUMN started_at_ms INTEGER;
UPDATE encounters SET uid = lower(hex(randomblob(16))), started_at_ms = encounter_started_at_ms;

-- One uid per (login, old key). The temp table is the whole of the backfill: every row that names an
-- old key is given the uid of that pair.
CREATE TEMP TABLE encounter_uid_map (persona_session_id INTEGER, key INTEGER, uid TEXT);
CREATE INDEX temp.ix_encounter_uid_map ON encounter_uid_map(key, persona_session_id);

-- Real encounters. When two of one login share a key the lowest id takes it, and the other keeps a
-- uid that nothing refers to: the old rows cannot say which of the two they belonged to.
INSERT INTO encounter_uid_map (persona_session_id, key, uid)
SELECT persona_session_id, encounter_started_at_ms, uid
FROM encounters
WHERE id IN (SELECT MIN(id) FROM encounters GROUP BY persona_session_id, encounter_started_at_ms);

-- Keys that facts carry but no encounters row exists for. Those are from before the encounter
-- tables were written; they still group the swings, fights and score lines of one encounter, so they
-- get a uid of their own and the grouping survives.
INSERT INTO encounter_uid_map (persona_session_id, key, uid)
SELECT persona_session_id, encounter_started_at_ms, lower(hex(randomblob(16)))
FROM (
    SELECT DISTINCT persona_session_id, encounter_started_at_ms
    FROM (
        SELECT persona_session_id, encounter_started_at_ms FROM swings
        UNION SELECT persona_session_id, encounter_started_at_ms FROM fights
        UNION SELECT persona_session_id, encounter_started_at_ms FROM score_events
        UNION SELECT persona_session_id, encounter_started_at_ms FROM npc_stamina_reads
    ) AS f
    WHERE encounter_started_at_ms IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM encounter_uid_map m
                      WHERE m.persona_session_id IS f.persona_session_id
                        AND m.key = f.encounter_started_at_ms)
);

-- Keys only an encounter child carries.
INSERT INTO encounter_uid_map (persona_session_id, key, uid)
SELECT NULL, encounter_started_at_ms, lower(hex(randomblob(16)))
FROM (
    SELECT encounter_started_at_ms FROM encounter_lines
    UNION SELECT encounter_started_at_ms FROM encounter_stats
    UNION SELECT encounter_started_at_ms FROM encounter_contents
    UNION SELECT encounter_started_at_ms FROM encounter_events
    UNION SELECT encounter_started_at_ms FROM creature_values
) AS c
WHERE NOT EXISTS (SELECT 1 FROM encounter_uid_map m WHERE m.key = c.encounter_started_at_ms);

-- ----------------------------------------------------------- the references, by uid ----

ALTER TABLE swings            ADD COLUMN encounter_uid TEXT;
ALTER TABLE fights            ADD COLUMN encounter_uid TEXT;
ALTER TABLE score_events      ADD COLUMN encounter_uid TEXT;
ALTER TABLE npc_stamina_reads ADD COLUMN encounter_uid TEXT;
ALTER TABLE encounter_lines   ADD COLUMN encounter_uid TEXT;
ALTER TABLE encounter_stats   ADD COLUMN encounter_uid TEXT;
ALTER TABLE encounter_contents ADD COLUMN encounter_uid TEXT;
ALTER TABLE encounter_events  ADD COLUMN encounter_uid TEXT;
ALTER TABLE creature_values   ADD COLUMN encounter_uid TEXT;

-- Facts carry their login, so the pair is exact.
UPDATE swings SET encounter_uid = (
    SELECT m.uid FROM encounter_uid_map m
    WHERE m.persona_session_id IS swings.persona_session_id AND m.key = swings.encounter_started_at_ms)
WHERE encounter_started_at_ms IS NOT NULL;
UPDATE fights SET encounter_uid = (
    SELECT m.uid FROM encounter_uid_map m
    WHERE m.persona_session_id IS fights.persona_session_id AND m.key = fights.encounter_started_at_ms)
WHERE encounter_started_at_ms IS NOT NULL;
UPDATE score_events SET encounter_uid = (
    SELECT m.uid FROM encounter_uid_map m
    WHERE m.persona_session_id IS score_events.persona_session_id AND m.key = score_events.encounter_started_at_ms)
WHERE encounter_started_at_ms IS NOT NULL;
UPDATE npc_stamina_reads SET encounter_uid = (
    SELECT m.uid FROM encounter_uid_map m
    WHERE m.persona_session_id IS npc_stamina_reads.persona_session_id AND m.key = npc_stamina_reads.encounter_started_at_ms)
WHERE encounter_started_at_ms IS NOT NULL;

-- The encounter tables carry no login of their own, so they can only follow the key. Where two
-- logins shared one, every such row goes to the same one of them (the lowest login id, a row with no login last): what this
-- picks was never recorded. Nothing is lost - each row still belongs to an encounter.
UPDATE encounter_lines SET encounter_uid = (
    SELECT m.uid FROM encounter_uid_map m
    WHERE m.key = encounter_lines.encounter_started_at_ms
    ORDER BY m.persona_session_id IS NULL, m.persona_session_id LIMIT 1);
UPDATE encounter_stats SET encounter_uid = (
    SELECT m.uid FROM encounter_uid_map m
    WHERE m.key = encounter_stats.encounter_started_at_ms
    ORDER BY m.persona_session_id IS NULL, m.persona_session_id LIMIT 1);
UPDATE encounter_contents SET encounter_uid = (
    SELECT m.uid FROM encounter_uid_map m
    WHERE m.key = encounter_contents.encounter_started_at_ms
    ORDER BY m.persona_session_id IS NULL, m.persona_session_id LIMIT 1);
UPDATE encounter_events SET encounter_uid = (
    SELECT m.uid FROM encounter_uid_map m
    WHERE m.key = encounter_events.encounter_started_at_ms
    ORDER BY m.persona_session_id IS NULL, m.persona_session_id LIMIT 1);
UPDATE creature_values SET encounter_uid = (
    SELECT m.uid FROM encounter_uid_map m
    WHERE m.key = creature_values.encounter_started_at_ms
    ORDER BY m.persona_session_id IS NULL, m.persona_session_id LIMIT 1);

DROP TABLE encounter_uid_map;

-- ---------------------------------------------------------------- the old key goes ----

-- Indexes first: SQLite refuses to drop a column an index still names.
DROP INDEX IF EXISTS ix_swings_encounter;
DROP INDEX IF EXISTS ix_fights_encounter;
DROP INDEX IF EXISTS ix_score_events_encounter;
DROP INDEX IF EXISTS ix_encounters_key;
DROP INDEX IF EXISTS ix_encounter_lines;
DROP INDEX IF EXISTS ix_encounter_stats;
DROP INDEX IF EXISTS ix_encounter_contents;
DROP INDEX IF EXISTS ix_encounter_events;
DROP INDEX IF EXISTS ix_creature_values;

ALTER TABLE swings             DROP COLUMN encounter_started_at_ms;
ALTER TABLE fights             DROP COLUMN encounter_started_at_ms;
ALTER TABLE score_events       DROP COLUMN encounter_started_at_ms;
ALTER TABLE npc_stamina_reads  DROP COLUMN encounter_started_at_ms;
ALTER TABLE encounters         DROP COLUMN encounter_started_at_ms;
ALTER TABLE encounter_lines    DROP COLUMN encounter_started_at_ms;
ALTER TABLE encounter_stats    DROP COLUMN encounter_started_at_ms;
ALTER TABLE encounter_contents DROP COLUMN encounter_started_at_ms;
ALTER TABLE encounter_events   DROP COLUMN encounter_started_at_ms;
ALTER TABLE creature_values    DROP COLUMN encounter_started_at_ms;

-- Unique on the encounter itself: a second copy of the same encounter - the same history merged in
-- twice - is refused rather than quietly doubled.
CREATE UNIQUE INDEX ix_encounters_uid ON encounters(uid);
CREATE INDEX ix_swings_encounter        ON swings(encounter_uid);
CREATE INDEX ix_fights_encounter        ON fights(encounter_uid);
CREATE INDEX ix_score_events_encounter  ON score_events(encounter_uid);
CREATE INDEX ix_stamina_reads_encounter ON npc_stamina_reads(encounter_uid);
CREATE INDEX ix_encounter_lines         ON encounter_lines(encounter_uid);
CREATE INDEX ix_encounter_stats         ON encounter_stats(encounter_uid);
CREATE INDEX ix_encounter_contents      ON encounter_contents(encounter_uid);
CREATE INDEX ix_encounter_events        ON encounter_events(encounter_uid);
CREATE INDEX ix_creature_values         ON creature_values(encounter_uid);
