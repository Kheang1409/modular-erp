namespace Erp.Modules.Purchasing;

public enum SupplierContractState
{
    Draft,
    Active,
    Terminated
}

public sealed class SupplierContract
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid SupplierId { get; set; }
    public required string SupplierName { get; set; }
    public required string ContractNumber { get; set; }
    public required string Title { get; set; }
    public DateOnly StartsOn { get; set; }
    public DateOnly EndsOn { get; set; }
    public decimal? ContractValue { get; set; }
    public string? CurrencyCode { get; set; }
    public SupplierContractState State { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? TerminatedAtUtc { get; set; }
    public required string ConcurrencyStamp { get; set; }
}

public sealed class SupplierEvaluation
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid SupplierId { get; set; }
    public required string SupplierName { get; set; }
    public DateOnly AssessedOn { get; set; }
    public int QualityScore { get; set; }
    public int DeliveryScore { get; set; }
    public int ResponsivenessScore { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed record SupplierContractRequest(Guid SupplierId, string ContractNumber, string Title, DateOnly StartsOn, DateOnly EndsOn, decimal? ContractValue, string? CurrencyCode);
public sealed record SupplierContractDto(Guid Id, Guid SupplierId, string SupplierName, string ContractNumber, string Title, DateOnly StartsOn, DateOnly EndsOn, decimal? ContractValue, string? CurrencyCode, SupplierContractState State, bool IsExpired, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, string ConcurrencyStamp);
public sealed record SupplierContractActionRequest(string ConcurrencyStamp);
public sealed record SupplierEvaluationRequest(Guid SupplierId, DateOnly AssessedOn, int QualityScore, int DeliveryScore, int ResponsivenessScore, string? Notes);
public sealed record SupplierEvaluationDto(Guid Id, Guid SupplierId, string SupplierName, DateOnly AssessedOn, int QualityScore, int DeliveryScore, int ResponsivenessScore, decimal OverallScore, string? Notes, DateTimeOffset CreatedAtUtc);
