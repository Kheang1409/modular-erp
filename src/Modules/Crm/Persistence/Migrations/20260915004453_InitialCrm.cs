using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // Generated EF migration array arguments

namespace Erp.Modules.Crm.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCrm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "crm");

            migrationBuilder.CreateTable(
                name: "leads",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: true),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExpectedRevenue = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Probability = table.Column<int>(type: "integer", nullable: false),
                    AssignedUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlannedCloseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LostReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leads", x => x.Id);
                    table.CheckConstraint("CK_leads_LostReason", "\"State\" <> 'Lost' OR \"LostReason\" IS NOT NULL");
                    table.CheckConstraint("CK_leads_Probability", "\"Probability\" BETWEEN 0 AND 100");
                    table.CheckConstraint("CK_leads_Revenue", "\"ExpectedRevenue\" >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_leads_TenantId_CompanyId_AssignedUserId",
                schema: "crm",
                table: "leads",
                columns: new[] { "TenantId", "CompanyId", "AssignedUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_leads_TenantId_CompanyId_ContactId",
                schema: "crm",
                table: "leads",
                columns: new[] { "TenantId", "CompanyId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_leads_TenantId_CompanyId_State_IsArchived",
                schema: "crm",
                table: "leads",
                columns: new[] { "TenantId", "CompanyId", "State", "IsArchived" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "leads",
                schema: "crm");
        }
    }
}
