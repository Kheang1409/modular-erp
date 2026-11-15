using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Hcm.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PayrollReversalRequestIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "ReversalBusinessDate",
                schema: "hcm",
                table: "payroll_runs",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReversalBusinessDate",
                schema: "hcm",
                table: "payroll_runs");
        }
    }
}
