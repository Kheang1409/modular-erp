using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Hcm.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PayrollProrationAndEmploymentDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CurrencyPrecision",
                schema: "hcm",
                table: "payroll_rule_configurations",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<string>(
                name: "HireDateTreatment",
                schema: "hcm",
                table: "payroll_rule_configurations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Inclusive");

            migrationBuilder.AddColumn<string>(
                name: "ProrationMethod",
                schema: "hcm",
                table: "payroll_rule_configurations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "LegacyAttendanceHours");

            migrationBuilder.AddColumn<string>(
                name: "RoundingMode",
                schema: "hcm",
                table: "payroll_rule_configurations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "MidpointAwayFromZero");

            migrationBuilder.AddColumn<string>(
                name: "TerminationDateTreatment",
                schema: "hcm",
                table: "payroll_rule_configurations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Inclusive");

            migrationBuilder.AddColumn<string>(
                name: "UnpaidLeaveTreatment",
                schema: "hcm",
                table: "payroll_rule_configurations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "ContinueProration");

            migrationBuilder.AddColumn<DateOnly>(
                name: "EmploymentEndDate",
                schema: "hcm",
                table: "employees",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ScheduledHoursPerPeriod",
                schema: "hcm",
                table: "employee_compensations",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_hcm_payroll_rules_currency_precision",
                schema: "hcm",
                table: "payroll_rule_configurations",
                sql: "\"CurrencyPrecision\" BETWEEN 0 AND 4");

            migrationBuilder.CreateIndex(
                name: "IX_employees_TenantId_CompanyId_EmploymentEndDate",
                schema: "hcm",
                table: "employees",
                columns: ["TenantId", "CompanyId", "EmploymentEndDate"]);

            migrationBuilder.AddCheckConstraint(
                name: "CK_hcm_employees_employment_dates",
                schema: "hcm",
                table: "employees",
                sql: "\"EmploymentEndDate\" IS NULL OR \"EmploymentEndDate\" >= \"HireDate\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_hcm_compensation_schedule_hours",
                schema: "hcm",
                table: "employee_compensations",
                sql: "\"ScheduledHoursPerPeriod\" IS NULL OR \"ScheduledHoursPerPeriod\" > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_hcm_payroll_rules_currency_precision",
                schema: "hcm",
                table: "payroll_rule_configurations");

            migrationBuilder.DropIndex(
                name: "IX_employees_TenantId_CompanyId_EmploymentEndDate",
                schema: "hcm",
                table: "employees");

            migrationBuilder.DropCheckConstraint(
                name: "CK_hcm_employees_employment_dates",
                schema: "hcm",
                table: "employees");

            migrationBuilder.DropCheckConstraint(
                name: "CK_hcm_compensation_schedule_hours",
                schema: "hcm",
                table: "employee_compensations");

            migrationBuilder.DropColumn(
                name: "CurrencyPrecision",
                schema: "hcm",
                table: "payroll_rule_configurations");

            migrationBuilder.DropColumn(
                name: "HireDateTreatment",
                schema: "hcm",
                table: "payroll_rule_configurations");

            migrationBuilder.DropColumn(
                name: "ProrationMethod",
                schema: "hcm",
                table: "payroll_rule_configurations");

            migrationBuilder.DropColumn(
                name: "RoundingMode",
                schema: "hcm",
                table: "payroll_rule_configurations");

            migrationBuilder.DropColumn(
                name: "TerminationDateTreatment",
                schema: "hcm",
                table: "payroll_rule_configurations");

            migrationBuilder.DropColumn(
                name: "UnpaidLeaveTreatment",
                schema: "hcm",
                table: "payroll_rule_configurations");

            migrationBuilder.DropColumn(
                name: "EmploymentEndDate",
                schema: "hcm",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "ScheduledHoursPerPeriod",
                schema: "hcm",
                table: "employee_compensations");
        }
    }
}
