using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Slice3DiningArea : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sections", x => x.id);
                    table.ForeignKey(
                        name: "FK_sections_branches_branch_id",
                        column: x => x.branch_id,
                        principalTable: "branches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_sections_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dining_tables",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    section_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dining_tables", x => x.id);
                    table.ForeignKey(
                        name: "FK_dining_tables_sections_section_id",
                        column: x => x.section_id,
                        principalTable: "sections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dining_tables_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    table_id = table.Column<Guid>(type: "uuid", nullable: true),
                    waiter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    daily_no = table.Column<int>(type: "integer", nullable: true),
                    customer_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    row_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.id);
                    table.ForeignKey(
                        name: "FK_orders_branches_branch_id",
                        column: x => x.branch_id,
                        principalTable: "branches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_orders_dining_tables_table_id",
                        column: x => x.table_id,
                        principalTable: "dining_tables",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_orders_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_orders_users_waiter_id",
                        column: x => x.waiter_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    station_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fire_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_lines", x => x.id);
                    table.ForeignKey(
                        name: "FK_order_lines_menu_items_item_id",
                        column: x => x.item_id,
                        principalTable: "menu_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_order_lines_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_order_lines_stations_station_id",
                        column: x => x.station_id,
                        principalTable: "stations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_order_lines_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_line_modifiers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    price_delta = table.Column<decimal>(type: "numeric(10,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_line_modifiers", x => x.id);
                    table.ForeignKey(
                        name: "FK_order_line_modifiers_order_lines_order_line_id",
                        column: x => x.order_line_id,
                        principalTable: "order_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_order_line_modifiers_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dining_tables_section_id",
                table: "dining_tables",
                column: "section_id");

            migrationBuilder.CreateIndex(
                name: "IX_dining_tables_tenant_id",
                table: "dining_tables",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_order_line_modifiers_order_line_id",
                table: "order_line_modifiers",
                column: "order_line_id");

            migrationBuilder.CreateIndex(
                name: "IX_order_line_modifiers_tenant_id",
                table: "order_line_modifiers",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_item_id",
                table: "order_lines",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_order_id",
                table: "order_lines",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_station_id",
                table: "order_lines",
                column: "station_id");

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_tenant_id",
                table: "order_lines",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_orders_branch_id",
                table: "orders",
                column: "branch_id");

            migrationBuilder.CreateIndex(
                name: "IX_orders_table_id",
                table: "orders",
                column: "table_id",
                unique: true,
                filter: "table_id IS NOT NULL AND status IN ('Open', 'InProgress')");

            migrationBuilder.CreateIndex(
                name: "IX_orders_tenant_id",
                table: "orders",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_orders_waiter_id",
                table: "orders",
                column: "waiter_id");

            migrationBuilder.CreateIndex(
                name: "IX_sections_branch_id",
                table: "sections",
                column: "branch_id");

            migrationBuilder.CreateIndex(
                name: "IX_sections_tenant_id",
                table: "sections",
                column: "tenant_id");

            // Row-Level Security policies for Slice 3 tables
            migrationBuilder.Sql("""
                ALTER TABLE sections ENABLE ROW LEVEL SECURITY;
                ALTER TABLE sections FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON sections
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);

            migrationBuilder.Sql("""
                ALTER TABLE dining_tables ENABLE ROW LEVEL SECURITY;
                ALTER TABLE dining_tables FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON dining_tables
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);

            migrationBuilder.Sql("""
                ALTER TABLE orders ENABLE ROW LEVEL SECURITY;
                ALTER TABLE orders FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON orders
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);

            migrationBuilder.Sql("""
                ALTER TABLE order_lines ENABLE ROW LEVEL SECURITY;
                ALTER TABLE order_lines FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON order_lines
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);

            migrationBuilder.Sql("""
                ALTER TABLE order_line_modifiers ENABLE ROW LEVEL SECURITY;
                ALTER TABLE order_line_modifiers FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON order_line_modifiers
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS tenant_isolation ON order_line_modifiers;
                DROP POLICY IF EXISTS tenant_isolation ON order_lines;
                DROP POLICY IF EXISTS tenant_isolation ON orders;
                DROP POLICY IF EXISTS tenant_isolation ON dining_tables;
                DROP POLICY IF EXISTS tenant_isolation ON sections;
                """);

            migrationBuilder.DropTable(
                name: "order_line_modifiers");

            migrationBuilder.DropTable(
                name: "order_lines");

            migrationBuilder.DropTable(
                name: "orders");

            migrationBuilder.DropTable(
                name: "dining_tables");

            migrationBuilder.DropTable(
                name: "sections");
        }
    }
}
