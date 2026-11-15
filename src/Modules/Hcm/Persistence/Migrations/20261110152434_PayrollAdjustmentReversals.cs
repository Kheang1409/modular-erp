using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace Erp.Modules.Hcm.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PayrollAdjustmentReversals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "ReversalBusinessDate",
                schema: "hcm",
                table: "payroll_adjustments",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReversalIdempotencyKey",
                schema: "hcm",
                table: "payroll_adjustments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReversalJournalEntryId",
                schema: "hcm",
                table: "payroll_adjustments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReversalJournalNumber",
                schema: "hcm",
                table: "payroll_adjustments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReversalReason",
                schema: "hcm",
                table: "payroll_adjustments",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_payroll_adjustments_TenantId_CompanyId_ReversalIdempotencyK~",
                schema: "hcm",
                table: "payroll_adjustments",
                columns: new[] { "TenantId", "CompanyId", "ReversalIdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payroll_adjustments_TenantId_CompanyId_ReversalJournalEntry~",
                schema: "hcm",
                table: "payroll_adjustments",
                columns: new[] { "TenantId", "CompanyId", "ReversalJournalEntryId" },
                unique: true,
                filter: "\"ReversalJournalEntryId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_payroll_adjustments_TenantId_CompanyId_ReversalIdempotencyK~",
                schema: "hcm",
                table: "payroll_adjustments");

            migrationBuilder.DropIndex(
                name: "IX_payroll_adjustments_TenantId_CompanyId_ReversalJournalEntry~",
                schema: "hcm",
                table: "payroll_adjustments");

            migrationBuilder.DropColumn(
                name: "ReversalBusinessDate",
                schema: "hcm",
                table: "payroll_adjustments");

            migrationBuilder.DropColumn(
                name: "ReversalIdempotencyKey",
                schema: "hcm",
                table: "payroll_adjustments");

            migrationBuilder.DropColumn(
                name: "ReversalJournalEntryId",
                schema: "hcm",
                table: "payroll_adjustments");

            migrationBuilder.DropColumn(
                name: "ReversalJournalNumber",
                schema: "hcm",
                table: "payroll_adjustments");

            migrationBuilder.DropColumn(
                name: "ReversalReason",
                schema: "hcm",
                table: "payroll_adjustments");
        }
    }
}
#pragma warning restore CA1861
