using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace Erp.Modules.Inventory.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.CreateTable(
                name: "reservations",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    State = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ReferenceDocumentType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ReferenceDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reservations", x => x.Id);
                    table.CheckConstraint("CK_reservations_quantity", "\"Quantity\">0");
                });

            migrationBuilder.CreateTable(
                name: "stock_moves",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    State = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductSku = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UnitCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    SourceLocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    DestinationLocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReferenceDocumentType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ReferenceDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ReversesMoveId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompletionKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CompletedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_moves", x => x.Id);
                    table.CheckConstraint("CK_stock_moves_quantity", "\"Quantity\">0");
                });

            migrationBuilder.CreateTable(
                name: "warehouses",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warehouses", x => x.Id);
                    table.UniqueConstraint("AK_warehouses_TenantId_CompanyId_Id", x => new { x.TenantId, x.CompanyId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "locations",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: true),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_locations", x => x.Id);
                    table.UniqueConstraint("AK_locations_TenantId_CompanyId_Id", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_locations_warehouses_TenantId_CompanyId_WarehouseId",
                        columns: x => new { x.TenantId, x.CompanyId, x.WarehouseId },
                        principalSchema: "inventory",
                        principalTable: "warehouses",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_locations_TenantId_CompanyId_Code",
                schema: "inventory",
                table: "locations",
                columns: new[] { "TenantId", "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_locations_TenantId_CompanyId_WarehouseId",
                schema: "inventory",
                table: "locations",
                columns: new[] { "TenantId", "CompanyId", "WarehouseId" });

            migrationBuilder.CreateIndex(
                name: "IX_reservations_TenantId_CompanyId_IdempotencyKey",
                schema: "inventory",
                table: "reservations",
                columns: new[] { "TenantId", "CompanyId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reservations_TenantId_CompanyId_ProductId_LocationId_State",
                schema: "inventory",
                table: "reservations",
                columns: new[] { "TenantId", "CompanyId", "ProductId", "LocationId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_moves_TenantId_CompanyId_CompletionKey",
                schema: "inventory",
                table: "stock_moves",
                columns: new[] { "TenantId", "CompanyId", "CompletionKey" },
                unique: true,
                filter: "\"CompletionKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_stock_moves_TenantId_CompanyId_Number",
                schema: "inventory",
                table: "stock_moves",
                columns: new[] { "TenantId", "CompanyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stock_moves_TenantId_CompanyId_ProductId_State",
                schema: "inventory",
                table: "stock_moves",
                columns: new[] { "TenantId", "CompanyId", "ProductId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_warehouses_TenantId_CompanyId_Code",
                schema: "inventory",
                table: "warehouses",
                columns: new[] { "TenantId", "CompanyId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "locations",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "reservations",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "stock_moves",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "warehouses",
                schema: "inventory");
        }
    }
}
