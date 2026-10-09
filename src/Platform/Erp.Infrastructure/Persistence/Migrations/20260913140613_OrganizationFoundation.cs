using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrganizationFoundation : Migration
    {
        private static readonly string[] TenantIdAndId = ["TenantId", "Id"];
        private static readonly string[] TenantIdAndCode = ["TenantId", "Code"];
        private static readonly string[] TenantCompanyAndCode = ["TenantId", "CompanyId", "Code"];
        private static readonly string[] TenantAndCompany = ["TenantId", "CompanyId"];
        private static readonly string[] UserAndCompany = ["UserId", "CompanyId"];
        private static readonly string[] TenantIdAndName = ["TenantId", "Name"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_companies_TenantId_Name",
                schema: "platform",
                table: "companies");

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                schema: "platform",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Slug",
                schema: "platform",
                table: "tenants",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                schema: "platform",
                table: "tenants",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "BaseCurrencyCode",
                schema: "platform",
                table: "companies",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Code",
                schema: "platform",
                table: "companies",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                schema: "platform",
                table: "companies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                schema: "platform",
                table: "companies",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(
                """
                UPDATE platform.tenants
                SET "Slug" = 'tenant-' || replace("Id"::text, '-', ''),
                    "TimeZoneId" = 'UTC',
                    "IsActive" = TRUE;

                UPDATE platform.companies
                SET "Code" = 'C-' || upper(substr(replace("Id"::text, '-', ''), 1, 12)),
                    "BaseCurrencyCode" = 'XXX',
                    "TimeZoneId" = 'UTC',
                    "IsActive" = TRUE;
                """);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_companies_TenantId_Id",
                schema: "platform",
                table: "companies",
                columns: TenantIdAndId);

            migrationBuilder.CreateTable(
                name: "branches",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_branches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_branches_companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "platform",
                        principalTable: "companies",
                        principalColumns: TenantIdAndId,
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_company_memberships",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_company_memberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_company_memberships_companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "platform",
                        principalTable: "companies",
                        principalColumns: TenantIdAndId,
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_company_memberships_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "platform",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tenants_Slug",
                schema: "platform",
                table: "tenants",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_companies_TenantId_Code",
                schema: "platform",
                table: "companies",
                columns: TenantIdAndCode,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_branches_TenantId_CompanyId_Code",
                schema: "platform",
                table: "branches",
                columns: TenantCompanyAndCode,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_company_memberships_TenantId_CompanyId",
                schema: "platform",
                table: "user_company_memberships",
                columns: TenantAndCompany);

            migrationBuilder.CreateIndex(
                name: "IX_user_company_memberships_UserId_CompanyId",
                schema: "platform",
                table: "user_company_memberships",
                columns: UserAndCompany,
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_companies_tenants_TenantId",
                schema: "platform",
                table: "companies",
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
                name: "FK_companies_tenants_TenantId",
                schema: "platform",
                table: "companies");

            migrationBuilder.DropTable(
                name: "branches",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "user_company_memberships",
                schema: "platform");

            migrationBuilder.DropIndex(
                name: "IX_tenants_Slug",
                schema: "platform",
                table: "tenants");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_companies_TenantId_Id",
                schema: "platform",
                table: "companies");

            migrationBuilder.DropIndex(
                name: "IX_companies_TenantId_Code",
                schema: "platform",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "IsActive",
                schema: "platform",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "Slug",
                schema: "platform",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                schema: "platform",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "BaseCurrencyCode",
                schema: "platform",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "Code",
                schema: "platform",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "IsActive",
                schema: "platform",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                schema: "platform",
                table: "companies");

            migrationBuilder.CreateIndex(
                name: "IX_companies_TenantId_Name",
                schema: "platform",
                table: "companies",
                columns: TenantIdAndName,
                unique: true);
        }
    }
}
