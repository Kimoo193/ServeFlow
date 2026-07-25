using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddComboProvenanceToOrderLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "combo_id",
                table: "order_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "combo_instance_id",
                table: "order_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "combo_name",
                table: "order_lines",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_tenant_id_combo_instance_id",
                table: "order_lines",
                columns: new[] { "tenant_id", "combo_instance_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_order_lines_tenant_id_combo_instance_id",
                table: "order_lines");

            migrationBuilder.DropColumn(
                name: "combo_id",
                table: "order_lines");

            migrationBuilder.DropColumn(
                name: "combo_instance_id",
                table: "order_lines");

            migrationBuilder.DropColumn(
                name: "combo_name",
                table: "order_lines");
        }
    }
}
