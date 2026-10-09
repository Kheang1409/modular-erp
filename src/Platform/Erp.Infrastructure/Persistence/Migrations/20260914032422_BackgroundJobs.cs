using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF Core migration scaffolding emits constant metadata arrays.

namespace Erp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackgroundJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "background_jobs",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    ExecuteAfterUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LockedUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LastError = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_background_jobs", x => x.Id);
                    table.CheckConstraint("CK_background_jobs_Attempts", "\"Attempts\" >= 0 AND \"MaxAttempts\" BETWEEN 1 AND 20");
                    table.CheckConstraint("CK_background_jobs_State", "\"State\" IN ('queued', 'running', 'succeeded', 'dead_letter')");
                    table.ForeignKey(
                        name: "FK_background_jobs_companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "platform",
                        principalTable: "companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_background_jobs_State_ExecuteAfterUtc",
                schema: "platform",
                table: "background_jobs",
                columns: new[] { "State", "ExecuteAfterUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_background_jobs_TenantId_CompanyId_Name_IdempotencyKey",
                schema: "platform",
                table: "background_jobs",
                columns: new[] { "TenantId", "CompanyId", "Name", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "background_jobs",
                schema: "platform");
        }
    }
}
#pragma warning restore CA1861
