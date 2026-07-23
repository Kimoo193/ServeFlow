using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M4StaffAndPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "role_permissions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    permission = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_permissions", x => x.id);
                    table.ForeignKey(
                        name: "FK_role_permissions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_role_permissions_tenant_id",
                table: "role_permissions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_role_permissions_tenant_id_role_permission",
                table: "role_permissions",
                columns: new[] { "tenant_id", "role", "permission" },
                unique: true);

            // ============================================================
            // Row-Level Security for role_permissions (BR-T-01/02; CLAUDE.md).
            // FORCE ROW LEVEL SECURITY applies even to the table owner.
            // ============================================================
            migrationBuilder.Sql(
                """
                ALTER TABLE role_permissions ENABLE ROW LEVEL SECURITY;
                ALTER TABLE role_permissions FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON role_permissions
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS tenant_isolation ON role_permissions;
                """);

            migrationBuilder.DropTable(
                name: "role_permissions");

            migrationBuilder.DropColumn(
                name: "is_active",
                table: "users");
        }
    }
}
