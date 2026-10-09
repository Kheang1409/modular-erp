using System.Security.Claims;
using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Currencies;
using Erp.Abstractions.Sequences;
using Erp.Abstractions.Events;
using Erp.Modules.Contacts.Contracts;
using Erp.Modules.Products.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Sales;

public interface IQuotationService
{
    Task<QuotationPage> SearchAsync(QuotationQuery query, CancellationToken token);
    Task<QuotationDto?> GetAsync(Guid id, CancellationToken token);
    Task<QuotationDto> CreateAsync(QuotationRequest request, CancellationToken token);
    Task<QuotationDto> UpdateAsync(Guid id, QuotationRequest request, CancellationToken token);
    Task<QuotationDto> TransitionAsync(Guid id, QuotationState state, string stamp, CancellationToken token);
    Task<QuotationDto> SetArchivedAsync(Guid id, string stamp, bool archived, CancellationToken token);
}

public sealed class QuotationService(SalesDbContext database, ITenantContext tenant, ICompanyContext company, IContactDirectory contacts, IProductDirectory products, ICurrencyConfigurationService currencies, ISequenceService sequences, IClock clock, IAuditLogWriter audit, IOutboxWriter outbox, IHttpContextAccessor http) : IQuotationService
{
    public async Task<QuotationPage> SearchAsync(QuotationQuery query, CancellationToken token)
    {
        ValidateQuery(query); var source = database.Quotations.AsNoTracking().Include(x => x.Lines).AsQueryable();
        if (!query.IncludeArchived) source = source.Where(x => !x.IsArchived); if (query.State is not null) source = source.Where(x => x.State == query.State);
        if (!string.IsNullOrWhiteSpace(query.Search)) { var search = query.Search.Trim(); source = source.Where(x => EF.Functions.ILike(x.Number, $"%{search}%") || EF.Functions.ILike(x.CustomerName, $"%{search}%")); }
        source = (query.Sort.ToLowerInvariant(), query.Descending) switch { ("number", false) => source.OrderBy(x => x.Number), ("number", true) => source.OrderByDescending(x => x.Number), ("total", false) => source.OrderBy(x => x.Total), ("total", true) => source.OrderByDescending(x => x.Total), ("date", false) => source.OrderBy(x => x.QuotationDate), ("date", true) => source.OrderByDescending(x => x.QuotationDate), ("updated", false) => source.OrderBy(x => x.UpdatedAtUtc), _ => source.OrderByDescending(x => x.UpdatedAtUtc) };
        var total = await source.CountAsync(token); var items = await source.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(token); return new(query.Page, query.PageSize, total, items.Select(Dto).ToList());
    }
    public async Task<QuotationDto?> GetAsync(Guid id, CancellationToken token) => (await database.Quotations.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, token)) is { } item ? Dto(item) : null;
    public async Task<QuotationDto> CreateAsync(QuotationRequest request, CancellationToken token)
    {
        Validate(request, false); var customer = await Customer(request.CustomerId, token); var currency = await Currency(request.CurrencyCode, request.QuotationDate, token); var now = clock.UtcNow;
        var item = new Quotation { Id = Guid.NewGuid(), TenantId = Tenant(), CompanyId = Company(), Number = await sequences.NextAsync(new("sales.quotation", "QUO-{yyyy}-", 6, request.QuotationDate), token), CustomerId = customer.Id, CustomerName = customer.DisplayName, CurrencyCode = currency.code, BaseCurrencyCode = currency.baseCode, ExchangeRate = currency.rate, QuotationDate = request.QuotationDate, ValidUntil = request.ValidUntil, State = QuotationState.Draft, Notes = Clean(request.Notes), Terms = Clean(request.Terms), CreatedAtUtc = now, UpdatedAtUtc = now, CreatedBy = User(), UpdatedBy = User(), ConcurrencyStamp = Stamp() };
        item.Lines = await BuildLines(item, request.Lines, token); Calculate(item); database.Quotations.Add(item); await Save(token); outbox.Add(new QuotationCreatedEvent(Guid.NewGuid(), clock.UtcNow, item.Id, item.Number, item.CustomerId, item.CurrencyCode, item.Total)); await Audit("sales.quotation.created", item, token); return Dto(item);
    }
    public async Task<QuotationDto> UpdateAsync(Guid id, QuotationRequest request, CancellationToken token)
    {
        Validate(request, true); var item = await Find(id, token); Check(item, request.ConcurrencyStamp!); if (item.State != QuotationState.Draft) throw Error("state", "Only draft quotations can be edited.");
        var customer = await Customer(request.CustomerId, token); var currency = await Currency(request.CurrencyCode, request.QuotationDate, token); database.QuotationLines.RemoveRange(item.Lines);
        item.CustomerId = customer.Id; item.CustomerName = customer.DisplayName; item.CurrencyCode = currency.code; item.BaseCurrencyCode = currency.baseCode; item.ExchangeRate = currency.rate; item.QuotationDate = request.QuotationDate; item.ValidUntil = request.ValidUntil; item.Notes = Clean(request.Notes); item.Terms = Clean(request.Terms); item.Lines = await BuildLines(item, request.Lines, token); Touch(item); Calculate(item); await Save(token); await Audit("sales.quotation.updated", item, token); return Dto(item);
    }
    public async Task<QuotationDto> TransitionAsync(Guid id, QuotationState state, string stamp, CancellationToken token)
    {
        var item = await Find(id, token); Check(item, stamp); var allowed = (item.State, state) switch { (QuotationState.Draft, QuotationState.Sent) => true, (QuotationState.Sent, QuotationState.Accepted or QuotationState.Rejected) => true, (QuotationState.Draft or QuotationState.Sent, QuotationState.Cancelled) => true, (QuotationState.Draft or QuotationState.Sent, QuotationState.Expired) => item.ValidUntil is not null && item.ValidUntil < DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), _ => false }; if (!allowed) throw Error("state", $"Cannot move quotation from {item.State} to {state}."); item.State = state; Touch(item); await Save(token); if (state == QuotationState.Sent) outbox.Add(new QuotationSentEvent(Guid.NewGuid(), clock.UtcNow, item.Id, item.Number)); if (state == QuotationState.Accepted) outbox.Add(new QuotationAcceptedEvent(Guid.NewGuid(), clock.UtcNow, item.Id, item.Number, item.CustomerId, item.CurrencyCode, item.Total)); await Audit($"sales.quotation.{state.ToString().ToLowerInvariant()}", item, token); return Dto(item);
    }
    public async Task<QuotationDto> SetArchivedAsync(Guid id, string stamp, bool archived, CancellationToken token) { var item = await Find(id, token); Check(item, stamp); if (archived && item.State is QuotationState.Draft or QuotationState.Sent) throw Error("state", "Active quotations must be cancelled before archiving."); item.IsArchived = archived; Touch(item); await Save(token); await Audit(archived ? "sales.quotation.archived" : "sales.quotation.reactivated", item, token); return Dto(item); }

    internal static (decimal subtotal, decimal discount, decimal tax, decimal total) CalculateLine(decimal quantity, decimal price, decimal discountPercent, decimal taxPercent)
    { if (quantity <= 0 || price < 0 || discountPercent is < 0 or > 100 || taxPercent is < 0 or > 100) throw Error("lines", "Quantity, price, discount, or tax is invalid."); var subtotal = Round(quantity * price); var discount = Round(subtotal * discountPercent / 100m); var tax = Round((subtotal - discount) * taxPercent / 100m); return (subtotal, discount, tax, Round(subtotal - discount + tax)); }
    private async Task<List<QuotationLine>> BuildLines(Quotation owner, IReadOnlyList<QuotationLineRequest> requests, CancellationToken token)
    {
        var ids = requests.Select(x => x.ProductId).Distinct().ToArray(); var references = await products.FindAsync(ids, token); var lines = new List<QuotationLine>(); var sequence = 0;
        foreach (var request in requests) { if (!references.TryGetValue(request.ProductId, out var product) || product.IsArchived) throw Error("lines", "Every product must exist and be active in the current company."); if (!StringComparer.OrdinalIgnoreCase.Equals(product.CurrencyCode, owner.CurrencyCode) && request.UnitPrice is null) throw Error("lines", "A unit price is required when product and quotation currencies differ."); var price = request.UnitPrice ?? product.SalePrice; var values = CalculateLine(request.Quantity, price, request.DiscountPercent, request.TaxPercent); lines.Add(new() { Id = Guid.NewGuid(), TenantId = owner.TenantId, CompanyId = owner.CompanyId, QuotationId = owner.Id, Sequence = ++sequence, ProductId = product.Id, ProductSku = product.Sku, Description = string.IsNullOrWhiteSpace(request.Description) ? product.Name : request.Description.Trim(), UnitCode = product.SalesUnitCode, Quantity = request.Quantity, UnitPrice = price, DiscountPercent = request.DiscountPercent, TaxPercent = request.TaxPercent, Subtotal = values.subtotal, DiscountAmount = values.discount, TaxAmount = values.tax, Total = values.total }); }
        return lines;
    }
    private static void Calculate(Quotation item) { item.Subtotal = Round(item.Lines.Sum(x => x.Subtotal)); item.DiscountTotal = Round(item.Lines.Sum(x => x.DiscountAmount)); item.TaxTotal = Round(item.Lines.Sum(x => x.TaxAmount)); item.Total = Round(item.Lines.Sum(x => x.Total)); }
    private async Task<ContactReference> Customer(Guid id, CancellationToken token) { var value = await contacts.FindAsync(id, token); if (value is null || value.IsArchived || !value.IsCustomer) throw Error("customerId", "An active customer in the current company is required."); return value; }
    private async Task<(string code, string baseCode, decimal rate)> Currency(string? requested, DateOnly date, CancellationToken token) { var config = await currencies.GetAsync(token); var code = string.IsNullOrWhiteSpace(requested) ? config.BaseCurrencyCode : requested.Trim().ToUpperInvariant(); if (!config.Currencies.Any(x => x.IsActive && x.Code == code)) throw Error("currencyCode", "An active currency is required."); if (code == config.BaseCurrencyCode) return (code, config.BaseCurrencyCode, 1m); var direct = config.ExchangeRates.Where(x => x.FromCurrencyCode == code && x.ToCurrencyCode == config.BaseCurrencyCode && x.EffectiveDate <= date).MaxBy(x => x.EffectiveDate); if (direct is null) throw Error("currencyCode", "No effective exchange rate to the base currency exists."); return (code, config.BaseCurrencyCode, direct.Rate); }
    private static void Validate(QuotationRequest value, bool stamp) { var errors = new Dictionary<string, string[]>(); if (value.CustomerId == Guid.Empty) errors["customerId"] = ["Customer is required."]; if (value.QuotationDate == default) errors["quotationDate"] = ["Quotation date is required."]; if (value.ValidUntil < value.QuotationDate) errors["validUntil"] = ["Valid-until date cannot precede quotation date."]; if (value.Lines is null || value.Lines.Count is < 1 or > 500) errors["lines"] = ["Between 1 and 500 lines is required."]; if (value.Notes?.Length > 4000 || value.Terms?.Length > 4000) errors["notes"] = ["Notes and terms cannot exceed 4,000 characters."]; if (stamp && string.IsNullOrWhiteSpace(value.ConcurrencyStamp)) errors["concurrencyStamp"] = ["Concurrency stamp is required."]; if (errors.Count > 0) throw new SalesValidationException(errors); }
    private static void ValidateQuery(QuotationQuery value) { if (value.Page < 1 || value.PageSize is < 1 or > 100 || value.Sort.ToLowerInvariant() is not ("updated" or "date" or "number" or "total")) throw Error("page", "Invalid paging or sorting parameters."); }
    private async Task<Quotation> Find(Guid id, CancellationToken token) => await database.Quotations.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new KeyNotFoundException();
    private static void Check(Quotation item, string stamp) { if (!StringComparer.Ordinal.Equals(item.ConcurrencyStamp, stamp)) throw new SalesConcurrencyException(); }
    private void Touch(Quotation item) { item.UpdatedAtUtc = clock.UtcNow; item.UpdatedBy = User(); item.ConcurrencyStamp = Stamp(); }
    private async Task Save(CancellationToken token) { try { await database.SaveChangesAsync(token); } catch (DbUpdateConcurrencyException) { throw new SalesConcurrencyException(); } }
    private async Task Audit(string action, Quotation item, CancellationToken token) { audit.Append(new(action, "Quotation", item.Id.ToString(), null, null, JsonSerializer.Serialize(Dto(item)))); await audit.FlushAsync(token); }
    private Guid Tenant() => tenant.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required."); private Guid Company() => company.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required."); private Guid? User() => Guid.TryParse(http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    private static string Stamp() => Guid.NewGuid().ToString("N"); private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim(); private static decimal Round(decimal value) => decimal.Round(value, 4, MidpointRounding.AwayFromZero); private static SalesValidationException Error(string key, string message) => new(new Dictionary<string, string[]> { { key, [message] } });
    private static QuotationDto Dto(Quotation x) => new(x.Id, x.Number, x.CustomerId, x.CustomerName, x.CurrencyCode, x.BaseCurrencyCode, x.ExchangeRate, x.QuotationDate, x.ValidUntil, x.State, x.Subtotal, x.DiscountTotal, x.TaxTotal, x.Total, x.Notes, x.Terms, x.IsArchived, x.CreatedAtUtc, x.UpdatedAtUtc, x.ConcurrencyStamp, x.Lines.OrderBy(l => l.Sequence).Select(l => new QuotationLineDto(l.Id, l.Sequence, l.ProductId, l.ProductSku, l.Description, l.UnitCode, l.Quantity, l.UnitPrice, l.DiscountPercent, l.TaxPercent, l.Subtotal, l.DiscountAmount, l.TaxAmount, l.Total)).ToList());
}
