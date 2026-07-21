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
GRANT SELECT, INSERT, UPDATE, DELETE ON tenants, branches, branch_settings, users TO pos_app;

-- So each future migration's new tables are granted automatically, without a
-- manual grant statement added to every slice's migration.
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO pos_app;
