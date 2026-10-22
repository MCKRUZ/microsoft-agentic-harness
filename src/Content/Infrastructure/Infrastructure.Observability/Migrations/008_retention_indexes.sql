-- =============================================================================
-- Indexes for the retention sweep (#901).
--
-- The sweep deletes a session's audit rows by session id before the session
-- itself (deleting the session only nulls their link), and ages loaded context
-- bodies out by capture time. Neither column was indexed, so both deletes would
-- scan the whole table on every sweep.
-- =============================================================================

CREATE INDEX IF NOT EXISTS idx_audit_session
    ON audit_log (session_id);

CREATE INDEX IF NOT EXISTS idx_loaded_bodies_captured
    ON context_snapshot_loaded_bodies (captured_at);
