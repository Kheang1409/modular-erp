namespace Erp.Modules.Contacts;

public enum ContactKind { Person, Organization }

public sealed class Contact
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public ContactKind Kind { get; set; }
    public required string DisplayName { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? OrganizationName { get; set; }
    public string? LegalName { get; set; }
    public string? Website { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public List<ContactEmailAddress> EmailAddresses { get; set; } = [];
    public List<ContactPhoneNumber> PhoneNumbers { get; set; } = [];
    public string? TaxNumber { get; set; }
    public string? Notes { get; set; }
    public bool IsCustomer { get; set; }
    public bool IsSupplier { get; set; }
    public ContactAddress BillingAddress { get; set; } = new();
    public ContactAddress ShippingAddress { get; set; } = new();
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public required string ConcurrencyStamp { get; set; }
}

public sealed class ContactEmailAddress
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ContactId { get; set; }
    public required string Address { get; set; }
    public bool IsPrimary { get; set; }
}

public sealed class ContactPhoneNumber
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ContactId { get; set; }
    public required string Number { get; set; }
    public bool IsPrimary { get; set; }
}

public sealed record ContactEmailRequest(string? Address, bool IsPrimary = false);
public sealed record ContactPhoneRequest(string? Number, bool IsPrimary = false);

public sealed class ContactAddress
{
    public string? Line1 { get; set; }
    public string? Line2 { get; set; }
    public string? City { get; set; }
    public string? StateOrProvince { get; set; }
    public string? PostalCode { get; set; }
    public string? CountryCode { get; set; }
}

public sealed record ContactAddressRequest(
    string? Line1, string? Line2, string? City, string? StateOrProvince,
    string? PostalCode, string? CountryCode);

public sealed record ContactRequest(
    ContactKind Kind, string? DisplayName, string? FirstName, string? LastName,
    string? OrganizationName, string? Email, string? Phone, string? TaxNumber,
    string? Notes, string? ConcurrencyStamp = null, bool IsCustomer = false,
    bool IsSupplier = false, string? LegalName = null, string? Website = null,
    ContactAddressRequest? BillingAddress = null, ContactAddressRequest? ShippingAddress = null,
    IReadOnlyList<ContactEmailRequest>? EmailAddresses = null,
    IReadOnlyList<ContactPhoneRequest>? PhoneNumbers = null);

public sealed record ContactDto(
    Guid Id, ContactKind Kind, string DisplayName, string? FirstName, string? LastName,
    string? OrganizationName, string? Email, string? Phone, string? TaxNumber,
    string? Notes, bool IsArchived, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc,
    string ConcurrencyStamp, bool IsCustomer, bool IsSupplier, string? LegalName,
    string? Website, ContactAddressRequest? BillingAddress, ContactAddressRequest? ShippingAddress,
    Guid? CreatedBy, Guid? UpdatedBy, IReadOnlyList<ContactEmailRequest> EmailAddresses,
    IReadOnlyList<ContactPhoneRequest> PhoneNumbers);

public sealed record ContactQuery(
    string? Search = null, ContactKind? Kind = null, bool IncludeArchived = false,
    string Sort = "name", bool Descending = false, int Page = 1, int PageSize = 25,
    bool? IsCustomer = null, bool? IsSupplier = null);

public sealed record ContactPage(int Page, int PageSize, int Total, IReadOnlyList<ContactDto> Items);
public sealed record CsvImportResult(int Imported, IReadOnlyList<string> Errors);

public static class ContactRoleText
{
    public static string Describe(bool isCustomer, bool isSupplier) =>
        isCustomer && isSupplier ? "Customer and supplier" :
        isCustomer ? "Customer" :
        isSupplier ? "Supplier" :
        "No business role";
}

public sealed class ContactValidationException(IReadOnlyDictionary<string, string[]> errors)
    : Exception("Contact validation failed.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

public sealed class ContactConcurrencyException(string? detail = null)
    : Exception(detail is null ? "The contact changed since it was loaded." : $"The contact changed since it was loaded ({detail}).");
