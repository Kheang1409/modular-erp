using System;
using Microsoft.EntityFrameworkCore.Migrations;
#pragma warning disable CA1861

#nullable disable

namespace Erp.Modules.Hcm.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PayrollApprovalActors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByUserId",
                schema: "hcm",
                table: "payroll_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ActorUserId",
                schema: "hcm",
                table: "payroll_approval_events",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                schema: "hcm",
                table: "payroll_runs");

            migrationBuilder.DropColumn(
                name: "ActorUserId",
                schema: "hcm",
                table: "payroll_approval_events");
        }
    }
}

