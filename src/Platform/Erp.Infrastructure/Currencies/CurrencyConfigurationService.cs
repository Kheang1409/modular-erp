using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Currencies;
using Erp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Currencies;

public sealed partial class CurrencyConfigurationService(
    PlatformDbContext database,
    ITenantContext tenantContext,
    ICompanyContext companyContext,
    IAuditLogWriter auditLog) : ICurrencyConfigurationService
{
    public async Task<CurrencyConfiguration> GetAsync(CancellationToken cancellationToken)
    {
        var (_, companyId) = RequireContext();
        var baseCode = await database.Companies
            .Where(x => x.Id == companyId)
            .Select(x => x.BaseCurrencyCode)
            .SingleAsync(cancellationToken);
        var currencies = await database.CompanyCurrencies
            .Where(x => x.CompanyId == companyId)
            .OrderBy(x => x.Code)
            .Select(x => new CurrencyDefinition(x.Code, x.Name, x.Symbol, x.MinorUnits, x.IsActive, x.ConcurrencyStamp))
            .ToListAsync(cancellationToken);
        var rates = await database.CompanyExchangeRates
            .Where(x => x.CompanyId == companyId)
            .OrderByDescending(x => x.EffectiveDate)
            .ThenBy(x => x.FromCurrencyCode)
            .ThenBy(x => x.ToCurrencyCode)
            .Select(x => new ExchangeRateDefinition(x.Id, x.FromCurrencyCode, x.ToCurrencyCode, x.Rate, x.EffectiveDate))
            .ToListAsync(cancellationToken);
        return new(baseCode, currencies, rates);
    }

    public async Task<CurrencyDefinition> ConfigureAsync(ConfigureCurrencyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (tenantId, companyId) = RequireContext();
        var code = NormalizeCode(request.Code);
        var name = request.Name.Trim();
        var symbol = request.Symbol.Trim();
        if (name.Length is < 2 or > 100) throw new ArgumentException("Currency name must contain between 2 and 100 characters.", nameof(request));
        if (symbol.Length is < 1 or > 10) throw new ArgumentException("Currency symbol must contain between 1 and 10 characters.", nameof(request));
        if (request.MinorUnits is < 0 or > 6) throw new ArgumentException("Currency minor units must be between 0 and 6.", nameof(request));

        var currency = await database.CompanyCurrencies.SingleOrDefaultAsync(
            x => x.CompanyId == companyId && x.Code == code, cancellationToken);
        if (currency is null)
        {
            if (request.ConcurrencyStamp is not null) throw new InvalidOperationException("The currency no longer exists. Refresh and try again.");
            currency = new CompanyCurrency
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                CompanyId = companyId,
                Code = code,
                Name = name,
                Symbol = symbol,
                MinorUnits = request.MinorUnits,
                IsActive = request.IsActive,
                ConcurrencyStamp = Guid.NewGuid().ToString("N")
            };
            database.CompanyCurrencies.Add(currency);
            auditLog.Append(new("currency.configured", "Currency", code, null, null,
                JsonSerializer.Serialize(new { currency.Code, currency.Name, currency.Symbol, currency.MinorUnits, currency.IsActive })));
        }
        else
        {
            if (!string.Equals(currency.ConcurrencyStamp, request.ConcurrencyStamp, StringComparison.Ordinal))
                throw new InvalidOperationException("The currency changed after it was loaded. Refresh and try again.");
            var baseCode = await database.Companies.Where(x => x.Id == companyId).Select(x => x.BaseCurrencyCode).SingleAsync(cancellationToken);
            if (!request.IsActive && code == baseCode)
                throw new InvalidOperationException("The base currency cannot be deactivated.");
            var before = JsonSerializer.Serialize(new { currency.Name, currency.Symbol, currency.MinorUnits, currency.IsActive });
            currency.Name = name;
            currency.Symbol = symbol;
            currency.MinorUnits = request.MinorUnits;
            currency.IsActive = request.IsActive;
            currency.ConcurrencyStamp = Guid.NewGuid().ToString("N");
            auditLog.Append(new("currency.changed", "Currency", code, null, before,
                JsonSerializer.Serialize(new { currency.Name, currency.Symbol, currency.MinorUnits, currency.IsActive })));
        }
        await SaveAsync(cancellationToken);
        return new(currency.Code, currency.Name, currency.Symbol, currency.MinorUnits, currency.IsActive, currency.ConcurrencyStamp);
    }

    public async Task<CurrencyConfiguration> SetBaseCurrencyAsync(SetBaseCurrencyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (_, companyId) = RequireContext();
        var code = NormalizeCode(request.Code);
        var expected = NormalizeCode(request.ExpectedBaseCurrencyCode);
        if (!await database.CompanyCurrencies.AnyAsync(x => x.CompanyId == companyId && x.Code == code && x.IsActive, cancellationToken))
            throw new ArgumentException("The base currency must be configured and active.", nameof(request));
        var affected = await database.Companies
            .Where(x => x.Id == companyId && x.BaseCurrencyCode == expected)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.BaseCurrencyCode, code), cancellationToken);
        if (affected == 0) throw new InvalidOperationException("The base currency changed after it was loaded. Refresh and try again.");
        auditLog.Append(new("currency.base.changed", "Company", companyId.ToString(), null,
            JsonSerializer.Serialize(new { BaseCurrencyCode = expected }), JsonSerializer.Serialize(new { BaseCurrencyCode = code })));
        await database.SaveChangesAsync(cancellationToken);
        return await GetAsync(cancellationToken);
    }

    public async Task<ExchangeRateDefinition> AddExchangeRateAsync(AddExchangeRateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (tenantId, companyId) = RequireContext();
        var from = NormalizeCode(request.FromCurrencyCode);
        var to = NormalizeCode(request.ToCurrencyCode);
        if (from == to) throw new ArgumentException("Exchange-rate currencies must be different.", nameof(request));
        if (request.Rate <= 0) throw new ArgumentException("Exchange rate must be greater than zero.", nameof(request));
        var activeCodes = await database.CompanyCurrencies
            .Where(x => x.CompanyId == companyId && x.IsActive && (x.Code == from || x.Code == to))
            .Select(x => x.Code).ToListAsync(cancellationToken);
        if (!activeCodes.Contains(from) || !activeCodes.Contains(to))
            throw new ArgumentException("Both exchange-rate currencies must be configured and active.", nameof(request));
        if (await database.CompanyExchangeRates.AnyAsync(x => x.CompanyId == companyId && x.FromCurrencyCode == from && x.ToCurrencyCode == to && x.EffectiveDate == request.EffectiveDate, cancellationToken))
            throw new InvalidOperationException("An exchange rate already exists for this currency pair and effective date.");
        var rate = new CompanyExchangeRate
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CompanyId = companyId,
            FromCurrencyCode = from,
            ToCurrencyCode = to,
            Rate = request.Rate,
            EffectiveDate = request.EffectiveDate
        };
        database.CompanyExchangeRates.Add(rate);
        auditLog.Append(new("exchange_rate.created", "ExchangeRate", rate.Id.ToString(), null, null,
            JsonSerializer.Serialize(new { From = from, To = to, rate.Rate, rate.EffectiveDate })));
        await SaveAsync(cancellationToken);
        return new(rate.Id, from, to, rate.Rate, rate.EffectiveDate);
    }

    private (Guid TenantId, Guid CompanyId) RequireContext() => (
        tenantContext.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required."),
        companyContext.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required."));

    private static string NormalizeCode(string value)
    {
        var code = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!CurrencyCodePattern().IsMatch(code)) throw new ArgumentException("Currency code must contain exactly three ASCII letters.", nameof(value));
        return code;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException exception) { throw new InvalidOperationException("The currency changed after it was loaded. Refresh and try again.", exception); }
        catch (DbUpdateException exception) when (exception.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true)
        { throw new InvalidOperationException("The currency configuration conflicts with an existing record.", exception); }
    }

    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyCodePattern();
}
