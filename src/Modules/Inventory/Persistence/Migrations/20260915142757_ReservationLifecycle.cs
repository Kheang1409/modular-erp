using System;
using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861

#nullable disable

namespace Erp.Modules.Inventory.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReservationLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConsumedAtUtc",
                schema: "inventory",
                table: "reservations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ConsumedQuantity",
                schema: "inventory",
                table: "reservations",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "ReleaseKey",
                schema: "inventory",
                table: "reservations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_reservations_TenantId_CompanyId_ReferenceDocumentId_State",
                schema: "inventory",
                table: "reservations",
                columns: new[] { "TenantId", "CompanyId", "ReferenceDocumentId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_reservations_TenantId_CompanyId_ReleaseKey",
                schema: "inventory",
                table: "reservations",
                columns: new[] { "TenantId", "CompanyId", "ReleaseKey" },
                unique: true,
                filter: "\"ReleaseKey\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_reservations_consumed_quantity",
                schema: "inventory",
                table: "reservations",
                sql: "\"ConsumedQuantity\">=0 AND \"ConsumedQuantity\"<=\"Quantity\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_reservations_TenantId_CompanyId_ReferenceDocumentId_State",
                schema: "inventory",
                table: "reservations");

            migrationBuilder.DropIndex(
                name: "IX_reservations_TenantId_CompanyId_ReleaseKey",
                schema: "inventory",
                table: "reservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_reservations_consumed_quantity",
                schema: "inventory",
                table: "reservations");

            migrationBuilder.DropColumn(
                name: "ConsumedAtUtc",
                schema: "inventory",
                table: "reservations");

            migrationBuilder.DropColumn(
                name: "ConsumedQuantity",
                schema: "inventory",
                table: "reservations");

            migrationBuilder.DropColumn(
                name: "ReleaseKey",
                schema: "inventory",
                table: "reservations");
        }
    }
}
