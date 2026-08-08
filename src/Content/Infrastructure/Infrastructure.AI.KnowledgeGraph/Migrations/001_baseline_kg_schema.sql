-- =============================================================================
-- Knowledge graph baseline — nodes and edges with their isolation and temporal
-- columns.
--
-- This was previously a string constant inside PostgreSqlGraphStore, executed on
-- first connection. That could create the tables but could never change them: a
-- consumer whose database already held a graph would keep the old shape forever,
-- the same dead end #301 fixed for the observability schema. It is a migration
-- now so the next column can actually reach them.
--
-- Idempotent, because a database created by the old inline DDL already has these
-- tables and no ledger row saying so.
-- =============================================================================

CREATE TABLE IF NOT EXISTS kg_nodes (
    id          TEXT PRIMARY KEY,
    name        TEXT NOT NULL,
    type        TEXT NOT NULL,
    properties  JSONB,
    chunk_ids   TEXT[],
    provenance  JSONB,
    owner_id    TEXT,
    tenant_id   TEXT,
    created_at  TIMESTAMPTZ,
    expires_at  TIMESTAMPTZ
);

CREATE TABLE IF NOT EXISTS kg_edges (
    id              TEXT PRIMARY KEY,
    source_node_id  TEXT NOT NULL,
    target_node_id  TEXT NOT NULL,
    predicate       TEXT NOT NULL,
    properties      JSONB,
    chunk_id        TEXT,
    provenance      JSONB,
    owner_id        TEXT,
    tenant_id       TEXT,
    created_at      TIMESTAMPTZ,
    expires_at      TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_kg_nodes_owner ON kg_nodes (owner_id);
CREATE INDEX IF NOT EXISTS idx_kg_nodes_tenant ON kg_nodes (tenant_id);
