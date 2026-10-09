namespace Erp.Modules.Contacts.Contracts;

public enum ContactType { Person, Organization }

public sealed record ContactReference(
    Guid Id,
    ContactType Type,
    string DisplayName,
    string? Email,
    string? Phone,
    bool IsCustomer,
    bool IsSupplier,
    bool IsArchived);

public interface IContactDirectory
{
    Task<ContactReference?> FindAsync(Guid contactId, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, ContactReference>> FindAsync(
        IReadOnlyCollection<Guid> contactIds,
        CancellationToken cancellationToken);
}

public sealed record ContactAddressReference(string? Line1, string? Line2, string? City, string? StateOrProvince, string? PostalCode, string? CountryCode);
public sealed record CustomerDocumentReference(Guid Id, string DisplayName, string? TaxNumber, ContactAddressReference BillingAddress, ContactAddressReference ShippingAddress, bool IsArchived);
public interface ICustomerDocumentDirectory
{
    Task<CustomerDocumentReference?> FindAsync(Guid customerId, CancellationToken cancellationToken);
}
public interface ISupplierDocumentDirectory
{
    Task<CustomerDocumentReference?> FindAsync(Guid supplierId, CancellationToken cancellationToken);
}
