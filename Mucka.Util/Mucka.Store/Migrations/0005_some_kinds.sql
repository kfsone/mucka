-- Which anonymous word each species gets when the player cannot see it.
--
-- MUD2 replaces a Creature's name with "someone" (person-shaped: players, the man, the thief,
-- zombies) or "something" (everything else) whenever the player is blind, the room is dark, or the
-- Creature is invisible. The word is a property of the species, not of the cause, so once a blow
-- from "something" has been attributed to a rat, every rat is a "something" for good - and that fact
-- is what lets the combat tracker rule a rat OUT as the "someone" that hit next. Learned per
-- install, from attributions this client made itself; nothing ships it.
--
-- One row per species, upserted: a later deduction that disagrees replaces the row rather than
-- adding to it. A fresh install has none, and a species never fought unseen never gets one.

CREATE TABLE IF NOT EXISTS some_kinds (
    species     TEXT PRIMARY KEY,            -- Mucka.Combat's NpcGroups.Normalize of the instance name
    kind        TEXT NOT NULL CHECK (kind IN ('someone', 'something')),
    learned_ms  INTEGER NOT NULL             -- when this row was last written, unix ms UTC
);
