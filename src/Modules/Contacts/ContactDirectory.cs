using Erp.Modules.Contacts.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Contacts;

internal sealed class ContactDirectory(ContactsDbContext database) : IContactDirectory
{
    public Task<ContactReference?> FindAsync(Guid contactId, CancellationToken cancellationToken) =>
        database.Contacts.AsNoTracking()
            .Where(contact => contact.Id == contactId)
            .Select(contact => new ContactReference(
                contact.Id,
                contact.Kind == ContactKind.Person ? ContactType.Person : ContactType.Organization,
                contact.DisplayName,
                contact.Email,
                contact.Phone,
                contact.IsCustomer,
                contact.IsSupplier,
                contact.IsArchived))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, ContactReference>> FindAsync(
        IReadOnlyCollection<Guid> contactIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contactIds);
        if (contactIds.Count == 0) return new Dictionary<Guid, ContactReference>();
        if (contactIds.Count > 100) throw new ArgumentOutOfRangeException(nameof(contactIds), "At most 100 contacts can be resolved at once.");
        var distinctIds = contactIds.Distinct().ToArray();
        return await database.Contacts.AsNoTracking()
            .Where(contact => distinctIds.Contains(contact.Id))
            .Select(contact => new ContactReference(
                contact.Id,
                contact.Kind == ContactKind.Person ? ContactType.Person : ContactType.Organization,
                contact.DisplayName,
                contact.Email,
                contact.Phone,
                contact.IsCustomer,
                contact.IsSupplier,
                contact.IsArchived))
            .ToDictionaryAsync(contact => contact.Id, cancellationToken);
    }
}

internal sealed class CustomerDocumentDirectory(ContactsDbContext database) : ICustomerDocumentDirectory
{
    public Task<CustomerDocumentReference?> FindAsync(Guid id, CancellationToken token) => database.Contacts.AsNoTracking()
        .Where(x => x.Id == id && x.IsCustomer).Select(x => new CustomerDocumentReference(x.Id, x.DisplayName, x.TaxNumber,
            new(x.BillingAddress.Line1, x.BillingAddress.Line2, x.BillingAddress.City, x.BillingAddress.StateOrProvince, x.BillingAddress.PostalCode, x.BillingAddress.CountryCode),
            new(x.ShippingAddress.Line1, x.ShippingAddress.Line2, x.ShippingAddress.City, x.ShippingAddress.StateOrProvince, x.ShippingAddress.PostalCode, x.ShippingAddress.CountryCode), x.IsArchived)).SingleOrDefaultAsync(token);
}
internal sealed class SupplierDocumentDirectory(ContactsDbContext database) : ISupplierDocumentDirectory
{
    public Task<CustomerDocumentReference?> FindAsync(Guid id, CancellationToken token) => database.Contacts.AsNoTracking()
        .Where(x => x.Id == id && x.IsSupplier).Select(x => new CustomerDocumentReference(x.Id, x.DisplayName, x.TaxNumber,
            new(x.BillingAddress.Line1, x.BillingAddress.Line2, x.BillingAddress.City, x.BillingAddress.StateOrProvince, x.BillingAddress.PostalCode, x.BillingAddress.CountryCode),
            new(x.ShippingAddress.Line1, x.ShippingAddress.Line2, x.ShippingAddress.City, x.ShippingAddress.StateOrProvince, x.ShippingAddress.PostalCode, x.ShippingAddress.CountryCode), x.IsArchived)).SingleOrDefaultAsync(token);
}
