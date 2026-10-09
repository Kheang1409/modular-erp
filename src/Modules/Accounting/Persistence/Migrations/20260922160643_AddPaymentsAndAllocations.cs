using System;
using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861 // EF generates composite index column arrays.

#nullable disable

namespace Erp.Modules.Accounting.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentsAndAllocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payments",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Direction = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    PartyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PaymentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ExchangeRate = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversalJournalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PostingKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ReversalKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payments", x => x.Id);
                    table.UniqueConstraint("AK_payments_TenantId_CompanyId_Id", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.CheckConstraint("CK_payments_amount", "\"Amount\">0 AND \"ExchangeRate\">0");
                });

            migrationBuilder.CreateTable(
                name: "payment_allocations",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerInvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupplierBillId = table.Column<Guid>(type: "uuid", nullable: true),
                    DocumentNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_allocations", x => x.Id);
                    table.CheckConstraint("CK_payment_allocations_amount", "\"Amount\">0");
                    table.CheckConstraint("CK_payment_allocations_target", "(\"CustomerInvoiceId\" IS NOT NULL AND \"SupplierBillId\" IS NULL) OR (\"CustomerInvoiceId\" IS NULL AND \"SupplierBillId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_payment_allocations_payments_TenantId_CompanyId_PaymentId",
                        columns: x => new { x.TenantId, x.CompanyId, x.PaymentId },
                        principalSchema: "accounting",
                        principalTable: "payments",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payment_allocations_TenantId_CompanyId_PaymentId_CustomerIn~",
                schema: "accounting",
                table: "payment_allocations",
                columns: new[] { "TenantId", "CompanyId", "PaymentId", "CustomerInvoiceId" },
                unique: true,
                filter: "\"CustomerInvoiceId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_payment_allocations_TenantId_CompanyId_PaymentId_SupplierBi~",
                schema: "accounting",
                table: "payment_allocations",
                columns: new[] { "TenantId", "CompanyId", "PaymentId", "SupplierBillId" },
                unique: true,
                filter: "\"SupplierBillId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_payments_TenantId_CompanyId_IdempotencyKey",
                schema: "accounting",
                table: "payments",
                columns: new[] { "TenantId", "CompanyId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payments_TenantId_CompanyId_Number",
                schema: "accounting",
                table: "payments",
                columns: new[] { "TenantId", "CompanyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payments_TenantId_CompanyId_PostingKey",
                schema: "accounting",
                table: "payments",
                columns: new[] { "TenantId", "CompanyId", "PostingKey" },
                unique: true,
                filter: "\"PostingKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_payments_TenantId_CompanyId_ReversalKey",
                schema: "accounting",
                table: "payments",
                columns: new[] { "TenantId", "CompanyId", "ReversalKey" },
                unique: true,
                filter: "\"ReversalKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_allocations",
                schema: "accounting");

            migrationBuilder.DropTable(
                name: "payments",
                schema: "accounting");
        }
    }
}
