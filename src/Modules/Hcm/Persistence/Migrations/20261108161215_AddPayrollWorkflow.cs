using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace Erp.Modules.Hcm.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPayrollWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payroll_runs",
                schema: "hcm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    PayDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CurrencyCode = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_runs", x => x.Id);
                    table.UniqueConstraint("AK_payroll_runs_TenantId_CompanyId_Id", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.CheckConstraint("CK_hcm_payroll_pay_date", "\"PayDate\" >= \"PeriodEnd\"");
                    table.CheckConstraint("CK_hcm_payroll_period", "\"PeriodStart\" <= \"PeriodEnd\"");
                });

            migrationBuilder.CreateTable(
                name: "payroll_lines",
                schema: "hcm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayrollRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EmployeeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    GrossPay = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxWithheld = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    OtherDeductions = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EmployerContributions = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    NetPay = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_lines", x => x.Id);
                    table.CheckConstraint("CK_hcm_payroll_deductions", "\"OtherDeductions\" >= 0");
                    table.CheckConstraint("CK_hcm_payroll_employer_contributions", "\"EmployerContributions\" >= 0");
                    table.CheckConstraint("CK_hcm_payroll_gross", "\"GrossPay\" >= 0");
                    table.CheckConstraint("CK_hcm_payroll_net", "\"NetPay\" >= 0");
                    table.CheckConstraint("CK_hcm_payroll_tax", "\"TaxWithheld\" >= 0");
                    table.ForeignKey(
                        name: "FK_payroll_lines_employees_TenantId_CompanyId_EmployeeId",
                        columns: x => new { x.TenantId, x.CompanyId, x.EmployeeId },
                        principalSchema: "hcm",
                        principalTable: "employees",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_payroll_lines_payroll_runs_TenantId_CompanyId_PayrollRunId",
                        columns: x => new { x.TenantId, x.CompanyId, x.PayrollRunId },
                        principalSchema: "hcm",
                        principalTable: "payroll_runs",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_lines_TenantId_CompanyId_EmployeeId",
                schema: "hcm",
                table: "payroll_lines",
                columns: new[] { "TenantId", "CompanyId", "EmployeeId" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_lines_TenantId_CompanyId_PayrollRunId_EmployeeId",
                schema: "hcm",
                table: "payroll_lines",
                columns: new[] { "TenantId", "CompanyId", "PayrollRunId", "EmployeeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payroll_runs_TenantId_CompanyId_Number",
                schema: "hcm",
                table: "payroll_runs",
                columns: new[] { "TenantId", "CompanyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payroll_runs_TenantId_CompanyId_PeriodStart_PeriodEnd",
                schema: "hcm",
                table: "payroll_runs",
                columns: new[] { "TenantId", "CompanyId", "PeriodStart", "PeriodEnd" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payroll_lines",
                schema: "hcm");

            migrationBuilder.DropTable(
                name: "payroll_runs",
                schema: "hcm");
        }
    }
}
