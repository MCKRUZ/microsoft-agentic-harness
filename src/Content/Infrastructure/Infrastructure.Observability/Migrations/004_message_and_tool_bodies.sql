-- =============================================================================
-- Foresight message + tool-execution body capture — adds the full-content
-- columns the file-body and per-invocation deep-link endpoints read.
-- Applied by PostgresMigrationRunner. All ADDs are guarded with IF NOT EXISTS so
-- re-applying is a no-op — which is also why this script is safe to run against a
-- database created before the migration ledger existed, where 001 finds every
-- table already present and these columns may or may not be.
-- =============================================================================

ALTER TABLE session_messages
    ADD COLUMN IF NOT EXISTS content_full TEXT;

ALTER TABLE tool_executions
    ADD COLUMN IF NOT EXISTS call_id TEXT,
    ADD COLUMN IF NOT EXISTS args    TEXT,
    ADD COLUMN IF NOT EXISTS stdout  TEXT;

-- call_id is part of the per-invocation deep-link lookup keyspace alongside
-- (session_id, id); the dedicated index keeps debug-time CallId lookups cheap
-- without bloating the main session_id index.
CREATE INDEX IF NOT EXISTS idx_tools_call_id
    ON tool_executions (call_id)
    WHERE call_id IS NOT NULL;
