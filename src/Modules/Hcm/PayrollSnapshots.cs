using System.Text.Json;
using System.Text.Json.Serialization;
using Erp.Modules.Hcm.Contracts;

namespace Erp.Modules.Hcm;

/// <summary>Typed immutable evidence for replaying and auditing a payroll calculation.</summary>
public sealed record PayrollRunCalculationSnapshot(
    string EngineVersion,
    DateTimeOffset CalculatedAtUtc,
    Guid TenantId,
    Guid CompanyId,
    PayrollPeriodDto PayrollPeriod,
    Guid PayrollRuleConfigurationId,
    PayrollRuleConfigurationDto PayrollRules,
    IReadOnlyList<PayrollEmployeeCalculationSnapshot> Employees);

public sealed record PayrollEmployeeCalculationSnapshot(
    Guid EmployeeId,
    string EmployeeNumber,
    string EmployeeName,
    Guid CompensationId,
    EmployeeCompensationDto Compensation,
    IReadOnlyList<PayrollAttendanceSnapshot> Attendance,
    PayrollCalculationResult Calculation,
    IReadOnlyList<PayrollCompensationSegmentSnapshot>? Segments = null);

public sealed record PayrollCompensationSegmentSnapshot(
    DateOnly StartDate,
    DateOnly EndDate,
    decimal ProrationFactor,
    Guid CompensationId,
    EmployeeCompensationDto Compensation,
    decimal StandardHoursForPeriod,
    decimal RegularHours,
    decimal OvertimeHours,
    IReadOnlyList<PayrollComponentInput> Allowances,
    IReadOnlyList<PayrollComponentInput> Deductions);

public sealed record PayrollAdjustmentSnapshot(
    string SnapshotVersion,
    Guid AdjustmentId,
    Guid OriginalPayrollRunId,
    Guid EmployeeId,
    DateOnly OriginalPeriodStart,
    DateOnly OriginalPeriodEnd,
    string OriginalCalculationRevision,
    PayrollRunStatus OriginalRunStatus,
    PayrollAdjustmentCategory Category,
    PayrollAccountMappingType FinancialMapping,
    DateOnly EffectiveDate,
    string CurrencyCode,
    decimal OriginalGrossPay,
    decimal OriginalTaxWithheld,
    decimal OriginalDeductions,
    decimal OriginalNetPay,
    decimal AdjustmentAmount,
    string Reason,
    string? EvidenceReference,
    int CurrencyPrecision,
    PayrollRoundingMode RoundingMode,
    PayrollAdjustmentCalculationMode CalculationMode = PayrollAdjustmentCalculationMode.Manual,
    PayrollRecalculationCorrectionInput? CorrectionInput = null,
    PayrollCalculationResult? OriginalCalculation = null,
    PayrollCalculationResult? CorrectedCalculation = null,
    IReadOnlyList<PayrollCompensationSegmentSnapshot>? CorrectedSegments = null);

public sealed record PayrollAttendanceSnapshot(
    Guid AttendanceId,
    DateOnly WorkDate,
    AttendanceApprovalStatus ApprovalStatus,
    AttendanceDisposition? Disposition,
    DateTimeOffset? CheckedInAtUtc,
    DateTimeOffset? CheckedOutAtUtc,
    decimal ApprovedOvertimeHours,
    IReadOnlyList<AttendanceBreakInput> Breaks);

public static class PayrollSnapshotJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(PayrollRunCalculationSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, Options);

    public static PayrollRunCalculationSnapshot? Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<PayrollRunCalculationSnapshot>(json, Options);
}
