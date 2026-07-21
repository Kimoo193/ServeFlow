-- Idempotent: safe to re-run. Creates the non-superuser application role that the
-- API connects as at runtime (CLAUDE.md: "the app connects as a non-superuser role").
-- Run this as the privileged bootstrap role (docker-compose's POSTGRES_USER), never
-- as this new role itself.

DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'pos_app') THEN
        CREATE ROLE pos_app
            LOGIN
            PASSWORD 'pos_app_dev_password'
            NOSUPERUSER
            NOCREATEDB
            NOCREATEROLE
            NOBYPASSRLS;
    END IF;
END
$$;

GRANT CONNECT ON DATABASE pos TO pos_app;
GRANT USAGE ON SCHEMA public TO pos_app;

-- Covers every table that already exists at the moment this script runs (which is
-- every slice's tables, since migrations always run before this script — see
-- PostgresRlsFixture). ALTER DEFAULT PRIVILEGES below is NOT retroactive: it only
-- applies to tables created after it executes, so relying on it alone here would
-- silently miss every table that existed before this script's first run.
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO pos_app;

-- Covers tables from any future migration applied AFTER this script has already run
-- once (e.g. a real deployment: role bootstrapped once, then new slices' migrations
-- applied over time) — so no per-slice edit to this file is needed either way.
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO pos_app;
