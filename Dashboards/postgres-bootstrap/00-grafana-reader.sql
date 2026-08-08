-- =============================================================================
-- Cluster bootstrap: the read-only role Grafana connects as.
--
-- This is NOT schema, and it is deliberately the only thing still fed to
-- /docker-entrypoint-initdb.d. Tables, columns and constraints now come from the
-- application's migration runner (see Infrastructure.Observability/Migrations),
-- because they change over the life of an installation and have to reach a
-- database that already holds data.
--
-- A role does not: it is created once when the cluster is created, and it lives
-- in the cluster rather than in the database. It also cannot come from the
-- application, because CREATE ROLE requires the CREATEROLE privilege and no
-- least-privilege application account should hold it. An enterprise consumer
-- pointing the harness at a managed Postgres runs this once, by hand or from
-- their provisioning tooling, and never again.
--
-- The password below is a local-development default for a role with SELECT and
-- nothing else. Change it — along with Grafana's datasource configuration —
-- before pointing this at anything that is not a laptop.
-- =============================================================================

DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'grafana_reader') THEN
        CREATE ROLE grafana_reader LOGIN PASSWORD 'grafana_readonly';
    END IF;
END
$$;

-- current_database() rather than a hardcoded name: POSTGRES_DB is configurable
-- in docker-compose.yml, and the previous version of this statement named
-- 'observability' literally, so it broke for anyone who changed it.
DO $$
BEGIN
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO grafana_reader', current_database());
END
$$;

GRANT USAGE ON SCHEMA public TO grafana_reader;

-- Retroactive, for a database that already has tables.
GRANT SELECT ON ALL TABLES IN SCHEMA public TO grafana_reader;

-- Prospective, and the important one: tables the migration runner creates from
-- here on are readable by Grafana without any migration having to grant it, which
-- is what lets the migrations stay pure schema and stay runnable against a
-- database where this role was never provisioned at all.
--
-- READ THIS BEFORE ADAPTING THE FILE. Default privileges attach to the role that
-- GRANTS them, not to the schema. Written without FOR ROLE, the statement below
-- covers only tables created by whoever runs this script. On the docker-compose
-- path that is the same account the harness connects as, so it just works. On a
-- managed Postgres — where the header above tells you to run this yourself — you
-- will typically run it as an admin while the harness connects as something else,
-- and then it silently covers nothing, because every migration-created table is
-- owned by the app role instead. Name that role explicitly:
--
--   ALTER DEFAULT PRIVILEGES FOR ROLE <role the harness connects as>
--       IN SCHEMA public GRANT SELECT ON TABLES TO grafana_reader;
--
-- The symptom of getting this wrong is Grafana reporting "permission denied" on
-- tables that plainly exist, which reads like a broken dashboard rather than a
-- missing grant.
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO grafana_reader;
