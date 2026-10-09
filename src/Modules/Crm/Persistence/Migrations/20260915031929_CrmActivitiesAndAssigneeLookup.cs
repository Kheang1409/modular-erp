using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace Erp.Modules.Crm.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CrmActivitiesAndAssigneeLookup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_opportunities_TenantId_CompanyId_Id",
                schema: "crm",
                table: "opportunities",
                columns: new[] { "TenantId", "CompanyId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_leads_TenantId_CompanyId_Id",
                schema: "crm",
                table: "leads",
                columns: new[] { "TenantId", "CompanyId", "Id" });

            migrationBuilder.CreateTable(
                name: "activities",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeadId = table.Column<Guid>(type: "uuid", nullable: true),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Details = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    DueAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AssignedUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_activities", x => x.Id);
                    table.CheckConstraint("CK_activities_Parent", "(\"LeadId\" IS NOT NULL)::int + (\"OpportunityId\" IS NOT NULL)::int = 1");
                    table.ForeignKey(
                        name: "FK_activities_leads_TenantId_CompanyId_LeadId",
                        columns: x => new { x.TenantId, x.CompanyId, x.LeadId },
                        principalSchema: "crm",
                        principalTable: "leads",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_activities_opportunities_TenantId_CompanyId_OpportunityId",
                        columns: x => new { x.TenantId, x.CompanyId, x.OpportunityId },
                        principalSchema: "crm",
                        principalTable: "opportunities",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_activities_TenantId_CompanyId_LeadId_CreatedAtUtc",
                schema: "crm",
                table: "activities",
                columns: new[] { "TenantId", "CompanyId", "LeadId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_activities_TenantId_CompanyId_OpportunityId_CreatedAtUtc",
                schema: "crm",
                table: "activities",
                columns: new[] { "TenantId", "CompanyId", "OpportunityId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "activities",
                schema: "crm");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_opportunities_TenantId_CompanyId_Id",
                schema: "crm",
                table: "opportunities");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_leads_TenantId_CompanyId_Id",
                schema: "crm",
                table: "leads");
        }
    }
}
