using System.Globalization;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Security.Claims;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Contacts;

public interface IContactService
{
    Task<ContactPage> SearchAsync(ContactQuery query, CancellationToken cancellationToken);
    Task<ContactDto?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<ContactDto> CreateAsync(ContactRequest request, CancellationToken cancellationToken);
    Task<ContactDto> UpdateAsync(Guid id, ContactRequest request, CancellationToken cancellationToken);
    Task<ContactDto> SetArchivedAsync(Guid id, string concurrencyStamp, bool archived, CancellationToken cancellationToken);
    Task<CsvImportResult> ImportCsvAsync(Stream csv, CancellationToken cancellationToken);
    Task<string> ExportCsvAsync(ContactQuery query, CancellationToken cancellationToken);
    Task ExportCsvAsync(ContactQuery query, Stream destination, CancellationToken cancellationToken);
}

public sealed class ContactService(
    ContactsDbContext database, ITenantContext tenantContext, ICompanyContext companyContext,
    IClock clock, IAuditLogWriter audit, IHttpContextAccessor httpContextAccessor,
    IBusinessTransactionCoordinator transactions) : IContactService
{
    public async Task<ContactPage> SearchAsync(ContactQuery query, CancellationToken cancellationToken)
    {
        ValidateQuery(query);
        var contacts = database.Contacts.AsNoTracking();
        if (!query.IncludeArchived) contacts = contacts.Where(x => !x.IsArchived);
        if (query.Kind is not null) contacts = contacts.Where(x => x.Kind == query.Kind);
        if (query.IsCustomer is not null) contacts = contacts.Where(x => x.IsCustomer == query.IsCustomer);
        if (query.IsSupplier is not null) contacts = contacts.Where(x => x.IsSupplier == query.IsSupplier);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            contacts = contacts.Where(x => EF.Functions.ILike(x.DisplayName, $"%{search}%") ||
                (x.Email != null && EF.Functions.ILike(x.Email, $"%{search}%")) ||
                (x.Phone != null && EF.Functions.ILike(x.Phone, $"%{search}%")) ||
                x.EmailAddresses.Any(email => EF.Functions.ILike(email.Address, $"%{search}%")) ||
                x.PhoneNumbers.Any(phone => EF.Functions.ILike(phone.Number, $"%{search}%")) ||
                (x.TaxNumber != null && EF.Functions.ILike(x.TaxNumber, $"%{search}%")));
        }
        contacts = (query.Sort.ToLowerInvariant(), query.Descending) switch
        {
            ("created", false) => contacts.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id),
            ("created", true) => contacts.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id),
            ("updated", false) => contacts.OrderBy(x => x.UpdatedAtUtc).ThenBy(x => x.Id),
            ("updated", true) => contacts.OrderByDescending(x => x.UpdatedAtUtc).ThenByDescending(x => x.Id),
            ("name", true) => contacts.OrderByDescending(x => x.DisplayName).ThenByDescending(x => x.Id),
            _ => contacts.OrderBy(x => x.DisplayName).ThenBy(x => x.Id)
        };
        var total = await contacts.CountAsync(cancellationToken);
        var entities = await contacts.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Include(x => x.EmailAddresses).Include(x => x.PhoneNumbers).ToListAsync(cancellationToken);
        var items = entities.Select(ToDto).ToList();
        return new(query.Page, query.PageSize, total, items);
    }

    public async Task<ContactDto?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        (await database.Contacts.AsNoTracking().Include(x => x.EmailAddresses).Include(x => x.PhoneNumbers)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken)) is { } contact ? ToDto(contact) : null;

    public async Task<ContactDto> CreateAsync(ContactRequest request, CancellationToken cancellationToken)
    {
        var values = Validate(request);
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var now = clock.UtcNow;
        var contactId = Guid.NewGuid();
        var contact = new Contact
        {
            Id = contactId,
            TenantId = RequiredTenant(),
            CompanyId = RequiredCompany(),
            Kind = request.Kind,
            DisplayName = values.DisplayName!,
            FirstName = values.FirstName,
            LastName = values.LastName,
            OrganizationName = values.OrganizationName,
            LegalName = values.LegalName,
            Website = values.Website,
            Email = values.Email,
            Phone = values.Phone,
            TaxNumber = values.TaxNumber,
            Notes = values.Notes,
            IsCustomer = values.IsCustomer,
            IsSupplier = values.IsSupplier,
            BillingAddress = ToAddress(values.BillingAddress),
            ShippingAddress = ToAddress(values.ShippingAddress),
            EmailAddresses = BuildEmails(values, RequiredTenant(), RequiredCompany(), contactId),
            PhoneNumbers = BuildPhones(values, RequiredTenant(), RequiredCompany(), contactId),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedBy = CurrentUserId(),
            UpdatedBy = CurrentUserId(),
            ConcurrencyStamp = NewStamp()
        };
        database.Contacts.Add(contact);
        await database.SaveChangesAsync(cancellationToken);
        await AuditAsync("contacts.created", contact, null, cancellationToken);
        var result = ToDto(contact);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<ContactDto> UpdateAsync(Guid id, ContactRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ConcurrencyStamp)) throw Validation("concurrencyStamp", "Concurrency stamp is required.");
        var values = Validate(request);
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var contact = await database.Contacts.Include(x => x.EmailAddresses).Include(x => x.PhoneNumbers)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Contact was not found.");
        if (!StringComparer.Ordinal.Equals(contact.ConcurrencyStamp, request.ConcurrencyStamp)) throw new ContactConcurrencyException();
        var before = JsonSerializer.Serialize(ToDto(contact));
        contact.Kind = request.Kind; contact.DisplayName = values.DisplayName!; contact.FirstName = values.FirstName;
        contact.LastName = values.LastName; contact.OrganizationName = values.OrganizationName; contact.Email = values.Email;
        contact.Phone = values.Phone; contact.TaxNumber = values.TaxNumber; contact.Notes = values.Notes;
        contact.LegalName = values.LegalName; contact.Website = values.Website;
        contact.IsCustomer = values.IsCustomer; contact.IsSupplier = values.IsSupplier;
        ApplyAddress(contact.BillingAddress, values.BillingAddress);
        ApplyAddress(contact.ShippingAddress, values.ShippingAddress);
        database.ContactEmails.RemoveRange(contact.EmailAddresses);
        database.ContactPhones.RemoveRange(contact.PhoneNumbers);
        database.ContactEmails.AddRange(BuildEmails(values, contact.TenantId, contact.CompanyId, contact.Id));
        database.ContactPhones.AddRange(BuildPhones(values, contact.TenantId, contact.CompanyId, contact.Id));
        contact.UpdatedBy = CurrentUserId();
        contact.UpdatedAtUtc = clock.UtcNow; contact.ConcurrencyStamp = NewStamp();
        await SaveWithConcurrencyAsync(cancellationToken);
        database.ChangeTracker.Clear();
        contact = await database.Contacts.Include(x => x.EmailAddresses).Include(x => x.PhoneNumbers)
            .SingleAsync(x => x.Id == id, cancellationToken);
        await AuditAsync("contacts.updated", contact, before, cancellationToken);
        var result = ToDto(contact);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<ContactDto> SetArchivedAsync(Guid id, string concurrencyStamp, bool archived, CancellationToken cancellationToken)
    {
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var contact = await database.Contacts.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Contact was not found.");
        if (!StringComparer.Ordinal.Equals(contact.ConcurrencyStamp, concurrencyStamp)) throw new ContactConcurrencyException();
        var before = JsonSerializer.Serialize(ToDto(contact));
        contact.IsArchived = archived; contact.UpdatedAtUtc = clock.UtcNow; contact.ConcurrencyStamp = NewStamp();
        contact.UpdatedBy = CurrentUserId();
        await SaveWithConcurrencyAsync(cancellationToken);
        await AuditAsync(archived ? "contacts.archived" : "contacts.reactivated", contact, before, cancellationToken);
        var result = ToDto(contact);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<CsvImportResult> ImportCsvAsync(Stream csv, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(csv, Encoding.UTF8, true, leaveOpen: true);
        var header = await reader.ReadLineAsync(cancellationToken);
        if (header is null) return new(0, ["CSV is empty."]);
        var columns = ParseCsvLine(header).Select((value, index) => (value: value.Trim().ToLowerInvariant(), index))
            .ToDictionary(x => x.value, x => x.index, StringComparer.OrdinalIgnoreCase);
        if (!columns.ContainsKey("kind") || !columns.ContainsKey("displayname"))
            return new(0, ["Header must include kind and displayName."]);
        var errors = new List<string>(); var imported = 0; var lineNumber = 1;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            if (lineNumber > 10_001) return new(imported, [.. errors, "Import cannot exceed 10,000 data rows."]);
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var fields = ParseCsvLine(line);
                string? At(string name) => columns.TryGetValue(name, out var index) && index < fields.Count ? fields[index] : null;
                bool Flag(string name) => bool.TryParse(At(name), out var value) && value;
                if (!Enum.TryParse<ContactKind>(At("kind"), true, out var kind)) throw new FormatException("kind must be Person or Organization");
                await CreateAsync(new(kind, At("displayname"), At("firstname"), At("lastname"), At("organizationname"),
                    At("email"), At("phone"), At("taxnumber"), At("notes"), IsCustomer: Flag("iscustomer"),
                    IsSupplier: Flag("issupplier"), LegalName: At("legalname"), Website: At("website")), cancellationToken);
                imported++;
            }
            catch (Exception exception) when (exception is ContactValidationException or FormatException)
            {
                errors.Add($"Line {lineNumber}: {exception.Message}");
            }
        }
        return new(imported, errors);
    }

    public async Task<string> ExportCsvAsync(ContactQuery query, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder("kind,displayName,firstName,lastName,organizationName,legalName,email,phone,website,taxNumber,isCustomer,isSupplier,notes,isArchived\r\n");
        for (var pageNumber = 1; ; pageNumber++)
        {
            var page = await SearchAsync(query with { Page = pageNumber, PageSize = 100 }, cancellationToken);
            foreach (var item in page.Items)
                builder.AppendLine(string.Join(',', new[] { item.Kind.ToString(), item.DisplayName, item.FirstName, item.LastName,
                    item.OrganizationName, item.LegalName, item.Email, item.Phone, item.Website, item.TaxNumber,
                    item.IsCustomer.ToString(CultureInfo.InvariantCulture), item.IsSupplier.ToString(CultureInfo.InvariantCulture),
                    item.Notes, item.IsArchived.ToString(CultureInfo.InvariantCulture) }.Select(Csv)));
            if (page.Page * page.PageSize >= page.Total) break;
        }
        return builder.ToString();
    }

    public async Task ExportCsvAsync(ContactQuery query, Stream destination, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(destination, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync("kind,displayName,firstName,lastName,organizationName,legalName,email,phone,website,taxNumber,isCustomer,isSupplier,notes,isArchived".AsMemory(), cancellationToken);
        for (var pageNumber = 1; ; pageNumber++)
        {
            var page = await SearchAsync(query with { Page = pageNumber, PageSize = 100 }, cancellationToken);
            foreach (var item in page.Items)
            {
                var line = string.Join(',', new[] { item.Kind.ToString(), item.DisplayName, item.FirstName, item.LastName,
                    item.OrganizationName, item.LegalName, item.Email, item.Phone, item.Website, item.TaxNumber,
                    item.IsCustomer.ToString(CultureInfo.InvariantCulture), item.IsSupplier.ToString(CultureInfo.InvariantCulture),
                    item.Notes, item.IsArchived.ToString(CultureInfo.InvariantCulture) }.Select(Csv));
                await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
            }
            await writer.FlushAsync(cancellationToken);
            if (page.Page * page.PageSize >= page.Total) break;
        }
    }

    private async Task SaveWithConcurrencyAsync(CancellationToken cancellationToken)
    {
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ContactConcurrencyException(string.Join(",", exception.Entries.Select(entry => entry.Metadata.DisplayName())));
        }
    }

    private async Task AuditAsync(string action, Contact contact, string? before, CancellationToken cancellationToken)
    {
        audit.Append(new(action, "Contact", contact.Id.ToString(), null, before, JsonSerializer.Serialize(ToDto(contact))));
        await audit.FlushAsync(cancellationToken);
    }

    private Guid RequiredTenant() => tenantContext.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required.");
    private Guid RequiredCompany() => companyContext.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required.");
    private static string NewStamp() => Guid.NewGuid().ToString("N");
    private static ContactDto ToDto(Contact x) => new(x.Id, x.Kind, x.DisplayName, x.FirstName, x.LastName, x.OrganizationName,
        x.Email, x.Phone, x.TaxNumber, x.Notes, x.IsArchived, x.CreatedAtUtc, x.UpdatedAtUtc, x.ConcurrencyStamp,
        x.IsCustomer, x.IsSupplier, x.LegalName, x.Website, ToRequest(x.BillingAddress), ToRequest(x.ShippingAddress),
        x.CreatedBy, x.UpdatedBy, x.EmailAddresses.OrderByDescending(value => value.IsPrimary)
            .Select(value => new ContactEmailRequest(value.Address, value.IsPrimary)).ToList(),
        x.PhoneNumbers.OrderByDescending(value => value.IsPrimary)
            .Select(value => new ContactPhoneRequest(value.Number, value.IsPrimary)).ToList());
    private static void ValidateQuery(ContactQuery query)
    {
        if (query.Page < 1) throw Validation("page", "Page must be at least 1.");
        if (query.PageSize is < 1 or > 100) throw Validation("pageSize", "Page size must be between 1 and 100.");
        if (query.Search?.Length > 200) throw Validation("search", "Search cannot exceed 200 characters.");
        if (!new[] { "name", "created", "updated" }.Contains(query.Sort, StringComparer.OrdinalIgnoreCase))
            throw Validation("sort", "Sort must be name, created, or updated.");
    }
    internal static ContactRequest Validate(ContactRequest input)
    {
        string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var request = input with
        {
            DisplayName = Clean(input.DisplayName),
            FirstName = Clean(input.FirstName),
            LastName = Clean(input.LastName),
            OrganizationName = Clean(input.OrganizationName),
            LegalName = Clean(input.LegalName),
            Website = Clean(input.Website),
            Email = Clean(input.Email)?.ToLowerInvariant(),
            Phone = Clean(input.Phone),
            TaxNumber = Clean(input.TaxNumber),
            Notes = Clean(input.Notes)
        };
        var errors = new Dictionary<string, string[]>();
        if (request.DisplayName is null || request.DisplayName.Length > 200) errors["displayName"] = ["Display name is required and cannot exceed 200 characters."];
        if (request.Kind == ContactKind.Person && request.FirstName is null && request.LastName is null) errors["name"] = ["A person requires a first or last name."];
        if (request.Kind == ContactKind.Organization && request.OrganizationName is null) errors["organizationName"] = ["An organization name is required."];
        if (!request.IsCustomer && !request.IsSupplier) errors["roles"] = ["A contact must be a customer, a supplier, or both."];
        if (request.LegalName?.Length > 200) errors["legalName"] = ["Legal name cannot exceed 200 characters."];
        if (request.Website is { } website && (website.Length > 500 || !Uri.TryCreate(website, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            errors["website"] = ["Website must be an absolute HTTP or HTTPS URL."];
        ValidateAddress(request.BillingAddress, "billingAddress", errors);
        ValidateAddress(request.ShippingAddress, "shippingAddress", errors);
        if (request.Email is { Length: > 254 } || request.Email is not null && !MailAddress.TryCreate(request.Email, out _)) errors["email"] = ["Email is invalid."];
        ValidateEmails(request.EmailAddresses, errors);
        ValidatePhones(request.PhoneNumbers, errors);
        if (request.Phone?.Length > 50) errors["phone"] = ["Phone cannot exceed 50 characters."];
        if (request.TaxNumber?.Length > 100) errors["taxNumber"] = ["Tax number cannot exceed 100 characters."];
        if (request.Notes?.Length > 4000) errors["notes"] = ["Notes cannot exceed 4000 characters."];
        if (errors.Count > 0) throw new ContactValidationException(errors);
        return request;
    }
    private Guid? CurrentUserId() => Guid.TryParse(
        httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : null;
    private static ContactAddress ToAddress(ContactAddressRequest? value) => value is null ? new() : new()
    {
        Line1 = Clean(value.Line1),
        Line2 = Clean(value.Line2),
        City = Clean(value.City),
        StateOrProvince = Clean(value.StateOrProvince),
        PostalCode = Clean(value.PostalCode),
        CountryCode = Clean(value.CountryCode)?.ToUpperInvariant()
    };
    private static ContactAddressRequest? ToRequest(ContactAddress? value) => value is null ? null :
        new(value.Line1, value.Line2, value.City, value.StateOrProvince, value.PostalCode, value.CountryCode);
    private static void ApplyAddress(ContactAddress target, ContactAddressRequest? value)
    {
        var source = ToAddress(value);
        target.Line1 = source.Line1; target.Line2 = source.Line2; target.City = source.City;
        target.StateOrProvince = source.StateOrProvince; target.PostalCode = source.PostalCode;
        target.CountryCode = source.CountryCode;
    }
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static void ValidateAddress(ContactAddressRequest? address, string key, Dictionary<string, string[]> errors)
    {
        if (address is null) return;
        if (address.Line1?.Length > 200 || address.Line2?.Length > 200 || address.City?.Length > 100 ||
            address.StateOrProvince?.Length > 100 || address.PostalCode?.Length > 30)
            errors[key] = ["Address fields exceed their allowed length."];
        var country = Clean(address.CountryCode);
        if (country is not null && (country.Length != 2 || !country.All(char.IsAsciiLetter)))
            errors[$"{key}.countryCode"] = ["Country code must contain two ASCII letters."];
    }
    private static void ValidateEmails(IReadOnlyList<ContactEmailRequest>? values, Dictionary<string, string[]> errors)
    {
        if (values is null) return;
        if (values.Count > 20) errors["emailAddresses"] = ["A contact cannot have more than 20 email addresses."];
        if (values.Count(value => value.IsPrimary) > 1) errors["emailAddresses.primary"] = ["Only one email address can be primary."];
        if (values.Any(value => !MailAddress.TryCreate(Clean(value.Address), out _))) errors["emailAddresses"] = ["Every email address must be valid."];
    }
    private static void ValidatePhones(IReadOnlyList<ContactPhoneRequest>? values, Dictionary<string, string[]> errors)
    {
        if (values is null) return;
        if (values.Count > 20) errors["phoneNumbers"] = ["A contact cannot have more than 20 phone numbers."];
        if (values.Count(value => value.IsPrimary) > 1) errors["phoneNumbers.primary"] = ["Only one phone number can be primary."];
        if (values.Any(value => Clean(value.Number) is not { Length: > 0 and <= 50 })) errors["phoneNumbers"] = ["Every phone number is required and cannot exceed 50 characters."];
    }
    private static List<ContactEmailAddress> BuildEmails(ContactRequest request, Guid tenant, Guid company, Guid contactId)
    {
        var values = request.EmailAddresses?.ToList() ?? [];
        if (values.Count == 0 && request.Email is not null) values.Add(new(request.Email, true));
        return values.Select(value => new ContactEmailAddress
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            CompanyId = company,
            ContactId = contactId,
            Address = Clean(value.Address)!.ToLowerInvariant(),
            IsPrimary = value.IsPrimary
        }).ToList();
    }
    private static List<ContactPhoneNumber> BuildPhones(ContactRequest request, Guid tenant, Guid company, Guid contactId)
    {
        var values = request.PhoneNumbers?.ToList() ?? [];
        if (values.Count == 0 && request.Phone is not null) values.Add(new(request.Phone, true));
        return values.Select(value => new ContactPhoneNumber
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            CompanyId = company,
            ContactId = contactId,
            Number = Clean(value.Number)!,
            IsPrimary = value.IsPrimary
        }).ToList();
    }
    private static ContactValidationException Validation(string key, string message) => new(new Dictionary<string, string[]> { [key] = [message] });
    internal static string Csv(string? value)
    {
        var safe = value ?? string.Empty;
        if (safe.Length > 0 && safe[0] is '=' or '+' or '-' or '@') safe = $"'{safe}";
        return $"\"{safe.Replace("\"", "\"\"")}\"";
    }
    internal static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var result = new List<string>(); var value = new StringBuilder(); var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"' && quoted && index + 1 < line.Length && line[index + 1] == '"') { value.Append('"'); index++; }
            else if (character == '"') quoted = !quoted;
            else if (character == ',' && !quoted) { result.Add(value.ToString()); value.Clear(); }
            else value.Append(character);
        }
        if (quoted) throw new FormatException("Unclosed quoted field.");
        result.Add(value.ToString()); return result;
    }
}
