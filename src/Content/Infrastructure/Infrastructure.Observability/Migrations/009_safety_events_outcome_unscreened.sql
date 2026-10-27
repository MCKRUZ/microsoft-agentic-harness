-- =============================================================================
-- Add 'unscreened' to the safety_events.outcome vocabulary.
--
-- When the content-safety service cannot screen a request (it is down, throttled,
-- or rejects the credential), the evaluation is neither a pass nor a block: the
-- text was never judged (#904). Recording it as 'pass' would show healthy
-- screening on every dashboard for the length of the outage, so the pipeline
-- records 'unscreened'. Before this migration the baseline constraint refused the
-- word, and the observability store's best-effort write swallowed the refusal, so
-- the one record of unscreened traffic was silently never written. This migration
-- makes the record exist; the compliance report and the Grafana safety panels do
-- not break it out yet (#953).
--
-- Same shape as 005_sessions_status_cancelled.sql and for the same reason: the old
-- constraint is dropped by DISCOVERED name rather than assumed name, so a database
-- whose schema was hand-applied or restored under a different constraint name is
-- brought forward instead of being left carrying two outcome constraints with the
-- old narrow one still refusing the new word. 005's header records how much that
-- does and does not buy. The baseline declared this constraint inline and unnamed;
-- it is named here so a later widening can find it by name too.
-- =============================================================================

DO $$
DECLARE
    constraint_name TEXT;
BEGIN
    -- Every CHECK constraint on safety_events whose definition mentions the outcome
    -- column. The phase constraint does not, so it is left alone.
    FOR constraint_name IN
        SELECT con.conname
        FROM pg_constraint con
        JOIN pg_class rel ON rel.oid = con.conrelid
        JOIN pg_namespace nsp ON nsp.oid = rel.relnamespace
        WHERE rel.relname = 'safety_events'
          AND nsp.nspname = current_schema()
          AND con.contype = 'c'
          AND pg_get_constraintdef(con.oid) ILIKE '%outcome%'
    LOOP
        EXECUTE format('ALTER TABLE safety_events DROP CONSTRAINT %I', constraint_name);
    END LOOP;

    ALTER TABLE safety_events
        ADD CONSTRAINT safety_events_outcome_check
        CHECK (outcome IN ('pass','block','redact','unscreened'));
END
$$;
