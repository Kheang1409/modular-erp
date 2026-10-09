using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Contacts.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompleteContactProfile : Migration
    {
        private static readonly string[] RoleIndexColumns = ["TenantId", "CompanyId", "IsCustomer", "IsSupplier"];
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BillingCity",
                schema: "contacts",
                table: "contacts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingCountryCode",
                schema: "contacts",
                table: "contacts",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingLine1",
                schema: "contacts",
                table: "contacts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingLine2",
                schema: "contacts",
                table: "contacts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingPostalCode",
                schema: "contacts",
                table: "contacts",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingStateOrProvince",
                schema: "contacts",
                table: "contacts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedBy",
                schema: "contacts",
                table: "contacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCustomer",
                schema: "contacts",
                table: "contacts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsSupplier",
                schema: "contacts",
                table: "contacts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LegalName",
                schema: "contacts",
                table: "contacts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShippingCity",
                schema: "contacts",
                table: "contacts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShippingCountryCode",
                schema: "contacts",
                table: "contacts",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShippingLine1",
                schema: "contacts",
                table: "contacts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShippingLine2",
                schema: "contacts",
                table: "contacts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShippingPostalCode",
                schema: "contacts",
                table: "contacts",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShippingStateOrProvince",
                schema: "contacts",
                table: "contacts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UpdatedBy",
                schema: "contacts",
                table: "contacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Website",
                schema: "contacts",
                table: "contacts",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.Sql("UPDATE contacts.contacts SET \"IsCustomer\" = TRUE WHERE NOT \"IsCustomer\" AND NOT \"IsSupplier\";");
            migrationBuilder.AddCheckConstraint(
                name: "CK_contacts_Role",
                schema: "contacts",
                table: "contacts",
                sql: "\"IsCustomer\" OR \"IsSupplier\"");

            migrationBuilder.CreateIndex(
                name: "IX_contacts_TenantId_CompanyId_IsCustomer_IsSupplier",
                schema: "contacts",
                table: "contacts",
                columns: RoleIndexColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_contacts_Role",
                schema: "contacts",
                table: "contacts");
            migrationBuilder.DropIndex(
                name: "IX_contacts_TenantId_CompanyId_IsCustomer_IsSupplier",
                schema: "contacts",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "BillingCity",
                schema: "contacts",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "BillingCountryCode",
                schema: "contacts",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "BillingLine1",
                schema: "contacts",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "BillingLine2",
                schema: "contacts",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "BillingPostalCode",
                schema: "contacts",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "BillingStateOrProvince",
                schema: "contacts",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                schema: "contacts",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "IsCustomer",
                schema: "contacts",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "IsSupplier",
                schema: "contacts",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "LegalName",
                schema: "contacts",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "ShippingCity",
                schema: "contacts",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "ShippingCountryCode",
                schema: "contacts",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "ShippingLine1",
                schema: "contacts",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "ShippingLine2",
                schema: "contacts",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "ShippingPostalCode",
                schema: "contacts",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "ShippingStateOrProvince",
                schema: "contacts",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "UpdatedBy",
                schema: "contacts",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "Website",
                schema: "contacts",
                table: "contacts");
        }
    }
}
