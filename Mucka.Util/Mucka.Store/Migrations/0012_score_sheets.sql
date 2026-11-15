-- score_sheets: the total the post-login score sheet printed ("score:  27,296 points   this game: ..."),
-- one row per login that drew one. A reading of what the character's score IS when the Session
-- starts, whatever it earned where nothing recorded it. Kept out of score_events, which holds the
-- "(Persona saved on ...)" lines as printed.

CREATE TABLE IF NOT EXISTS score_sheets (
    id                 INTEGER PRIMARY KEY,
    persona_session_id INTEGER NOT NULL REFERENCES persona_sessions(id),
    ts                 INTEGER NOT NULL,
    total              INTEGER NOT NULL,
    raw_text           TEXT    NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_score_sheets_session ON score_sheets(persona_session_id);
