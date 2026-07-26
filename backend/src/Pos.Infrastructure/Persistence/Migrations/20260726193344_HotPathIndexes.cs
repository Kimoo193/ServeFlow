using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HotPathIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_orders_tenant_id_branch_id_order_type",
                table: "orders",
                columns: new[] { "tenant_id", "branch_id", "order_type" });

            migrationBuilder.CreateIndex(
                name: "IX_orders_tenant_id_daily_no",
                table: "orders",
                columns: new[] { "tenant_id", "daily_no" },
                filter: "status IN ('Open','InProgress')");

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_tenant_id_station_id_sequence",
                table: "order_lines",
                columns: new[] { "tenant_id", "station_id", "sequence" },
                filter: "status IN ('Fired','Preparing')");

            migrationBuilder.CreateIndex(
                name: "IX_order_events_tenant_id_event_type_performed_at",
                table: "order_events",
                columns: new[] { "tenant_id", "event_type", "performed_at" },
                descending: new[] { false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_orders_tenant_id_branch_id_order_type",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "IX_orders_tenant_id_daily_no",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "IX_order_lines_tenant_id_station_id_sequence",
                table: "order_lines");

            migrationBuilder.DropIndex(
                name: "IX_order_events_tenant_id_event_type_performed_at",
                table: "order_events");
        }
    }
}
