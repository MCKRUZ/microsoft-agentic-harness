-- =============================================================================
-- Add 'assistant_failed' to the session_messages.source vocabulary.
--
-- A turn that fails or is cancelled after its model calls have been paid for is
-- charged to the conversation budget (#778) and added to the session rollup
-- (#780). The per-message row is what a dashboard drills into, so that turn has
-- to be visible there too, and visibly not a completed answer: 'assistant_text'
-- would have it read as one. Its tokens and cost sit on the row exactly as a
-- completed turn's do.
--
-- Same shape as 005_sessions_status_cancelled.sql, and for the same reason: the
-- old constraint is dropped by DISCOVERED name rather than assumed name, so a
-- database whose schema was hand-applied, adapted, or restored under a different
-- constraint name is brought forward instead of being left carrying two source
-- constraints with the old narrow one still refusing the new word. 005's header
-- records how much that does and does not buy.
-- =============================================================================

DO $$
DECLARE
    constraint_name TEXT;
BEGIN
    -- Every CHECK constraint on session_messages whose definition mentions the
    -- source column. The role constraint does not, so it is left alone.
    FOR constraint_name IN
        SELECT con.conname
        FROM pg_constraint con
        JOIN pg_class rel ON rel.oid = con.conrelid
        JOIN pg_namespace nsp ON nsp.oid = rel.relnamespace
        WHERE rel.relname = 'session_messages'
          AND nsp.nspname = current_schema()
          AND con.contype = 'c'
          AND pg_get_constraintdef(con.oid) ILIKE '%source%'
    LOOP
        EXECUTE format('ALTER TABLE session_messages DROP CONSTRAINT %I', constraint_name);
    END LOOP;

    ALTER TABLE session_messages
        ADD CONSTRAINT session_messages_source_check
        CHECK (source IN (
            'user_message','assistant_text','assistant_tool',
            'assistant_mixed','tool_result','system_context',
            'hook_injection','assistant_failed'));
END
$$;
