-- A Session's name and tags, which the player gives it in History. A Session is one login, a
-- persona_sessions row, and both tables key on its id the way the fact tables do: RunTransfer
-- renumbers that id on import and rewrites the key with it.
--
-- Tags are rows rather than a delimited string, so "every Session tagged X" is a query.

CREATE TABLE IF NOT EXISTS session_names (
    id                 INTEGER PRIMARY KEY,
    persona_session_id INTEGER NOT NULL REFERENCES persona_sessions(id),
    name               TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_session_names_session ON session_names(persona_session_id);

CREATE TABLE IF NOT EXISTS session_tags (
    id                 INTEGER PRIMARY KEY,
    persona_session_id INTEGER NOT NULL REFERENCES persona_sessions(id),
    tag                TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_session_tags_session ON session_tags(persona_session_id, tag);
CREATE INDEX IF NOT EXISTS ix_session_tags_tag ON session_tags(tag);
