-- MUD2 Combat Analysis Database Schema
-- Immutable evidence first, derived encounter/fight state second.
-- Encounters are continuous combat sessions.
-- Fights are per-NPC engagements within an encounter.

PRAGMA foreign_keys = ON;

-- ============================================================
-- SOURCE LAYER
-- ============================================================

CREATE TABLE IF NOT EXISTS captures (
    id               TEXT    PRIMARY KEY,
    source_file      TEXT    NOT NULL UNIQUE,
    started_at_ms    INTEGER,
    stopped_at_ms    INTEGER,
    loaded_at_ms     INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS raw_events (
    id                  TEXT    PRIMARY KEY,
    capture_id          TEXT    NOT NULL REFERENCES captures(id) ON DELETE CASCADE,
    seq_index           INTEGER NOT NULL,
    event_ordinal       INTEGER NOT NULL,
    timestamp_ms        INTEGER,
    direction           TEXT    NOT NULL,    -- tx | rx | an
    tag_code            TEXT,                -- protocol tag or synthetic plain.* code
    category            TEXT    NOT NULL,
    event_type          TEXT    NOT NULL,
    actor               TEXT,
    subject_name        TEXT,
    weapon_name         TEXT,
    decoded_text        TEXT    NOT NULL,
    snippet_text        TEXT,
    record_json         TEXT    NOT NULL,
    is_client_probe     INTEGER NOT NULL DEFAULT 0,
    UNIQUE (capture_id, seq_index, event_ordinal)
);

CREATE INDEX IF NOT EXISTS idx_raw_events_capture_ts
    ON raw_events(capture_id, timestamp_ms, seq_index, event_ordinal);
CREATE INDEX IF NOT EXISTS idx_raw_events_capture_type
    ON raw_events(capture_id, event_type, timestamp_ms);
CREATE INDEX IF NOT EXISTS idx_raw_events_capture_code
    ON raw_events(capture_id, tag_code, timestamp_ms);

-- ============================================================
-- ANCILLARY SNAPSHOT LAYER
-- ============================================================

CREATE TABLE IF NOT EXISTS room_snapshots (
    id                  TEXT    PRIMARY KEY,
    capture_id          TEXT    NOT NULL REFERENCES captures(id) ON DELETE CASCADE,
    source_event_id     TEXT    REFERENCES raw_events(id) ON DELETE SET NULL,
    timestamp_ms        INTEGER NOT NULL,
    seq_index           INTEGER NOT NULL,
    ambient_code        TEXT,
    ambient_name        TEXT,
    room_short          TEXT,
    room_long           TEXT,
    exits_text          TEXT,
    raw_text            TEXT    NOT NULL,
    note                TEXT
);

CREATE INDEX IF NOT EXISTS idx_room_snapshots_capture_ts
    ON room_snapshots(capture_id, timestamp_ms, seq_index);

CREATE TABLE IF NOT EXISTS stats_snapshots (
    id                  TEXT    PRIMARY KEY,
    capture_id          TEXT    NOT NULL REFERENCES captures(id) ON DELETE CASCADE,
    source_event_id     TEXT    NOT NULL REFERENCES raw_events(id) ON DELETE CASCADE,
    timestamp_ms        INTEGER NOT NULL,
    seq_index           INTEGER NOT NULL,
    stamina             INTEGER,
    max_stamina         INTEGER,
    strength            INTEGER,
    raw_strength        INTEGER,
    max_strength        INTEGER,
    dexterity           INTEGER,
    raw_dexterity       INTEGER,
    max_dexterity       INTEGER,
    current_magic       INTEGER,
    max_magic           INTEGER,
    score               INTEGER,
    weight_carried_grams INTEGER,
    max_weight_grams    INTEGER,
    objects_carried     INTEGER,
    max_objects_carried INTEGER,
    level               INTEGER,
    games_played        INTEGER,
    is_blind            INTEGER NOT NULL DEFAULT 0,
    is_deaf             INTEGER NOT NULL DEFAULT 0,
    is_crippled         INTEGER NOT NULL DEFAULT 0,
    is_dumb             INTEGER NOT NULL DEFAULT 0,
    reset_minutes       INTEGER,
    weather             TEXT,
    raw_text            TEXT    NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_stats_snapshots_capture_ts
    ON stats_snapshots(capture_id, timestamp_ms, seq_index);

CREATE TABLE IF NOT EXISTS inventory_snapshots (
    id                  TEXT    PRIMARY KEY,
    capture_id          TEXT    NOT NULL REFERENCES captures(id) ON DELETE CASCADE,
    source_event_id     TEXT    NOT NULL REFERENCES raw_events(id) ON DELETE CASCADE,
    timestamp_ms        INTEGER NOT NULL,
    seq_index           INTEGER NOT NULL,
    room_items_json     TEXT    NOT NULL,
    carried_items_json  TEXT    NOT NULL,
    raw_text            TEXT    NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_inventory_snapshots_capture_ts
    ON inventory_snapshots(capture_id, timestamp_ms, seq_index);

CREATE TABLE IF NOT EXISTS status_effect_events (
    id                  TEXT    PRIMARY KEY,
    capture_id          TEXT    NOT NULL REFERENCES captures(id) ON DELETE CASCADE,
    raw_event_id        TEXT    NOT NULL REFERENCES raw_events(id) ON DELETE CASCADE,
    timestamp_ms        INTEGER NOT NULL,
    effect_name         TEXT    NOT NULL,
    phase               TEXT    NOT NULL,    -- start | end
    confidence          TEXT    NOT NULL,    -- high | medium | low
    detail_text         TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS status_effect_windows (
    id                  TEXT    PRIMARY KEY,
    capture_id          TEXT    NOT NULL REFERENCES captures(id) ON DELETE CASCADE,
    effect_name         TEXT    NOT NULL,
    start_event_id      TEXT    REFERENCES status_effect_events(id) ON DELETE SET NULL,
    end_event_id        TEXT    REFERENCES status_effect_events(id) ON DELETE SET NULL,
    start_timestamp_ms  INTEGER,
    end_timestamp_ms    INTEGER,
    confidence          TEXT    NOT NULL,
    note                TEXT
);

CREATE INDEX IF NOT EXISTS idx_status_effect_windows_capture_ts
    ON status_effect_windows(capture_id, start_timestamp_ms, end_timestamp_ms);

-- ============================================================
-- ENCOUNTER LAYER
-- ============================================================

CREATE TABLE IF NOT EXISTS combat_sessions (
    id                          INTEGER PRIMARY KEY AUTOINCREMENT,
    capture_id                  TEXT    NOT NULL REFERENCES captures(id) ON DELETE CASCADE,
    session_index               INTEGER NOT NULL,
    initiator                   TEXT,       -- player | npc
    start_event_id              TEXT    NOT NULL REFERENCES raw_events(id) ON DELETE RESTRICT,
    end_event_id                TEXT    REFERENCES raw_events(id) ON DELETE SET NULL,
    start_timestamp_ms          INTEGER NOT NULL,
    end_timestamp_ms            INTEGER,
    duration_ms                 INTEGER,
    end_reason                  TEXT,
    end_detail                  TEXT,
    primary_target              TEXT,
    participant_names_json      TEXT    NOT NULL DEFAULT '[]',
    participant_confidence      TEXT    NOT NULL DEFAULT 'low',
    start_weapon                TEXT,
    last_explicit_weapon        TEXT,
    start_room_snapshot_id      TEXT    REFERENCES room_snapshots(id) ON DELETE SET NULL,
    end_room_snapshot_id        TEXT    REFERENCES room_snapshots(id) ON DELETE SET NULL,
    start_stats_snapshot_id     TEXT    REFERENCES stats_snapshots(id) ON DELETE SET NULL,
    end_stats_snapshot_id       TEXT    REFERENCES stats_snapshots(id) ON DELETE SET NULL,
    start_inventory_snapshot_id TEXT    REFERENCES inventory_snapshots(id) ON DELETE SET NULL,
    end_inventory_snapshot_id   TEXT    REFERENCES inventory_snapshots(id) ON DELETE SET NULL,
    you_hits                    INTEGER NOT NULL DEFAULT 0,
    you_misses                  INTEGER NOT NULL DEFAULT 0,
    they_hits                   INTEGER NOT NULL DEFAULT 0,
    they_misses                 INTEGER NOT NULL DEFAULT 0,
    withdraw_offers             INTEGER NOT NULL DEFAULT 0,
    kills_by_you                INTEGER NOT NULL DEFAULT 0,
    kills_by_them               INTEGER NOT NULL DEFAULT 0,
    join_events                 INTEGER NOT NULL DEFAULT 0,
    notes                       TEXT,
    UNIQUE (capture_id, session_index)
);

CREATE INDEX IF NOT EXISTS idx_combat_sessions_capture_ts
    ON combat_sessions(capture_id, start_timestamp_ms, end_timestamp_ms);

CREATE TABLE IF NOT EXISTS combat_events (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    capture_id          TEXT    NOT NULL REFERENCES captures(id) ON DELETE CASCADE,
    session_id          INTEGER NOT NULL REFERENCES combat_sessions(id) ON DELETE CASCADE,
    fight_id            INTEGER REFERENCES combat_fights(id) ON DELETE SET NULL,
    raw_event_id        TEXT    NOT NULL REFERENCES raw_events(id) ON DELETE RESTRICT,
    timestamp_ms        INTEGER NOT NULL,
    seq_index           INTEGER NOT NULL,
    tag_code            TEXT,
    event_type          TEXT    NOT NULL,
    actor               TEXT,
    participant_name    TEXT,
    weapon_name         TEXT,
    approx_damage_done  REAL,
    approx_damage_taken REAL,
    plain_text          TEXT    NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_combat_events_session_ts
    ON combat_events(session_id, timestamp_ms, id);

CREATE TABLE IF NOT EXISTS combat_session_commands (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    capture_id          TEXT    NOT NULL REFERENCES captures(id) ON DELETE CASCADE,
    session_id          INTEGER NOT NULL REFERENCES combat_sessions(id) ON DELETE CASCADE,
    raw_event_id        TEXT    NOT NULL REFERENCES raw_events(id) ON DELETE RESTRICT,
    timestamp_ms        INTEGER NOT NULL,
    phase               TEXT    NOT NULL,   -- pre | during | post
    command_text        TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS combat_session_stats (
    session_id          INTEGER NOT NULL REFERENCES combat_sessions(id) ON DELETE CASCADE,
    snapshot_id         TEXT    NOT NULL REFERENCES stats_snapshots(id) ON DELETE CASCADE,
    relation            TEXT    NOT NULL,   -- start | during | end
    PRIMARY KEY (session_id, snapshot_id, relation)
);

CREATE TABLE IF NOT EXISTS combat_session_inventory (
    session_id          INTEGER NOT NULL REFERENCES combat_sessions(id) ON DELETE CASCADE,
    snapshot_id         TEXT    NOT NULL REFERENCES inventory_snapshots(id) ON DELETE CASCADE,
    relation            TEXT    NOT NULL,   -- start | during | end
    PRIMARY KEY (session_id, snapshot_id, relation)
);

CREATE TABLE IF NOT EXISTS combat_session_status_effects (
    session_id          INTEGER NOT NULL REFERENCES combat_sessions(id) ON DELETE CASCADE,
    status_window_id    TEXT    NOT NULL REFERENCES status_effect_windows(id) ON DELETE CASCADE,
    overlap_start_ms    INTEGER,
    overlap_end_ms      INTEGER,
    PRIMARY KEY (session_id, status_window_id)
);

-- ============================================================
-- PER-NPC FIGHT LAYER
-- ============================================================

CREATE TABLE IF NOT EXISTS combat_fights (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    capture_id          TEXT    NOT NULL REFERENCES captures(id) ON DELETE CASCADE,
    session_id          INTEGER NOT NULL REFERENCES combat_sessions(id) ON DELETE CASCADE,
    npc_name            TEXT    NOT NULL,
    npc_group           TEXT    NOT NULL,
    initiator           TEXT,       -- player | npc
    start_event_id      TEXT    NOT NULL REFERENCES raw_events(id) ON DELETE RESTRICT,
    end_event_id        TEXT    REFERENCES raw_events(id) ON DELETE SET NULL,
    start_timestamp_ms  INTEGER NOT NULL,
    end_timestamp_ms    INTEGER,
    duration_ms         INTEGER,
    start_weapon        TEXT,
    weapon_used         TEXT,
    outcome             TEXT    NOT NULL,   -- killed | npc-fled | you-fled | withdrawn | pass/unresolved
    resolution_text     TEXT,
    you_hits            INTEGER NOT NULL DEFAULT 0,
    you_misses          INTEGER NOT NULL DEFAULT 0,
    they_hits           INTEGER NOT NULL DEFAULT 0,
    they_misses         INTEGER NOT NULL DEFAULT 0,
    approx_damage_done  REAL    NOT NULL DEFAULT 0,
    approx_damage_taken REAL    NOT NULL DEFAULT 0,
    notes               TEXT,
    UNIQUE (session_id, npc_name)
);

CREATE INDEX IF NOT EXISTS idx_combat_fights_session
    ON combat_fights(session_id, start_timestamp_ms, npc_name);
CREATE INDEX IF NOT EXISTS idx_combat_fights_group
    ON combat_fights(npc_group, weapon_used, outcome);

-- ============================================================
-- VIEWS
-- ============================================================

CREATE VIEW IF NOT EXISTS combat_session_summary AS
SELECT
    cs.id,
    cs.capture_id,
    cs.session_index,
    cs.initiator,
    cs.start_timestamp_ms,
    cs.end_timestamp_ms,
    cs.duration_ms,
    cs.end_reason,
    cs.primary_target,
    cs.start_weapon,
    cs.you_hits,
    cs.you_misses,
    cs.they_hits,
    cs.they_misses,
    cs.kills_by_you,
    cs.kills_by_them,
    cs.join_events,
    rs_start.room_short AS start_room,
    rs_end.room_short   AS end_room
FROM combat_sessions cs
LEFT JOIN room_snapshots rs_start ON rs_start.id = cs.start_room_snapshot_id
LEFT JOIN room_snapshots rs_end   ON rs_end.id   = cs.end_room_snapshot_id;

CREATE VIEW IF NOT EXISTS v_summary_total AS
WITH fight_rollup AS (
    SELECT
        COUNT(*) AS fight_count,
        COUNT(CASE WHEN outcome = 'killed' THEN 1 END) AS kills,
        COUNT(CASE WHEN outcome = 'pass/unresolved' THEN 1 END) AS passes,
        COUNT(CASE WHEN outcome = 'npc-fled' THEN 1 END) AS npc_flees,
        COUNT(CASE WHEN outcome = 'you-fled' THEN 1 END) AS your_flees,
        COUNT(CASE WHEN outcome = 'withdrawn' THEN 1 END) AS withdrawn,
        ROUND(SUM(approx_damage_done), 3) AS approx_damage_done,
        ROUND(SUM(approx_damage_taken), 3) AS approx_health_lost,
        ROUND(SUM(duration_ms) / 1000.0, 3) AS fight_duration_seconds,
        CASE
            WHEN SUM(duration_ms) > 0
                THEN ROUND(SUM(approx_damage_done) / (SUM(duration_ms) / 1000.0), 3)
            ELSE NULL
        END AS approx_dps,
        GROUP_CONCAT(DISTINCT npc_name) AS unique_npcs_csv
    FROM combat_fights
)
SELECT
    'total' AS summary_key,
    (SELECT COUNT(*) FROM combat_sessions) AS encounter_count,
    fight_count,
    unique_npcs_csv,
    kills,
    passes,
    npc_flees,
    your_flees,
    withdrawn,
    approx_damage_done,
    approx_health_lost,
    fight_duration_seconds,
    approx_dps
FROM fight_rollup;

CREATE VIEW IF NOT EXISTS v_summary_by_weapon AS
SELECT
    COALESCE(NULLIF(weapon_used, ''), '(unknown)') AS weapon_used,
    COUNT(DISTINCT session_id) AS encounter_count,
    COUNT(*) AS fight_count,
    GROUP_CONCAT(DISTINCT npc_name) AS unique_npcs_csv,
    COUNT(CASE WHEN outcome = 'killed' THEN 1 END) AS kills,
    COUNT(CASE WHEN outcome = 'pass/unresolved' THEN 1 END) AS passes,
    COUNT(CASE WHEN outcome = 'npc-fled' THEN 1 END) AS npc_flees,
    COUNT(CASE WHEN outcome = 'you-fled' THEN 1 END) AS your_flees,
    COUNT(CASE WHEN outcome = 'withdrawn' THEN 1 END) AS withdrawn,
    ROUND(SUM(approx_damage_done), 3) AS approx_damage_done,
    ROUND(SUM(approx_damage_taken), 3) AS approx_health_lost,
    ROUND(SUM(duration_ms) / 1000.0, 3) AS fight_duration_seconds,
    CASE
        WHEN SUM(duration_ms) > 0
            THEN ROUND(SUM(approx_damage_done) / (SUM(duration_ms) / 1000.0), 3)
        ELSE NULL
    END AS approx_dps
FROM combat_fights
GROUP BY COALESCE(NULLIF(weapon_used, ''), '(unknown)')
ORDER BY fight_count DESC, weapon_used;

CREATE VIEW IF NOT EXISTS v_summary_by_npc AS
SELECT
    npc_name,
    NULL AS encounter_count,
    COUNT(*) AS fight_count,
    GROUP_CONCAT(DISTINCT npc_name) AS unique_npcs_csv,
    COUNT(CASE WHEN outcome = 'killed' THEN 1 END) AS kills,
    COUNT(CASE WHEN outcome = 'pass/unresolved' THEN 1 END) AS passes,
    COUNT(CASE WHEN outcome = 'npc-fled' THEN 1 END) AS npc_flees,
    COUNT(CASE WHEN outcome = 'you-fled' THEN 1 END) AS your_flees,
    COUNT(CASE WHEN outcome = 'withdrawn' THEN 1 END) AS withdrawn,
    ROUND(SUM(approx_damage_done), 3) AS approx_damage_done,
    ROUND(SUM(approx_damage_taken), 3) AS approx_health_lost,
    ROUND(SUM(duration_ms) / 1000.0, 3) AS fight_duration_seconds,
    CASE
        WHEN SUM(duration_ms) > 0
            THEN ROUND(SUM(approx_damage_done) / (SUM(duration_ms) / 1000.0), 3)
        ELSE NULL
    END AS approx_dps
FROM combat_fights
GROUP BY npc_name
ORDER BY fight_count DESC, npc_name;

CREATE VIEW IF NOT EXISTS v_summary_by_npc_group AS
SELECT
    npc_group,
    NULL AS encounter_count,
    COUNT(*) AS fight_count,
    GROUP_CONCAT(DISTINCT npc_name) AS unique_npcs_csv,
    COUNT(CASE WHEN outcome = 'killed' THEN 1 END) AS kills,
    COUNT(CASE WHEN outcome = 'pass/unresolved' THEN 1 END) AS passes,
    COUNT(CASE WHEN outcome = 'npc-fled' THEN 1 END) AS npc_flees,
    COUNT(CASE WHEN outcome = 'you-fled' THEN 1 END) AS your_flees,
    COUNT(CASE WHEN outcome = 'withdrawn' THEN 1 END) AS withdrawn,
    ROUND(SUM(approx_damage_done), 3) AS approx_damage_done,
    ROUND(SUM(approx_damage_taken), 3) AS approx_health_lost,
    ROUND(SUM(duration_ms) / 1000.0, 3) AS fight_duration_seconds,
    CASE
        WHEN SUM(duration_ms) > 0
            THEN ROUND(SUM(approx_damage_done) / (SUM(duration_ms) / 1000.0), 3)
        ELSE NULL
    END AS approx_dps
FROM combat_fights
GROUP BY npc_group
ORDER BY fight_count DESC, npc_group;

CREATE VIEW IF NOT EXISTS v_summary_by_weapon_npc_group AS
SELECT
    COALESCE(NULLIF(weapon_used, ''), '(unknown)') AS weapon_used,
    npc_group,
    COUNT(*) AS fight_count,
    COUNT(CASE WHEN outcome = 'killed' THEN 1 END) AS kills,
    COUNT(CASE WHEN outcome = 'npc-fled' THEN 1 END) AS npc_flees,
    COUNT(CASE WHEN outcome = 'you-fled' THEN 1 END) AS your_flees,
    COUNT(CASE WHEN outcome = 'withdrawn' THEN 1 END) AS withdrawn,
    ROUND(SUM(approx_damage_done), 3) AS approx_damage_done,
    ROUND(SUM(approx_damage_taken), 3) AS approx_health_lost,
    ROUND(SUM(duration_ms) / 1000.0, 3) AS fight_duration_seconds,
    CASE
        WHEN SUM(duration_ms) > 0
            THEN ROUND(SUM(approx_damage_done) / (SUM(duration_ms) / 1000.0), 3)
        ELSE NULL
    END AS approx_dps
FROM combat_fights
GROUP BY COALESCE(NULLIF(weapon_used, ''), '(unknown)'), npc_group
ORDER BY fight_count DESC, weapon_used, npc_group;
