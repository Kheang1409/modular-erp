using System;
using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861 // EF generates constant index column arrays.

#nullable disable

namespace Erp.Modules.Purchasing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SupplierContractsAndEvaluation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "supplier_contracts",
                schema: "purchasing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ContractNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    EndsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    ContractValue = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    CurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TerminatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_contracts", x => x.Id);
                    table.CheckConstraint("CK_supplier_contracts_dates", "\"StartsOn\" <= \"EndsOn\"");
                    table.CheckConstraint("CK_supplier_contracts_value_currency", "(\"ContractValue\" IS NULL AND \"CurrencyCode\" IS NULL) OR (\"ContractValue\" IS NOT NULL AND \"ContractValue\" >= 0 AND \"CurrencyCode\" IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "supplier_evaluations",
                schema: "purchasing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AssessedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    QualityScore = table.Column<int>(type: "integer", nullable: false),
                    DeliveryScore = table.Column<int>(type: "integer", nullable: false),
                    ResponsivenessScore = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_evaluations", x => x.Id);
                    table.CheckConstraint("CK_supplier_evaluations_scores", "\"QualityScore\" BETWEEN 1 AND 5 AND \"DeliveryScore\" BETWEEN 1 AND 5 AND \"ResponsivenessScore\" BETWEEN 1 AND 5");
                });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_contracts_TenantId_CompanyId_State_EndsOn",
                schema: "purchasing",
                table: "supplier_contracts",
                columns: new[] { "TenantId", "CompanyId", "State", "EndsOn" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_contracts_TenantId_CompanyId_SupplierId_ContractNu~",
                schema: "purchasing",
                table: "supplier_contracts",
                columns: new[] { "TenantId", "CompanyId", "SupplierId", "ContractNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_evaluations_TenantId_CompanyId_SupplierId_Assessed~",
                schema: "purchasing",
                table: "supplier_evaluations",
                columns: new[] { "TenantId", "CompanyId", "SupplierId", "AssessedOn" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "supplier_contracts",
                schema: "purchasing");

            migrationBuilder.DropTable(
                name: "supplier_evaluations",
                schema: "purchasing");
        }
    }
}
