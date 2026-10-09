using System;
using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861 // EF generates composite index column arrays.

#nullable disable

namespace Erp.Modules.Accounting.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierBills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "supplier_bills",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SupplierReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseOrderNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SupplierTaxNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    BillDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    BaseCurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ExchangeRate = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Subtotal = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    DiscountTotal = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    TaxTotal = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Total = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    AmountPaid = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    State = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_bills", x => x.Id);
                    table.UniqueConstraint("AK_supplier_bills_TenantId_CompanyId_Id", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.CheckConstraint("CK_supplier_bills_amounts", "\"Total\">=0 AND \"AmountPaid\">=0 AND \"AmountPaid\"<=\"Total\"");
                    table.CheckConstraint("CK_supplier_bills_dates", "\"DueDate\">=\"BillDate\"");
                });

            migrationBuilder.CreateTable(
                name: "supplier_bill_lines",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierBillId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductSku = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    UnitCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    DiscountPercent = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: false),
                    TaxPercent = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: false),
                    Subtotal = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Total = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_bill_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_supplier_bill_lines_supplier_bills_TenantId_CompanyId_Suppl~",
                        columns: x => new { x.TenantId, x.CompanyId, x.SupplierBillId },
                        principalSchema: "accounting",
                        principalTable: "supplier_bills",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_bill_lines_TenantId_CompanyId_SupplierBillId_Seque~",
                schema: "accounting",
                table: "supplier_bill_lines",
                columns: new[] { "TenantId", "CompanyId", "SupplierBillId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_bills_TenantId_CompanyId_IdempotencyKey",
                schema: "accounting",
                table: "supplier_bills",
                columns: new[] { "TenantId", "CompanyId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_bills_TenantId_CompanyId_Number",
                schema: "accounting",
                table: "supplier_bills",
                columns: new[] { "TenantId", "CompanyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_bills_TenantId_CompanyId_PurchaseOrderId",
                schema: "accounting",
                table: "supplier_bills",
                columns: new[] { "TenantId", "CompanyId", "PurchaseOrderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_bills_TenantId_CompanyId_SupplierId_SupplierRefere~",
                schema: "accounting",
                table: "supplier_bills",
                columns: new[] { "TenantId", "CompanyId", "SupplierId", "SupplierReference" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "supplier_bill_lines",
                schema: "accounting");

            migrationBuilder.DropTable(
                name: "supplier_bills",
                schema: "accounting");
        }
    }
}
