using Erp.Modules.Hcm.Contracts;

namespace Erp.Modules.Hcm;

public sealed class Employee
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public required string EmployeeNumber { get; set; }
    public required string FullName { get; set; }
    public string? Email { get; set; }
    public DateOnly HireDate { get; set; }
    public DateOnly? EmploymentEndDate { get; set; }
    public EmployeeStatus Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid ConcurrencyStamp { get; set; }
}

public sealed class LeaveRequestRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid EmployeeId { get; set; }
    public required string EmployeeName { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public required string Reason { get; set; }
    public LeaveRequestStatus Status { get; set; }
    public DateTimeOffset SubmittedAtUtc { get; set; }
    public DateTimeOffset? DecidedAtUtc { get; set; }
    public Guid ConcurrencyStamp { get; set; }
}

public sealed class AttendanceRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid EmployeeId { get; set; }
    public required string EmployeeName { get; set; }
    public DateOnly WorkDate { get; set; }
    public DateTimeOffset? CheckedInAtUtc { get; set; }
    public DateTimeOffset? CheckedOutAtUtc { get; set; }
    public AttendanceApprovalStatus ApprovalStatus { get; set; }
    public AttendanceDisposition? Disposition { get; set; }
    public decimal ApprovedOvertimeHours { get; set; }
    public string? BreaksJson { get; set; }
    public Guid? FinalizedBy { get; set; }
    public DateTimeOffset? FinalizedAtUtc { get; set; }
    public Guid ConcurrencyStamp { get; set; }
}

public sealed class EmployeeBenefitRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid EmployeeId { get; set; }
    public required string EmployeeName { get; set; }
    public required string BenefitName { get; set; }
    public string? Provider { get; set; }
    public DateOnly EnrollmentDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public BenefitEnrollmentStatus Status { get; set; }
    public Guid ConcurrencyStamp { get; set; }
}

public sealed class TrainingRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid EmployeeId { get; set; }
    public required string EmployeeName { get; set; }
    public required string Title { get; set; }
    public string? Provider { get; set; }
    public DateOnly CompletedOn { get; set; }
    public DateOnly? ExpiresOn { get; set; }
    public string? CertificateReference { get; set; }
    public Guid ConcurrencyStamp { get; set; }
}

public sealed class PerformanceReview
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid EmployeeId { get; set; }
    public required string EmployeeName { get; set; }
    public required string ReviewerName { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public required string Summary { get; set; }
    public string? Strengths { get; set; }
    public string? DevelopmentGoals { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid ConcurrencyStamp { get; set; }
}

public sealed class PayrollRun
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public required string Number { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public DateOnly PayDate { get; set; }
    public required string CurrencyCode { get; set; }
    public PayrollRunStatus Status { get; set; }
    public string? CalculationRuleVersion { get; set; }
    public string? CalculationRevision { get; set; }
    public string? CalculationSnapshotJson { get; set; }
    public Guid? PayrollPeriodId { get; set; }
    public string? IdempotencyKey { get; set; }
    public Guid? AccountingJournalEntryId { get; set; }
    public string? AccountingJournalNumber { get; set; }
    public Guid? ReversalJournalEntryId { get; set; }
    public string? ReversalJournalNumber { get; set; }
    public string? ReversalIdempotencyKey { get; set; }
    public string? ReversalReason { get; set; }
    public DateOnly? ReversalBusinessDate { get; set; }
    public Guid? ReversedByUserId { get; set; }
    public DateTimeOffset? ReversedAtUtc { get; set; }
    public string? PostingError { get; set; }
    public DateTimeOffset? PostedAtUtc { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid ConcurrencyStamp { get; set; }
    public List<PayrollLine> Lines { get; set; } = [];
    public List<PayrollApprovalEvent> ApprovalHistory { get; set; } = [];
}

public sealed class PayrollApprovalEvent
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid PayrollRunId { get; set; }
    public PayrollRunStatus Status { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? Comment { get; set; }
    public string? CalculationRuleVersion { get; set; }
    public string? CalculationRevision { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

public sealed class PayrollAdjustmentRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid PayrollRunId { get; set; }
    public Guid EmployeeId { get; set; }
    public DateOnly OriginalPeriodStart { get; set; }
    public DateOnly OriginalPeriodEnd { get; set; }
    public required string EmployeeNumber { get; set; }
    public required string EmployeeName { get; set; }
    public PayrollAdjustmentCategory Category { get; set; }
    public PayrollAdjustmentCalculationMode CalculationMode { get; set; }
    public PayrollAdjustmentStatus Status { get; set; }
    public required string Reason { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public required string CurrencyCode { get; set; }
    public decimal Amount { get; set; }
    public string? EvidenceReference { get; set; }
    public required string SnapshotJson { get; set; }
    public required string SnapshotRevision { get; set; }
    public required string IdempotencyKey { get; set; }
    public string? PostingIdempotencyKey { get; set; }
    public DateOnly? PostingBusinessDate { get; set; }
    public string? PostingError { get; set; }
    public Guid? AccountingJournalEntryId { get; set; }
    public string? AccountingJournalNumber { get; set; }
    public Guid? ReversalJournalEntryId { get; set; }
    public string? ReversalJournalNumber { get; set; }
    public string? ReversalIdempotencyKey { get; set; }
    public string? ReversalReason { get; set; }
    public DateOnly? ReversalBusinessDate { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? PostedAtUtc { get; set; }
    public Guid ConcurrencyStamp { get; set; }
    public List<PayrollAdjustmentEvent> History { get; set; } = [];
}

public sealed class PayrollAdjustmentEvent
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid PayrollAdjustmentId { get; set; }
    public PayrollAdjustmentStatus Status { get; set; }
    public Guid? ActorUserId { get; set; }
    public required string Reason { get; set; }
    public required string SnapshotRevision { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

public sealed class PayrollAccountMapping
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public PayrollAccountMappingType Type { get; set; }
    public Guid AccountId { get; set; }
    public string? CurrencyCode { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class PayrollLine
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid PayrollRunId { get; set; }
    public Guid EmployeeId { get; set; }
    public required string EmployeeNumber { get; set; }
    public required string EmployeeName { get; set; }
    public decimal GrossPay { get; set; }
    public decimal TaxWithheld { get; set; }
    public decimal OtherDeductions { get; set; }
    public decimal EmployerContributions { get; set; }
    public decimal NetPay { get; set; }
    public decimal AttendanceHours { get; set; }
    public string? CalculationDetailsJson { get; set; }
}

public sealed class PayrollPeriod
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public required string Number { get; set; }
    public PayrollPeriodType Type { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public DateOnly PayDate { get; set; }
    public required string JurisdictionCode { get; set; }
    public PayrollPeriodStatus Status { get; set; }
    public required string ExpectedWorkDatesJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid ConcurrencyStamp { get; set; }
}

public sealed class PayrollRuleConfiguration
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public required string JurisdictionCode { get; set; }
    public required string Version { get; set; }
    public PayrollPeriodType PeriodType { get; set; }
    public bool IsSupported { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public decimal StandardHoursPerPeriod { get; set; }
    public decimal OvertimeMultiplier { get; set; }
    public required string TaxBandsJson { get; set; }
    public required string EmployerContributionsJson { get; set; }
    public PayrollProrationMethod ProrationMethod { get; set; }
    public PayrollDateBoundaryTreatment HireDateTreatment { get; set; }
    public PayrollDateBoundaryTreatment TerminationDateTreatment { get; set; }
    public PayrollUnpaidLeaveTreatment UnpaidLeaveTreatment { get; set; }
    public int CurrencyPrecision { get; set; }
    public PayrollRoundingMode RoundingMode { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class EmployeeCompensation
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid EmployeeId { get; set; }
    public PayrollPeriodType PeriodType { get; set; }
    public required string CurrencyCode { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public decimal BasePayForPeriod { get; set; }
    public decimal? ScheduledHoursPerPeriod { get; set; }
    public required string AllowancesJson { get; set; }
    public required string DeductionsJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class HcmValidationException(IReadOnlyDictionary<string, string[]> errors)
    : Exception(string.Join(" ", errors.Values.SelectMany(value => value)))
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

public sealed class HcmConflictException(string message) : Exception(message);

public sealed class HcmConcurrencyException()
    : Exception("The HCM record was changed by another operation. Reload it and retry.");
