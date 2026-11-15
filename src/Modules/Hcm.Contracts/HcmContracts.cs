using System.Text.Json.Serialization;

namespace Erp.Modules.Hcm.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EmployeeStatus { Active, Inactive }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LeaveRequestStatus { Pending, Approved, Rejected }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BenefitEnrollmentStatus { Active, Ended }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PayrollRunStatus { Draft, Submitted, Rejected, Approved, Posted, Paid, Reversed }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AttendanceApprovalStatus { Pending, Finalized, Rejected }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AttendanceDisposition { Worked, PaidLeave, UnpaidLeave, Absence, Holiday }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PayrollPeriodType { Weekly, Biweekly, Monthly, Custom }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PayrollPeriodStatus { Open, Closed }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PayrollPostingStatus { NotPosted, Posted, Failed, ReversalPending, Reversed, AdjustmentRequired }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PayrollAdjustmentCategory { SalaryCorrection, OvertimeCorrection, AllowanceCorrection, DeductionCorrection, TaxWithholdingCorrection, AttendanceCorrection, RetroactiveCompensationCorrection, UnderpaymentCorrection, OverpaymentRecovery }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PayrollAdjustmentStatus { Draft, Submitted, Approved, Rejected, Posted, Reversed }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PayrollAdjustmentCalculationMode { Manual, Recalculated }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PayrollContributionBasis { BasePay, GrossPay }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PayrollProrationMethod { LegacyAttendanceHours, CalendarDays, ScheduledWorkingDays }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PayrollDateBoundaryTreatment { Inclusive, Exclusive }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PayrollUnpaidLeaveTreatment { ContinueProration, ExcludeUnpaidScheduledDays }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PayrollRoundingMode { MidpointAwayFromZero, MidpointToEven }

/// <summary>Employee information suitable for use by other modules.</summary>
public sealed record EmployeeReference(Guid Id, string EmployeeNumber, string FullName, string? Email, EmployeeStatus Status);

/// <summary>Employee record exposed by the HCM module.</summary>
public sealed record EmployeeDto(
    Guid Id, string EmployeeNumber, string FullName, string? Email,
    DateOnly HireDate, EmployeeStatus Status, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc,
    DateOnly? EmploymentEndDate = null);

/// <summary>Input for creating an employee.</summary>
public sealed record CreateEmployeeRequest(string? EmployeeNumber, string? FullName, string? Email, DateOnly HireDate,
    DateOnly? EmploymentEndDate = null);

/// <summary>Input for updating an employee's editable fields.</summary>
public sealed record UpdateEmployeeRequest(string? FullName, string? Email, DateOnly HireDate, EmployeeStatus Status,
    DateOnly? EmploymentEndDate = null);

/// <summary>A submitted or decided leave request.</summary>
public sealed record LeaveRequestDto(
    Guid Id, Guid EmployeeId, string EmployeeName, DateOnly StartDate, DateOnly EndDate,
    string Reason, LeaveRequestStatus Status, DateTimeOffset SubmittedAtUtc, DateTimeOffset? DecidedAtUtc);

/// <summary>Input for submitting a leave request.</summary>
public sealed record SubmitLeaveRequest(Guid EmployeeId, DateOnly StartDate, DateOnly EndDate, string? Reason);

/// <summary>A single employee attendance record for a work date.</summary>
public sealed record AttendanceRecordDto(
    Guid Id, Guid EmployeeId, string EmployeeName, DateOnly WorkDate,
    DateTimeOffset? CheckedInAtUtc, DateTimeOffset? CheckedOutAtUtc,
    AttendanceApprovalStatus ApprovalStatus = AttendanceApprovalStatus.Pending,
    AttendanceDisposition? Disposition = null, decimal ApprovedOvertimeHours = 0,
    IReadOnlyList<AttendanceBreakInput>? Breaks = null);

/// <summary>Input for recording an employee check-in.</summary>
public sealed record CheckInRequest(Guid EmployeeId, DateOnly WorkDate, DateTimeOffset CheckedInAtUtc);
public sealed record AttendanceBreakInput(DateTimeOffset StartedAtUtc, DateTimeOffset EndedAtUtc, bool Paid);
public sealed record FinalizeAttendanceRequest(AttendanceDisposition Disposition, decimal ApprovedOvertimeHours = 0,
    IReadOnlyList<AttendanceBreakInput>? Breaks = null);
public sealed record RecordAttendanceDispositionRequest(Guid EmployeeId, DateOnly WorkDate,
    AttendanceDisposition Disposition);

/// <summary>Employee benefit enrollment as recorded by the company.</summary>
public sealed record EmployeeBenefitDto(
    Guid Id, Guid EmployeeId, string EmployeeName, string BenefitName, string? Provider,
    DateOnly EnrollmentDate, DateOnly? EndDate, BenefitEnrollmentStatus Status);

/// <summary>Input for recording an employee benefit enrollment.</summary>
public sealed record CreateEmployeeBenefitRequest(
    Guid EmployeeId, string? BenefitName, string? Provider, DateOnly EnrollmentDate, DateOnly? EndDate);

/// <summary>Input for updating an employee benefit enrollment.</summary>
public sealed record UpdateEmployeeBenefitRequest(
    string? BenefitName, string? Provider, DateOnly EnrollmentDate, DateOnly? EndDate, BenefitEnrollmentStatus Status);

/// <summary>A completed employee training record.</summary>
public sealed record TrainingRecordDto(
    Guid Id, Guid EmployeeId, string EmployeeName, string Title, string? Provider,
    DateOnly CompletedOn, DateOnly? ExpiresOn, string? CertificateReference);

/// <summary>Input for recording completed employee training.</summary>
public sealed record CreateTrainingRecordRequest(
    Guid EmployeeId, string? Title, string? Provider, DateOnly CompletedOn,
    DateOnly? ExpiresOn, string? CertificateReference);

/// <summary>Input for updating a completed training record.</summary>
public sealed record UpdateTrainingRecordRequest(
    string? Title, string? Provider, DateOnly CompletedOn, DateOnly? ExpiresOn, string? CertificateReference);

/// <summary>A narrative performance review without a prescribed rating scale.</summary>
public sealed record PerformanceReviewDto(
    Guid Id, Guid EmployeeId, string EmployeeName, string ReviewerName,
    DateOnly PeriodStart, DateOnly PeriodEnd, string Summary, string? Strengths,
    string? DevelopmentGoals, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

/// <summary>Input for recording a narrative performance review.</summary>
public sealed record CreatePerformanceReviewRequest(
    Guid EmployeeId, string? ReviewerName, DateOnly PeriodStart, DateOnly PeriodEnd,
    string? Summary, string? Strengths, string? DevelopmentGoals);

/// <summary>Input for updating a narrative performance review.</summary>
public sealed record UpdatePerformanceReviewRequest(
    string? ReviewerName, DateOnly PeriodStart, DateOnly PeriodEnd,
    string? Summary, string? Strengths, string? DevelopmentGoals);

public sealed record PayrollLineDto(Guid EmployeeId, string EmployeeNumber, string EmployeeName,
    decimal GrossPay, decimal TaxWithheld, decimal OtherDeductions, decimal EmployerContributions, decimal NetPay,
    decimal AttendanceHours = 0, IReadOnlyList<PayrollCalculationDetailDto>? CalculationDetails = null);
public sealed record PayrollCalculationDetailDto(string Code, decimal Amount);
public sealed record PayrollRunDto(Guid Id, string Number, DateOnly PeriodStart, DateOnly PeriodEnd,
    DateOnly PayDate, string CurrencyCode, PayrollRunStatus Status, DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc, Guid ConcurrencyStamp, IReadOnlyList<PayrollLineDto> Lines,
    Guid? PayrollPeriodId = null, string? CalculationRuleVersion = null,
    Guid? AccountingJournalEntryId = null, string? AccountingJournalNumber = null,
    string? PostingError = null, DateTimeOffset? PostedAtUtc = null);
public sealed record PayrollReversalRequest(string? Reason, string? IdempotencyKey, DateOnly BusinessDate,
    Guid ConcurrencyStamp);
public sealed record PayrollReversalDto(Guid PayrollRunId, Guid OriginalJournalEntryId, string OriginalJournalNumber,
    Guid ReversalJournalEntryId, string ReversalJournalNumber, string Reason, Guid? ReversedByUserId,
    DateTimeOffset ReversedAtUtc, string IdempotencyKey);
public sealed record PayrollApprovalEventDto(Guid Id, PayrollRunStatus Status, string? Comment,
    Guid? ActorUserId, string? CalculationRuleVersion, string? CalculationRevision, DateTimeOffset OccurredAtUtc);
public sealed record PayrollReviewRequest(string? Comment, Guid ConcurrencyStamp);
public sealed record PayrollPostingRequest(string? IdempotencyKey, Guid ConcurrencyStamp);
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PayrollAccountMappingType { SalaryExpense, OvertimeExpense, AllowanceExpense, EmployerContributionExpense, EmployerContributionLiability, EmployeeDeductionLiability, TaxLiability, NetSalaryPayable, EmployeeReceivable }
public sealed record PayrollAccountMappingRequest(PayrollAccountMappingType Type, Guid AccountId,
    string? CurrencyCode, DateOnly EffectiveFrom, DateOnly? EffectiveTo);
public sealed record PayrollAccountMappingDto(Guid Id, PayrollAccountMappingType Type, Guid AccountId,
    string? CurrencyCode, DateOnly EffectiveFrom, DateOnly? EffectiveTo, DateTimeOffset CreatedAtUtc);
public sealed record PayrollPostingStatusDto(Guid? JournalEntryId, string? JournalNumber, string? Error,
    DateTimeOffset? PostedAtUtc, PayrollPostingStatus Status = PayrollPostingStatus.NotPosted);
public sealed record PayrollReconciliationDto(Guid PayrollRunId, Guid? JournalEntryId, bool IsReconciled,
    decimal ExpectedDebits, decimal ExpectedCredits, decimal ActualDebits, decimal ActualCredits,
    string? CurrencyCode, IReadOnlyList<string> Discrepancies);

public sealed record PayrollAdjustmentRequest(Guid PayrollRunId, Guid EmployeeId,
    PayrollAdjustmentCategory Category, string? Reason, DateOnly EffectiveDate, decimal Amount,
    string? EvidenceReference, string? IdempotencyKey);
public sealed record PayrollRecalculationCorrectionInput(decimal? CorrectedBasePayForPeriod = null,
    decimal? CorrectedRegularHours = null, decimal? CorrectedOvertimeHours = null,
    string? ComponentCode = null, decimal? CorrectedComponentAmount = null);
public sealed record PayrollRecalculatedAdjustmentRequest(Guid PayrollRunId, Guid EmployeeId,
    PayrollAdjustmentCategory Category, string? Reason, DateOnly EffectiveDate, string? EvidenceReference,
    string? IdempotencyKey, PayrollRecalculationCorrectionInput? Correction);
public sealed record PayrollAdjustmentReviewRequest(string? Reason, Guid ConcurrencyStamp);
public sealed record PayrollAdjustmentPostingRequest(string? IdempotencyKey, DateOnly BusinessDate, Guid ConcurrencyStamp);
public sealed record PayrollAdjustmentReversalRequest(string? Reason, string? IdempotencyKey, DateOnly BusinessDate, Guid ConcurrencyStamp);
public sealed record PayrollAdjustmentDto(Guid Id, Guid PayrollRunId, Guid EmployeeId, string EmployeeNumber,
    string EmployeeName, PayrollAdjustmentCategory Category, PayrollAdjustmentStatus Status, string Reason,
    DateOnly EffectiveDate, string CurrencyCode, decimal Amount, string? EvidenceReference,
    string SnapshotRevision, Guid? AccountingJournalEntryId, string? AccountingJournalNumber,
    Guid? CreatedByUserId, DateTimeOffset CreatedAtUtc, Guid ConcurrencyStamp, string? PostingError = null,
    PayrollAdjustmentCalculationMode CalculationMode = PayrollAdjustmentCalculationMode.Manual);
public sealed record PayrollAdjustmentEventDto(Guid Id, PayrollAdjustmentStatus Status, Guid? ActorUserId,
    string Reason, DateTimeOffset OccurredAtUtc, string SnapshotRevision);
public sealed record PayrollAdjustmentReversalDto(Guid AdjustmentId, Guid OriginalJournalEntryId,
    Guid ReversalJournalEntryId, string ReversalJournalNumber, string Reason, DateOnly BusinessDate, string IdempotencyKey);

public sealed record PayrollComponentInput(string Code, decimal Amount);
public sealed record PayrollRuleConfigurationRequest(string? JurisdictionCode, string? Version,
    bool IsSupported, DateOnly EffectiveFrom, DateOnly? EffectiveTo, decimal StandardHoursPerPeriod,
    decimal OvertimeMultiplier, IReadOnlyList<PayrollTaxBandInput>? TaxBands,
    PayrollPeriodType PeriodType = PayrollPeriodType.Monthly,
    IReadOnlyList<PayrollEmployerContributionInput>? EmployerContributions = null,
    PayrollProrationMethod? ProrationMethod = null,
    PayrollDateBoundaryTreatment? HireDateTreatment = null,
    PayrollDateBoundaryTreatment? TerminationDateTreatment = null,
    PayrollUnpaidLeaveTreatment? UnpaidLeaveTreatment = null,
    int CurrencyPrecision = 2,
    PayrollRoundingMode? RoundingMode = null);
public sealed record PayrollTaxBandInput(decimal AmountLimit, decimal Rate);
public sealed record PayrollEmployerContributionInput(string Code, bool IsPercentage, decimal Value,
    PayrollContributionBasis Basis = PayrollContributionBasis.GrossPay, decimal? MaximumAmount = null);
public sealed record PayrollRuleConfigurationDto(Guid Id, string JurisdictionCode, string Version,
    bool IsSupported, DateOnly EffectiveFrom, DateOnly? EffectiveTo, decimal StandardHoursPerPeriod,
    decimal OvertimeMultiplier, IReadOnlyList<PayrollTaxBandInput> TaxBands,
    PayrollPeriodType PeriodType = PayrollPeriodType.Monthly,
    IReadOnlyList<PayrollEmployerContributionInput>? EmployerContributions = null,
    PayrollProrationMethod ProrationMethod = PayrollProrationMethod.LegacyAttendanceHours,
    PayrollDateBoundaryTreatment HireDateTreatment = PayrollDateBoundaryTreatment.Inclusive,
    PayrollDateBoundaryTreatment TerminationDateTreatment = PayrollDateBoundaryTreatment.Inclusive,
    PayrollUnpaidLeaveTreatment UnpaidLeaveTreatment = PayrollUnpaidLeaveTreatment.ContinueProration,
    int CurrencyPrecision = 2,
    PayrollRoundingMode RoundingMode = PayrollRoundingMode.MidpointAwayFromZero);
public sealed record EmployeeCompensationRequest(Guid EmployeeId, DateOnly EffectiveFrom,
    DateOnly? EffectiveTo, string? CurrencyCode, decimal BasePayForPeriod,
    IReadOnlyList<PayrollComponentInput>? Allowances, IReadOnlyList<PayrollComponentInput>? Deductions,
    PayrollPeriodType PeriodType = PayrollPeriodType.Monthly, decimal? ScheduledHoursPerPeriod = null);
public sealed record EmployeeCompensationDto(Guid Id, Guid EmployeeId, string CurrencyCode,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo, decimal BasePayForPeriod,
    IReadOnlyList<PayrollComponentInput> Allowances, IReadOnlyList<PayrollComponentInput> Deductions,
    PayrollPeriodType PeriodType = PayrollPeriodType.Monthly, decimal? ScheduledHoursPerPeriod = null);
public sealed record PayrollPeriodRequest(string? Number, PayrollPeriodType Type, DateOnly StartDate,
    DateOnly EndDate, DateOnly PayDate, string? JurisdictionCode, IReadOnlyList<DateOnly>? ExpectedWorkDates);
public sealed record PayrollPeriodDto(Guid Id, string Number, PayrollPeriodType Type, DateOnly StartDate,
    DateOnly EndDate, DateOnly PayDate, string JurisdictionCode, PayrollPeriodStatus Status,
    IReadOnlyList<DateOnly> ExpectedWorkDates, DateTimeOffset CreatedAtUtc);
public sealed record CreatePayrollRunRequest(string? Number, string? IdempotencyKey);

/// <summary>Persistence-independent HCM operations used by this and other modules.</summary>
public interface IHcmService
{
    Task<IReadOnlyList<EmployeeDto>> ListEmployeesAsync(string? search, CancellationToken cancellationToken);
    Task<EmployeeDto?> GetEmployeeAsync(Guid employeeId, CancellationToken cancellationToken);
    Task<EmployeeDto> CreateEmployeeAsync(CreateEmployeeRequest request, CancellationToken cancellationToken);
    Task<EmployeeDto> UpdateEmployeeAsync(Guid employeeId, UpdateEmployeeRequest request, CancellationToken cancellationToken);
    Task<LeaveRequestDto?> GetLeaveRequestAsync(Guid requestId, CancellationToken cancellationToken);
    Task<IReadOnlyList<LeaveRequestDto>> ListLeaveRequestsAsync(Guid? employeeId, CancellationToken cancellationToken);
    Task<LeaveRequestDto> SubmitLeaveRequestAsync(SubmitLeaveRequest request, CancellationToken cancellationToken);
    Task<LeaveRequestDto> DecideLeaveRequestAsync(Guid requestId, LeaveRequestStatus decision, CancellationToken cancellationToken);
    Task<AttendanceRecordDto?> GetAttendanceAsync(Guid attendanceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AttendanceRecordDto>> ListAttendanceAsync(Guid? employeeId, DateOnly? workDate, CancellationToken cancellationToken);
    Task<AttendanceRecordDto> CheckInAsync(CheckInRequest request, CancellationToken cancellationToken);
    Task<AttendanceRecordDto> CheckOutAsync(Guid attendanceId, DateTimeOffset checkedOutAtUtc, CancellationToken cancellationToken);
    Task<AttendanceRecordDto> FinalizeAttendanceAsync(Guid attendanceId, FinalizeAttendanceRequest request, CancellationToken cancellationToken);
    Task<AttendanceRecordDto> RecordAttendanceDispositionAsync(RecordAttendanceDispositionRequest request, CancellationToken cancellationToken);
    Task<EmployeeBenefitDto?> GetBenefitAsync(Guid benefitId, CancellationToken cancellationToken);
    Task<IReadOnlyList<EmployeeBenefitDto>> ListBenefitsAsync(Guid? employeeId, CancellationToken cancellationToken);
    Task<EmployeeBenefitDto> CreateBenefitAsync(CreateEmployeeBenefitRequest request, CancellationToken cancellationToken);
    Task<EmployeeBenefitDto> UpdateBenefitAsync(Guid benefitId, UpdateEmployeeBenefitRequest request, CancellationToken cancellationToken);
    Task<TrainingRecordDto?> GetTrainingRecordAsync(Guid trainingRecordId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TrainingRecordDto>> ListTrainingRecordsAsync(Guid? employeeId, CancellationToken cancellationToken);
    Task<TrainingRecordDto> CreateTrainingRecordAsync(CreateTrainingRecordRequest request, CancellationToken cancellationToken);
    Task<TrainingRecordDto> UpdateTrainingRecordAsync(Guid trainingRecordId, UpdateTrainingRecordRequest request, CancellationToken cancellationToken);
    Task<PerformanceReviewDto?> GetPerformanceReviewAsync(Guid reviewId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PerformanceReviewDto>> ListPerformanceReviewsAsync(Guid? employeeId, CancellationToken cancellationToken);
    Task<PerformanceReviewDto> CreatePerformanceReviewAsync(CreatePerformanceReviewRequest request, CancellationToken cancellationToken);
    Task<PerformanceReviewDto> UpdatePerformanceReviewAsync(Guid reviewId, UpdatePerformanceReviewRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<PayrollRunDto>> ListPayrollRunsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<PayrollRuleConfigurationDto>> ListPayrollRulesAsync(CancellationToken cancellationToken);
    Task<PayrollRuleConfigurationDto> AddPayrollRuleAsync(PayrollRuleConfigurationRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<EmployeeCompensationDto>> ListCompensationAsync(Guid? employeeId, CancellationToken cancellationToken);
    Task<EmployeeCompensationDto> AddCompensationAsync(EmployeeCompensationRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<PayrollPeriodDto>> ListPayrollPeriodsAsync(CancellationToken cancellationToken);
    Task<PayrollPeriodDto?> GetPayrollPeriodAsync(Guid id, CancellationToken cancellationToken);
    Task<PayrollPeriodDto> CreatePayrollPeriodAsync(PayrollPeriodRequest request, CancellationToken cancellationToken);
    Task<PayrollPeriodDto> ClosePayrollPeriodAsync(Guid id, CancellationToken cancellationToken);
    Task<PayrollRunDto> CreatePayrollRunAsync(Guid payrollPeriodId, CreatePayrollRunRequest request, CancellationToken cancellationToken);
    Task<PayrollRunDto?> GetPayrollRunAsync(Guid id, CancellationToken cancellationToken);
    Task<PayrollRunDto> TransitionPayrollRunAsync(Guid id, PayrollRunStatus status, Guid concurrencyStamp, CancellationToken cancellationToken);
    Task<PayrollRunDto> SubmitPayrollRunAsync(Guid id, PayrollReviewRequest request, CancellationToken cancellationToken);
    Task<PayrollRunDto> ReviewPayrollRunAsync(Guid id, bool approve, PayrollReviewRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<PayrollApprovalEventDto>> GetPayrollApprovalHistoryAsync(Guid id, CancellationToken cancellationToken);
    Task<PayrollPostingStatusDto> PostPayrollRunAsync(Guid id, PayrollPostingRequest request, CancellationToken cancellationToken);
    Task<PayrollReversalDto> ReversePayrollRunAsync(Guid id, PayrollReversalRequest request, CancellationToken cancellationToken);
    Task<PayrollPostingStatusDto> GetPayrollPostingStatusAsync(Guid id, CancellationToken cancellationToken);
    Task<PayrollReconciliationDto> ReconcilePayrollRunAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<PayrollAccountMappingDto>> ListPayrollAccountMappingsAsync(CancellationToken cancellationToken);
    Task<PayrollAccountMappingDto> SavePayrollAccountMappingAsync(PayrollAccountMappingRequest request, CancellationToken cancellationToken);
    Task<PayrollAdjustmentDto> CreatePayrollAdjustmentAsync(PayrollAdjustmentRequest request, CancellationToken cancellationToken);
    Task<PayrollAdjustmentDto> CreateRecalculatedPayrollAdjustmentAsync(PayrollRecalculatedAdjustmentRequest request, CancellationToken cancellationToken);
    Task<PayrollAdjustmentDto?> GetPayrollAdjustmentAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<PayrollAdjustmentDto>> ListPayrollAdjustmentsAsync(Guid? payrollRunId, Guid? employeeId, CancellationToken cancellationToken);
    Task<PayrollAdjustmentDto> ReviewPayrollAdjustmentAsync(Guid id, bool approve, PayrollAdjustmentReviewRequest request, CancellationToken cancellationToken);
    Task<PayrollAdjustmentDto> SubmitPayrollAdjustmentAsync(Guid id, PayrollAdjustmentReviewRequest request, CancellationToken cancellationToken);
    Task<PayrollPostingStatusDto> PostPayrollAdjustmentAsync(Guid id, PayrollAdjustmentPostingRequest request, CancellationToken cancellationToken);
    Task<PayrollAdjustmentReversalDto> ReversePayrollAdjustmentAsync(Guid id, PayrollAdjustmentReversalRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<PayrollAdjustmentEventDto>> GetPayrollAdjustmentHistoryAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>Read-only employee lookup for integration with other modules.</summary>
public interface IEmployeeDirectory
{
    Task<EmployeeReference?> FindAsync(Guid employeeId, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, EmployeeReference>> FindAsync(
        IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken);
}
