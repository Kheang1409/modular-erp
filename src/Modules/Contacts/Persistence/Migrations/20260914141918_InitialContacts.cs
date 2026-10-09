using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Erp.Modules.Contacts.Persistence.Migrations;

[DbContext(typeof(ContactsDbContext))]
[Migration("20260914141918_InitialContacts")]
public sealed partial class InitialContacts : Migration
{
    private static readonly string[] NameIndex = ["TenantId", "CompanyId", "DisplayName"];
    private static readonly string[] EmailIndex = ["TenantId", "CompanyId", "Email"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema("contacts");
        migrationBuilder.CreateTable(name: "contacts", schema: "contacts", columns: table => new
        {
            Id = table.Column<Guid>("uuid"),
            TenantId = table.Column<Guid>("uuid"),
            CompanyId = table.Column<Guid>("uuid"),
            Kind = table.Column<string>("character varying(20)", maxLength: 20),
            DisplayName = table.Column<string>("character varying(200)", maxLength: 200),
            FirstName = table.Column<string>("character varying(100)", maxLength: 100, nullable: true),
            LastName = table.Column<string>("character varying(100)", maxLength: 100, nullable: true),
            OrganizationName = table.Column<string>("character varying(200)", maxLength: 200, nullable: true),
            Email = table.Column<string>("character varying(254)", maxLength: 254, nullable: true),
            Phone = table.Column<string>("character varying(50)", maxLength: 50, nullable: true),
            TaxNumber = table.Column<string>("character varying(100)", maxLength: 100, nullable: true),
            Notes = table.Column<string>("character varying(4000)", maxLength: 4000, nullable: true),
            IsArchived = table.Column<bool>("boolean"),
            CreatedAtUtc = table.Column<DateTimeOffset>("timestamp with time zone"),
            UpdatedAtUtc = table.Column<DateTimeOffset>("timestamp with time zone"),
            ConcurrencyStamp = table.Column<string>("character varying(32)", maxLength: 32)
        }, constraints: table => table.PrimaryKey("PK_contacts", row => row.Id));
        migrationBuilder.CreateIndex(name: "IX_contacts_TenantId_CompanyId_DisplayName", schema: "contacts", table: "contacts", columns: NameIndex);
        migrationBuilder.CreateIndex(name: "IX_contacts_TenantId_CompanyId_Email", schema: "contacts", table: "contacts", columns: EmailIndex);
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("contacts", "contacts");
}
