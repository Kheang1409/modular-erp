using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace Erp.Modules.Hcm.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPayrollConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "employee_compensations",
                schema: "hcm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrencyCode = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    BasePayForPeriod = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AllowancesJson = table.Column<string>(type: "jsonb", nullable: false),
                    DeductionsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_compensations", x => x.Id);
                    table.CheckConstraint("CK_hcm_compensation_base_pay", "\"BasePayForPeriod\" >= 0");
                    table.CheckConstraint("CK_hcm_compensation_effective_dates", "\"EffectiveTo\" IS NULL OR \"EffectiveTo\" >= \"EffectiveFrom\"");
                    table.ForeignKey(
                        name: "FK_employee_compensations_employees_TenantId_CompanyId_Employe~",
                        columns: x => new { x.TenantId, x.CompanyId, x.EmployeeId },
                        principalSchema: "hcm",
                        principalTable: "employees",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payroll_rule_configurations",
                schema: "hcm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    JurisdictionCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    IsSupported = table.Column<bool>(type: "boolean", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    StandardHoursPerPeriod = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    OvertimeMultiplier = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: false),
                    TaxBandsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_rule_configurations", x => x.Id);
                    table.CheckConstraint("CK_hcm_payroll_rules_effective_dates", "\"EffectiveTo\" IS NULL OR \"EffectiveTo\" >= \"EffectiveFrom\"");
                    table.CheckConstraint("CK_hcm_payroll_rules_hours", "\"StandardHoursPerPeriod\" > 0 AND \"OvertimeMultiplier\" >= 1");
                });

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "employee_compensations",
                schema: "hcm");

            migrationBuilder.DropTable(
                name: "payroll_rule_configurations",
                schema: "hcm");
        }
    }
}
