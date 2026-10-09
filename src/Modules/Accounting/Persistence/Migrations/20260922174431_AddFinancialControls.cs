using System;
using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861 // EF generates composite index column arrays.

#nullable disable

namespace Erp.Modules.Accounting.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bank_reconciliations",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    BankAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    StatementDate = table.Column<DateOnly>(type: "date", nullable: false),
                    OpeningBalance = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ClosingBalance = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReconciledAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_reconciliations", x => x.Id);
                    table.UniqueConstraint("AK_bank_reconciliations_TenantId_CompanyId_Id", x => new { x.TenantId, x.CompanyId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "currency_rates",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaseCurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    QuoteCurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_currency_rates", x => x.Id);
                    table.CheckConstraint("CK_currency_rates_rate", "\"Rate\">0");
                });

            migrationBuilder.CreateTable(
                name: "tax_codes",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: false),
                    OutputAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    InputAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tax_codes", x => x.Id);
                    table.CheckConstraint("CK_tax_codes_rate", "\"Rate\">=0 AND \"Rate\"<=100");
                });

            migrationBuilder.CreateTable(
                name: "bank_reconciliation_lines",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    BankReconciliationId = table.Column<Guid>(type: "uuid", nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    JournalEntryNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_reconciliation_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_bank_reconciliation_lines_bank_reconciliations_TenantId_Com~",
                        columns: x => new { x.TenantId, x.CompanyId, x.BankReconciliationId },
                        principalSchema: "accounting",
                        principalTable: "bank_reconciliations",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bank_reconciliation_lines_TenantId_CompanyId_BankReconcilia~",
                schema: "accounting",
                table: "bank_reconciliation_lines",
                columns: new[] { "TenantId", "CompanyId", "BankReconciliationId" });

            migrationBuilder.CreateIndex(
                name: "IX_bank_reconciliation_lines_TenantId_CompanyId_JournalEntryId",
                schema: "accounting",
                table: "bank_reconciliation_lines",
                columns: new[] { "TenantId", "CompanyId", "JournalEntryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bank_reconciliations_TenantId_CompanyId_BankAccountId_State~",
                schema: "accounting",
                table: "bank_reconciliations",
                columns: new[] { "TenantId", "CompanyId", "BankAccountId", "StatementDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bank_reconciliations_TenantId_CompanyId_Number",
                schema: "accounting",
                table: "bank_reconciliations",
                columns: new[] { "TenantId", "CompanyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_currency_rates_TenantId_CompanyId_BaseCurrencyCode_QuoteCur~",
                schema: "accounting",
                table: "currency_rates",
                columns: new[] { "TenantId", "CompanyId", "BaseCurrencyCode", "QuoteCurrencyCode", "EffectiveDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tax_codes_TenantId_CompanyId_Code",
                schema: "accounting",
                table: "tax_codes",
                columns: new[] { "TenantId", "CompanyId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bank_reconciliation_lines",
                schema: "accounting");

            migrationBuilder.DropTable(
                name: "currency_rates",
                schema: "accounting");

            migrationBuilder.DropTable(
                name: "tax_codes",
                schema: "accounting");

            migrationBuilder.DropTable(
                name: "bank_reconciliations",
                schema: "accounting");
        }
    }
}
