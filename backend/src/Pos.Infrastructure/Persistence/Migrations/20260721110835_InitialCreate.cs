using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "branches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_branches", x => x.id);
                    table.ForeignKey(
                        name: "FK_branches_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                    table.ForeignKey(
                        name: "FK_users_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "branch_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(5,4)", nullable: false),
                    tax_applies = table.Column<bool>(type: "boolean", nullable: false),
                    service_charge_rate = table.Column<decimal>(type: "numeric(5,4)", nullable: false),
                    service_applies = table.Column<bool>(type: "boolean", nullable: false),
                    tax_on_service = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_branch_settings", x => x.id);
                    table.ForeignKey(
                        name: "FK_branch_settings_branches_branch_id",
                        column: x => x.branch_id,
                        principalTable: "branches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_branch_settings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_branch_settings_branch_id",
                table: "branch_settings",
                column: "branch_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_branch_settings_tenant_id",
                table: "branch_settings",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_branches_tenant_id",
                table: "branches",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_users_tenant_id",
                table: "users",
                column: "tenant_id");

            // ============================================================
            // Row-Level Security (BR-T-01/02; CLAUDE.md multi-tenancy rules).
            // FORCE ROW LEVEL SECURITY applies even to the table owner. This migration
            // runs as the admin/owner role, so the owner of these tables is that admin
            // role, never pos_app — pos_app only receives GRANTs (002_create_app_role.sql).
            //
            // NULLIF(current_setting('app.tenant_id', true), '') is required, not just
            // current_setting(...): for a custom (non-built-in) GUC, once ANY session in
            // the server's lifetime has ever SET it, current_setting(..., true) on a
            // session that never set it returns '' (empty string), not NULL. Casting
            // ''::uuid throws 22P02 instead of comparing as NULL. NULLIF converts that
            // empty string to a true NULL first, so `tenant_id = NULL` cleanly evaluates
            // to false/zero-rows in every case — a missing tenant context always fails
            // CLOSED, whether "never set anywhere yet" or "unset this session".
            //
            // tenants has no separate tenant_id column (it IS the tenant); its own `id`
            // is compared instead.
            // ============================================================
            migrationBuilder.Sql(
                """
                ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
                ALTER TABLE tenants FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON tenants
                    USING (id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE branches ENABLE ROW LEVEL SECURITY;
                ALTER TABLE branches FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON branches
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE branch_settings ENABLE ROW LEVEL SECURITY;
                ALTER TABLE branch_settings FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON branch_settings
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE users ENABLE ROW LEVEL SECURITY;
                ALTER TABLE users FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON users
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS tenant_isolation ON users;
                DROP POLICY IF EXISTS tenant_isolation ON branch_settings;
                DROP POLICY IF EXISTS tenant_isolation ON branches;
                DROP POLICY IF EXISTS tenant_isolation ON tenants;
                """);

            migrationBuilder.DropTable(
                name: "branch_settings");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "branches");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
