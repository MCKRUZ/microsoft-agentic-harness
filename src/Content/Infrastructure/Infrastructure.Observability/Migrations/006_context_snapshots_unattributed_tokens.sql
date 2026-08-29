-- =============================================================================
-- Foresight context-bar reconciliation (#517) — adds the signed unattributed-
-- tokens gap to context_snapshots. Nullable: a turn with no model call (a
-- failed turn, or one with only local work) has nothing to reconcile against.
-- Applied by PostgresMigrationRunner. ADD is guarded with IF NOT EXISTS so
-- re-applying is a no-op, same convention as 004_message_and_tool_bodies.sql.
-- =============================================================================

ALTER TABLE context_snapshots
    ADD COLUMN IF NOT EXISTS unattributed_tokens INTEGER;
