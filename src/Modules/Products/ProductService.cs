using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Currencies;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Products;

public interface IProductService
{
    Task<ProductOptions> GetOptionsAsync(CancellationToken cancellationToken);
    Task<ProductPage> SearchAsync(ProductQuery query, CancellationToken cancellationToken);
    Task<ProductDto?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<ProductDto> CreateAsync(ProductRequest request, CancellationToken cancellationToken);
    Task<ProductDto> UpdateAsync(Guid id, ProductRequest request, CancellationToken cancellationToken);
    Task<ProductDto> SetArchivedAsync(Guid id, string concurrencyStamp, bool archived, CancellationToken cancellationToken);
    Task<ProductCsvImportResult> ImportCsvAsync(Stream csv, CancellationToken cancellationToken);
    Task ExportCsvAsync(ProductQuery query, Stream destination, CancellationToken cancellationToken);
}

public sealed class ProductService(
    ProductsDbContext database, ITenantContext tenantContext, ICompanyContext companyContext,
    ICurrencyConfigurationService currencies, IClock clock, IAuditLogWriter audit,
    IHttpContextAccessor httpContextAccessor) : IProductService
{
    public async Task<ProductOptions> GetOptionsAsync(CancellationToken cancellationToken)
    {
        var configuration = await currencies.GetAsync(cancellationToken);
        var currencyOptions = configuration.Currencies
            .Where(currency => currency.IsActive)
            .OrderBy(currency => currency.Code)
            .Select(currency => new ProductCurrencyOption(currency.Code, currency.Name, currency.Symbol))
            .ToList();
        var units = await database.UnitsOfMeasure.AsNoTracking()
            .Where(unit => unit.IsActive)
            .OrderBy(unit => unit.Category)
            .ThenBy(unit => unit.Name)
            .Select(unit => new ProductUnitOption(unit.Code, unit.Name, unit.Category, unit.DecimalPrecision))
            .ToListAsync(cancellationToken);
        return new(configuration.BaseCurrencyCode, currencyOptions, units);
    }

    public async Task<ProductPage> SearchAsync(ProductQuery query, CancellationToken cancellationToken)
    {
        ValidateQuery(query);
        var products = database.Products.AsNoTracking();
        if (!query.IncludeArchived) products = products.Where(x => !x.IsArchived);
        if (query.Type is not null) products = products.Where(x => x.Type == query.Type);
        if (query.TracksInventory is not null) products = products.Where(x => x.TracksInventory == query.TracksInventory);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            products = products.Where(x => EF.Functions.ILike(x.Name, $"%{search}%") ||
                EF.Functions.ILike(x.Sku, $"%{search}%") ||
                (x.Barcode != null && EF.Functions.ILike(x.Barcode, $"%{search}%")) ||
                (x.Description != null && EF.Functions.ILike(x.Description, $"%{search}%")));
        }
        products = (query.Sort.ToLowerInvariant(), query.Descending) switch
        {
            ("sku", false) => products.OrderBy(x => x.Sku).ThenBy(x => x.Id),
            ("sku", true) => products.OrderByDescending(x => x.Sku).ThenByDescending(x => x.Id),
            ("price", false) => products.OrderBy(x => x.SalePrice).ThenBy(x => x.Id),
            ("price", true) => products.OrderByDescending(x => x.SalePrice).ThenByDescending(x => x.Id),
            ("created", false) => products.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id),
            ("created", true) => products.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id),
            ("updated", false) => products.OrderBy(x => x.UpdatedAtUtc).ThenBy(x => x.Id),
            ("updated", true) => products.OrderByDescending(x => x.UpdatedAtUtc).ThenByDescending(x => x.Id),
            ("name", true) => products.OrderByDescending(x => x.Name).ThenByDescending(x => x.Id),
            _ => products.OrderBy(x => x.Name).ThenBy(x => x.Id)
        };
        var total = await products.CountAsync(cancellationToken);
        var items = await products.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(x => ToDto(x)).ToListAsync(cancellationToken);
        return new(query.Page, query.PageSize, total, items);
    }

    public async Task<ProductDto?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await database.Products.AsNoTracking().Where(x => x.Id == id).Select(x => ToDto(x))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<ProductDto> CreateAsync(ProductRequest request, CancellationToken cancellationToken)
    {
        var values = Validate(request);
        await ValidateReferencesAsync(values, cancellationToken);
        await EnsureUniqueAsync(values.Sku!, values.Barcode, null, cancellationToken);
        var now = clock.UtcNow;
        var product = new Product
        {
            Id = Guid.NewGuid(),
            TenantId = RequiredTenant(),
            CompanyId = RequiredCompany(),
            Type = values.Type,
            Sku = values.Sku!,
            Name = values.Name!,
            Description = values.Description,
            Barcode = values.Barcode,
            SalePrice = values.SalePrice,
            Cost = values.Cost,
            CurrencyCode = values.CurrencyCode!,
            SalesUnitCode = values.SalesUnitCode!,
            PurchaseUnitCode = values.PurchaseUnitCode!,
            TracksInventory = values.TracksInventory,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedBy = CurrentUserId(),
            UpdatedBy = CurrentUserId(),
            ConcurrencyStamp = NewStamp()
        };
        database.Products.Add(product);
        await SaveAsync(cancellationToken);
        await AuditAsync("products.created", product, null, cancellationToken);
        return ToDto(product);
    }

    public async Task<ProductDto> UpdateAsync(Guid id, ProductRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ConcurrencyStamp))
            throw Validation("concurrencyStamp", "Concurrency stamp is required.");
        var values = Validate(request);
        await ValidateReferencesAsync(values, cancellationToken);
        var product = await database.Products.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Product was not found.");
        if (!StringComparer.Ordinal.Equals(product.ConcurrencyStamp, values.ConcurrencyStamp))
            throw new ProductConcurrencyException();
        await EnsureUniqueAsync(values.Sku!, values.Barcode, id, cancellationToken);
        var before = JsonSerializer.Serialize(ToDto(product));
        product.Type = values.Type; product.Sku = values.Sku!; product.Name = values.Name!;
        product.Description = values.Description; product.Barcode = values.Barcode;
        product.SalePrice = values.SalePrice; product.Cost = values.Cost;
        product.CurrencyCode = values.CurrencyCode!; product.SalesUnitCode = values.SalesUnitCode!;
        product.PurchaseUnitCode = values.PurchaseUnitCode!; product.TracksInventory = values.TracksInventory;
        product.UpdatedAtUtc = clock.UtcNow; product.UpdatedBy = CurrentUserId(); product.ConcurrencyStamp = NewStamp();
        await SaveAsync(cancellationToken);
        await AuditAsync("products.updated", product, before, cancellationToken);
        return ToDto(product);
    }

    public async Task<ProductDto> SetArchivedAsync(Guid id, string concurrencyStamp, bool archived, CancellationToken cancellationToken)
    {
        var product = await database.Products.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Product was not found.");
        if (!StringComparer.Ordinal.Equals(product.ConcurrencyStamp, concurrencyStamp))
            throw new ProductConcurrencyException();
        var before = JsonSerializer.Serialize(ToDto(product));
        product.IsArchived = archived; product.UpdatedAtUtc = clock.UtcNow;
        product.UpdatedBy = CurrentUserId(); product.ConcurrencyStamp = NewStamp();
        await SaveAsync(cancellationToken);
        await AuditAsync(archived ? "products.archived" : "products.reactivated", product, before, cancellationToken);
        return ToDto(product);
    }

    public async Task<ProductCsvImportResult> ImportCsvAsync(Stream csv, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(csv, Encoding.UTF8, true, leaveOpen: true);
        var header = await reader.ReadLineAsync(cancellationToken);
        if (header is null) return new(0, ["CSV is empty."]);
        var columns = ParseCsvLine(header).Select((value, index) => (value: value.Trim().ToLowerInvariant(), index))
            .ToDictionary(x => x.value, x => x.index, StringComparer.OrdinalIgnoreCase);
        if (!columns.ContainsKey("type") || !columns.ContainsKey("sku") || !columns.ContainsKey("name"))
            return new(0, ["Header must include type, sku, and name."]);
        var imported = 0; var errors = new List<string>(); var lineNumber = 1;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            if (lineNumber > 10_001) return new(imported, [.. errors, "Import cannot exceed 10,000 data rows."]);
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var fields = ParseCsvLine(line);
                string? At(string name) => columns.TryGetValue(name, out var index) && index < fields.Count ? fields[index] : null;
                if (!Enum.TryParse<ProductType>(At("type"), true, out var type)) throw new FormatException("type must be Product or Service");
                decimal Money(string name) => decimal.TryParse(At(name), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
                    ? value : throw new FormatException($"{name} must be a decimal number");
                bool Flag(string name) => bool.TryParse(At(name), out var value) && value;
                await CreateAsync(new(type, At("sku"), At("name"), At("description"), At("barcode"),
                    Money("saleprice"), Money("cost"), At("currencycode"), At("salesunitcode"),
                    At("purchaseunitcode"), Flag("tracksinventory")), cancellationToken);
                imported++;
            }
            catch (Exception exception) when (exception is ProductValidationException or ProductConflictException or FormatException)
            {
                errors.Add($"Line {lineNumber}: {exception.Message}");
            }
        }
        return new(imported, errors);
    }

    public async Task ExportCsvAsync(ProductQuery query, Stream destination, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(destination, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync("type,sku,name,description,barcode,salePrice,cost,currencyCode,salesUnitCode,purchaseUnitCode,tracksInventory,isArchived".AsMemory(), cancellationToken);
        for (var pageNumber = 1; ; pageNumber++)
        {
            var page = await SearchAsync(query with { Page = pageNumber, PageSize = 100 }, cancellationToken);
            foreach (var item in page.Items)
            {
                var line = string.Join(',', new[] { item.Type.ToString(), item.Sku, item.Name, item.Description,
                    item.Barcode, item.SalePrice.ToString(CultureInfo.InvariantCulture), item.Cost.ToString(CultureInfo.InvariantCulture),
                    item.CurrencyCode, item.SalesUnitCode, item.PurchaseUnitCode,
                    item.TracksInventory.ToString(CultureInfo.InvariantCulture), item.IsArchived.ToString(CultureInfo.InvariantCulture) }.Select(Csv));
                await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
            }
            await writer.FlushAsync(cancellationToken);
            if (page.Page * page.PageSize >= page.Total) break;
        }
    }

    internal static ProductRequest Validate(ProductRequest input)
    {
        string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var request = input with
        {
            Sku = Clean(input.Sku)?.ToUpperInvariant(),
            Name = Clean(input.Name),
            Description = Clean(input.Description),
            Barcode = Clean(input.Barcode),
            CurrencyCode = Clean(input.CurrencyCode)?.ToUpperInvariant(),
            SalesUnitCode = Clean(input.SalesUnitCode)?.ToUpperInvariant(),
            PurchaseUnitCode = Clean(input.PurchaseUnitCode)?.ToUpperInvariant()
        };
        var errors = new Dictionary<string, string[]>();
        if (request.Sku is null || request.Sku.Length > 100) errors["sku"] = ["SKU is required and cannot exceed 100 characters."];
        if (request.Name is null || request.Name.Length > 200) errors["name"] = ["Name is required and cannot exceed 200 characters."];
        if (request.Description?.Length > 4000) errors["description"] = ["Description cannot exceed 4,000 characters."];
        if (request.Barcode?.Length > 100) errors["barcode"] = ["Barcode cannot exceed 100 characters."];
        if (request.SalePrice < 0) errors["salePrice"] = ["Sale price cannot be negative."];
        if (request.Cost < 0) errors["cost"] = ["Cost cannot be negative."];
        if (DecimalPlaces(request.SalePrice) > 4 || DecimalPlaces(request.Cost) > 4) errors["prices"] = ["Prices support at most four decimal places."];
        if (request.CurrencyCode is not { Length: 3 } || !request.CurrencyCode.All(char.IsAsciiLetter)) errors["currencyCode"] = ["Currency code must contain three letters."];
        if (request.SalesUnitCode is null) errors["salesUnitCode"] = ["Sales unit is required."];
        if (request.PurchaseUnitCode is null) errors["purchaseUnitCode"] = ["Purchase unit is required."];
        if (request.Type == ProductType.Service && request.TracksInventory) errors["tracksInventory"] = ["Services cannot track inventory."];
        if (errors.Count > 0) throw new ProductValidationException(errors);
        return request;
    }

    private async Task ValidateReferencesAsync(ProductRequest request, CancellationToken cancellationToken)
    {
        var configuration = await currencies.GetAsync(cancellationToken);
        if (!configuration.Currencies.Any(x => x.IsActive && x.Code == request.CurrencyCode))
            throw Validation("currencyCode", "Currency must be configured and active for the company.");
        var unitCodes = new[] { request.SalesUnitCode!, request.PurchaseUnitCode! }.Distinct().ToArray();
        if (await database.UnitsOfMeasure.CountAsync(x => unitCodes.Contains(x.Code) && x.IsActive, cancellationToken) != unitCodes.Length)
            throw Validation("units", "Sales and purchase units must be active units of measure.");
    }

    private async Task EnsureUniqueAsync(string sku, string? barcode, Guid? excludedId, CancellationToken cancellationToken)
    {
        if (await database.Products.AnyAsync(x => x.Id != excludedId && x.Sku == sku, cancellationToken))
            throw new ProductConflictException($"SKU '{sku}' already exists in this company.");
        if (barcode is not null && await database.Products.AnyAsync(x => x.Id != excludedId && x.Barcode == barcode, cancellationToken))
            throw new ProductConflictException($"Barcode '{barcode}' already exists in this company.");
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { throw new ProductConcurrencyException(); }
        catch (DbUpdateException exception) when (exception.InnerException?.Message.Contains("IX_products_", StringComparison.Ordinal) == true)
        { throw new ProductConflictException("The SKU or barcode already exists in this company."); }
    }

    private async Task AuditAsync(string action, Product product, string? before, CancellationToken cancellationToken)
    {
        audit.Append(new(action, "Product", product.Id.ToString(), null, before, JsonSerializer.Serialize(ToDto(product))));
        await audit.FlushAsync(cancellationToken);
    }

    private Guid RequiredTenant() => tenantContext.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required.");
    private Guid RequiredCompany() => companyContext.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required.");
    private Guid? CurrentUserId() => Guid.TryParse(httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    private static string NewStamp() => Guid.NewGuid().ToString("N");
    private static ProductDto ToDto(Product x) => new(x.Id, x.Type, x.Sku, x.Name, x.Description, x.Barcode,
        x.SalePrice, x.Cost, x.CurrencyCode, x.SalesUnitCode, x.PurchaseUnitCode, x.TracksInventory,
        x.IsArchived, x.CreatedAtUtc, x.UpdatedAtUtc, x.CreatedBy, x.UpdatedBy, x.ConcurrencyStamp);
    private static int DecimalPlaces(decimal value) => (decimal.GetBits(value)[3] >> 16) & 0x7F;
    private static void ValidateQuery(ProductQuery query)
    {
        if (query.Page < 1) throw Validation("page", "Page must be at least 1.");
        if (query.PageSize is < 1 or > 100) throw Validation("pageSize", "Page size must be between 1 and 100.");
        if (query.Search?.Length > 200) throw Validation("search", "Search cannot exceed 200 characters.");
        if (!new[] { "name", "sku", "price", "created", "updated" }.Contains(query.Sort, StringComparer.OrdinalIgnoreCase))
            throw Validation("sort", "Sort must be name, sku, price, created, or updated.");
    }
    private static ProductValidationException Validation(string key, string message) => new(new Dictionary<string, string[]> { [key] = [message] });
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
