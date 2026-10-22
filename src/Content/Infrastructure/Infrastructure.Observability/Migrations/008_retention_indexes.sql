-- =============================================================================
-- Session activity and indexes for the retention sweep (#901).
--
-- A session is swept once nothing has happened in it for the retention period.
-- Its own timestamps cannot say when that was: a resumed session keeps its first
-- started_at, and resuming clears ended_at, so a conversation reopened today
-- after a long gap would read as idle until its first turn landed. last_activity_at
-- is touched by every write to the session row (start, resume, metrics, end).
-- Existing rows are backfilled from the newest of their own timestamps; the
-- sweep also looks at child rows, which covers anything older.
--
-- The sweep deletes a session's audit rows by session id before the session
-- itself (deleting the session only nulls their link), and ages loaded context
-- bodies out by capture time; both columns are indexed so neither delete scans
-- its whole table.
-- =============================================================================

ALTER TABLE sessions ADD COLUMN IF NOT EXISTS last_activity_at TIMESTAMPTZ;

UPDATE sessions
SET last_activity_at = GREATEST(started_at, COALESCE(ended_at, started_at), created_at)
WHERE last_activity_at IS NULL;

ALTER TABLE sessions ALTER COLUMN last_activity_at SET DEFAULT NOW();
ALTER TABLE sessions ALTER COLUMN last_activity_at SET NOT NULL;

CREATE INDEX IF NOT EXISTS idx_sessions_last_activity
    ON sessions (last_activity_at);

CREATE INDEX IF NOT EXISTS idx_audit_session
    ON audit_log (session_id);

CREATE INDEX IF NOT EXISTS idx_loaded_bodies_captured
    ON context_snapshot_loaded_bodies (captured_at);
