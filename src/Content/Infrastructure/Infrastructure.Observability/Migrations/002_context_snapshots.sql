-- =============================================================================
-- Foresight context snapshots — one row per turn per conversation.
-- Applied by PostgresMigrationRunner after 001_baseline_schema.sql. The manual
-- "apply this file yourself with psql" instruction this header used to carry is
-- gone: that workaround is the thing the migration runner replaced.
-- =============================================================================

CREATE TABLE IF NOT EXISTS context_snapshots (
    id                BIGSERIAL PRIMARY KEY,
    conversation_id   TEXT        NOT NULL,
    turn_index        INTEGER     NOT NULL,
    turn_id           TEXT        NOT NULL,

    -- CategoryBreakdown — one integer column per Foresight category.
    -- Wide schema (not JSONB) so per-category aggregates / Grafana panels
    -- are cheap and don't require jsonb path extraction.
    cat_system        INTEGER     NOT NULL DEFAULT 0,
    cat_agents        INTEGER     NOT NULL DEFAULT 0,
    cat_skills        INTEGER     NOT NULL DEFAULT 0,
    cat_tools         INTEGER     NOT NULL DEFAULT 0,
    cat_mcp           INTEGER     NOT NULL DEFAULT 0,
    cat_messages      INTEGER     NOT NULL DEFAULT 0,

    -- LoadedItem[] — serialized via System.Text.Json with camelCase property
    -- names so the SignalR wire payload is the same shape the dashboard reads
    -- from the snapshots[] field in /api/sessions/:id.
    loaded_json       JSONB       NOT NULL DEFAULT '[]'::jsonb,

    captured_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- Idempotent replays: re-emitting a snapshot for an existing
    -- (conversation_id, turn_index) overwrites rather than duplicates.
    CONSTRAINT uq_context_snapshots_conv_turn UNIQUE (conversation_id, turn_index)
);

CREATE INDEX IF NOT EXISTS idx_context_snapshots_conv
    ON context_snapshots (conversation_id, turn_index);

CREATE INDEX IF NOT EXISTS idx_context_snapshots_captured
    ON context_snapshots (captured_at DESC);

-- No GRANT here. Dashboards/postgres-bootstrap/ sets ALTER DEFAULT PRIVILEGES
-- for grafana_reader, so every table a migration creates is readable already. A
-- grant in a migration would also fail outright on a database where that role
-- was never provisioned, taking the whole schema upgrade down with it.
