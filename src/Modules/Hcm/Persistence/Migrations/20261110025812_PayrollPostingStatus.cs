using System;
using Microsoft.EntityFrameworkCore.Migrations;
#pragma warning disable CA1861

#nullable disable

namespace Erp.Modules.Hcm.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PayrollPostingStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AccountingJournalEntryId",
                schema: "hcm",
                table: "payroll_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccountingJournalNumber",
                schema: "hcm",
                table: "payroll_runs",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PostedAtUtc",
                schema: "hcm",
                table: "payroll_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostingError",
                schema: "hcm",
                table: "payroll_runs",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccountingJournalEntryId",
                schema: "hcm",
                table: "payroll_runs");

            migrationBuilder.DropColumn(
                name: "AccountingJournalNumber",
                schema: "hcm",
                table: "payroll_runs");

            migrationBuilder.DropColumn(
                name: "PostedAtUtc",
                schema: "hcm",
                table: "payroll_runs");

            migrationBuilder.DropColumn(
                name: "PostingError",
                schema: "hcm",
                table: "payroll_runs");
        }
    }
}

