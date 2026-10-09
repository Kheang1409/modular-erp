using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Contacts.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContactCommunicationMethods : Migration
    {
        private static readonly string[] EmailScopeColumns = ["TenantId", "CompanyId", "ContactId", "Address"];
        private static readonly string[] PhoneScopeColumns = ["TenantId", "CompanyId", "ContactId", "Number"];
        private static readonly string[] ContactPrincipalColumns = ["TenantId", "CompanyId", "Id"];
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_contacts_TenantId_CompanyId_Id", schema: "contacts", table: "contacts",
                columns: ContactPrincipalColumns);
            migrationBuilder.CreateTable(
                name: "contact_emails",
                schema: "contacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: false),
                    Address = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contact_emails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_contact_emails_contacts_ContactId",
                        columns: x => new { x.TenantId, x.CompanyId, x.ContactId },
                        principalSchema: "contacts",
                        principalTable: "contacts",
                        principalColumns: ContactPrincipalColumns,
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "contact_phones",
                schema: "contacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contact_phones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_contact_phones_contacts_ContactId",
                        columns: x => new { x.TenantId, x.CompanyId, x.ContactId },
                        principalSchema: "contacts",
                        principalTable: "contacts",
                        principalColumns: ContactPrincipalColumns,
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_contact_emails_ContactId",
                schema: "contacts",
                table: "contact_emails",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_contact_emails_TenantId_CompanyId_ContactId_Address",
                schema: "contacts",
                table: "contact_emails",
                columns: EmailScopeColumns,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_contact_phones_ContactId",
                schema: "contacts",
                table: "contact_phones",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_contact_phones_TenantId_CompanyId_ContactId_Number",
                schema: "contacts",
                table: "contact_phones",
                columns: PhoneScopeColumns,
                unique: true);

            migrationBuilder.Sql("INSERT INTO contacts.contact_emails (\"Id\", \"TenantId\", \"CompanyId\", \"ContactId\", \"Address\", \"IsPrimary\") SELECT md5(\"Id\"::text || ':email')::uuid, \"TenantId\", \"CompanyId\", \"Id\", \"Email\", TRUE FROM contacts.contacts WHERE \"Email\" IS NOT NULL;");
            migrationBuilder.Sql("INSERT INTO contacts.contact_phones (\"Id\", \"TenantId\", \"CompanyId\", \"ContactId\", \"Number\", \"IsPrimary\") SELECT md5(\"Id\"::text || ':phone')::uuid, \"TenantId\", \"CompanyId\", \"Id\", \"Phone\", TRUE FROM contacts.contacts WHERE \"Phone\" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contact_emails",
                schema: "contacts");

            migrationBuilder.DropTable(
                name: "contact_phones",
                schema: "contacts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_contacts_TenantId_CompanyId_Id", schema: "contacts", table: "contacts");
        }
    }
}
