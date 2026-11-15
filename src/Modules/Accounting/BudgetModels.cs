namespace Erp.Modules.Accounting;

public enum BudgetPlanState
{
    Draft,
    Approved,
    Superseded
}

public sealed class BudgetPlan
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid FiscalPeriodId { get; set; }
    public required string Name { get; set; }
    public BudgetPlanState State { get; set; }
    public required string CurrencyCode { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? ApprovedAtUtc { get; set; }
    public required string ConcurrencyStamp { get; set; }
    public List<BudgetPlanLine> Lines { get; set; } = [];
}

public sealed class BudgetPlanLine
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BudgetPlanId { get; set; }
    public Guid AccountId { get; set; }
    public decimal BudgetAmount { get; set; }
}

public sealed record BudgetPlanLineRequest(Guid AccountId, decimal BudgetAmount);
public sealed record BudgetPlanRequest(Guid FiscalPeriodId, string Name, IReadOnlyList<BudgetPlanLineRequest> Lines, string? ConcurrencyStamp = null);
public sealed record BudgetPlanLineDto(Guid AccountId, string AccountCode, string AccountName, decimal BudgetAmount, decimal ActualAmount, decimal Variance);
public sealed record BudgetPlanDto(Guid Id, Guid FiscalPeriodId, string FiscalPeriodName, string Name, BudgetPlanState State, string CurrencyCode, decimal BudgetTotal, decimal ActualTotal, decimal Variance, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset? ApprovedAtUtc, string ConcurrencyStamp, IReadOnlyList<BudgetPlanLineDto> Lines);
public sealed class BudgetValidationException(IReadOnlyDictionary<string, string[]> errors) : Exception("Budget validation failed.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

public sealed class BudgetConcurrencyException : Exception
{
    public BudgetConcurrencyException() : base("The budget plan changed since it was loaded.") { }
}
