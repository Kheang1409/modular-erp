namespace Erp.Abstractions.Currencies;

public interface ICurrencyConfigurationService
{
    Task<CurrencyConfiguration> GetAsync(CancellationToken cancellationToken);
    Task<CurrencyDefinition> ConfigureAsync(ConfigureCurrencyRequest request, CancellationToken cancellationToken);
    Task<CurrencyConfiguration> SetBaseCurrencyAsync(SetBaseCurrencyRequest request, CancellationToken cancellationToken);
    Task<ExchangeRateDefinition> AddExchangeRateAsync(AddExchangeRateRequest request, CancellationToken cancellationToken);
}

public sealed record CurrencyConfiguration(
    string BaseCurrencyCode,
    IReadOnlyList<CurrencyDefinition> Currencies,
    IReadOnlyList<ExchangeRateDefinition> ExchangeRates);

public sealed record CurrencyDefinition(
    string Code,
    string Name,
    string Symbol,
    int MinorUnits,
    bool IsActive,
    string ConcurrencyStamp);

public sealed record ExchangeRateDefinition(
    Guid Id,
    string FromCurrencyCode,
    string ToCurrencyCode,
    decimal Rate,
    DateOnly EffectiveDate);

public sealed record ConfigureCurrencyRequest(
    string Code,
    string Name,
    string Symbol,
    int MinorUnits,
    bool IsActive,
    string? ConcurrencyStamp = null);

public sealed record SetBaseCurrencyRequest(string Code, string ExpectedBaseCurrencyCode);

public sealed record AddExchangeRateRequest(
    string FromCurrencyCode,
    string ToCurrencyCode,
    decimal Rate,
    DateOnly EffectiveDate);
