using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Slice7CloseAndReceipt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "discount_amount",
                table: "orders",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "service_amount",
                table: "orders",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "subtotal_amount",
                table: "orders",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "tax_amount",
                table: "orders",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "total_amount",
                table: "orders",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "printer_ip",
                table: "branch_settings",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "printer_port",
                table: "branch_settings",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "discount_amount",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "service_amount",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "subtotal_amount",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "tax_amount",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "total_amount",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "printer_ip",
                table: "branch_settings");

            migrationBuilder.DropColumn(
                name: "printer_port",
                table: "branch_settings");
        }
    }
}
