-- =============================================================================
-- Add 'cancelled' to the sessions.status vocabulary.
--
-- This is the change #301 existed to make possible. A cancelled run is a real
-- outcome and is not a failure; before there was a migration runner it had to be
-- recorded as 'error' with the reason 'conversation.cancelled', which overstated
-- the failure rate on every dashboard. Widening the constraint was impossible
-- because the only delivery mechanism reached databases being created for the
-- first time.
--
-- The old constraint is dropped by DISCOVERED name, not by assumed name. A
-- database created by 001 has 'sessions_status_check' because 001 says so, but a
-- database created before 001 was rewritten has whatever Postgres generated for
-- an inline column CHECK. Those happen to be the same string today; relying on
-- that would be assuming the very kind of thing this issue punished.
-- =============================================================================

DO $$
DECLARE
    constraint_name TEXT;
BEGIN
    -- Every CHECK constraint on sessions whose definition mentions the status
    -- column. There is exactly one; looping rather than selecting into a scalar
    -- means a database that somehow grew a second one is cleaned up instead of
    -- raising "more than one row returned".
    FOR constraint_name IN
        SELECT con.conname
        FROM pg_constraint con
        JOIN pg_class rel ON rel.oid = con.conrelid
        JOIN pg_namespace nsp ON nsp.oid = rel.relnamespace
        WHERE rel.relname = 'sessions'
          AND nsp.nspname = current_schema()
          AND con.contype = 'c'
          AND pg_get_constraintdef(con.oid) ILIKE '%status%'
    LOOP
        EXECUTE format('ALTER TABLE sessions DROP CONSTRAINT %I', constraint_name);
    END LOOP;

    ALTER TABLE sessions
        ADD CONSTRAINT sessions_status_check
        CHECK (status IN ('active','completed','error','cancelled'));
END
$$;
