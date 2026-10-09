using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // Generated migration APIs require array arguments.

namespace Erp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DocumentSequences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_sequences",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PrefixTemplate = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Padding = table.Column<int>(type: "integer", nullable: false),
                    NextValue = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_sequences", x => x.Id);
                    table.CheckConstraint("CK_document_sequences_NextValue", "\"NextValue\" > 0");
                    table.CheckConstraint("CK_document_sequences_Padding", "\"Padding\" BETWEEN 1 AND 18");
                    table.ForeignKey(
                        name: "FK_document_sequences_companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "platform",
                        principalTable: "companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_sequences_TenantId_CompanyId_Key",
                schema: "platform",
                table: "document_sequences",
                columns: new[] { "TenantId", "CompanyId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_sequences",
                schema: "platform");
        }
    }
}
