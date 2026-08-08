-- =============================================================================
-- Foresight loaded-item bodies — sidecar to context_snapshots.
-- One row per (conversation_id, turn_index, loaded_index). Stores the full
-- body text of a loaded artifact (composed system prompt, skill instructions,
-- tool schema, MCP descriptor, sub-agent description) so the dashboard's
-- ContextDrawer can lazily fetch on open. The parent context_snapshots row
-- carries metadata + token counts only, keeping SignalR / HTTP wire payloads
-- small.
--
-- Applied by PostgresMigrationRunner after 002_context_snapshots.sql.
--
-- This and 004 both used to be numbered 03-, so their order was decided by the
-- shell glob that fed them to the Docker entrypoint. They are independent — this
-- one creates a new table, 004 alters tables from 001 — so the split is safe;
-- the numbering exists so nobody has to establish that again.
-- =============================================================================

CREATE TABLE IF NOT EXISTS context_snapshot_loaded_bodies (
    conversation_id   TEXT        NOT NULL,
    turn_index        INTEGER     NOT NULL,
    loaded_index      INTEGER     NOT NULL,
    body              TEXT        NOT NULL,
    captured_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- Idempotent replays: re-emitting bodies for an existing
    -- (conversation_id, turn_index, loaded_index) overwrites rather than
    -- duplicates — matches the parent context_snapshots' replay semantics.
    CONSTRAINT pk_context_snapshot_loaded_bodies
        PRIMARY KEY (conversation_id, turn_index, loaded_index)
);

CREATE INDEX IF NOT EXISTS idx_loaded_bodies_conv_turn
    ON context_snapshot_loaded_bodies (conversation_id, turn_index);

-- No GRANT here; see the note in 002_context_snapshots.sql.
