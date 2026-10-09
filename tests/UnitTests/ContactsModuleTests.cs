using Erp.Abstractions.Modules;
using Erp.ModuleSystem;
using Erp.Modules.Contacts;

namespace Erp.UnitTests;

public sealed class ContactsModuleTests
{
    [Fact]
    public void ManifestAndPermissionsDescribeTheContactsBoundary()
    {
        var module = new ContactsModule();
        var permissions = new PermissionRegistry();
        module.ConfigurePermissions(permissions);
        Assert.Equal("erp.contacts", module.Manifest.Id);
        Assert.Equal(5, permissions.Permissions.Count);
        Assert.All(permissions.Permissions, permission => Assert.Equal("erp.contacts", permission.ModuleId));
    }

    [Fact]
    public void CsvParserSupportsQuotedCommasAndEscapedQuotes()
    {
        var values = ContactService.ParseCsvLine("Organization,\"Acme, Inc.\",\"A \"\"preferred\"\" account\"");
        Assert.Equal(["Organization", "Acme, Inc.", "A \"preferred\" account"], values);
    }

    [Theory]
    [InlineData("=2+3", "\"'=2+3\"")]
    [InlineData("+SUM(A1:A2)", "\"'+SUM(A1:A2)\"")]
    [InlineData("ordinary", "\"ordinary\"")]
    public void CsvExportPreventsSpreadsheetFormulaExecution(string value, string expected) =>
        Assert.Equal(expected, ContactService.Csv(value));

    [Theory]
    [InlineData(false, false, "No business role")]
    [InlineData(true, false, "Customer")]
    [InlineData(false, true, "Supplier")]
    [InlineData(true, true, "Customer and supplier")]
    public void RoleTextDescribesBusinessRoles(bool isCustomer, bool isSupplier, string expected) =>
        Assert.Equal(expected, ContactRoleText.Describe(isCustomer, isSupplier));

    [Fact]
    public void ValidationRequiresAtLeastOneBusinessRole()
    {
        var request = new ContactRequest(ContactKind.Person, "Jane Doe", "Jane", "Doe", null, null, null, null, null);
        var exception = Assert.Throws<ContactValidationException>(() => ContactService.Validate(request));
        Assert.Contains("roles", exception.Errors.Keys);
    }

    [Fact]
    public void ValidationNormalizesEmail()
    {
        var request = new ContactRequest(ContactKind.Organization, " Acme ", null, null, "Acme", " SALES@EXAMPLE.COM ",
            null, null, null, IsSupplier: true, BillingAddress: new("1 Main", null, "Austin", null, null, "us"));
        var normalized = ContactService.Validate(request);
        Assert.Equal("sales@example.com", normalized.Email);
        Assert.Equal("Acme", normalized.DisplayName);
    }

    [Fact]
    public void ValidationRejectsMultiplePrimaryEmails()
    {
        var request = new ContactRequest(ContactKind.Person, "Jane", "Jane", null, null, null, null, null, null,
            IsCustomer: true, EmailAddresses: [new("one@example.com", true), new("two@example.com", true)]);
        var exception = Assert.Throws<ContactValidationException>(() => ContactService.Validate(request));
        Assert.Contains("emailAddresses.primary", exception.Errors.Keys);
    }
}
