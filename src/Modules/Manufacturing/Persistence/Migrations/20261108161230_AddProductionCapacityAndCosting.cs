using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace Erp.Modules.Manufacturing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductionCapacityAndCosting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_work_orders_completed_quantity",
                schema: "manufacturing",
                table: "work_orders");

            migrationBuilder.AddColumn<decimal>(
                name: "ActualLaborCost",
                schema: "manufacturing",
                table: "work_orders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ActualLaborHours",
                schema: "manufacturing",
                table: "work_orders",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ActualMaterialCost",
                schema: "manufacturing",
                table: "work_orders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ActualOverheadCost",
                schema: "manufacturing",
                table: "work_orders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CostCurrencyCode",
                schema: "manufacturing",
                table: "work_orders",
                type: "character(3)",
                fixedLength: true,
                maxLength: 3,
                nullable: false,
                defaultValue: "USD");

            migrationBuilder.AddColumn<decimal>(
                name: "PlannedLaborHours",
                schema: "manufacturing",
                table: "work_orders",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 8m);

            migrationBuilder.AddColumn<decimal>(
                name: "PlannedMaterialCost",
                schema: "manufacturing",
                table: "work_orders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalProductionCost",
                schema: "manufacturing",
                table: "work_orders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StandardUnitCost",
                schema: "manufacturing",
                table: "bill_of_material_lines",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "production_capacity_policies",
                schema: "manufacturing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    HoursPerDay = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_capacity_policies", x => x.Id);
                    table.CheckConstraint("CK_manufacturing_capacity_hours", "\"HoursPerDay\" > 0 AND \"HoursPerDay\" <= 10000");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_work_orders_actual_labor_hours",
                schema: "manufacturing",
                table: "work_orders",
                sql: "\"ActualLaborHours\" IS NULL OR \"ActualLaborHours\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_work_orders_completed_quantity",
                schema: "manufacturing",
                table: "work_orders",
                sql: "\"CompletedQuantity\" IS NULL OR \"CompletedQuantity\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_work_orders_costs_nonnegative",
                schema: "manufacturing",
                table: "work_orders",
                sql: "(\"ActualMaterialCost\" IS NULL OR \"ActualMaterialCost\" >= 0) AND (\"ActualLaborCost\" IS NULL OR \"ActualLaborCost\" >= 0) AND (\"ActualOverheadCost\" IS NULL OR \"ActualOverheadCost\" >= 0) AND (\"TotalProductionCost\" IS NULL OR \"TotalProductionCost\" >= 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_work_orders_planned_labor_hours",
                schema: "manufacturing",
                table: "work_orders",
                sql: "\"PlannedLaborHours\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_work_orders_planned_material_cost",
                schema: "manufacturing",
                table: "work_orders",
                sql: "\"PlannedMaterialCost\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_production_capacity_policies_TenantId_CompanyId",
                schema: "manufacturing",
                table: "production_capacity_policies",
                columns: new[] { "TenantId", "CompanyId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "production_capacity_policies",
                schema: "manufacturing");

            migrationBuilder.DropCheckConstraint(
                name: "CK_work_orders_actual_labor_hours",
                schema: "manufacturing",
                table: "work_orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_work_orders_completed_quantity",
                schema: "manufacturing",
                table: "work_orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_work_orders_costs_nonnegative",
                schema: "manufacturing",
                table: "work_orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_work_orders_planned_labor_hours",
                schema: "manufacturing",
                table: "work_orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_work_orders_planned_material_cost",
                schema: "manufacturing",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "ActualLaborCost",
                schema: "manufacturing",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "ActualLaborHours",
                schema: "manufacturing",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "ActualMaterialCost",
                schema: "manufacturing",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "ActualOverheadCost",
                schema: "manufacturing",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "CostCurrencyCode",
                schema: "manufacturing",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "PlannedLaborHours",
                schema: "manufacturing",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "PlannedMaterialCost",
                schema: "manufacturing",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "TotalProductionCost",
                schema: "manufacturing",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "StandardUnitCost",
                schema: "manufacturing",
                table: "bill_of_material_lines");

            migrationBuilder.AddCheckConstraint(
                name: "CK_work_orders_completed_quantity",
                schema: "manufacturing",
                table: "work_orders",
                sql: "\"CompletedQuantity\" IS NULL OR \"CompletedQuantity\">0");
        }
    }
}
