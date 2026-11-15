using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Hcm.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PayrollAdjustmentCalculationMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CalculationMode",
                schema: "hcm",
                table: "payroll_adjustments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Manual");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CalculationMode",
                schema: "hcm",
                table: "payroll_adjustments");
        }
    }
}
