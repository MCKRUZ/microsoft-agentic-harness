-- =============================================================================
-- Agentic Harness Observability Schema — baseline
-- PostgreSQL 16. Applied by PostgresMigrationRunner, not by the Docker entrypoint.
--
-- Every statement here is idempotent, and that is load-bearing rather than
-- tidiness. A database that predates the migration runner already has these
-- tables but has no ledger recording them, so the runner will try to apply this
-- script against it. IF NOT EXISTS makes that a no-op and lets the later
-- migrations do the real work, which is simpler and more forgiving of drift than
-- trying to detect an existing installation and mark the baseline as applied.
--
-- The grafana_reader role and its grants are NOT here. Creating a role is a
-- cluster-level operation needing CREATEROLE, which no least-privilege
-- application account should hold; it lives in Dashboards/postgres-bootstrap/
-- and runs once when the cluster is created. That file also sets ALTER DEFAULT
-- PRIVILEGES, so every table created by a migration below is readable by Grafana
-- without any migration having to grant it.
-- =============================================================================

-- ---------------------------------------------------------------------------
-- Sessions: one row per agent conversation
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sessions (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    conversation_id       TEXT NOT NULL UNIQUE,
    agent_name            TEXT NOT NULL,
    model                 TEXT,
    started_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ended_at              TIMESTAMPTZ,
    duration_ms           INTEGER,
    turn_count            INTEGER NOT NULL DEFAULT 0,
    tool_call_count       INTEGER NOT NULL DEFAULT 0,
    subagent_count        INTEGER NOT NULL DEFAULT 0,
    total_input_tokens    INTEGER NOT NULL DEFAULT 0,
    total_output_tokens   INTEGER NOT NULL DEFAULT 0,
    total_cache_read      INTEGER NOT NULL DEFAULT 0,
    total_cache_write     INTEGER NOT NULL DEFAULT 0,
    total_cost_usd        NUMERIC(10,6) NOT NULL DEFAULT 0,
    cache_hit_rate        NUMERIC(5,4) NOT NULL DEFAULT 0,
    status                TEXT NOT NULL DEFAULT 'active',
    error_message         TEXT,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- Named explicitly. Postgres would name an inline column CHECK for us, but
    -- the generated name is a convention to be looked up rather than a fact to
    -- be relied on, and migration 005 has to drop this constraint by name.
    CONSTRAINT sessions_status_check CHECK (status IN ('active','completed','error'))
);

CREATE INDEX IF NOT EXISTS idx_sessions_started   ON sessions (started_at);
CREATE INDEX IF NOT EXISTS idx_sessions_agent     ON sessions (agent_name, started_at);
CREATE INDEX IF NOT EXISTS idx_sessions_status    ON sessions (status);

-- ---------------------------------------------------------------------------
-- Session messages: one row per turn
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS session_messages (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id      UUID NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
    turn_index      INTEGER NOT NULL,
    role            TEXT NOT NULL
                    CHECK (role IN ('user','assistant','system','tool')),
    source          TEXT
                    CHECK (source IN (
                        'user_message','assistant_text','assistant_tool',
                        'assistant_mixed','tool_result','system_context',
                        'hook_injection')),
    content_preview TEXT,
    -- Full message body. content_preview is the 500-char truncation used by
    -- the list panels; content_full is served by the file-body deep-link
    -- endpoint (GET /api/sessions/{id}/messages/{messageId}).
    content_full    TEXT,
    model           TEXT,
    input_tokens    INTEGER NOT NULL DEFAULT 0,
    output_tokens   INTEGER NOT NULL DEFAULT 0,
    cache_read      INTEGER NOT NULL DEFAULT 0,
    cache_write     INTEGER NOT NULL DEFAULT 0,
    cost_usd        NUMERIC(10,6) NOT NULL DEFAULT 0,
    cache_hit_pct   NUMERIC(5,4) NOT NULL DEFAULT 0,
    tool_names      TEXT[],
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_messages_session ON session_messages (session_id, turn_index);
CREATE INDEX IF NOT EXISTS idx_messages_created ON session_messages (created_at);

-- ---------------------------------------------------------------------------
-- Tool executions: one row per tool call
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS tool_executions (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id      UUID NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
    message_id      UUID REFERENCES session_messages (id) ON DELETE SET NULL,
    tool_name       TEXT NOT NULL,
    tool_source     TEXT
                    CHECK (tool_source IN ('keyed_di','mcp','semantic_kernel')),
    duration_ms     INTEGER,
    status          TEXT NOT NULL
                    CHECK (status IN ('success','failure','timeout')),
    error_type      TEXT,
    result_size     INTEGER,
    -- LLM-supplied call id (FunctionCallContent.CallId). Used by the
    -- middleware capture to pair function-call requests with their
    -- subsequent function-result payloads and by the per-invocation
    -- deep-link endpoint (GET /api/sessions/{id}/tools/{invocationId}).
    call_id         TEXT,
    -- JSON-serialized arguments the LLM passed to the tool. Truncated by
    -- ToolDiagnosticsMiddleware to MaxPayloadSummaryLength.
    args            TEXT,
    -- Result payload returned from the tool to the LLM. Truncated by
    -- ToolDiagnosticsMiddleware to MaxPayloadSummaryLength.
    stdout          TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_tools_session ON tool_executions (session_id);
CREATE INDEX IF NOT EXISTS idx_tools_name    ON tool_executions (tool_name, created_at);
CREATE INDEX IF NOT EXISTS idx_tools_status  ON tool_executions (status) WHERE status != 'success';

-- ---------------------------------------------------------------------------
-- Content safety evaluations
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS safety_events (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id      UUID NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
    phase           TEXT NOT NULL CHECK (phase IN ('prompt','response')),
    outcome         TEXT NOT NULL CHECK (outcome IN ('pass','block','redact')),
    category        TEXT,
    severity        INTEGER,
    filter_name     TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_safety_session ON safety_events (session_id);
CREATE INDEX IF NOT EXISTS idx_safety_outcome ON safety_events (outcome) WHERE outcome != 'pass';

-- ---------------------------------------------------------------------------
-- Budget configurations
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS budget_configs (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name            TEXT NOT NULL UNIQUE,
    period          TEXT NOT NULL CHECK (period IN ('daily','weekly','monthly')),
    warn_at         NUMERIC(10,2) NOT NULL,
    crit_at         NUMERIC(10,2) NOT NULL,
    warn_clear      NUMERIC(10,2) NOT NULL,
    crit_clear      NUMERIC(10,2) NOT NULL,
    enabled         BOOLEAN NOT NULL DEFAULT TRUE,
    silenced_until  TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ---------------------------------------------------------------------------
-- Budget state (current period)
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS budget_state (
    config_id       UUID PRIMARY KEY REFERENCES budget_configs (id) ON DELETE CASCADE,
    status          TEXT NOT NULL DEFAULT 'clear'
                    CHECK (status IN ('clear','warning','critical')),
    current_spend   NUMERIC(10,6) NOT NULL DEFAULT 0,
    period_start    TIMESTAMPTZ NOT NULL,
    last_evaluated  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_transition TIMESTAMPTZ
);

-- ---------------------------------------------------------------------------
-- Budget alert transitions (history)
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS budget_events (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    config_id       UUID NOT NULL REFERENCES budget_configs (id) ON DELETE CASCADE,
    prev_status     TEXT CHECK (prev_status IN ('clear','warning','critical')),
    new_status      TEXT NOT NULL CHECK (new_status IN ('clear','warning','critical')),
    spend           NUMERIC(10,6) NOT NULL,
    threshold       NUMERIC(10,2) NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_budget_events_config ON budget_events (config_id, created_at);

-- ---------------------------------------------------------------------------
-- Audit log
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS audit_log (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    operation       TEXT NOT NULL,
    source          TEXT NOT NULL CHECK (source IN ('harness','api','system')),
    session_id      UUID REFERENCES sessions (id) ON DELETE SET NULL,
    metadata        JSONB,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_audit_created   ON audit_log (created_at);
CREATE INDEX IF NOT EXISTS idx_audit_operation ON audit_log (operation);

-- ---------------------------------------------------------------------------
-- Model pricing reference (seed data)
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS model_pricing (
    model_name              TEXT PRIMARY KEY,
    input_per_million       NUMERIC(10,4) NOT NULL,
    output_per_million      NUMERIC(10,4) NOT NULL,
    cache_write_per_million NUMERIC(10,4) NOT NULL DEFAULT 0,
    cache_read_per_million  NUMERIC(10,4) NOT NULL DEFAULT 0,
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

INSERT INTO model_pricing (model_name, input_per_million, output_per_million, cache_write_per_million, cache_read_per_million) VALUES
    ('gpt-4o',              2.5000, 10.0000, 0, 0),
    ('gpt-4o-mini',         0.1500,  0.6000, 0, 0),
    ('gpt-4.1',             2.0000,  8.0000, 0, 0),
    ('gpt-4.1-mini',        0.4000,  1.6000, 0, 0),
    ('gpt-4.1-nano',        0.1000,  0.4000, 0, 0),
    ('claude-opus-4',       15.0000, 75.0000, 18.7500, 1.5000),
    ('claude-sonnet-4',      3.0000, 15.0000,  3.7500, 0.3000),
    ('claude-haiku-3.5',     0.8000,  4.0000,  1.0000, 0.0800)
ON CONFLICT (model_name) DO NOTHING;

-- ---------------------------------------------------------------------------
-- Materialized view: daily cost summary
-- ---------------------------------------------------------------------------
CREATE MATERIALIZED VIEW IF NOT EXISTS daily_cost_summary AS
SELECT
    DATE_TRUNC('day', started_at)   AS day,
    agent_name,
    model,
    COUNT(*)                        AS session_count,
    SUM(turn_count)                 AS total_turns,
    SUM(total_cost_usd)             AS total_cost,
    SUM(total_input_tokens)         AS total_input,
    SUM(total_output_tokens)        AS total_output,
    SUM(total_cache_read)           AS total_cache_read,
    SUM(total_cache_write)          AS total_cache_write,
    AVG(cache_hit_rate)             AS avg_cache_hit
FROM sessions
WHERE ended_at IS NOT NULL
GROUP BY DATE_TRUNC('day', started_at), agent_name, model;

CREATE UNIQUE INDEX IF NOT EXISTS idx_daily_cost ON daily_cost_summary (day, agent_name, model);

-- ---------------------------------------------------------------------------
-- Seed budget configurations
-- ---------------------------------------------------------------------------
INSERT INTO budget_configs (name, period, warn_at, crit_at, warn_clear, crit_clear) VALUES
    ('Daily Spend Limit', 'daily', 5.00, 10.00, 4.00, 8.00),
    ('Monthly Spend Cap', 'monthly', 100.00, 200.00, 80.00, 160.00)
ON CONFLICT (name) DO NOTHING;

INSERT INTO budget_state (config_id, status, current_spend, period_start)
SELECT id, 'clear', 0, DATE_TRUNC('day', NOW())
FROM budget_configs WHERE period = 'daily'
ON CONFLICT (config_id) DO NOTHING;

INSERT INTO budget_state (config_id, status, current_spend, period_start)
SELECT id, 'clear', 0, DATE_TRUNC('month', NOW())
FROM budget_configs WHERE period = 'monthly'
ON CONFLICT (config_id) DO NOTHING;
