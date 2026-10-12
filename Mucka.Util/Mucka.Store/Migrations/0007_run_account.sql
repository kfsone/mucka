-- Which account a run logged in as, when the client knew it (a profile supplies it; a login typed by
-- hand does not, and leaves this NULL).
--
-- A run that was killed leaves no end behind, and nothing could say whether the process was gone or
-- merely quiet. One fact settles it: an account is logged in once. When a new login of the same
-- account on the same host reaches game mode, every earlier run of that account and host that never
-- ended is dead, and MuckaStore.CloseAbandonedRuns ends it at its own last wire record. A run of a
-- different account is never touched, whatever its age.
--
-- Existing runs are filled at start-up by AccountBackfill, from what the server printed back during
-- the login. A migration cannot do that: the id has to be read out of the received text, which every
-- login carries, where what was sent is in clear for some runs and asterisks for others.

ALTER TABLE mucka_runs ADD COLUMN account_id TEXT;
