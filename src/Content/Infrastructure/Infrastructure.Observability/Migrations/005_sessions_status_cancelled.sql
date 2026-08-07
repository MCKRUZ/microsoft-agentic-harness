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
-- The old constraint is dropped by DISCOVERED name rather than assumed name.
--
-- Be honest about how much that buys, because an earlier draft of this comment
-- was not: for the two databases this template actually produces, the assumed
-- name would have worked. A database created by 001 is named
-- 'sessions_status_check' because 001 says so, and on a pre-#301 database
-- Postgres's generated name for an inline column CHECK is '<table>_<column>_check'
-- — the same string. Mutation-testing this proved it: replacing the lookup below
-- with a hardcoded DROP ... IF EXISTS left every test green.
--
-- What the lookup covers is the database this template did not produce: one whose
-- schema was hand-applied, adapted, or restored in a way that named the constraint
-- something else, which is exactly the population #301 exists to reach and the one
-- nobody can enumerate. There a hardcoded DROP silently matches nothing and the ADD
-- then SUCCEEDS, because it declares a name nothing is using yet. Nothing errors,
-- nothing rolls back, and the table is left carrying two status constraints — the
-- old narrow one still refusing 'cancelled'. A silent half-upgrade that reports
-- success is the exact failure shape #301 was filed about, which is why six lines
-- to not depend on a naming convention are worth it. The control is the test named
-- ...HandNamedConstraint...; with the lookup replaced by a hardcoded DROP it fails,
-- and it is the only test here that does.
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
