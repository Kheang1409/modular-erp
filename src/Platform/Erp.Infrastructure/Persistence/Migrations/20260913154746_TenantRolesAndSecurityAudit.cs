using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // Generated migration APIs require array arguments.

namespace Erp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantRolesAndSecurityAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "RoleNameIndex",
                schema: "platform",
                table: "roles");

            migrationBuilder.AddColumn<bool>(
                name: "IsSystem",
                schema: "platform",
                table: "roles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "platform",
                table: "roles",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE platform.roles AS role
                SET "TenantId" = inferred."TenantId"
                FROM (
                    SELECT assignment."RoleId", MIN(membership."TenantId"::text)::uuid AS "TenantId"
                    FROM platform.user_roles AS assignment
                    INNER JOIN platform.user_company_memberships AS membership
                        ON membership."UserId" = assignment."UserId"
                    GROUP BY assignment."RoleId"
                    HAVING COUNT(DISTINCT membership."TenantId") = 1
                ) AS inferred
                WHERE role."Id" = inferred."RoleId";

                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM platform.roles WHERE "TenantId" IS NULL) THEN
                        RAISE EXCEPTION 'Cannot safely infer a single tenant for one or more legacy roles.';
                    END IF;
                END $$;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                schema: "platform",
                table: "roles",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_roles_TenantId_Id",
                schema: "platform",
                table: "roles",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateTable(
                name: "audit_logs",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EntityId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: true),
                    BeforeJson = table.Column<string>(type: "jsonb", nullable: true),
                    AfterJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_logs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_audit_logs_companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "platform",
                        principalTable: "companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_audit_logs_tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "platform",
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "role_permissions",
                schema: "platform",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Permission = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_permissions", x => new { x.TenantId, x.RoleId, x.Permission });
                    table.ForeignKey(
                        name: "FK_role_permissions_roles_TenantId_RoleId",
                        columns: x => new { x.TenantId, x.RoleId },
                        principalSchema: "platform",
                        principalTable: "roles",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_roles_TenantId_NormalizedName",
                schema: "platform",
                table: "roles",
                columns: new[] { "TenantId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                schema: "platform",
                table: "roles",
                column: "NormalizedName");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_TenantId_CompanyId",
                schema: "platform",
                table: "audit_logs",
                columns: new[] { "TenantId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_TenantId_EntityType_EntityId",
                schema: "platform",
                table: "audit_logs",
                columns: new[] { "TenantId", "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_TenantId_TimestampUtc",
                schema: "platform",
                table: "audit_logs",
                columns: new[] { "TenantId", "TimestampUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_roles_tenants_TenantId",
                schema: "platform",
                table: "roles",
                column: "TenantId",
                principalSchema: "platform",
                principalTable: "tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_roles_tenants_TenantId",
                schema: "platform",
                table: "roles");

            migrationBuilder.DropTable(
                name: "audit_logs",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "role_permissions",
                schema: "platform");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_roles_TenantId_Id",
                schema: "platform",
                table: "roles");

            migrationBuilder.DropIndex(
                name: "IX_roles_TenantId_NormalizedName",
                schema: "platform",
                table: "roles");

            migrationBuilder.DropIndex(
                name: "RoleNameIndex",
                schema: "platform",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "IsSystem",
                schema: "platform",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "platform",
                table: "roles");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                schema: "platform",
                table: "roles",
                column: "NormalizedName",
                unique: true);
        }
    }
}
