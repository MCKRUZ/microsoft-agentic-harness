-- =============================================================================
-- Agentic Harness Observability Schema
-- PostgreSQL 16 — initialized via docker-entrypoint-initdb.d
-- =============================================================================

-- Read-only user for Grafana datasource
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'grafana_reader') THEN
        CREATE ROLE grafana_reader LOGIN PASSWORD 'grafana_readonly';
    END IF;
END
$$;

-- ---------------------------------------------------------------------------
-- Sessions: one row per agent conversation
-- ---------------------------------------------------------------------------
CREATE TABLE sessions (
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
    status                TEXT NOT NULL DEFAULT 'active'
                          CHECK (status IN ('active','completed','error')),
    error_message         TEXT,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_sessions_started   ON sessions (started_at);
CREATE INDEX idx_sessions_agent     ON sessions (agent_name, started_at);
CREATE INDEX idx_sessions_status    ON sessions (status);

-- ---------------------------------------------------------------------------
-- Session messages: one row per turn
-- ---------------------------------------------------------------------------
CREATE TABLE session_messages (
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

CREATE INDEX idx_messages_session ON session_messages (session_id, turn_index);
CREATE INDEX idx_messages_created ON session_messages (created_at);

-- ---------------------------------------------------------------------------
-- Tool executions: one row per tool call
-- ---------------------------------------------------------------------------
CREATE TABLE tool_executions (
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
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_tools_session ON tool_executions (session_id);
CREATE INDEX idx_tools_name    ON tool_executions (tool_name, created_at);
CREATE INDEX idx_tools_status  ON tool_executions (status) WHERE status != 'success';

-- ---------------------------------------------------------------------------
-- Content safety evaluations
-- ---------------------------------------------------------------------------
CREATE TABLE safety_events (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id      UUID NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
    phase           TEXT NOT NULL CHECK (phase IN ('prompt','response')),
    outcome         TEXT NOT NULL CHECK (outcome IN ('pass','block','redact')),
    category        TEXT,
    severity        INTEGER,
    filter_name     TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_safety_session ON safety_events (session_id);
CREATE INDEX idx_safety_outcome ON safety_events (outcome) WHERE outcome != 'pass';

-- ---------------------------------------------------------------------------
-- Budget configurations
-- ---------------------------------------------------------------------------
CREATE TABLE budget_configs (
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
CREATE TABLE budget_state (
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
CREATE TABLE budget_events (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    config_id       UUID NOT NULL REFERENCES budget_configs (id) ON DELETE CASCADE,
    prev_status     TEXT CHECK (prev_status IN ('clear','warning','critical')),
    new_status      TEXT NOT NULL CHECK (new_status IN ('clear','warning','critical')),
    spend           NUMERIC(10,6) NOT NULL,
    threshold       NUMERIC(10,2) NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_budget_events_config ON budget_events (config_id, created_at);

-- ---------------------------------------------------------------------------
-- Audit log
-- ---------------------------------------------------------------------------
CREATE TABLE audit_log (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    operation       TEXT NOT NULL,
    source          TEXT NOT NULL CHECK (source IN ('harness','api','system')),
    session_id      UUID REFERENCES sessions (id) ON DELETE SET NULL,
    metadata        JSONB,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_audit_created   ON audit_log (created_at);
CREATE INDEX idx_audit_operation ON audit_log (operation);

-- ---------------------------------------------------------------------------
-- Model pricing reference (seed data)
-- ---------------------------------------------------------------------------
CREATE TABLE model_pricing (
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
CREATE MATERIALIZED VIEW daily_cost_summary AS
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

CREATE UNIQUE INDEX idx_daily_cost ON daily_cost_summary (day, agent_name, model);

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

-- ---------------------------------------------------------------------------
-- Grant read-only access to Grafana user
-- ---------------------------------------------------------------------------
GRANT CONNECT ON DATABASE observability TO grafana_reader;
GRANT USAGE ON SCHEMA public TO grafana_reader;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO grafana_reader;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO grafana_reader;
