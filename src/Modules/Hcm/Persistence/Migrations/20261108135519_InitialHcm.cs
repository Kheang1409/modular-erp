using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // Generated EF migration array arguments

namespace Erp.Modules.Hcm.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialHcm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "hcm");

            migrationBuilder.CreateTable(
                name: "employees",
                schema: "hcm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FullName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    HireDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employees", x => x.Id);
                    table.UniqueConstraint("AK_employees_TenantId_CompanyId_Id", x => new { x.TenantId, x.CompanyId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "attendance_records",
                schema: "hcm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    WorkDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CheckedInAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CheckedOutAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_records", x => x.Id);
                    table.CheckConstraint("CK_hcm_attendance_checkout", "\"CheckedOutAtUtc\" IS NULL OR \"CheckedOutAtUtc\" >= \"CheckedInAtUtc\"");
                    table.ForeignKey(
                        name: "FK_attendance_records_employees_TenantId_CompanyId_EmployeeId",
                        columns: x => new { x.TenantId, x.CompanyId, x.EmployeeId },
                        principalSchema: "hcm",
                        principalTable: "employees",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "leave_requests",
                schema: "hcm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SubmittedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leave_requests", x => x.Id);
                    table.CheckConstraint("CK_hcm_leave_requests_DateRange", "\"StartDate\" <= \"EndDate\"");
                    table.ForeignKey(
                        name: "FK_leave_requests_employees_TenantId_CompanyId_EmployeeId",
                        columns: x => new { x.TenantId, x.CompanyId, x.EmployeeId },
                        principalSchema: "hcm",
                        principalTable: "employees",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_records_TenantId_CompanyId_EmployeeId_WorkDate",
                schema: "hcm",
                table: "attendance_records",
                columns: new[] { "TenantId", "CompanyId", "EmployeeId", "WorkDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attendance_records_TenantId_CompanyId_WorkDate",
                schema: "hcm",
                table: "attendance_records",
                columns: new[] { "TenantId", "CompanyId", "WorkDate" });

            migrationBuilder.CreateIndex(
                name: "IX_employees_TenantId_CompanyId_EmployeeNumber",
                schema: "hcm",
                table: "employees",
                columns: new[] { "TenantId", "CompanyId", "EmployeeNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_employees_TenantId_CompanyId_FullName",
                schema: "hcm",
                table: "employees",
                columns: new[] { "TenantId", "CompanyId", "FullName" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_requests_TenantId_CompanyId_EmployeeId_StartDate_EndD~",
                schema: "hcm",
                table: "leave_requests",
                columns: new[] { "TenantId", "CompanyId", "EmployeeId", "StartDate", "EndDate" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_requests_TenantId_CompanyId_Status_SubmittedAtUtc",
                schema: "hcm",
                table: "leave_requests",
                columns: new[] { "TenantId", "CompanyId", "Status", "SubmittedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attendance_records",
                schema: "hcm");

            migrationBuilder.DropTable(
                name: "leave_requests",
                schema: "hcm");

            migrationBuilder.DropTable(
                name: "employees",
                schema: "hcm");
        }
    }
}
