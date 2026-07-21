-- Slice 1: Tenant & Branch context + RLS foundation.
--
-- This is a hand-authored PREVIEW of the schema the EF Fluent configurations produce.
-- Once Microsoft.EntityFrameworkCore.Design is approved, `dotnet ef migrations add
-- InitialCreate` will generate the table/index/FK statements below verbatim from the
-- model; the RLS statements are added into that migration's Up() via migrationBuilder.Sql(),
-- since EF Core has no RLS-aware migration API and these are always hand-written.
--
-- NOT applied yet — this is the Slice 1 approval-gate deliverable, run only after approval.

BEGIN;

CREATE TABLE tenants (
    id uuid NOT NULL,
    name varchar(200) NOT NULL,
    type varchar(50) NOT NULL,
    CONSTRAINT pk_tenants PRIMARY KEY (id)
);

CREATE TABLE branches (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    name varchar(200) NOT NULL,
    CONSTRAINT pk_branches PRIMARY KEY (id),
    CONSTRAINT fk_branches_tenants_tenant_id FOREIGN KEY (tenant_id)
        REFERENCES tenants (id) ON DELETE RESTRICT
);
CREATE INDEX ix_branches_tenant_id ON branches (tenant_id);

CREATE TABLE branch_settings (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    branch_id uuid NOT NULL,
    currency varchar(3) NOT NULL,
    tax_rate numeric(5,4) NOT NULL,
    tax_applies boolean NOT NULL,
    service_charge_rate numeric(5,4) NOT NULL,
    service_applies boolean NOT NULL,
    tax_on_service boolean NOT NULL,
    CONSTRAINT pk_branch_settings PRIMARY KEY (id),
    CONSTRAINT fk_branch_settings_branches_branch_id FOREIGN KEY (branch_id)
        REFERENCES branches (id) ON DELETE CASCADE,
    CONSTRAINT fk_branch_settings_tenants_tenant_id FOREIGN KEY (tenant_id)
        REFERENCES tenants (id) ON DELETE RESTRICT
);
CREATE UNIQUE INDEX ix_branch_settings_branch_id ON branch_settings (branch_id);
CREATE INDEX ix_branch_settings_tenant_id ON branch_settings (tenant_id);

CREATE TABLE users (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    display_name varchar(200) NOT NULL,
    role varchar(20) NOT NULL,
    CONSTRAINT pk_users PRIMARY KEY (id),
    CONSTRAINT fk_users_tenants_tenant_id FOREIGN KEY (tenant_id)
        REFERENCES tenants (id) ON DELETE RESTRICT
);
CREATE INDEX ix_users_tenant_id ON users (tenant_id);

-- ============================================================
-- Row-Level Security (BR-T-01/02; CLAUDE.md multi-tenancy rules)
--
-- FORCE ROW LEVEL SECURITY applies even to the table owner (CLAUDE.md requirement) —
-- without FORCE, a table's own owner bypasses RLS by default in Postgres.
--
-- NULLIF(current_setting('app.tenant_id', true), '') is required, not just
-- current_setting(...) alone: for a custom (non-built-in) GUC, once ANY session in the
-- server's lifetime has ever SET it, current_setting(..., true) on a session that never
-- set it returns '' (empty string), not NULL — confirmed against a live Postgres 16 while
-- testing this migration. Casting ''::uuid throws 22P02 instead of comparing as NULL.
-- NULLIF converts that empty string to a true NULL first, so `tenant_id = NULL` cleanly
-- evaluates to false (zero rows / rejected write) in every case — a missing tenant
-- context always fails CLOSED, never with a leak. Never fail open.
--
-- tenants has no separate tenant_id column (it IS the tenant); its own `id` is compared
-- instead, so an authenticated request can only ever see its own tenant row.
-- ============================================================

ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenants FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON tenants
    USING (id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

ALTER TABLE branches ENABLE ROW LEVEL SECURITY;
ALTER TABLE branches FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON branches
    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

ALTER TABLE branch_settings ENABLE ROW LEVEL SECURITY;
ALTER TABLE branch_settings FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON branch_settings
    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

ALTER TABLE users ENABLE ROW LEVEL SECURITY;
ALTER TABLE users FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON users
    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

COMMIT;
