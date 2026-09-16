-- Level is a pure function of score - MudSharp.Combat.PersonaRanks.LevelForScore, thresholds
-- doubling from 200 - so recording it stored nothing score did not already carry. It means nothing
-- beyond the stat maxima, which are recorded outright, and magic probabilities.
--
-- It was also wrong. The game prints level on the score sheet, so the recorded value was a snapshot
-- from the last `score` command while score itself keeps updating off the save lines. Measured over
-- 25,585 rows before this migration: 12.2% disagreed with the score beside them, and the error was
-- one-directional - level low by 1 on 9.67%, by 2 on 1.55%, by 3 on 0.94%, and high on 4 rows in the
-- whole corpus. Lag, not corruption, which is also what clears score itself of suspicion.

ALTER TABLE swings DROP COLUMN level;
ALTER TABLE fights DROP COLUMN level;
ALTER TABLE encounter_stats DROP COLUMN level;
