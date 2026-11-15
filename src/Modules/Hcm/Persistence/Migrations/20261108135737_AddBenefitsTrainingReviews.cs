using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // Generated EF migration array arguments

namespace Erp.Modules.Hcm.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBenefitsTrainingReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "employee_benefits",
                schema: "hcm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BenefitName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Provider = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    EnrollmentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_benefits", x => x.Id);
                    table.CheckConstraint("CK_hcm_employee_benefits_end_date", "\"EndDate\" IS NULL OR \"EndDate\" >= \"EnrollmentDate\"");
                    table.ForeignKey(
                        name: "FK_employee_benefits_employees_TenantId_CompanyId_EmployeeId",
                        columns: x => new { x.TenantId, x.CompanyId, x.EmployeeId },
                        principalSchema: "hcm",
                        principalTable: "employees",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "performance_reviews",
                schema: "hcm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ReviewerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    Summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Strengths = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    DevelopmentGoals = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_performance_reviews", x => x.Id);
                    table.CheckConstraint("CK_hcm_performance_reviews_period", "\"PeriodStart\" <= \"PeriodEnd\"");
                    table.ForeignKey(
                        name: "FK_performance_reviews_employees_TenantId_CompanyId_EmployeeId",
                        columns: x => new { x.TenantId, x.CompanyId, x.EmployeeId },
                        principalSchema: "hcm",
                        principalTable: "employees",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "training_records",
                schema: "hcm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Provider = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CompletedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CertificateReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_training_records", x => x.Id);
                    table.CheckConstraint("CK_hcm_training_records_expiry", "\"ExpiresOn\" IS NULL OR \"ExpiresOn\" >= \"CompletedOn\"");
                    table.ForeignKey(
                        name: "FK_training_records_employees_TenantId_CompanyId_EmployeeId",
                        columns: x => new { x.TenantId, x.CompanyId, x.EmployeeId },
                        principalSchema: "hcm",
                        principalTable: "employees",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_employee_benefits_TenantId_CompanyId_EmployeeId_EnrollmentD~",
                schema: "hcm",
                table: "employee_benefits",
                columns: new[] { "TenantId", "CompanyId", "EmployeeId", "EnrollmentDate" });

            migrationBuilder.CreateIndex(
                name: "IX_performance_reviews_TenantId_CompanyId_EmployeeId_PeriodSta~",
                schema: "hcm",
                table: "performance_reviews",
                columns: new[] { "TenantId", "CompanyId", "EmployeeId", "PeriodStart", "PeriodEnd" });

            migrationBuilder.CreateIndex(
                name: "IX_training_records_TenantId_CompanyId_EmployeeId_CompletedOn",
                schema: "hcm",
                table: "training_records",
                columns: new[] { "TenantId", "CompanyId", "EmployeeId", "CompletedOn" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "employee_benefits",
                schema: "hcm");

            migrationBuilder.DropTable(
                name: "performance_reviews",
                schema: "hcm");

            migrationBuilder.DropTable(
                name: "training_records",
                schema: "hcm");
        }
    }
}
