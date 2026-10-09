using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional
#pragma warning disable CA1861 // Generated EF migration array arguments

namespace Erp.Modules.Products.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialProducts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "products");

            migrationBuilder.CreateTable(
                name: "units_of_measure",
                schema: "products",
                columns: table => new
                {
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DecimalPrecision = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_units_of_measure", x => x.Code);
                    table.CheckConstraint("CK_units_of_measure_Precision", "\"DecimalPrecision\" BETWEEN 0 AND 6");
                });

            migrationBuilder.CreateTable(
                name: "products",
                schema: "products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Sku = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Barcode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SalePrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Cost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    SalesUnitCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PurchaseUnitCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TracksInventory = table.Column<bool>(type: "boolean", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_products", x => x.Id);
                    table.CheckConstraint("CK_products_Prices", "\"SalePrice\" >= 0 AND \"Cost\" >= 0");
                    table.CheckConstraint("CK_products_ServiceInventory", "\"Type\" <> 'Service' OR NOT \"TracksInventory\"");
                    table.ForeignKey(
                        name: "FK_products_units_of_measure_PurchaseUnitCode",
                        column: x => x.PurchaseUnitCode,
                        principalSchema: "products",
                        principalTable: "units_of_measure",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_products_units_of_measure_SalesUnitCode",
                        column: x => x.SalesUnitCode,
                        principalSchema: "products",
                        principalTable: "units_of_measure",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "products",
                table: "units_of_measure",
                columns: new[] { "Code", "Category", "DecimalPrecision", "IsActive", "Name" },
                values: new object[,]
                {
                    { "EA", "Count", 0, true, "Each" },
                    { "HUR", "Time", 2, true, "Hour" },
                    { "KGM", "Mass", 3, true, "Kilogram" },
                    { "MTR", "Length", 3, true, "Metre" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_products_PurchaseUnitCode",
                schema: "products",
                table: "products",
                column: "PurchaseUnitCode");

            migrationBuilder.CreateIndex(
                name: "IX_products_SalesUnitCode",
                schema: "products",
                table: "products",
                column: "SalesUnitCode");

            migrationBuilder.CreateIndex(
                name: "IX_products_TenantId_CompanyId_Barcode",
                schema: "products",
                table: "products",
                columns: new[] { "TenantId", "CompanyId", "Barcode" },
                unique: true,
                filter: "\"Barcode\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_products_TenantId_CompanyId_Name",
                schema: "products",
                table: "products",
                columns: new[] { "TenantId", "CompanyId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_products_TenantId_CompanyId_Sku",
                schema: "products",
                table: "products",
                columns: new[] { "TenantId", "CompanyId", "Sku" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "products",
                schema: "products");

            migrationBuilder.DropTable(
                name: "units_of_measure",
                schema: "products");
        }
    }
}
