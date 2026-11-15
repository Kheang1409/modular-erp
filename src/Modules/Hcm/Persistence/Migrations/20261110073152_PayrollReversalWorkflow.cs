using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace Erp.Modules.Hcm.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PayrollReversalWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReversalIdempotencyKey",
                schema: "hcm",
                table: "payroll_runs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReversalJournalEntryId",
                schema: "hcm",
                table: "payroll_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReversalJournalNumber",
                schema: "hcm",
                table: "payroll_runs",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReversalReason",
                schema: "hcm",
                table: "payroll_runs",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReversedAtUtc",
                schema: "hcm",
                table: "payroll_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReversedByUserId",
                schema: "hcm",
                table: "payroll_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_payroll_runs_TenantId_CompanyId_ReversalIdempotencyKey",
                schema: "hcm",
                table: "payroll_runs",
                columns: new[] { "TenantId", "CompanyId", "ReversalIdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payroll_runs_TenantId_CompanyId_ReversalJournalEntryId",
                schema: "hcm",
                table: "payroll_runs",
                columns: new[] { "TenantId", "CompanyId", "ReversalJournalEntryId" },
                unique: true,
                filter: "\"ReversalJournalEntryId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_payroll_runs_TenantId_CompanyId_ReversalIdempotencyKey",
                schema: "hcm",
                table: "payroll_runs");

            migrationBuilder.DropIndex(
                name: "IX_payroll_runs_TenantId_CompanyId_ReversalJournalEntryId",
                schema: "hcm",
                table: "payroll_runs");

            migrationBuilder.DropColumn(
                name: "ReversalIdempotencyKey",
                schema: "hcm",
                table: "payroll_runs");

            migrationBuilder.DropColumn(
                name: "ReversalJournalEntryId",
                schema: "hcm",
                table: "payroll_runs");

            migrationBuilder.DropColumn(
                name: "ReversalJournalNumber",
                schema: "hcm",
                table: "payroll_runs");

            migrationBuilder.DropColumn(
                name: "ReversalReason",
                schema: "hcm",
                table: "payroll_runs");

            migrationBuilder.DropColumn(
                name: "ReversedAtUtc",
                schema: "hcm",
                table: "payroll_runs");

            migrationBuilder.DropColumn(
                name: "ReversedByUserId",
                schema: "hcm",
                table: "payroll_runs");
        }
    }
}
