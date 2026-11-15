using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace Erp.Modules.Hcm.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PayrollAdjustments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payroll_adjustments",
                schema: "hcm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayrollRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalPeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    OriginalPeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    EmployeeNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EmployeeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Category = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CurrencyCode = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    EvidenceReference = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    SnapshotRevision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PostingIdempotencyKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PostingError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AccountingJournalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    AccountingJournalNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PostedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_adjustments", x => x.Id);
                    table.UniqueConstraint("AK_payroll_adjustments_TenantId_CompanyId_Id", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.CheckConstraint("CK_hcm_payroll_adjustments_amount", "\"Amount\" <> 0");
                    table.CheckConstraint("CK_hcm_payroll_adjustments_effective_date", "\"EffectiveDate\" >= \"OriginalPeriodStart\" AND \"EffectiveDate\" <= \"OriginalPeriodEnd\"");
                    table.ForeignKey(
                        name: "FK_payroll_adjustments_employees_TenantId_CompanyId_EmployeeId",
                        columns: x => new { x.TenantId, x.CompanyId, x.EmployeeId },
                        principalSchema: "hcm",
                        principalTable: "employees",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_payroll_adjustments_payroll_runs_TenantId_CompanyId_Payroll~",
                        columns: x => new { x.TenantId, x.CompanyId, x.PayrollRunId },
                        principalSchema: "hcm",
                        principalTable: "payroll_runs",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payroll_adjustment_events",
                schema: "hcm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayrollAdjustmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    SnapshotRevision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_adjustment_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_payroll_adjustment_events_payroll_adjustments_TenantId_Comp~",
                        columns: x => new { x.TenantId, x.CompanyId, x.PayrollAdjustmentId },
                        principalSchema: "hcm",
                        principalTable: "payroll_adjustments",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_adjustment_events_TenantId_CompanyId_PayrollAdjustm~",
                schema: "hcm",
                table: "payroll_adjustment_events",
                columns: new[] { "TenantId", "CompanyId", "PayrollAdjustmentId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_adjustments_TenantId_CompanyId_AccountingJournalEnt~",
                schema: "hcm",
                table: "payroll_adjustments",
                columns: new[] { "TenantId", "CompanyId", "AccountingJournalEntryId" },
                unique: true,
                filter: "\"AccountingJournalEntryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_payroll_adjustments_TenantId_CompanyId_EmployeeId",
                schema: "hcm",
                table: "payroll_adjustments",
                columns: new[] { "TenantId", "CompanyId", "EmployeeId" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_adjustments_TenantId_CompanyId_IdempotencyKey",
                schema: "hcm",
                table: "payroll_adjustments",
                columns: new[] { "TenantId", "CompanyId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payroll_adjustments_TenantId_CompanyId_PayrollRunId_Employe~",
                schema: "hcm",
                table: "payroll_adjustments",
                columns: new[] { "TenantId", "CompanyId", "PayrollRunId", "EmployeeId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payroll_adjustment_events",
                schema: "hcm");

            migrationBuilder.DropTable(
                name: "payroll_adjustments",
                schema: "hcm");
        }
    }
}
#pragma warning restore CA1861
