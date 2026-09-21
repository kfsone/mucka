-- Fact rows recorded before any login could be attributed to them.
--
-- 0003 gave every fact table a persona_session_id and PersonaSessionBackfill fills it by replaying
-- the wire log. Nothing older than the wire log can ever be filled: a reconstructed login is stamped
-- with a wire record's own timestamp, and a login only claims rows at or after its start, so a row
-- older than the FIRST wire record is unreachable by construction. Those rows are deleted, so that
-- "every fact row belongs to a login" is a property queries can rely on rather than one they must
-- keep special-casing.
--
-- THE CUT IS A LITERAL, AND THAT IS THE WHOLE POINT.
--
-- 1789001000023 is 2026-09-10 00:43:20 UTC, the first record in the wire log of the database this
-- was written against. Two earlier attempts at this cut used `(SELECT MIN(ts_ms) FROM wire)` instead,
-- and both were wrong in the same way: docs/persistence-design.md tells people that clearing the wire
-- log by hand is how you reclaim space, and trimming it RAISES that minimum - so the amount of
-- irreplaceable history destroyed grew with an unrelated maintenance action. The second attempt also
-- moved the delete out of a migration and into start-up code, which made it re-decide on every
-- launch instead of once. A frozen literal in a journalled script cannot do either.
--
-- What it costs: an install whose combat data predates this instant AND whose wire log also reaches
-- back that far loses rows its own wire could have attributed. That is accepted deliberately - it is
-- bounded, it is stated here, and it is the price of the cut being a constant rather than something
-- a user's own housekeeping can move.
--
-- Runs once per database, recorded in SchemaVersions like every other script. On a file that has
-- nothing older than the cut - every fresh install - it deletes nothing.

-- Also the index 0003 forgot. It was added to 0003 after that script had already run on a real
-- database, which meant the fresh-install path got it and the migrated path never would - the exact
-- divergence the frozen-hash guard exists to prevent, slipped past because the guard cannot tell a
-- new script from an edited one. IF NOT EXISTS, so a file that got it from 0003 is unaffected.
CREATE INDEX IF NOT EXISTS ix_stamina_reads_session ON npc_stamina_reads(persona_session_id);

DELETE FROM swings            WHERE persona_session_id IS NULL AND ts            < 1789001000023;
DELETE FROM fights            WHERE persona_session_id IS NULL AND started_at_ms < 1789001000023;
DELETE FROM score_events      WHERE persona_session_id IS NULL AND ts            < 1789001000023;
DELETE FROM npc_stamina_reads WHERE persona_session_id IS NULL AND ts            < 1789001000023;
DELETE FROM encounters        WHERE persona_session_id IS NULL
                                AND encounter_started_at_ms < 1789001000023;

-- The encounter children have no session key of their own - they reach it through encounters, on
-- encounter_started_at_ms - so the guard their siblings above write inline is a NOT EXISTS against
-- the parent here. They follow their parent: a child goes when every encounter sharing its key is
-- unattributed, and when no encounter holds that key at all, which is the whole of what
-- "unattributable" can mean for a row with no column of its own to say so. A child of an attributed
-- encounter stays, exactly as the attributed parent does.
--
-- NOT EXISTS and not NOT IN: one NULL in the subquery's result makes a NOT IN predicate match no
-- rows whatsoever, and a delete that silently does nothing reads exactly like a delete that had
-- nothing to do.
--
-- ix_encounters_key is deliberately not unique, so two encounters can share a start instant. A child
-- of such a pair is kept when EITHER parent is attributed - the direction that keeps rows, because
-- a row deleted here cannot be got back and a row kept can be deleted later.
--
-- The cut bounds all of them as before. An orphan NEWER than the cut is a gap in reconstruction,
-- which is a bug to find rather than evidence to destroy.
DELETE FROM encounter_contents_items WHERE contents_id IN (
    SELECT c.id FROM encounter_contents c
     WHERE c.ts < 1789001000023
       AND NOT EXISTS (SELECT 1 FROM encounters e
                        WHERE e.encounter_started_at_ms = c.encounter_started_at_ms
                          AND e.persona_session_id IS NOT NULL));

DELETE FROM encounter_contents WHERE ts < 1789001000023
  AND NOT EXISTS (SELECT 1 FROM encounters e
                   WHERE e.encounter_started_at_ms = encounter_contents.encounter_started_at_ms
                     AND e.persona_session_id IS NOT NULL);

DELETE FROM encounter_lines WHERE encounter_started_at_ms < 1789001000023
  AND NOT EXISTS (SELECT 1 FROM encounters e
                   WHERE e.encounter_started_at_ms = encounter_lines.encounter_started_at_ms
                     AND e.persona_session_id IS NOT NULL);

DELETE FROM encounter_events WHERE ts < 1789001000023
  AND NOT EXISTS (SELECT 1 FROM encounters e
                   WHERE e.encounter_started_at_ms = encounter_events.encounter_started_at_ms
                     AND e.persona_session_id IS NOT NULL);

DELETE FROM encounter_stats WHERE ts < 1789001000023
  AND NOT EXISTS (SELECT 1 FROM encounters e
                   WHERE e.encounter_started_at_ms = encounter_stats.encounter_started_at_ms
                     AND e.persona_session_id IS NOT NULL);

DELETE FROM creature_values WHERE ts < 1789001000023
  AND NOT EXISTS (SELECT 1 FROM encounters e
                   WHERE e.encounter_started_at_ms = creature_values.encounter_started_at_ms
                     AND e.persona_session_id IS NOT NULL);
