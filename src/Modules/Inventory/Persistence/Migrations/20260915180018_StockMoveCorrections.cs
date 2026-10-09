using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861
#nullable disable

namespace Erp.Modules.Inventory.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StockMoveCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_stock_moves_TenantId_CompanyId_Id",
                schema: "inventory",
                table: "stock_moves",
                columns: new[] { "TenantId", "CompanyId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_moves_TenantId_CompanyId_ReversesMoveId",
                schema: "inventory",
                table: "stock_moves",
                columns: new[] { "TenantId", "CompanyId", "ReversesMoveId" },
                unique: true,
                filter: "\"ReversesMoveId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_stock_moves_stock_moves_TenantId_CompanyId_ReversesMoveId",
                schema: "inventory",
                table: "stock_moves",
                columns: new[] { "TenantId", "CompanyId", "ReversesMoveId" },
                principalSchema: "inventory",
                principalTable: "stock_moves",
                principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_stock_moves_stock_moves_TenantId_CompanyId_ReversesMoveId",
                schema: "inventory",
                table: "stock_moves");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_stock_moves_TenantId_CompanyId_Id",
                schema: "inventory",
                table: "stock_moves");

            migrationBuilder.DropIndex(
                name: "IX_stock_moves_TenantId_CompanyId_ReversesMoveId",
                schema: "inventory",
                table: "stock_moves");
        }
    }
}
