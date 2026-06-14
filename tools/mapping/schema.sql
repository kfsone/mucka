-- MUD2 Mapping Database Schema
-- Full event-sourcing model: raw_captures, observations, and edge_events are
-- immutable. Assignment tables are append-only (superseded rows are never deleted).
-- Derivations log every algorithm decision to support full replay.

-- ============================================================
-- SOURCE LAYER: raw records from walk JSONL files
-- ============================================================

CREATE TABLE IF NOT EXISTS raw_captures (
    id          TEXT    PRIMARY KEY,            -- SHA256(source_file || ':' || seq_index)
    source_file TEXT    NOT NULL,               -- walk file path (relative to mapping dir)
    seq_index   INTEGER NOT NULL,               -- position in file (0-based)
    op          TEXT    NOT NULL,               -- 'probe' | 'move' | 'extra' | ...
    record_json TEXT    NOT NULL,               -- original JSON record, verbatim
    captured_at INTEGER,                        -- unix ms from record timestamp
    UNIQUE(source_file, seq_index)
);

-- ============================================================
-- OBSERVATION LAYER: immutable decoded room states
-- ============================================================

CREATE TABLE IF NOT EXISTS observations (
    id          TEXT    PRIMARY KEY,            -- SHA256(short|long|fex|exits_sorted)
    kind        TEXT    NOT NULL,               -- 'full' | 'dark' | 'partial' |
                                                --   'mist_occluded' | 'corrupt'
                                                -- mist_occluded: room visible but exits hidden
                                                --   by environmental effect (fog, mist, fumes);
                                                --   distinct from 'dark' (no room info at all)
    short       TEXT,
    long        TEXT,
    fex         TEXT,                           -- sorted exit keywords, space-separated
                                                -- recognized keywords include 'over' (e.g. bridges,
                                                --   elevated crossings) in addition to compass dirs
    exits_json  TEXT,                           -- JSON array of exit strings as observed
    created_at  INTEGER NOT NULL
);

-- Which raw captures contributed to which observations (many-to-many).
-- One capture line can yield 'origin' and 'destination' observations;
-- one observation can appear in many captures.
CREATE TABLE IF NOT EXISTS capture_observations (
    capture_id      TEXT    NOT NULL REFERENCES raw_captures(id),
    observation_id  TEXT    NOT NULL REFERENCES observations(id),
    role            TEXT    NOT NULL,           -- 'origin' | 'destination' | 'probe'
    PRIMARY KEY (capture_id, observation_id, role)
);

-- ============================================================
-- EDGE EVENT LAYER: immutable raw traversal records
-- ============================================================

CREATE TABLE IF NOT EXISTS edge_events (
    id                  TEXT    PRIMARY KEY,    -- UUID
    capture_id          TEXT    NOT NULL REFERENCES raw_captures(id),
    from_observation_id TEXT    REFERENCES observations(id),    -- null if dark origin
    direction           TEXT    NOT NULL,
    to_observation_id   TEXT    REFERENCES observations(id),    -- null if dark/unknown dest
    outcome             TEXT    NOT NULL,       -- 'arrived' | 'rejected' | 'dark' |
                                                --   'occluded' | 'teleported' | 'unknown'
    rejection_reason    TEXT,                   -- for 'rejected' outcomes
    condition           TEXT,                   -- observed gating condition (e.g. 'door:open')
    created_at          INTEGER NOT NULL
);

-- ============================================================
-- IMPRESSION LAYER: immutable hypotheses about rooms
-- An 'observed' impression maps 1:1 to one observation.
-- A 'synthesized' impression is derived from multiple observations
--   (dark+lit subsumption, door-state merge, etc).
-- A 'canonical' impression is the current best model of a location;
--   it is what the map renders and the router queries. When evidence
--   improves, a new canonical impression is created and the old
--   impression_assignment is superseded.
-- ============================================================

CREATE TABLE IF NOT EXISTS impressions (
    id          TEXT    PRIMARY KEY,            -- UUID (synthetic)
    kind        TEXT    NOT NULL,               -- 'observed' | 'synthesized' | 'canonical'
    short       TEXT,
    long        TEXT,
    fex         TEXT,
    exits_json  TEXT,                           -- JSON array (union across assigned observations)
    can_be_dark INTEGER NOT NULL DEFAULT 0,     -- 1 if ever observed dark
    -- sequence_context: predecessor impression id + direction that produced this impression.
    -- Required for rooms where content-hash is identical to other rooms (e.g. maze/graveyard).
    -- NULL for rooms with unique content; non-null for sequence-dependent impressions.
    -- Format: "<impression_id>/<direction>" (e.g. "abc123/se")
    sequence_context TEXT,
    created_at  INTEGER NOT NULL
);

-- ============================================================
-- LOCATION LAYER: identity anchors only
-- Current state is derived from the location's canonical impression.
-- ============================================================

CREATE TABLE IF NOT EXISTS locations (
    id          TEXT    PRIMARY KEY,            -- UUID
    created_at  INTEGER NOT NULL
);

-- ============================================================
-- ASSIGNMENT TABLES: append-only derivation linkage
-- Never delete rows. To revise: insert a new row with supersedes=old.id,
-- then update old row's superseded_by to new.id.
-- ============================================================

CREATE TABLE IF NOT EXISTS observation_assignments (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    observation_id  TEXT    NOT NULL REFERENCES observations(id),
    impression_id   TEXT    NOT NULL REFERENCES impressions(id),
    algorithm       TEXT    NOT NULL,           -- e.g. 'auto-observed-v1'
    confidence      REAL    NOT NULL DEFAULT 1.0,
    supersedes      INTEGER REFERENCES observation_assignments(id),
    superseded_by   INTEGER REFERENCES observation_assignments(id),
    created_at      INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS impression_assignments (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    impression_id   TEXT    NOT NULL REFERENCES impressions(id),
    location_id     TEXT    NOT NULL REFERENCES locations(id),
    algorithm       TEXT    NOT NULL,
    confidence      REAL    NOT NULL DEFAULT 1.0,
    supersedes      INTEGER REFERENCES impression_assignments(id),
    superseded_by   INTEGER REFERENCES impression_assignments(id),
    created_at      INTEGER NOT NULL
);

-- ============================================================
-- DERIVATION LOG: every algorithm decision
-- Supports full replay: fix algorithm, re-feed raw captures,
-- recompute derivations, bad observations fall out automatically.
-- ============================================================

CREATE TABLE IF NOT EXISTS derivations (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    algorithm   TEXT    NOT NULL,               -- e.g. 'subsume-dark-v1', 'merge-same-v1'
    input_ids   TEXT    NOT NULL,               -- JSON array of input entity IDs
    output_id   TEXT,                           -- created/updated entity ID (null if rejected)
    outcome     TEXT    NOT NULL,               -- 'new' | 'merged' | 'subsumed' | 'split' | 'rejected'
    reason      TEXT,
    created_at  INTEGER NOT NULL
);

-- ============================================================
-- CANONICAL EDGE TABLE: derived from edge_events via resolution
-- from/to are impression_id until the impression is assigned to
-- a location, then location_id is filled in.
-- ============================================================

CREATE TABLE IF NOT EXISTS edges (
    id                  TEXT    PRIMARY KEY,    -- UUID
    from_location_id    TEXT    REFERENCES locations(id),
    from_impression_id  TEXT    REFERENCES impressions(id),
    direction           TEXT    NOT NULL,
    to_location_id      TEXT    REFERENCES locations(id),
    to_impression_id    TEXT    REFERENCES impressions(id),
    condition           TEXT,
    confidence          TEXT    NOT NULL DEFAULT 'unknown', -- 'confirmed'|'inferred'|'unknown'
    created_at          INTEGER NOT NULL
);

-- ============================================================
-- VIEWS
-- ============================================================

CREATE VIEW IF NOT EXISTS current_observation_assignments AS
    SELECT * FROM observation_assignments WHERE superseded_by IS NULL;

CREATE VIEW IF NOT EXISTS current_impression_assignments AS
    SELECT * FROM impression_assignments WHERE superseded_by IS NULL;

-- Resolved rooms: location id + its current canonical impression
CREATE VIEW IF NOT EXISTS resolved_rooms AS
    SELECT
        l.id        AS location_id,
        i.id        AS impression_id,
        i.short,
        i.long,
        i.fex,
        i.exits_json,
        i.can_be_dark
    FROM locations l
    JOIN current_impression_assignments cia ON cia.location_id = l.id
    JOIN impressions i ON i.id = cia.impression_id
    WHERE i.kind = 'canonical';
