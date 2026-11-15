using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace Erp.Modules.Hcm.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PayrollCalculationSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CalculationRuleVersion",
                schema: "hcm",
                table: "payroll_runs",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CalculationSnapshotJson",
                schema: "hcm",
                table: "payroll_runs",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CalculationRuleVersion",
                schema: "hcm",
                table: "payroll_runs");

            migrationBuilder.DropColumn(
                name: "CalculationSnapshotJson",
                schema: "hcm",
                table: "payroll_runs");
        }
    }
}
