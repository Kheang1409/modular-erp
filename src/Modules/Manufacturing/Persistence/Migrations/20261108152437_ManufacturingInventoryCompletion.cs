using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Manufacturing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManufacturingInventoryCompletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CompletedQuantity",
                schema: "manufacturing",
                table: "work_orders",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InventoryLocationId",
                schema: "manufacturing",
                table: "work_orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_work_orders_completed_quantity",
                schema: "manufacturing",
                table: "work_orders",
                sql: "\"CompletedQuantity\" IS NULL OR \"CompletedQuantity\" > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_work_orders_completed_quantity",
                schema: "manufacturing",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "CompletedQuantity",
                schema: "manufacturing",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "InventoryLocationId",
                schema: "manufacturing",
                table: "work_orders");
        }
    }
}
