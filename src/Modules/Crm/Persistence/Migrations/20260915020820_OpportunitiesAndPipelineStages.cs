using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // Generated EF migration array arguments

namespace Erp.Modules.Crm.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OpportunitiesAndPipelineStages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pipeline_stages",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    DefaultProbability = table.Column<int>(type: "integer", nullable: false),
                    IsWon = table.Column<bool>(type: "boolean", nullable: false),
                    IsLost = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pipeline_stages", x => x.Id);
                    table.UniqueConstraint("AK_pipeline_stages_TenantId_CompanyId_Id", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.CheckConstraint("CK_pipeline_stages_Probability", "\"DefaultProbability\" BETWEEN 0 AND 100");
                    table.CheckConstraint("CK_pipeline_stages_Terminal", "NOT (\"IsWon\" AND \"IsLost\")");
                });

            migrationBuilder.CreateTable(
                name: "opportunities",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceLeadId = table.Column<Guid>(type: "uuid", nullable: true),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: true),
                    StageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
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
                    table.PrimaryKey("PK_opportunities", x => x.Id);
                    table.CheckConstraint("CK_opportunities_LostReason", "\"State\" <> 'Lost' OR \"LostReason\" IS NOT NULL");
                    table.CheckConstraint("CK_opportunities_Probability", "\"Probability\" BETWEEN 0 AND 100");
                    table.CheckConstraint("CK_opportunities_Revenue", "\"ExpectedRevenue\" >= 0");
                    table.ForeignKey(
                        name: "FK_opportunities_pipeline_stages_TenantId_CompanyId_StageId",
                        columns: x => new { x.TenantId, x.CompanyId, x.StageId },
                        principalSchema: "crm",
                        principalTable: "pipeline_stages",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_opportunities_TenantId_CompanyId_ContactId",
                schema: "crm",
                table: "opportunities",
                columns: new[] { "TenantId", "CompanyId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_opportunities_TenantId_CompanyId_SourceLeadId",
                schema: "crm",
                table: "opportunities",
                columns: new[] { "TenantId", "CompanyId", "SourceLeadId" },
                unique: true,
                filter: "\"SourceLeadId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_opportunities_TenantId_CompanyId_StageId",
                schema: "crm",
                table: "opportunities",
                columns: new[] { "TenantId", "CompanyId", "StageId" });

            migrationBuilder.CreateIndex(
                name: "IX_opportunities_TenantId_CompanyId_State_IsArchived",
                schema: "crm",
                table: "opportunities",
                columns: new[] { "TenantId", "CompanyId", "State", "IsArchived" });

            migrationBuilder.CreateIndex(
                name: "IX_pipeline_stages_TenantId_CompanyId_Name",
                schema: "crm",
                table: "pipeline_stages",
                columns: new[] { "TenantId", "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pipeline_stages_TenantId_CompanyId_Sequence",
                schema: "crm",
                table: "pipeline_stages",
                columns: new[] { "TenantId", "CompanyId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "opportunities",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "pipeline_stages",
                schema: "crm");
        }
    }
}
