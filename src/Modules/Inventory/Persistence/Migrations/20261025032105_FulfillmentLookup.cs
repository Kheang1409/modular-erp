using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861

#nullable disable

namespace Erp.Modules.Inventory.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FulfillmentLookup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_stock_moves_fulfillment_lookup",
                schema: "inventory",
                table: "stock_moves",
                columns: new[] { "TenantId", "CompanyId", "ReferenceDocumentType", "ReferenceDocumentId", "Type", "State", "ProductId" })
                .Annotation("Npgsql:CreatedConcurrently", true)
                .Annotation("Npgsql:IndexInclude", new[] { "Quantity" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_stock_moves_fulfillment_lookup",
                schema: "inventory",
                table: "stock_moves");
        }
    }
}
