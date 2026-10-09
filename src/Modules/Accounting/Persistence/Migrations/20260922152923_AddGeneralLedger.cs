using System;
using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861 // EF generates composite index column arrays.

#nullable disable

namespace Erp.Modules.Accounting.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGeneralLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "journal_entries",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    JournalId = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ExchangeRate = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PostingKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ReversesEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversedByEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PostedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_journal_entries", x => x.Id);
                    table.UniqueConstraint("AK_journal_entries_TenantId_CompanyId_Id", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.CheckConstraint("CK_journal_entries_rate", "\"ExchangeRate\">0");
                    table.ForeignKey(
                        name: "FK_journal_entries_journal_entries_TenantId_CompanyId_Reverses~",
                        columns: x => new { x.TenantId, x.CompanyId, x.ReversesEntryId },
                        principalSchema: "accounting",
                        principalTable: "journal_entries",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "journal_entry_lines",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    AccountName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Debit = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Credit = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    BaseDebit = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    BaseCredit = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_journal_entry_lines", x => x.Id);
                    table.CheckConstraint("CK_journal_lines_amounts", "\"Debit\">=0 AND \"Credit\">=0 AND ((\"Debit\">0 AND \"Credit\"=0) OR (\"Credit\">0 AND \"Debit\"=0))");
                    table.ForeignKey(
                        name: "FK_journal_entry_lines_journal_entries_TenantId_CompanyId_Jour~",
                        columns: x => new { x.TenantId, x.CompanyId, x.JournalEntryId },
                        principalSchema: "accounting",
                        principalTable: "journal_entries",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_journal_entries_TenantId_CompanyId_IdempotencyKey",
                schema: "accounting",
                table: "journal_entries",
                columns: new[] { "TenantId", "CompanyId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_journal_entries_TenantId_CompanyId_Number",
                schema: "accounting",
                table: "journal_entries",
                columns: new[] { "TenantId", "CompanyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_journal_entries_TenantId_CompanyId_PostingKey",
                schema: "accounting",
                table: "journal_entries",
                columns: new[] { "TenantId", "CompanyId", "PostingKey" },
                unique: true,
                filter: "\"PostingKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_journal_entries_TenantId_CompanyId_ReversesEntryId",
                schema: "accounting",
                table: "journal_entries",
                columns: new[] { "TenantId", "CompanyId", "ReversesEntryId" },
                unique: true,
                filter: "\"ReversesEntryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_journal_entry_lines_TenantId_CompanyId_JournalEntryId_Seque~",
                schema: "accounting",
                table: "journal_entry_lines",
                columns: new[] { "TenantId", "CompanyId", "JournalEntryId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "journal_entry_lines",
                schema: "accounting");

            migrationBuilder.DropTable(
                name: "journal_entries",
                schema: "accounting");
        }
    }
}
