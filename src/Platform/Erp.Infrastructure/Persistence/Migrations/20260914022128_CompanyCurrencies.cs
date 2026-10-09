using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF Core migration scaffolding emits constant metadata arrays.

namespace Erp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompanyCurrencies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "company_currencies",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    MinorUnits = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_company_currencies", x => x.Id);
                    table.CheckConstraint("CK_company_currencies_MinorUnits", "\"MinorUnits\" BETWEEN 0 AND 6");
                    table.ForeignKey(
                        name: "FK_company_currencies_companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "platform",
                        principalTable: "companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "company_exchange_rates",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromCurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ToCurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_company_exchange_rates", x => x.Id);
                    table.CheckConstraint("CK_company_exchange_rates_Rate", "\"Rate\" > 0");
                    table.ForeignKey(
                        name: "FK_company_exchange_rates_companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "platform",
                        principalTable: "companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO platform.company_currencies
                    ("Id", "TenantId", "CompanyId", "Code", "Name", "Symbol", "MinorUnits", "IsActive", "ConcurrencyStamp")
                SELECT gen_random_uuid(), "TenantId", "Id", upper("BaseCurrencyCode"), upper("BaseCurrencyCode"), upper("BaseCurrencyCode"), 2, TRUE,
                       md5(random()::text || clock_timestamp()::text)
                FROM platform.companies;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_company_currencies_TenantId_CompanyId_Code",
                schema: "platform",
                table: "company_currencies",
                columns: new[] { "TenantId", "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_company_exchange_rates_TenantId_CompanyId_FromCurrencyCode_~",
                schema: "platform",
                table: "company_exchange_rates",
                columns: new[] { "TenantId", "CompanyId", "FromCurrencyCode", "ToCurrencyCode", "EffectiveDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "company_currencies",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "company_exchange_rates",
                schema: "platform");
        }
    }
}
#pragma warning restore CA1861
