using System;
using Microsoft.EntityFrameworkCore.Migrations;
#pragma warning disable CA1861

#nullable disable

namespace Erp.Modules.Hcm.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PayrollPeriodsAndFinalizedAttendance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_payroll_rule_configurations_TenantId_CompanyId_Jurisdictio~1",
                schema: "hcm",
                table: "payroll_rule_configurations");

            migrationBuilder.DropIndex(
                name: "IX_payroll_rule_configurations_TenantId_CompanyId_Jurisdiction~",
                schema: "hcm",
                table: "payroll_rule_configurations");

            migrationBuilder.DropIndex(
                name: "IX_employee_compensations_TenantId_CompanyId_EmployeeId_Effec~1",
                schema: "hcm",
                table: "employee_compensations");

            migrationBuilder.DropIndex(
                name: "IX_employee_compensations_TenantId_CompanyId_EmployeeId_Effect~",
                schema: "hcm",
                table: "employee_compensations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_hcm_attendance_checkout",
                schema: "hcm",
                table: "attendance_records");

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                schema: "hcm",
                table: "payroll_runs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PayrollPeriodId",
                schema: "hcm",
                table: "payroll_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PeriodType",
                schema: "hcm",
                table: "payroll_rule_configurations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "AttendanceHours",
                schema: "hcm",
                table: "payroll_lines",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "CalculationDetailsJson",
                schema: "hcm",
                table: "payroll_lines",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PeriodType",
                schema: "hcm",
                table: "employee_compensations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "CheckedInAtUtc",
                schema: "hcm",
                table: "attendance_records",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            migrationBuilder.AddColumn<string>(
                name: "ApprovalStatus",
                schema: "hcm",
                table: "attendance_records",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "ApprovedOvertimeHours",
                schema: "hcm",
                table: "attendance_records",
                type: "numeric(8,2)",
                precision: 8,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Disposition",
                schema: "hcm",
                table: "attendance_records",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FinalizedAtUtc",
                schema: "hcm",
                table: "attendance_records",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FinalizedBy",
                schema: "hcm",
                table: "attendance_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "payroll_periods",
                schema: "hcm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PayDate = table.Column<DateOnly>(type: "date", nullable: false),
                    JurisdictionCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ExpectedWorkDatesJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_periods", x => x.Id);
                    table.UniqueConstraint("AK_payroll_periods_TenantId_CompanyId_Id", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.CheckConstraint("CK_hcm_payroll_period_dates", "\"StartDate\" <= \"EndDate\"");
                    table.CheckConstraint("CK_hcm_payroll_period_pay_date", "\"PayDate\" >= \"EndDate\"");
                });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_runs_TenantId_CompanyId_IdempotencyKey",
                schema: "hcm",
                table: "payroll_runs",
                columns: new[] { "TenantId", "CompanyId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payroll_runs_TenantId_CompanyId_PayrollPeriodId",
                schema: "hcm",
                table: "payroll_runs",
                columns: new[] { "TenantId", "CompanyId", "PayrollPeriodId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payroll_rule_configurations_TenantId_CompanyId_Jurisdictio~1",
                schema: "hcm",
                table: "payroll_rule_configurations",
                columns: new[] { "TenantId", "CompanyId", "JurisdictionCode", "Version", "PeriodType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payroll_rule_configurations_TenantId_CompanyId_Jurisdiction~",
                schema: "hcm",
                table: "payroll_rule_configurations",
                columns: new[] { "TenantId", "CompanyId", "JurisdictionCode", "PeriodType", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_compensations_TenantId_CompanyId_EmployeeId_Perio~1",
                schema: "hcm",
                table: "employee_compensations",
                columns: new[] { "TenantId", "CompanyId", "EmployeeId", "PeriodType", "EffectiveTo" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_compensations_TenantId_CompanyId_EmployeeId_Period~",
                schema: "hcm",
                table: "employee_compensations",
                columns: new[] { "TenantId", "CompanyId", "EmployeeId", "PeriodType", "EffectiveFrom" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_hcm_attendance_checkout",
                schema: "hcm",
                table: "attendance_records",
                sql: "\"CheckedInAtUtc\" IS NULL OR \"CheckedOutAtUtc\" IS NULL OR \"CheckedOutAtUtc\" >= \"CheckedInAtUtc\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_hcm_attendance_finalized",
                schema: "hcm",
                table: "attendance_records",
                sql: "\"ApprovalStatus\" <> 'Finalized' OR (\"Disposition\" IS NOT NULL AND \"FinalizedAtUtc\" IS NOT NULL AND (\"Disposition\" <> 'Worked' OR (\"CheckedInAtUtc\" IS NOT NULL AND \"CheckedOutAtUtc\" IS NOT NULL)))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_hcm_attendance_overtime",
                schema: "hcm",
                table: "attendance_records",
                sql: "\"ApprovedOvertimeHours\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_payroll_periods_TenantId_CompanyId_Number",
                schema: "hcm",
                table: "payroll_periods",
                columns: new[] { "TenantId", "CompanyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payroll_periods_TenantId_CompanyId_StartDate_EndDate",
                schema: "hcm",
                table: "payroll_periods",
                columns: new[] { "TenantId", "CompanyId", "StartDate", "EndDate" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_payroll_runs_payroll_periods_TenantId_CompanyId_PayrollPeri~",
                schema: "hcm",
                table: "payroll_runs",
                columns: new[] { "TenantId", "CompanyId", "PayrollPeriodId" },
                principalSchema: "hcm",
                principalTable: "payroll_periods",
                principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_payroll_runs_payroll_periods_TenantId_CompanyId_PayrollPeri~",
                schema: "hcm",
                table: "payroll_runs");

            migrationBuilder.DropTable(
                name: "payroll_periods",
                schema: "hcm");

            migrationBuilder.DropIndex(
                name: "IX_payroll_runs_TenantId_CompanyId_IdempotencyKey",
                schema: "hcm",
                table: "payroll_runs");

            migrationBuilder.DropIndex(
                name: "IX_payroll_runs_TenantId_CompanyId_PayrollPeriodId",
                schema: "hcm",
                table: "payroll_runs");

            migrationBuilder.DropIndex(
                name: "IX_payroll_rule_configurations_TenantId_CompanyId_Jurisdictio~1",
                schema: "hcm",
                table: "payroll_rule_configurations");

            migrationBuilder.DropIndex(
                name: "IX_payroll_rule_configurations_TenantId_CompanyId_Jurisdiction~",
                schema: "hcm",
                table: "payroll_rule_configurations");

            migrationBuilder.DropIndex(
                name: "IX_employee_compensations_TenantId_CompanyId_EmployeeId_Perio~1",
                schema: "hcm",
                table: "employee_compensations");

            migrationBuilder.DropIndex(
                name: "IX_employee_compensations_TenantId_CompanyId_EmployeeId_Period~",
                schema: "hcm",
                table: "employee_compensations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_hcm_attendance_checkout",
                schema: "hcm",
                table: "attendance_records");

            migrationBuilder.DropCheckConstraint(
                name: "CK_hcm_attendance_finalized",
                schema: "hcm",
                table: "attendance_records");

            migrationBuilder.DropCheckConstraint(
                name: "CK_hcm_attendance_overtime",
                schema: "hcm",
                table: "attendance_records");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                schema: "hcm",
                table: "payroll_runs");

            migrationBuilder.DropColumn(
                name: "PayrollPeriodId",
                schema: "hcm",
                table: "payroll_runs");

            migrationBuilder.DropColumn(
                name: "PeriodType",
                schema: "hcm",
                table: "payroll_rule_configurations");

            migrationBuilder.DropColumn(
                name: "AttendanceHours",
                schema: "hcm",
                table: "payroll_lines");

            migrationBuilder.DropColumn(
                name: "CalculationDetailsJson",
                schema: "hcm",
                table: "payroll_lines");

            migrationBuilder.DropColumn(
                name: "PeriodType",
                schema: "hcm",
                table: "employee_compensations");

            migrationBuilder.DropColumn(
                name: "ApprovalStatus",
                schema: "hcm",
                table: "attendance_records");

            migrationBuilder.DropColumn(
                name: "ApprovedOvertimeHours",
                schema: "hcm",
                table: "attendance_records");

            migrationBuilder.DropColumn(
                name: "Disposition",
                schema: "hcm",
                table: "attendance_records");

            migrationBuilder.DropColumn(
                name: "FinalizedAtUtc",
                schema: "hcm",
                table: "attendance_records");

            migrationBuilder.DropColumn(
                name: "FinalizedBy",
                schema: "hcm",
                table: "attendance_records");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "CheckedInAtUtc",
                schema: "hcm",
                table: "attendance_records",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_payroll_rule_configurations_TenantId_CompanyId_Jurisdictio~1",
                schema: "hcm",
                table: "payroll_rule_configurations",
                columns: new[] { "TenantId", "CompanyId", "JurisdictionCode", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payroll_rule_configurations_TenantId_CompanyId_Jurisdiction~",
                schema: "hcm",
                table: "payroll_rule_configurations",
                columns: new[] { "TenantId", "CompanyId", "JurisdictionCode", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_compensations_TenantId_CompanyId_EmployeeId_Effec~1",
                schema: "hcm",
                table: "employee_compensations",
                columns: new[] { "TenantId", "CompanyId", "EmployeeId", "EffectiveTo" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_compensations_TenantId_CompanyId_EmployeeId_Effect~",
                schema: "hcm",
                table: "employee_compensations",
                columns: new[] { "TenantId", "CompanyId", "EmployeeId", "EffectiveFrom" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_hcm_attendance_checkout",
                schema: "hcm",
                table: "attendance_records",
                sql: "\"CheckedOutAtUtc\" IS NULL OR \"CheckedOutAtUtc\" >= \"CheckedInAtUtc\"");
        }
    }
}


