using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace Erp.Modules.Sales.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SalesOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sales_orders",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceQuotationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CustomerTaxNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    BillingLine1 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    BillingLine2 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    BillingCity = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    BillingStateOrProvince = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    BillingPostalCode = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    BillingCountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    ShippingLine1 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ShippingLine2 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ShippingCity = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ShippingStateOrProvince = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ShippingPostalCode = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ShippingCountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    CurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    BaseCurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ExchangeRate = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    OrderDate = table.Column<DateOnly>(type: "date", nullable: false),
                    State = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Subtotal = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    DiscountTotal = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    TaxTotal = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Total = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Terms = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_orders", x => x.Id);
                    table.UniqueConstraint("AK_sales_orders_TenantId_CompanyId_Id", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.CheckConstraint("CK_sales_orders_totals", "\"Subtotal\" >= 0 AND \"DiscountTotal\" >= 0 AND \"TaxTotal\" >= 0 AND \"Total\" >= 0 AND \"ExchangeRate\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "sales_order_lines",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductSku = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    UnitCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    DiscountPercent = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    TaxPercent = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Subtotal = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Total = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_order_lines", x => x.Id);
                    table.CheckConstraint("CK_sales_order_lines_values", "\"Quantity\" > 0 AND \"UnitPrice\" >= 0 AND \"DiscountPercent\" BETWEEN 0 AND 100 AND \"TaxPercent\" BETWEEN 0 AND 100 AND \"Total\" >= 0");
                    table.ForeignKey(
                        name: "FK_sales_order_lines_sales_orders_TenantId_CompanyId_SalesOrde~",
                        columns: x => new { x.TenantId, x.CompanyId, x.SalesOrderId },
                        principalSchema: "sales",
                        principalTable: "sales_orders",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_lines_SalesOrderId_Sequence",
                schema: "sales",
                table: "sales_order_lines",
                columns: new[] { "SalesOrderId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_order_lines_TenantId_CompanyId_SalesOrderId",
                schema: "sales",
                table: "sales_order_lines",
                columns: new[] { "TenantId", "CompanyId", "SalesOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_orders_TenantId_CompanyId_Number",
                schema: "sales",
                table: "sales_orders",
                columns: new[] { "TenantId", "CompanyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_orders_TenantId_CompanyId_SourceQuotationId",
                schema: "sales",
                table: "sales_orders",
                columns: new[] { "TenantId", "CompanyId", "SourceQuotationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_orders_TenantId_CompanyId_State_UpdatedAtUtc",
                schema: "sales",
                table: "sales_orders",
                columns: new[] { "TenantId", "CompanyId", "State", "UpdatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sales_order_lines",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "sales_orders",
                schema: "sales");
        }
    }
}
