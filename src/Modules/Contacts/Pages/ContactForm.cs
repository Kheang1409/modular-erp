namespace Erp.Modules.Contacts.Pages;

public sealed class ContactForm
{
    public ContactKind Kind { get; set; }
    public string? DisplayName { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? OrganizationName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? AlternateEmail { get; set; }
    public string? AlternatePhone { get; set; }
    public string? TaxNumber { get; set; }
    public string? Notes { get; set; }
    public bool IsCustomer { get; set; }
    public bool IsSupplier { get; set; }
    public string? LegalName { get; set; }
    public string? Website { get; set; }
    public string? BillingLine1 { get; set; }
    public string? BillingCity { get; set; }
    public string? BillingCountryCode { get; set; }
    public string? ShippingLine1 { get; set; }
    public string? ShippingCity { get; set; }
    public string? ShippingCountryCode { get; set; }
    public string? ConcurrencyStamp { get; set; }
    public ContactRequest ToRequest() => new(Kind, DisplayName, FirstName, LastName, OrganizationName, Email, Phone, TaxNumber, Notes,
        ConcurrencyStamp, IsCustomer, IsSupplier, LegalName, Website,
        new(BillingLine1, null, BillingCity, null, null, BillingCountryCode),
        new(ShippingLine1, null, ShippingCity, null, null, ShippingCountryCode),
        Communications(Email, AlternateEmail, (value, primary) => new ContactEmailRequest(value, primary)),
        Communications(Phone, AlternatePhone, (value, primary) => new ContactPhoneRequest(value, primary)));
    public static ContactForm From(ContactDto contact) => new()
    {
        Kind = contact.Kind,
        DisplayName = contact.DisplayName,
        FirstName = contact.FirstName,
        LastName = contact.LastName,
        OrganizationName = contact.OrganizationName,
        Email = contact.Email,
        Phone = contact.Phone,
        AlternateEmail = contact.EmailAddresses.FirstOrDefault(value => !value.IsPrimary)?.Address,
        AlternatePhone = contact.PhoneNumbers.FirstOrDefault(value => !value.IsPrimary)?.Number,
        TaxNumber = contact.TaxNumber,
        Notes = contact.Notes,
        ConcurrencyStamp = contact.ConcurrencyStamp,
        IsCustomer = contact.IsCustomer,
        IsSupplier = contact.IsSupplier,
        LegalName = contact.LegalName,
        Website = contact.Website,
        BillingLine1 = contact.BillingAddress?.Line1,
        BillingCity = contact.BillingAddress?.City,
        BillingCountryCode = contact.BillingAddress?.CountryCode,
        ShippingLine1 = contact.ShippingAddress?.Line1,
        ShippingCity = contact.ShippingAddress?.City,
        ShippingCountryCode = contact.ShippingAddress?.CountryCode
    };

    private static List<T> Communications<T>(string? primary, string? alternate, Func<string, bool, T> factory)
    {
        var values = new List<T>();
        if (!string.IsNullOrWhiteSpace(primary)) values.Add(factory(primary, true));
        if (!string.IsNullOrWhiteSpace(alternate)) values.Add(factory(alternate, false));
        return values;
    }
}

public sealed class LifecycleForm { public string? ConcurrencyStamp { get; set; } public bool IsArchived { get; set; } }
