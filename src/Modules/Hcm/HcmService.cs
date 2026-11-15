using System.Text.Json;
using System.Security.Claims;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Persistence;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Hcm.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Erp.Modules.Hcm;

public sealed class HcmService(
    HcmDbContext database,
    ITenantContext tenantContext,
    ICompanyContext companyContext,
    IClock clock,
    IAuditLogWriter audit,
    IBusinessTransactionCoordinator transactions,
    IPayrollJournalPoster payrollJournalPoster,
    IPayrollAccountMappingValidator accountMappingValidator,
    IHttpContextAccessor httpContextAccessor) : IHcmService, IEmployeeDirectory
{
    public async Task<IReadOnlyList<EmployeeDto>> ListEmployeesAsync(string? search, CancellationToken cancellationToken)
    {
        var query = database.Employees.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var value = search.Trim();
            query = query.Where(x => x.EmployeeNumber.Contains(value) || x.FullName.Contains(value) ||
                                     (x.Email != null && x.Email.Contains(value)));
        }
        return await query.OrderBy(x => x.FullName).Take(200).Select(x => ToDto(x))
            .ToListAsync(cancellationToken);
    }

    public async Task<EmployeeDto?> GetEmployeeAsync(Guid employeeId, CancellationToken cancellationToken)
    {
        var employee = await database.Employees.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == employeeId, cancellationToken);
        return employee is null ? null : ToDto(employee);
    }

    public async Task<EmployeeDto> CreateEmployeeAsync(CreateEmployeeRequest request, CancellationToken cancellationToken)
    {
        var number = Required(request.EmployeeNumber, "employeeNumber", 50);
        var fullName = Required(request.FullName, "fullName", 200);
        var email = NormalizeEmail(request.Email);
        ValidateHireDate(request.HireDate);
        if (request.EmploymentEndDate is { } endDate && endDate < request.HireDate)
            throw Validation("employmentEndDate", "Employment end date cannot precede the hire date.");
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var employee = new Employee
        {
            Id = Guid.NewGuid(),
            TenantId = RequireTenant(),
            CompanyId = RequireCompany(),
            EmployeeNumber = number,
            FullName = fullName,
            Email = email,
            HireDate = request.HireDate,
            EmploymentEndDate = request.EmploymentEndDate,
            Status = EmployeeStatus.Active,
            CreatedAtUtc = clock.UtcNow,
            UpdatedAtUtc = clock.UtcNow,
            ConcurrencyStamp = NewStamp()
        };
        database.Employees.Add(employee);
        await SaveAsync(cancellationToken);
        await AuditAsync("hcm.employee.created", "Employee", employee.Id, null, ToDto(employee), cancellationToken);
        var result = ToDto(employee);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<EmployeeDto> UpdateEmployeeAsync(Guid employeeId, UpdateEmployeeRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var employee = await FindEmployeeAsync(employeeId, cancellationToken);
        var before = ToDto(employee);
        employee.FullName = Required(request.FullName, "fullName", 200);
        employee.Email = NormalizeEmail(request.Email);
        ValidateHireDate(request.HireDate);
        if (request.EmploymentEndDate is { } endDate && endDate < request.HireDate)
            throw Validation("employmentEndDate", "Employment end date cannot precede the hire date.");
        if (!Enum.IsDefined(request.Status))
            throw Validation("status", "Select a valid employee status.");
        employee.HireDate = request.HireDate;
        employee.EmploymentEndDate = request.EmploymentEndDate;
        employee.Status = request.Status;
        employee.UpdatedAtUtc = clock.UtcNow;
        employee.ConcurrencyStamp = NewStamp();
        await SaveAsync(cancellationToken);
        await AuditAsync("hcm.employee.updated", "Employee", employee.Id, before, ToDto(employee), cancellationToken);
        var result = ToDto(employee);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<LeaveRequestDto>> ListLeaveRequestsAsync(Guid? employeeId, CancellationToken cancellationToken)
    {
        var query = database.LeaveRequests.AsNoTracking();
        if (employeeId.HasValue)
            query = query.Where(x => x.EmployeeId == employeeId.Value);
        return await query.OrderByDescending(x => x.SubmittedAtUtc).Take(200)
            .Select(x => ToDto(x)).ToListAsync(cancellationToken);
    }

    public async Task<LeaveRequestDto?> GetLeaveRequestAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var record = await database.LeaveRequests.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == requestId, cancellationToken);
        return record is null ? null : ToDto(record);
    }

    public async Task<LeaveRequestDto> SubmitLeaveRequestAsync(SubmitLeaveRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        await LockEmployeeLeaveAsync(request.EmployeeId, cancellationToken);
        var employee = await FindEmployeeAsync(request.EmployeeId, cancellationToken);
        if (employee.Status != EmployeeStatus.Active)
            throw Validation("employeeId", "Leave can only be requested for an active employee.");
        if (request.StartDate == default || request.EndDate == default || request.StartDate > request.EndDate)
            throw Validation("startDate", "The leave start date must be on or before its end date.");
        var reason = Required(request.Reason, "reason", 2000);
        if (await database.LeaveRequests.AnyAsync(x =>
                x.EmployeeId == employee.Id &&
                (x.Status == LeaveRequestStatus.Pending || x.Status == LeaveRequestStatus.Approved) &&
                x.StartDate <= request.EndDate && x.EndDate >= request.StartDate, cancellationToken))
            throw new HcmConflictException("The requested dates overlap an existing pending or approved leave request.");

        var record = new LeaveRequestRecord
        {
            Id = Guid.NewGuid(),
            TenantId = RequireTenant(),
            CompanyId = RequireCompany(),
            EmployeeId = employee.Id,
            EmployeeName = employee.FullName,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Reason = reason,
            Status = LeaveRequestStatus.Pending,
            SubmittedAtUtc = clock.UtcNow,
            ConcurrencyStamp = NewStamp()
        };
        database.LeaveRequests.Add(record);
        await SaveAsync(cancellationToken);
        await AuditAsync("hcm.leave.submitted", "LeaveRequest", record.Id, null, ToDto(record), cancellationToken);
        var result = ToDto(record);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<LeaveRequestDto> DecideLeaveRequestAsync(
        Guid requestId, LeaveRequestStatus decision, CancellationToken cancellationToken)
    {
        if (decision is not (LeaveRequestStatus.Approved or LeaveRequestStatus.Rejected))
            throw Validation("decision", "A leave request can only be approved or rejected.");
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var record = await database.LeaveRequests.SingleOrDefaultAsync(x => x.Id == requestId, cancellationToken)
            ?? throw new KeyNotFoundException("Leave request not found.");
        await LockEmployeeLeaveAsync(record.EmployeeId, cancellationToken);
        await database.Entry(record).ReloadAsync(cancellationToken);
        if (record.Status != LeaveRequestStatus.Pending)
            throw new HcmConflictException("Only pending leave requests can be decided.");
        if (decision == LeaveRequestStatus.Approved &&
            await database.LeaveRequests.AnyAsync(x =>
                x.Id != record.Id && x.EmployeeId == record.EmployeeId &&
                (x.Status == LeaveRequestStatus.Pending || x.Status == LeaveRequestStatus.Approved) &&
                x.StartDate <= record.EndDate && x.EndDate >= record.StartDate, cancellationToken))
            throw new HcmConflictException("The leave dates overlap another pending or approved request.");
        var before = ToDto(record);
        record.Status = decision;
        record.DecidedAtUtc = clock.UtcNow;
        record.ConcurrencyStamp = NewStamp();
        await SaveAsync(cancellationToken);
        await AuditAsync($"hcm.leave.{decision.ToString().ToLowerInvariant()}", "LeaveRequest",
            record.Id, before, ToDto(record), cancellationToken);
        var result = ToDto(record);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<AttendanceRecordDto>> ListAttendanceAsync(
        Guid? employeeId, DateOnly? workDate, CancellationToken cancellationToken)
    {
        var query = database.AttendanceRecords.AsNoTracking();
        if (employeeId.HasValue)
            query = query.Where(x => x.EmployeeId == employeeId.Value);
        if (workDate.HasValue)
            query = query.Where(x => x.WorkDate == workDate.Value);
        return await query.OrderByDescending(x => x.WorkDate).ThenBy(x => x.EmployeeName).Take(200)
            .Select(x => ToDto(x)).ToListAsync(cancellationToken);
    }

    public async Task<AttendanceRecordDto?> GetAttendanceAsync(Guid attendanceId, CancellationToken cancellationToken)
    {
        var record = await database.AttendanceRecords.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == attendanceId, cancellationToken);
        return record is null ? null : ToDto(record);
    }

    public async Task<AttendanceRecordDto> CheckInAsync(CheckInRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var employee = await FindEmployeeAsync(request.EmployeeId, cancellationToken);
        if (employee.Status != EmployeeStatus.Active)
            throw Validation("employeeId", "Attendance can only be recorded for an active employee.");
        if (request.WorkDate == default)
            throw Validation("workDate", "A work date is required.");
        if (request.CheckedInAtUtc.Offset != TimeSpan.Zero)
            throw Validation("checkedInAtUtc", "Check-in timestamps must be expressed in UTC.");
        if (await database.AttendanceRecords.AnyAsync(x =>
                x.EmployeeId == employee.Id && x.WorkDate == request.WorkDate, cancellationToken))
            throw new HcmConflictException("An attendance record already exists for this employee and work date.");
        var record = new AttendanceRecord
        {
            Id = Guid.NewGuid(),
            TenantId = RequireTenant(),
            CompanyId = RequireCompany(),
            EmployeeId = employee.Id,
            EmployeeName = employee.FullName,
            WorkDate = request.WorkDate,
            CheckedInAtUtc = request.CheckedInAtUtc,
            ConcurrencyStamp = NewStamp()
        };
        database.AttendanceRecords.Add(record);
        await SaveAsync(cancellationToken);
        await AuditAsync("hcm.attendance.checked_in", "AttendanceRecord", record.Id, null, ToDto(record), cancellationToken);
        var result = ToDto(record);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<AttendanceRecordDto> CheckOutAsync(
        Guid attendanceId, DateTimeOffset checkedOutAtUtc, CancellationToken cancellationToken)
    {
        if (checkedOutAtUtc.Offset != TimeSpan.Zero)
            throw Validation("checkedOutAtUtc", "Check-out timestamps must be expressed in UTC.");
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var record = await database.AttendanceRecords.SingleOrDefaultAsync(x => x.Id == attendanceId, cancellationToken)
            ?? throw new KeyNotFoundException("Attendance record not found.");
        if (record.ApprovalStatus != AttendanceApprovalStatus.Pending)
            throw new HcmConflictException("Finalized or rejected attendance cannot be changed.");
        if (record.CheckedOutAtUtc.HasValue)
            throw new HcmConflictException("This attendance record has already been checked out.");
        if (record.CheckedInAtUtc is not { } checkedInAtUtc)
            throw new HcmConflictException("A record without check-in cannot be checked out.");
        if (checkedOutAtUtc < checkedInAtUtc)
            throw Validation("checkedOutAtUtc", "Check-out time cannot be earlier than check-in time.");
        var before = ToDto(record);
        record.CheckedOutAtUtc = checkedOutAtUtc;
        record.ConcurrencyStamp = NewStamp();
        await SaveAsync(cancellationToken);
        await AuditAsync("hcm.attendance.checked_out", "AttendanceRecord", record.Id, before, ToDto(record), cancellationToken);
        var result = ToDto(record);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<AttendanceRecordDto> FinalizeAttendanceAsync(Guid attendanceId,
        FinalizeAttendanceRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var record = await database.AttendanceRecords.SingleOrDefaultAsync(x => x.Id == attendanceId, cancellationToken)
            ?? throw new KeyNotFoundException("Attendance record not found.");
        if (record.ApprovalStatus != AttendanceApprovalStatus.Pending)
            throw new HcmConflictException("Only pending attendance can be finalized.");
        if (!Enum.IsDefined(request.Disposition) || request.ApprovedOvertimeHours < 0 ||
            decimal.Round(request.ApprovedOvertimeHours, 2) != request.ApprovedOvertimeHours)
            throw Validation("attendance", "Choose a valid disposition and nonnegative overtime rounded to two decimals.");
        if (request.Disposition == AttendanceDisposition.Worked &&
            (record.CheckedInAtUtc is null || record.CheckedOutAtUtc is null))
            throw Validation("attendance", "Worked attendance must have both check-in and check-out timestamps.");
        var breaks = request.Breaks ?? [];
        if (request.Disposition != AttendanceDisposition.Worked && breaks.Count != 0)
            throw Validation("breaks", "Breaks can only be recorded for worked attendance.");
        if (request.Disposition == AttendanceDisposition.Worked)
        {
            var ordered = breaks.OrderBy(x => x.StartedAtUtc).ToArray();
            try { _ = AttendanceBreakPolicy.CalculatePaidHours(record.CheckedInAtUtc!.Value, record.CheckedOutAtUtc!.Value, ordered); }
            catch (ArgumentException exception) { throw Validation("breaks", exception.Message); }
            record.BreaksJson = JsonSerializer.Serialize(ordered);
        }
        if (request.Disposition != AttendanceDisposition.Worked &&
            (record.CheckedInAtUtc.HasValue || record.CheckedOutAtUtc.HasValue || request.ApprovedOvertimeHours != 0))
            throw Validation("attendance", "Leave, absence, and holiday entries cannot contain punch times or overtime.");
        await ValidateAttendanceDispositionAsync(record.EmployeeId, record.WorkDate, request.Disposition, cancellationToken);
        var before = ToDto(record);
        record.Disposition = request.Disposition;
        record.ApprovedOvertimeHours = request.ApprovedOvertimeHours;
        record.ApprovalStatus = AttendanceApprovalStatus.Finalized;
        record.FinalizedAtUtc = clock.UtcNow;
        record.ConcurrencyStamp = NewStamp();
        await SaveAsync(cancellationToken);
        var result = ToDto(record);
        await AuditAsync("hcm.attendance.finalized", nameof(AttendanceRecord), record.Id, before, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<AttendanceRecordDto> RecordAttendanceDispositionAsync(
        RecordAttendanceDispositionRequest request, CancellationToken cancellationToken)
    {
        if (request.EmployeeId == Guid.Empty || request.WorkDate == default ||
            request.Disposition is AttendanceDisposition.Worked)
            throw Validation("attendance", "A valid employee, work date, and non-work disposition are required.");
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var employee = await FindEmployeeAsync(request.EmployeeId, cancellationToken);
        if (employee.Status != EmployeeStatus.Active)
            throw Validation("employeeId", "Attendance can only be recorded for an active employee.");
        if (await database.AttendanceRecords.AnyAsync(x => x.EmployeeId == employee.Id &&
                x.WorkDate == request.WorkDate, cancellationToken))
            throw new HcmConflictException("An attendance record already exists for this employee and work date.");
        await ValidateAttendanceDispositionAsync(employee.Id, request.WorkDate, request.Disposition, cancellationToken);
        var record = new AttendanceRecord
        {
            Id = Guid.NewGuid(),
            TenantId = RequireTenant(),
            CompanyId = RequireCompany(),
            EmployeeId = employee.Id,
            EmployeeName = employee.FullName,
            WorkDate = request.WorkDate,
            ApprovalStatus = AttendanceApprovalStatus.Finalized,
            Disposition = request.Disposition,
            FinalizedAtUtc = clock.UtcNow,
            ConcurrencyStamp = NewStamp()
        };
        database.AttendanceRecords.Add(record);
        await SaveAsync(cancellationToken);
        var result = ToDto(record);
        await AuditAsync("hcm.attendance.disposition_recorded", nameof(AttendanceRecord), record.Id, null, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task ValidateAttendanceDispositionAsync(Guid employeeId, DateOnly workDate,
        AttendanceDisposition disposition, CancellationToken cancellationToken)
    {
        var approvedLeave = await database.LeaveRequests.AnyAsync(x => x.EmployeeId == employeeId &&
            x.Status == LeaveRequestStatus.Approved && x.StartDate <= workDate && x.EndDate >= workDate,
            cancellationToken);
        if (disposition is AttendanceDisposition.PaidLeave or AttendanceDisposition.UnpaidLeave && !approvedLeave)
            throw Validation("disposition", "Leave attendance must be covered by an approved leave request.");
        if (disposition == AttendanceDisposition.Absence && approvedLeave)
            throw Validation("disposition", "An approved leave date cannot be recorded as an absence.");
    }

    public async Task<EmployeeBenefitDto?> GetBenefitAsync(Guid benefitId, CancellationToken cancellationToken)
    {
        var record = await database.EmployeeBenefits.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == benefitId, cancellationToken);
        return record is null ? null : ToDto(record);
    }

    public async Task<IReadOnlyList<EmployeeBenefitDto>> ListBenefitsAsync(Guid? employeeId, CancellationToken cancellationToken)
    {
        var query = database.EmployeeBenefits.AsNoTracking();
        if (employeeId.HasValue)
            query = query.Where(x => x.EmployeeId == employeeId.Value);
        return await query.OrderByDescending(x => x.EnrollmentDate).Take(200)
            .Select(x => ToDto(x)).ToListAsync(cancellationToken);
    }

    public async Task<EmployeeBenefitDto> CreateBenefitAsync(
        CreateEmployeeBenefitRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var employee = await FindEmployeeAsync(request.EmployeeId, cancellationToken);
        var benefitName = Required(request.BenefitName, "benefitName", 200);
        var provider = Optional(request.Provider, "provider", 200);
        ValidateDate(request.EnrollmentDate, "enrollmentDate");
        ValidateDateRange(request.EnrollmentDate, request.EndDate, "endDate");
        var record = new EmployeeBenefitRecord
        {
            Id = Guid.NewGuid(),
            TenantId = RequireTenant(),
            CompanyId = RequireCompany(),
            EmployeeId = employee.Id,
            EmployeeName = employee.FullName,
            BenefitName = benefitName,
            Provider = provider,
            EnrollmentDate = request.EnrollmentDate,
            EndDate = request.EndDate,
            Status = BenefitEnrollmentStatus.Active,
            ConcurrencyStamp = NewStamp()
        };
        database.EmployeeBenefits.Add(record);
        await SaveAsync(cancellationToken);
        await AuditAsync("hcm.benefit.created", "EmployeeBenefit", record.Id, null, ToDto(record), cancellationToken);
        var result = ToDto(record);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<EmployeeBenefitDto> UpdateBenefitAsync(
        Guid benefitId, UpdateEmployeeBenefitRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var record = await database.EmployeeBenefits.SingleOrDefaultAsync(x => x.Id == benefitId, cancellationToken)
            ?? throw new KeyNotFoundException("Employee benefit not found.");
        var before = ToDto(record);
        var benefitName = Required(request.BenefitName, "benefitName", 200);
        var provider = Optional(request.Provider, "provider", 200);
        ValidateDate(request.EnrollmentDate, "enrollmentDate");
        ValidateDateRange(request.EnrollmentDate, request.EndDate, "endDate");
        if (!Enum.IsDefined(request.Status))
            throw Validation("status", "Select a valid benefit enrollment status.");
        if (request.Status == BenefitEnrollmentStatus.Ended && request.EndDate is null)
            throw Validation("endDate", "An end date is required when ending an enrollment.");
        record.BenefitName = benefitName;
        record.Provider = provider;
        record.EnrollmentDate = request.EnrollmentDate;
        record.EndDate = request.EndDate;
        record.Status = request.Status;
        record.ConcurrencyStamp = NewStamp();
        await SaveAsync(cancellationToken);
        await AuditAsync("hcm.benefit.updated", "EmployeeBenefit", record.Id, before, ToDto(record), cancellationToken);
        var result = ToDto(record);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<TrainingRecordDto?> GetTrainingRecordAsync(Guid trainingRecordId, CancellationToken cancellationToken)
    {
        var record = await database.TrainingRecords.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == trainingRecordId, cancellationToken);
        return record is null ? null : ToDto(record);
    }

    public async Task<IReadOnlyList<TrainingRecordDto>> ListTrainingRecordsAsync(
        Guid? employeeId, CancellationToken cancellationToken)
    {
        var query = database.TrainingRecords.AsNoTracking();
        if (employeeId.HasValue)
            query = query.Where(x => x.EmployeeId == employeeId.Value);
        return await query.OrderByDescending(x => x.CompletedOn).Take(200)
            .Select(x => ToDto(x)).ToListAsync(cancellationToken);
    }

    public async Task<TrainingRecordDto> CreateTrainingRecordAsync(
        CreateTrainingRecordRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var employee = await FindEmployeeAsync(request.EmployeeId, cancellationToken);
        var values = ValidateTraining(request.Title, request.Provider, request.CompletedOn,
            request.ExpiresOn, request.CertificateReference);
        var record = new TrainingRecord
        {
            Id = Guid.NewGuid(),
            TenantId = RequireTenant(),
            CompanyId = RequireCompany(),
            EmployeeId = employee.Id,
            EmployeeName = employee.FullName,
            Title = values.Title,
            Provider = values.Provider,
            CompletedOn = request.CompletedOn,
            ExpiresOn = request.ExpiresOn,
            CertificateReference = values.CertificateReference,
            ConcurrencyStamp = NewStamp()
        };
        database.TrainingRecords.Add(record);
        await SaveAsync(cancellationToken);
        await AuditAsync("hcm.training.created", "TrainingRecord", record.Id, null, ToDto(record), cancellationToken);
        var result = ToDto(record);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<TrainingRecordDto> UpdateTrainingRecordAsync(
        Guid trainingRecordId, UpdateTrainingRecordRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var record = await database.TrainingRecords.SingleOrDefaultAsync(x => x.Id == trainingRecordId, cancellationToken)
            ?? throw new KeyNotFoundException("Training record not found.");
        var before = ToDto(record);
        var values = ValidateTraining(request.Title, request.Provider, request.CompletedOn,
            request.ExpiresOn, request.CertificateReference);
        record.Title = values.Title;
        record.Provider = values.Provider;
        record.CompletedOn = request.CompletedOn;
        record.ExpiresOn = request.ExpiresOn;
        record.CertificateReference = values.CertificateReference;
        record.ConcurrencyStamp = NewStamp();
        await SaveAsync(cancellationToken);
        await AuditAsync("hcm.training.updated", "TrainingRecord", record.Id, before, ToDto(record), cancellationToken);
        var result = ToDto(record);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<PerformanceReviewDto?> GetPerformanceReviewAsync(Guid reviewId, CancellationToken cancellationToken)
    {
        var review = await database.PerformanceReviews.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == reviewId, cancellationToken);
        return review is null ? null : ToDto(review);
    }

    public async Task<IReadOnlyList<PerformanceReviewDto>> ListPerformanceReviewsAsync(
        Guid? employeeId, CancellationToken cancellationToken)
    {
        var query = database.PerformanceReviews.AsNoTracking();
        if (employeeId.HasValue)
            query = query.Where(x => x.EmployeeId == employeeId.Value);
        return await query.OrderByDescending(x => x.PeriodEnd).Take(200)
            .Select(x => ToDto(x)).ToListAsync(cancellationToken);
    }

    public async Task<PerformanceReviewDto> CreatePerformanceReviewAsync(
        CreatePerformanceReviewRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var employee = await FindEmployeeAsync(request.EmployeeId, cancellationToken);
        var values = ValidateReview(request.ReviewerName, request.PeriodStart, request.PeriodEnd,
            request.Summary, request.Strengths, request.DevelopmentGoals);
        var review = new PerformanceReview
        {
            Id = Guid.NewGuid(),
            TenantId = RequireTenant(),
            CompanyId = RequireCompany(),
            EmployeeId = employee.Id,
            EmployeeName = employee.FullName,
            ReviewerName = values.ReviewerName,
            PeriodStart = request.PeriodStart,
            PeriodEnd = request.PeriodEnd,
            Summary = values.Summary,
            Strengths = values.Strengths,
            DevelopmentGoals = values.DevelopmentGoals,
            CreatedAtUtc = clock.UtcNow,
            UpdatedAtUtc = clock.UtcNow,
            ConcurrencyStamp = NewStamp()
        };
        database.PerformanceReviews.Add(review);
        await SaveAsync(cancellationToken);
        await AuditAsync("hcm.performance_review.created", "PerformanceReview", review.Id, null, ToDto(review), cancellationToken);
        var result = ToDto(review);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<PerformanceReviewDto> UpdatePerformanceReviewAsync(
        Guid reviewId, UpdatePerformanceReviewRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var review = await database.PerformanceReviews.SingleOrDefaultAsync(x => x.Id == reviewId, cancellationToken)
            ?? throw new KeyNotFoundException("Performance review not found.");
        var before = ToDto(review);
        var values = ValidateReview(request.ReviewerName, request.PeriodStart, request.PeriodEnd,
            request.Summary, request.Strengths, request.DevelopmentGoals);
        review.ReviewerName = values.ReviewerName;
        review.PeriodStart = request.PeriodStart;
        review.PeriodEnd = request.PeriodEnd;
        review.Summary = values.Summary;
        review.Strengths = values.Strengths;
        review.DevelopmentGoals = values.DevelopmentGoals;
        review.UpdatedAtUtc = clock.UtcNow;
        review.ConcurrencyStamp = NewStamp();
        await SaveAsync(cancellationToken);
        await AuditAsync("hcm.performance_review.updated", "PerformanceReview", review.Id, before, ToDto(review), cancellationToken);
        var result = ToDto(review);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<PayrollRunDto>> ListPayrollRunsAsync(CancellationToken cancellationToken) =>
        (await database.PayrollRuns.AsNoTracking().Include(x => x.Lines)
            .OrderByDescending(x => x.PeriodStart).Take(100).ToListAsync(cancellationToken))
        .Select(ToDto).ToArray();

    public async Task<PayrollRunDto?> GetPayrollRunAsync(Guid id, CancellationToken cancellationToken)
    {
        var run = await database.PayrollRuns.AsNoTracking().Include(x => x.Lines)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return run is null ? null : ToDto(run);
    }

    public async Task<IReadOnlyList<PayrollPeriodDto>> ListPayrollPeriodsAsync(CancellationToken cancellationToken) =>
        (await database.PayrollPeriods.AsNoTracking().OrderByDescending(x => x.StartDate).Take(200)
            .ToListAsync(cancellationToken)).Select(ToPayrollPeriodDto).ToArray();

    public async Task<PayrollPeriodDto?> GetPayrollPeriodAsync(Guid id, CancellationToken cancellationToken)
    {
        var period = await database.PayrollPeriods.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return period is null ? null : ToPayrollPeriodDto(period);
    }

    public async Task<PayrollPeriodDto> CreatePayrollPeriodAsync(PayrollPeriodRequest request,
        CancellationToken cancellationToken)
    {
        var number = Required(request.Number, "number", 50);
        var jurisdiction = Required(request.JurisdictionCode, "jurisdictionCode", 20).ToUpperInvariant();
        ValidateDate(request.StartDate, "startDate");
        ValidateDate(request.EndDate, "endDate");
        if (request.EndDate < request.StartDate || request.PayDate < request.EndDate)
            throw Validation("period", "End date must follow start date and pay date must be on or after period end.");
        if (!Enum.IsDefined(request.Type)) throw Validation("type", "Choose a supported payroll period type.");
        ValidatePayrollPeriodLength(request.Type, request.StartDate, request.EndDate);
        var expectedDates = request.ExpectedWorkDates?.Distinct().Order().ToArray() ?? [];
        if (expectedDates.Length == 0 || request.ExpectedWorkDates!.Count != expectedDates.Length ||
            expectedDates.Any(x => x < request.StartDate || x > request.EndDate))
            throw Validation("expectedWorkDates", "Provide unique expected work dates within the payroll period.");

        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        await LockPayrollScheduleAsync(cancellationToken);
        if (await database.PayrollPeriods.AnyAsync(x => x.Number == number ||
                (x.StartDate <= request.EndDate && x.EndDate >= request.StartDate), cancellationToken))
            throw new HcmConflictException("Payroll period number already exists or its dates overlap another company payroll period.");
        var period = new PayrollPeriod
        {
            Id = Guid.NewGuid(),
            TenantId = RequireTenant(),
            CompanyId = RequireCompany(),
            Number = number,
            Type = request.Type,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            PayDate = request.PayDate,
            JurisdictionCode = jurisdiction,
            Status = PayrollPeriodStatus.Open,
            ExpectedWorkDatesJson = JsonSerializer.Serialize(expectedDates),
            CreatedAtUtc = clock.UtcNow,
            ConcurrencyStamp = NewStamp()
        };
        database.PayrollPeriods.Add(period);
        await SaveAsync(cancellationToken);
        var result = ToPayrollPeriodDto(period);
        await AuditAsync("hcm.payroll.period.created", nameof(PayrollPeriod), period.Id, null, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<PayrollPeriodDto> ClosePayrollPeriodAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        await LockPayrollPeriodAsync(id, cancellationToken);
        var period = await database.PayrollPeriods.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Payroll period not found.");
        if (period.Status == PayrollPeriodStatus.Closed)
        {
            await transaction.CommitAsync(cancellationToken);
            return ToPayrollPeriodDto(period);
        }
        var run = await database.PayrollRuns.SingleOrDefaultAsync(x => x.PayrollPeriodId == period.Id, cancellationToken);
        if (run is null || run.Status is not (PayrollRunStatus.Approved or PayrollRunStatus.Posted or PayrollRunStatus.Paid))
            throw new HcmConflictException("A payroll period can close only after its calculated run is approved or paid.");
        var before = ToPayrollPeriodDto(period);
        period.Status = PayrollPeriodStatus.Closed;
        period.ConcurrencyStamp = NewStamp();
        await SaveAsync(cancellationToken);
        var result = ToPayrollPeriodDto(period);
        await AuditAsync("hcm.payroll.period.closed", nameof(PayrollPeriod), period.Id, before, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<PayrollRunDto> CreatePayrollRunAsync(Guid payrollPeriodId, CreatePayrollRunRequest request,
        CancellationToken cancellationToken)
    {
        if (payrollPeriodId == Guid.Empty) throw Validation("payrollPeriodId", "A payroll period is required.");
        var number = Required(request.Number, "number", 50);
        var idempotencyKey = Required(request.IdempotencyKey, "idempotencyKey", 100);
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        await LockPayrollPeriodAsync(payrollPeriodId, cancellationToken);
        var prior = await database.PayrollRuns.Include(x => x.Lines)
            .SingleOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken);
        if (prior is not null)
        {
            if (prior.PayrollPeriodId != payrollPeriodId || prior.Number != number)
                throw new HcmConflictException("The idempotency key is already assigned to a different payroll run request.");
            await transaction.CommitAsync(cancellationToken);
            return ToDto(prior);
        }
        var period = await database.PayrollPeriods.SingleOrDefaultAsync(x => x.Id == payrollPeriodId, cancellationToken)
            ?? throw new KeyNotFoundException("Payroll period not found.");
        if (period.Status != PayrollPeriodStatus.Open)
            throw new HcmConflictException("A closed payroll period cannot be calculated.");
        if (await database.PayrollRuns.AnyAsync(x => x.PayrollPeriodId == period.Id || x.Number == number, cancellationToken))
            throw new HcmConflictException("A payroll run already exists for this period or run number.");

        var expectedDates = JsonSerializer.Deserialize<DateOnly[]>(period.ExpectedWorkDatesJson) ?? [];
        var ruleCandidates = await database.PayrollRuleConfigurations.Where(x =>
            x.JurisdictionCode == period.JurisdictionCode && x.PeriodType == period.Type &&
            x.EffectiveFrom <= period.StartDate && (!x.EffectiveTo.HasValue || x.EffectiveTo >= period.EndDate))
            .ToListAsync(cancellationToken);
        if (ruleCandidates.Count != 1 || !ruleCandidates[0].IsSupported)
            throw Validation("jurisdiction", ruleCandidates.Count == 0
                ? $"No supported {period.Type} payroll rule version covers this period for {period.JurisdictionCode}."
                : "Payroll rule configuration is unsupported or ambiguous for this period.");
        var ruleEntity = ruleCandidates[0];
        var taxBands = JsonSerializer.Deserialize<PayrollTaxBandInput[]>(ruleEntity.TaxBandsJson) ?? [];
        var contributionRules = JsonSerializer.Deserialize<PayrollEmployerContributionInput[]>(ruleEntity.EmployerContributionsJson) ?? [];
        var rules = new PayrollRuleSet(ruleEntity.JurisdictionCode, ruleEntity.Version, ruleEntity.IsSupported,
            ruleEntity.StandardHoursPerPeriod, ruleEntity.OvertimeMultiplier,
            taxBands.Select(x => new PayrollTaxBand(x.AmountLimit, x.Rate)).ToArray(),
            contributionRules.Select(x => new PayrollContributionRule(x.Code, x.IsPercentage, x.Value, x.Basis, x.MaximumAmount)).ToArray(),
            ruleEntity.CurrencyPrecision, ruleEntity.RoundingMode);
        var employees = await database.Employees.Where(x => x.Status == EmployeeStatus.Active &&
                x.HireDate <= period.EndDate && (!x.EmploymentEndDate.HasValue || x.EmploymentEndDate >= period.StartDate))
            .OrderBy(x => x.EmployeeNumber).ToListAsync(cancellationToken);
        if (employees.Count == 0) throw Validation("employees", "No active employees are eligible for this payroll period.");
        var employeeIds = employees.Select(x => x.Id).ToArray();
        var compensationRows = await database.EmployeeCompensations.Where(x => employeeIds.Contains(x.EmployeeId) &&
            x.PeriodType == period.Type && x.EffectiveFrom <= period.EndDate &&
            (!x.EffectiveTo.HasValue || x.EffectiveTo >= period.StartDate))
            .OrderBy(x => x.EffectiveFrom).ToListAsync(cancellationToken);
        var compensations = compensationRows.GroupBy(x => x.EmployeeId).ToDictionary(x => x.Key, x => x.ToArray());
        var errors = new Dictionary<string, string[]>();
        var currencyCodes = compensationRows.Select(x => x.CurrencyCode).Distinct(StringComparer.Ordinal).ToArray();
        if (currencyCodes.Length != 1)
            throw Validation("currencyCode", "All compensation versions used by a payroll run must use the same currency.");

        var attendanceRows = await database.AttendanceRecords.Where(x => employeeIds.Contains(x.EmployeeId) &&
            x.WorkDate >= period.StartDate && x.WorkDate <= period.EndDate).ToListAsync(cancellationToken);
        var attendanceByKey = attendanceRows.ToDictionary(x => (x.EmployeeId, x.WorkDate));
        var leaves = await database.LeaveRequests.Where(x => employeeIds.Contains(x.EmployeeId) &&
            x.Status == LeaveRequestStatus.Approved && x.StartDate <= period.EndDate && x.EndDate >= period.StartDate)
            .ToListAsync(cancellationToken);
        var prepared = new List<(Employee Employee, EmployeeCompensation Compensation,
            PayrollCalculationResult Calculation, decimal Hours, AttendanceRecord[] Attendance,
            PayrollCompensationSegmentSnapshot[] Segments)>();
        if (expectedDates.Length == 0 || expectedDates.Distinct().Count() != expectedDates.Length ||
            expectedDates.Any(x => x < period.StartDate || x > period.EndDate))
            throw Validation("expectedWorkDates", "Expected work dates must be nonempty, unique, and within the payroll period.");
        var method = ruleEntity.ProrationMethod;
        if (method == PayrollProrationMethod.LegacyAttendanceHours)
            throw Validation("prorationMethod", "This payroll rule uses legacy attendance-hour calculation and cannot calculate hire, termination, or compensation changes within a period.");
        foreach (var employee in employees)
        {
            var attendance = new List<AttendanceRecord>();
            var employedDates = expectedDates.Where(date => PayrollProrationCalculator.IsEmployedOn(date,
                employee.HireDate, employee.EmploymentEndDate, ruleEntity.HireDateTreatment,
                ruleEntity.TerminationDateTreatment)).ToArray();
            if (employedDates.Length == 0) continue;
            var employeeCompensationVersions = compensations.GetValueOrDefault(employee.Id, []);
            decimal payableHours = 0;
            var recordsByDate = new Dictionary<DateOnly, AttendanceRecord>();
            foreach (var workDate in employedDates)
            {
                if (!attendanceByKey.TryGetValue((employee.Id, workDate), out var record))
                {
                    errors[$"employee:{employee.EmployeeNumber}:attendance:{workDate:yyyy-MM-dd}"] =
                        [$"{employee.FullName} has no attendance disposition for expected work date {workDate:yyyy-MM-dd}."];
                    continue;
                }
                attendance.Add(record);
                recordsByDate[workDate] = record;
                if (record.ApprovalStatus != AttendanceApprovalStatus.Finalized || record.Disposition is null)
                {
                    errors[$"employee:{employee.EmployeeNumber}:attendance:{workDate:yyyy-MM-dd}"] =
                        [$"{employee.FullName}'s attendance for {workDate:yyyy-MM-dd} is not finalized."];
                    continue;
                }
                var approvedLeave = leaves.Any(x => x.EmployeeId == employee.Id && x.StartDate <= workDate && x.EndDate >= workDate);
                var applicableForDate = employeeCompensationVersions.Where(x => x.EffectiveFrom <= workDate &&
                    (!x.EffectiveTo.HasValue || x.EffectiveTo >= workDate)).ToArray();
                var dailyStandardHours = (applicableForDate.Length == 1
                    ? applicableForDate[0].ScheduledHoursPerPeriod ?? ruleEntity.StandardHoursPerPeriod
                    : ruleEntity.StandardHoursPerPeriod) / expectedDates.Length;
                switch (record.Disposition.Value)
                {
                    case AttendanceDisposition.Worked:
                        if (record.CheckedInAtUtc is not { } checkIn || record.CheckedOutAtUtc is not { } checkOut || checkOut < checkIn)
                        {
                            errors[$"employee:{employee.EmployeeNumber}:attendance:{workDate:yyyy-MM-dd}"] =
                                [$"{employee.FullName}'s worked attendance for {workDate:yyyy-MM-dd} has invalid punches."];
                            continue;
                        }
                        var recordedBreaks = JsonSerializer.Deserialize<AttendanceBreakInput[]>(record.BreaksJson ?? "[]") ?? [];
                        var workedHours = AttendanceBreakPolicy.CalculatePaidHours(checkIn, checkOut, recordedBreaks);
                        if (workedHours < 0)
                        {
                            errors[$"employee:{employee.EmployeeNumber}:breaks:{workDate:yyyy-MM-dd}"] = ["Unpaid breaks exceed shift duration."];
                            continue;
                        }
                var requiredOvertime = decimal.Max(0, workedHours - dailyStandardHours);
                        if (decimal.Abs(record.ApprovedOvertimeHours - requiredOvertime) > 0.01m)
                        {
                            errors[$"employee:{employee.EmployeeNumber}:overtime:{workDate:yyyy-MM-dd}"] =
                                [$"{employee.FullName}'s overtime for {workDate:yyyy-MM-dd} must be approved for {requiredOvertime:N2} hours."];
                            continue;
                        }
                        payableHours += decimal.Min(workedHours, dailyStandardHours) + record.ApprovedOvertimeHours;
                        break;
                    case AttendanceDisposition.PaidLeave:
                        if (!approvedLeave) errors[$"employee:{employee.EmployeeNumber}:leave:{workDate:yyyy-MM-dd}"] =
                            [$"Paid leave for {workDate:yyyy-MM-dd} must be covered by an approved leave request."];
                        else payableHours += dailyStandardHours;
                        break;
                    case AttendanceDisposition.UnpaidLeave:
                        if (!approvedLeave) errors[$"employee:{employee.EmployeeNumber}:leave:{workDate:yyyy-MM-dd}"] =
                            [$"Unpaid leave for {workDate:yyyy-MM-dd} must be covered by an approved leave request."];
                        break;
                    case AttendanceDisposition.Absence:
                        if (approvedLeave) errors[$"employee:{employee.EmployeeNumber}:leave:{workDate:yyyy-MM-dd}"] =
                            [$"Approved leave for {workDate:yyyy-MM-dd} cannot be recorded as absence."];
                        break;
                    case AttendanceDisposition.Holiday:
                        break;
                    default:
                        errors[$"employee:{employee.EmployeeNumber}:attendance:{workDate:yyyy-MM-dd}"] =
                            [$"Unsupported attendance disposition for {workDate:yyyy-MM-dd}."];
                        break;
                }
            }
            if (errors.Keys.Any(key => key.StartsWith($"employee:{employee.EmployeeNumber}:", StringComparison.Ordinal))) continue;
            var employeeCompensations = employeeCompensationVersions;
            var covered = new Dictionary<Guid, EmployeeCompensation>();
            foreach (var workDate in employedDates)
            {
                var applicable = employeeCompensations.Where(x => x.EffectiveFrom <= workDate &&
                    (!x.EffectiveTo.HasValue || x.EffectiveTo >= workDate)).ToArray();
                if (applicable.Length != 1)
                {
                    errors[$"employee:{employee.EmployeeNumber}:compensation:{workDate:yyyy-MM-dd}"] =
                        [$"{employee.FullName} needs exactly one {period.Type} compensation version covering employed work date {workDate:yyyy-MM-dd}."];
                    continue;
                }
                covered[applicable[0].Id] = applicable[0];
            }
            if (covered.Count == 0 || errors.Keys.Any(key => key.StartsWith($"employee:{employee.EmployeeNumber}:", StringComparison.Ordinal))) continue;
            try
            {
                var segments = new List<PayrollCalculationSegment>();
                var segmentSnapshots = new List<PayrollCompensationSegmentSnapshot>();
                foreach (var compensation in covered.Values.OrderBy(x => x.EffectiveFrom))
                {
                    var segmentStart = compensation.EffectiveFrom > period.StartDate ? compensation.EffectiveFrom : period.StartDate;
                    var nextEffectiveFrom = covered.Values.Where(x => x.EffectiveFrom > compensation.EffectiveFrom)
                        .Select(x => (DateOnly?)x.EffectiveFrom).Min();
                    var segmentEnd = nextEffectiveFrom is { } nextDate
                        ? nextDate.AddDays(-1)
                        : compensation.EffectiveTo is { } effectiveTo && effectiveTo < period.EndDate ? effectiveTo : period.EndDate;
                    var segmentExpectedDates = employedDates.Where(date => date >= segmentStart && date <= segmentEnd).ToArray();
                    if (segmentExpectedDates.Length == 0) continue;
                    var factorDates = employedDates;
                    var factor = method == PayrollProrationMethod.ScheduledWorkingDays
                        ? (decimal)employedDates.Count(date => date >= segmentStart && date <= segmentEnd &&
                            (ruleEntity.UnpaidLeaveTreatment != PayrollUnpaidLeaveTreatment.ExcludeUnpaidScheduledDays ||
                             !recordsByDate.TryGetValue(date, out var attendanceForDate) || attendanceForDate.Disposition != AttendanceDisposition.UnpaidLeave)) /
                            Math.Max(1, employedDates.Count(date => ruleEntity.UnpaidLeaveTreatment != PayrollUnpaidLeaveTreatment.ExcludeUnpaidScheduledDays ||
                                !recordsByDate.TryGetValue(date, out var attendanceForDate) || attendanceForDate.Disposition != AttendanceDisposition.UnpaidLeave))
                        : PayrollProrationCalculator.SegmentFactor(method, period.StartDate, period.EndDate,
                            segmentStart, segmentEnd, factorDates);
                    var baselineHours = compensation.ScheduledHoursPerPeriod ?? ruleEntity.StandardHoursPerPeriod;
                    var standardHours = baselineHours * factor;
                    decimal regularHours = 0, overtimeHours = 0;
                    foreach (var workDate in segmentExpectedDates)
                    {
                        if (!recordsByDate.TryGetValue(workDate, out var record)) continue;
                        var perDayStandard = baselineHours / expectedDates.Length;
                        switch (record.Disposition)
                        {
                            case AttendanceDisposition.Worked:
                                var breaks = JsonSerializer.Deserialize<AttendanceBreakInput[]>(record.BreaksJson ?? "[]") ?? [];
                                var worked = AttendanceBreakPolicy.CalculatePaidHours(record.CheckedInAtUtc!.Value,
                                    record.CheckedOutAtUtc!.Value, breaks);
                                regularHours += decimal.Min(worked, perDayStandard);
                                overtimeHours += record.ApprovedOvertimeHours;
                                break;
                            case AttendanceDisposition.PaidLeave:
                            case AttendanceDisposition.Holiday:
                                regularHours += perDayStandard;
                                break;
                        }
                    }
                    var allowanceComponents = JsonSerializer.Deserialize<PayrollComponentInput[]>(compensation.AllowancesJson) ?? [];
                    var deductionComponents = JsonSerializer.Deserialize<PayrollComponentInput[]>(compensation.DeductionsJson) ?? [];
                    var scaledAllowances = allowanceComponents.Select(x => new PayrollAdjustment(x.Code, x.Amount * factor)).ToArray();
                    var scaledDeductions = deductionComponents.Select(x => new PayrollAdjustment(x.Code, x.Amount * factor)).ToArray();
                    segments.Add(new PayrollCalculationSegment(compensation.BasePayForPeriod * factor, standardHours,
                        regularHours, overtimeHours, scaledAllowances, scaledDeductions));
                    segmentSnapshots.Add(new(segmentStart, segmentEnd, factor, compensation.Id,
                        ToCompensationDto(compensation), standardHours, regularHours, overtimeHours,
                        allowanceComponents.Select(x => x with { Amount = x.Amount * factor }).ToArray(),
                        deductionComponents.Select(x => x with { Amount = x.Amount * factor }).ToArray()));
                }
                if (errors.Keys.Any(key => key.StartsWith($"employee:{employee.EmployeeNumber}:", StringComparison.Ordinal))) continue;
                var leadCompensation = covered.Values.OrderBy(x => x.EffectiveFrom).First();
                var calculation = PayrollCalculationEngine.Calculate(rules,
                    new PayrollCalculationInput(0, payableHours, [], [], segments));
                prepared.Add((employee, leadCompensation, calculation, payableHours, attendance.ToArray(), segmentSnapshots.ToArray()));
            }
            catch (ArgumentException exception)
            {
                errors[$"employee:{employee.EmployeeNumber}:calculation"] = [$"{employee.FullName}: {exception.Message}"];
            }
        }
        if (errors.Count > 0) throw new HcmValidationException(errors);

        var calculatedAt = clock.UtcNow;
        var runId = Guid.NewGuid();
        var run = new PayrollRun
        {
            Id = runId,
            TenantId = RequireTenant(),
            CompanyId = RequireCompany(),
            Number = number,
            PeriodStart = period.StartDate,
            PeriodEnd = period.EndDate,
            PayDate = period.PayDate,
            CurrencyCode = currencyCodes[0],
            Status = PayrollRunStatus.Draft,
            PayrollPeriodId = period.Id,
            IdempotencyKey = idempotencyKey,
            CreatedByUserId = CurrentUserId(),
            CalculationRuleVersion = $"{ruleEntity.JurisdictionCode}:{ruleEntity.Version}:{ruleEntity.PeriodType}",
            CreatedAtUtc = calculatedAt,
            UpdatedAtUtc = calculatedAt,
            ConcurrencyStamp = NewStamp()
        };
        var employeeSnapshots = new List<PayrollEmployeeCalculationSnapshot>();
        foreach (var item in prepared)
        {
            var lineId = Guid.NewGuid();
            var result = item.Calculation;
            var line = new PayrollLine
            {
                Id = lineId,
                TenantId = run.TenantId,
                CompanyId = run.CompanyId,
                PayrollRunId = runId,
                EmployeeId = item.Employee.Id,
                EmployeeNumber = item.Employee.EmployeeNumber,
                EmployeeName = item.Employee.FullName,
                GrossPay = result.GrossPay,
                TaxWithheld = result.TaxWithheld,
                OtherDeductions = result.OtherDeductions,
                EmployerContributions = result.EmployerContributions,
                NetPay = result.NetPay,
                AttendanceHours = item.Hours,
                CalculationDetailsJson = JsonSerializer.Serialize(result.Details)
            };
            run.Lines.Add(line);
            employeeSnapshots.Add(new(item.Employee.Id, item.Employee.EmployeeNumber, item.Employee.FullName,
                item.Compensation.Id, ToCompensationDto(item.Compensation), item.Attendance.Select(x =>
                    new PayrollAttendanceSnapshot(x.Id, x.WorkDate, x.ApprovalStatus, x.Disposition,
                        x.CheckedInAtUtc, x.CheckedOutAtUtc, x.ApprovedOvertimeHours,
                        JsonSerializer.Deserialize<AttendanceBreakInput[]>(x.BreaksJson ?? "[]") ?? []))
                    .ToArray(), result, item.Segments));
        }
        run.CalculationSnapshotJson = PayrollSnapshotJson.Serialize(new("2", calculatedAt, run.TenantId,
            run.CompanyId, ToPayrollPeriodDto(period), ruleEntity.Id, ToPayrollRuleDto(ruleEntity), employeeSnapshots));
        run.CalculationRevision = CalculationRevision(run);
        database.PayrollRuns.Add(run);
        await SaveAsync(cancellationToken);
        var dto = ToDto(run);
        await AuditAsync("hcm.payroll.calculated", nameof(PayrollRun), run.Id, null, dto, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return dto;
    }

    private async Task LockPayrollScheduleAsync(CancellationToken cancellationToken)
    {
        var key = $"{RequireTenant():N}:{RequireCompany():N}:payroll-schedule";
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
    }

    private async Task LockPayrollPeriodAsync(Guid periodId, CancellationToken cancellationToken)
    {
        var key = $"{RequireTenant():N}:{RequireCompany():N}:payroll-period:{periodId:N}";
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
    }

    private async Task LockPayrollRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var key = $"{RequireTenant():N}:{RequireCompany():N}:payroll-run:{runId:N}";
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
    }

    private static void ValidatePayrollPeriodLength(PayrollPeriodType type, DateOnly start, DateOnly end)
    {
        var days = end.DayNumber - start.DayNumber + 1;
        if ((type == PayrollPeriodType.Weekly && days != 7) ||
            (type == PayrollPeriodType.Biweekly && days != 14) ||
            (type == PayrollPeriodType.Monthly && (start.Day != 1 || end.Day != DateTime.DaysInMonth(end.Year, end.Month) || start.Month != end.Month || start.Year != end.Year)))
            throw Validation("period", "Weekly periods must be 7 days, biweekly 14 days, and monthly periods must match one calendar month.");
    }

    public async Task<IReadOnlyList<PayrollRuleConfigurationDto>> ListPayrollRulesAsync(CancellationToken cancellationToken) =>
        (await database.PayrollRuleConfigurations.AsNoTracking().OrderBy(x => x.JurisdictionCode)
            .ThenByDescending(x => x.EffectiveFrom).Take(500).ToListAsync(cancellationToken))
        .Select(ToPayrollRuleDto).ToArray();

    public async Task<PayrollRuleConfigurationDto> AddPayrollRuleAsync(PayrollRuleConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var jurisdiction = Required(request.JurisdictionCode, "jurisdictionCode", 20).ToUpperInvariant();
        var version = Required(request.Version, "version", 50);
        if (!Enum.IsDefined(request.PeriodType)) throw Validation("periodType", "Choose a supported payroll period type.");
        ValidateDate(request.EffectiveFrom, "effectiveFrom");
        ValidateDateRange(request.EffectiveFrom, request.EffectiveTo, "effectiveTo");
        if (request.StandardHoursPerPeriod <= 0 || request.OvertimeMultiplier < 1)
            throw Validation("rules", "Standard hours must be positive and the overtime multiplier must be at least one.");
        if (request.ProrationMethod is null or PayrollProrationMethod.LegacyAttendanceHours ||
            !Enum.IsDefined(request.ProrationMethod.Value) || request.HireDateTreatment is null ||
            request.TerminationDateTreatment is null || request.UnpaidLeaveTreatment is null || request.RoundingMode is null ||
            !Enum.IsDefined(request.HireDateTreatment.Value) || !Enum.IsDefined(request.TerminationDateTreatment.Value) ||
            !Enum.IsDefined(request.UnpaidLeaveTreatment.Value) || !Enum.IsDefined(request.RoundingMode.Value) ||
            request.CurrencyPrecision is < 0 or > 4)
            throw Validation("proration", "Choose an explicit supported proration method, employment-date treatment, unpaid-leave treatment, currency precision, and rounding mode.");
        var bands = request.TaxBands ?? [];
        var contributions = request.EmployerContributions ?? [];
        try
        {
            _ = PayrollCalculationEngine.Calculate(new PayrollRuleSet(jurisdiction, version, true,
                request.StandardHoursPerPeriod, request.OvertimeMultiplier,
                bands.Select(x => new PayrollTaxBand(x.AmountLimit, x.Rate)).ToArray(),
                contributions.Select(x => new PayrollContributionRule(x.Code, x.IsPercentage, x.Value, x.Basis, x.MaximumAmount)).ToArray(),
                request.CurrencyPrecision, request.RoundingMode.Value),
                new PayrollCalculationInput(0, 0, [], []));
        }
        catch (ArgumentException exception) { throw Validation("rules", exception.Message); }

        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        if (await database.PayrollRuleConfigurations.AnyAsync(x => x.JurisdictionCode == jurisdiction &&
                x.Version == version && x.PeriodType == request.PeriodType, cancellationToken))
            throw new HcmConflictException("Payroll rule versions are immutable and must have unique jurisdiction/version identifiers.");
        var entity = new PayrollRuleConfiguration
        {
            Id = Guid.NewGuid(),
            TenantId = RequireTenant(),
            CompanyId = RequireCompany(),
            JurisdictionCode = jurisdiction,
            Version = version,
            PeriodType = request.PeriodType,
            IsSupported = request.IsSupported,
            EffectiveFrom = request.EffectiveFrom,
            EffectiveTo = request.EffectiveTo,
            StandardHoursPerPeriod = request.StandardHoursPerPeriod,
            OvertimeMultiplier = request.OvertimeMultiplier,
            TaxBandsJson = JsonSerializer.Serialize(bands),
            EmployerContributionsJson = JsonSerializer.Serialize(contributions),
            ProrationMethod = request.ProrationMethod.Value,
            HireDateTreatment = request.HireDateTreatment.Value,
            TerminationDateTreatment = request.TerminationDateTreatment.Value,
            UnpaidLeaveTreatment = request.UnpaidLeaveTreatment.Value,
            CurrencyPrecision = request.CurrencyPrecision,
            RoundingMode = request.RoundingMode.Value,
            CreatedAtUtc = clock.UtcNow
        };
        database.PayrollRuleConfigurations.Add(entity);
        await SaveAsync(cancellationToken);
        var result = ToPayrollRuleDto(entity);
        await AuditAsync("hcm.payroll.rules.created", nameof(PayrollRuleConfiguration), entity.Id, null, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<EmployeeCompensationDto>> ListCompensationAsync(Guid? employeeId,
        CancellationToken cancellationToken)
    {
        var query = database.EmployeeCompensations.AsNoTracking().AsQueryable();
        if (employeeId.HasValue) query = query.Where(x => x.EmployeeId == employeeId.Value);
        return (await query.OrderBy(x => x.EmployeeId).ThenByDescending(x => x.EffectiveFrom)
            .Take(1000).ToListAsync(cancellationToken)).Select(ToCompensationDto).ToArray();
    }

    public async Task<EmployeeCompensationDto> AddCompensationAsync(EmployeeCompensationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.EmployeeId == Guid.Empty) throw Validation("employeeId", "An employee is required.");
        if (!Enum.IsDefined(request.PeriodType)) throw Validation("periodType", "Choose a supported payroll period type.");
        ValidateDate(request.EffectiveFrom, "effectiveFrom");
        ValidateDateRange(request.EffectiveFrom, request.EffectiveTo, "effectiveTo");
        var currency = Required(request.CurrencyCode, "currencyCode", 3).ToUpperInvariant();
        if (currency.Length != 3 || currency.Any(c => c is < 'A' or > 'Z'))
            throw Validation("currencyCode", "Use a three-letter ISO currency code.");
        if (request.BasePayForPeriod < 0 || decimal.Round(request.BasePayForPeriod, 2) != request.BasePayForPeriod)
            throw Validation("basePayForPeriod", "Base pay must be nonnegative and rounded to two decimal places.");
        if (request.ScheduledHoursPerPeriod is { } scheduledHours &&
            (scheduledHours <= 0 || decimal.Round(scheduledHours, 2) != scheduledHours))
            throw Validation("scheduledHoursPerPeriod", "Scheduled hours must be positive and rounded to two decimal places.");
        var allowances = ValidatePayrollComponents(request.Allowances, "allowances");
        var deductions = ValidatePayrollComponents(request.Deductions, "deductions");
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        await LockCompensationHistoryAsync(request.EmployeeId, cancellationToken);
        _ = await database.Employees.SingleOrDefaultAsync(x => x.Id == request.EmployeeId && x.Status == EmployeeStatus.Active,
            cancellationToken) ?? throw Validation("employeeId", "Compensation must reference an active employee in this company.");
        var overlap = await database.EmployeeCompensations.AnyAsync(x => x.EmployeeId == request.EmployeeId &&
            x.PeriodType == request.PeriodType &&
            (!x.EffectiveTo.HasValue || x.EffectiveTo.Value >= request.EffectiveFrom) &&
            (!request.EffectiveTo.HasValue || request.EffectiveTo.Value >= x.EffectiveFrom), cancellationToken);
        if (overlap) throw new HcmConflictException("Compensation effective dates cannot overlap; add a new nonoverlapping version to preserve history.");
        var entity = new EmployeeCompensation
        {
            Id = Guid.NewGuid(),
            TenantId = RequireTenant(),
            CompanyId = RequireCompany(),
            EmployeeId = request.EmployeeId,
            PeriodType = request.PeriodType,
            CurrencyCode = currency,
            EffectiveFrom = request.EffectiveFrom,
            EffectiveTo = request.EffectiveTo,
            BasePayForPeriod = request.BasePayForPeriod,
            ScheduledHoursPerPeriod = request.ScheduledHoursPerPeriod,
            AllowancesJson = JsonSerializer.Serialize(allowances),
            DeductionsJson = JsonSerializer.Serialize(deductions),
            CreatedAtUtc = clock.UtcNow
        };
        database.EmployeeCompensations.Add(entity);
        await SaveAsync(cancellationToken);
        var result = ToCompensationDto(entity);
        await AuditAsync("hcm.payroll.compensation.created", nameof(EmployeeCompensation), entity.Id, null, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task LockCompensationHistoryAsync(Guid employeeId, CancellationToken cancellationToken)
    {
        var key = $"{RequireTenant():N}:{RequireCompany():N}:{employeeId:N}";
        _ = await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
    }

    public async Task<PayrollRunDto> TransitionPayrollRunAsync(Guid id, PayrollRunStatus status,
        Guid concurrencyStamp, CancellationToken cancellationToken)
    {
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        await LockPayrollRunAsync(id, cancellationToken);
        var run = await database.PayrollRuns.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Payroll run not found.");
        if (run.ConcurrencyStamp != concurrencyStamp) throw new HcmConcurrencyException();
        if (!Enum.IsDefined(status) || !((run.Status, status) is
            (PayrollRunStatus.Posted, PayrollRunStatus.Paid)))
            throw new HcmConflictException("Payroll can be marked paid only after Accounting posting has completed.");
        if (run.AccountingJournalEntryId is null || string.IsNullOrWhiteSpace(run.AccountingJournalNumber))
            throw new HcmConflictException("A posted Accounting journal is required before payroll can be marked paid.");
        if (status == PayrollRunStatus.Approved && (run.Lines.Count == 0 ||
            string.IsNullOrWhiteSpace(run.CalculationRuleVersion) ||
            string.IsNullOrWhiteSpace(run.CalculationSnapshotJson) ||
            run.Lines.Any(line => line.GrossPay <= 0 || line.TaxWithheld + line.OtherDeductions > line.GrossPay ||
                line.NetPay != line.GrossPay - line.TaxWithheld - line.OtherDeductions)))
            throw new HcmConflictException("Payroll cannot be approved without a complete, reproducible calculation snapshot.");
        var before = ToDto(run);
        run.Status = status;
        run.UpdatedAtUtc = clock.UtcNow;
        run.ConcurrencyStamp = NewStamp();
        await SaveAsync(cancellationToken);
        var result = ToDto(run);
        await AuditAsync(status == PayrollRunStatus.Approved ? "hcm.payroll.approved" : "hcm.payroll.paid",
            "PayrollRun", run.Id, before, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public Task<PayrollRunDto> SubmitPayrollRunAsync(Guid id, PayrollReviewRequest request,
        CancellationToken cancellationToken) => ChangePayrollReviewAsync(id, request, PayrollRunStatus.Submitted, cancellationToken);

    public async Task<PayrollRunDto> ReviewPayrollRunAsync(Guid id, bool approve, PayrollReviewRequest request,
        CancellationToken cancellationToken)
    {
        if (!approve && string.IsNullOrWhiteSpace(request.Comment))
            throw Validation("comment", "A reason is required when rejecting payroll.");
        return await ChangePayrollReviewAsync(id, request,
            approve ? PayrollRunStatus.Approved : PayrollRunStatus.Rejected, cancellationToken);
    }

    public async Task<IReadOnlyList<PayrollApprovalEventDto>> GetPayrollApprovalHistoryAsync(Guid id,
        CancellationToken cancellationToken)
    {
        if (!await database.PayrollRuns.AnyAsync(x => x.Id == id, cancellationToken))
            throw new KeyNotFoundException("Payroll run not found.");
        return await database.PayrollApprovalEvents.AsNoTracking().Where(x => x.PayrollRunId == id)
            .OrderBy(x => x.OccurredAtUtc)
            .Select(x => new PayrollApprovalEventDto(x.Id, x.Status, x.Comment, x.ActorUserId,
                x.CalculationRuleVersion, x.CalculationRevision, x.OccurredAtUtc))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<PayrollPostingStatusDto> PostPayrollRunAsync(Guid id, PayrollPostingRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        await LockPayrollRunAsync(id, cancellationToken);
        var run = await database.PayrollRuns.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Payroll run not found.");
        if (run.Status is PayrollRunStatus.Posted or PayrollRunStatus.Paid)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(run.AccountingJournalEntryId, run.AccountingJournalNumber, run.PostingError, run.PostedAtUtc, PayrollPostingStatus.Posted);
        }
        if (run.Status == PayrollRunStatus.Reversed)
            throw new HcmConflictException("A reversed payroll run cannot be posted again.");
        if (run.Status != PayrollRunStatus.Approved)
            throw new HcmConflictException("Only approved payroll runs can be posted to Accounting.");
        if (request.ConcurrencyStamp != run.ConcurrencyStamp) throw new HcmConcurrencyException();
        var key = Required(request.IdempotencyKey, "idempotencyKey", 100);
        var revision = run.CalculationRevision ?? CalculationRevision(run);
        if (revision != CalculationRevision(run))
            throw new HcmConflictException("The stored payroll calculation revision does not match its immutable snapshot.");
        var approvalRevision = await database.PayrollApprovalEvents.AsNoTracking()
            .Where(x => x.PayrollRunId == run.Id && x.Status == PayrollRunStatus.Approved)
            .OrderByDescending(x => x.OccurredAtUtc).Select(x => x.CalculationRevision).FirstOrDefaultAsync(cancellationToken);
        if (approvalRevision is null || approvalRevision != revision)
            throw new HcmConflictException("The approved calculation revision does not match the payroll snapshot selected for posting.");
        var totals = run.Lines.Aggregate((Gross: 0m, Tax: 0m, Deductions: 0m, Net: 0m),
            (sum, line) => (sum.Gross + line.GrossPay, sum.Tax + line.TaxWithheld,
                sum.Deductions + line.OtherDeductions, sum.Net + line.NetPay));
        var mappingRows = await database.PayrollAccountMappings.AsNoTracking()
            .Where(x => x.EffectiveFrom <= run.PayDate && (x.EffectiveTo == null || x.EffectiveTo >= run.PayDate) &&
                (x.CurrencyCode == null || x.CurrencyCode == run.CurrencyCode))
            .OrderByDescending(x => x.CurrencyCode != null).ToListAsync(cancellationToken);
        var mappings = mappingRows.GroupBy(x => x.Type).ToDictionary(x => x.Key, x => x.First());
        var lines = new List<PayrollJournalLineRequest>();
        var details = run.Lines.SelectMany(x => JsonSerializer.Deserialize<PayrollCalculationDetailDto[]>(x.CalculationDetailsJson ?? "[]") ?? []).ToArray();
        AddMapped(PayrollAccountMappingType.SalaryExpense, "Payroll salary expense",
            details.Where(x => x.Code == "attendance-pay").Sum(x => x.Amount), true, required: true);
        AddMapped(PayrollAccountMappingType.OvertimeExpense, "Payroll overtime expense",
            details.Where(x => x.Code == "overtime-pay").Sum(x => x.Amount), true, required: false);
        AddMapped(PayrollAccountMappingType.AllowanceExpense, "Payroll allowance expense",
            details.Where(x => x.Code.StartsWith("allowance:", StringComparison.Ordinal)).Sum(x => x.Amount), true, required: false);
        AddMapped(PayrollAccountMappingType.EmployerContributionExpense, "Employer contribution expense",
            run.Lines.Sum(x => x.EmployerContributions), true, required: run.Lines.Any(x => x.EmployerContributions > 0));
        AddMapped(PayrollAccountMappingType.EmployerContributionLiability, "Employer contributions payable",
            run.Lines.Sum(x => x.EmployerContributions), false, required: run.Lines.Any(x => x.EmployerContributions > 0));
        AddMapped(PayrollAccountMappingType.EmployeeDeductionLiability, "Employee deductions payable", totals.Deductions, false, required: totals.Deductions > 0);
        AddMapped(PayrollAccountMappingType.TaxLiability, "Employee tax withholding payable", totals.Tax, false, required: totals.Tax > 0);
        AddMapped(PayrollAccountMappingType.NetSalaryPayable, "Net payroll payable", totals.Net, false, required: true);
        var grouped = lines.Where(x => x.Debit != 0 || x.Credit != 0).ToArray();
        if (grouped.Length < 2 || decimal.Round(grouped.Sum(x => x.Debit), 2) != decimal.Round(grouped.Sum(x => x.Credit), 2))
            throw new HcmConflictException("Payroll totals do not produce a balanced journal; no accounting entry was created.");
        var posted = await payrollJournalPoster.PostPayrollAsync(new PayrollJournalRequest(run.Id, run.Number,
                revision, run.PayDate, run.CurrencyCode, key, grouped), cancellationToken);
        run.Status = PayrollRunStatus.Posted;
        run.AccountingJournalEntryId = posted.JournalEntryId;
        run.AccountingJournalNumber = posted.Number;
        run.PostingError = null;
        run.PostedAtUtc = clock.UtcNow;
        run.UpdatedAtUtc = clock.UtcNow;
        run.ConcurrencyStamp = NewStamp();
        await SaveAsync(cancellationToken);
        await AuditAsync("hcm.payroll.posted", nameof(PayrollRun), run.Id, null,
            new { run.Id, run.AccountingJournalEntryId, run.AccountingJournalNumber, revision }, cancellationToken);
        var result = new PayrollPostingStatusDto(run.AccountingJournalEntryId, run.AccountingJournalNumber,
            null, run.PostedAtUtc, PayrollPostingStatus.Posted);
        await transaction.CommitAsync(cancellationToken);
        return result;

        void AddMapped(PayrollAccountMappingType type, string description, decimal amount, bool debit, bool required)
        {
            if (amount == 0) return;
            if (!mappings.TryGetValue(type, out var mapping))
            {
                if (required) throw Validation("accountMappings", $"Configure an effective {type} account mapping for {run.CurrencyCode}.");
                throw Validation("accountMappings", $"Payroll contains {description.ToLowerInvariant()} but no effective {type} account mapping exists.");
            }
            lines.Add(new PayrollJournalLineRequest(mapping.AccountId, description, debit ? amount : 0, debit ? 0 : amount));
        }
    }

    public async Task<PayrollPostingStatusDto> GetPayrollPostingStatusAsync(Guid id, CancellationToken cancellationToken)
    {
        var run = await database.PayrollRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Payroll run not found.");
        var status = run.Status == PayrollRunStatus.Posted ? PayrollPostingStatus.Posted :
            run.PostingError is not null ? PayrollPostingStatus.Failed : PayrollPostingStatus.NotPosted;
        return new(run.AccountingJournalEntryId, run.AccountingJournalNumber, run.PostingError, run.PostedAtUtc, status);
    }

    public async Task<PayrollReversalDto> ReversePayrollRunAsync(Guid id, PayrollReversalRequest request,
        CancellationToken cancellationToken)
    {
        var reason = Required(request.Reason, "reason", 1000);
        var key = Required(request.IdempotencyKey, "idempotencyKey", 100);
        if (request.BusinessDate == default) throw Validation("businessDate", "A reversal business date is required.");
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        await LockPayrollRunAsync(id, cancellationToken);
        var run = await database.PayrollRuns.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Payroll run not found.");
        if (run.ReversalJournalEntryId is { } existingReversal)
        {
            if (run.ReversalIdempotencyKey != key || run.ReversalReason != reason ||
                (run.ReversalBusinessDate.HasValue && run.ReversalBusinessDate.Value != request.BusinessDate))
                throw new HcmConflictException("This payroll run has already been reversed with another request.");
            await transaction.CommitAsync(cancellationToken);
            return new(run.Id, run.AccountingJournalEntryId!.Value, run.AccountingJournalNumber!, existingReversal,
                run.ReversalJournalNumber!, run.ReversalReason!, run.ReversedByUserId, run.ReversedAtUtc!.Value, key);
        }
        if (run.Status != PayrollRunStatus.Posted || run.AccountingJournalEntryId is not { } journalId ||
            string.IsNullOrWhiteSpace(run.AccountingJournalNumber))
            throw new HcmConflictException("Only posted payroll runs with a linked Accounting journal can be reversed.");
        if (run.ConcurrencyStamp != request.ConcurrencyStamp) throw new HcmConcurrencyException();
        var actor = CurrentUserId();
        if (actor is null) throw new HcmConflictException("An authenticated user is required to reverse payroll.");

        // Accounting owns the compensating journal and enforces one reversal per original journal.
        var reversal = await payrollJournalPoster.ReversePayrollAsync(journalId, request.BusinessDate, reason,
            key, cancellationToken);
        run.Status = PayrollRunStatus.Reversed;
        run.ReversalJournalEntryId = reversal.JournalEntryId;
        run.ReversalJournalNumber = reversal.Number;
        run.ReversalIdempotencyKey = key;
        run.ReversalReason = reason;
        run.ReversalBusinessDate = request.BusinessDate;
        run.ReversedByUserId = actor;
        run.ReversedAtUtc = clock.UtcNow;
        run.UpdatedAtUtc = clock.UtcNow;
        run.ConcurrencyStamp = NewStamp();
        await SaveAsync(cancellationToken);
        await AuditAsync("hcm.payroll.reversed", nameof(PayrollRun), run.Id,
            new { run.AccountingJournalEntryId, run.AccountingJournalNumber },
            new { run.ReversalJournalEntryId, run.ReversalJournalNumber, reason, actor, key }, cancellationToken);
        var result = new PayrollReversalDto(run.Id, journalId, run.AccountingJournalNumber, reversal.JournalEntryId, reversal.Number,
            reason, actor, run.ReversedAtUtc.Value, key);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<PayrollReconciliationDto> ReconcilePayrollRunAsync(Guid id, CancellationToken cancellationToken)
    {
        var run = await database.PayrollRuns.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Payroll run not found.");
        var totals = run.Lines.Aggregate((Gross: 0m, Employer: 0m, Tax: 0m, Deductions: 0m, Net: 0m),
            (sum, line) => (sum.Gross + line.GrossPay, sum.Employer + line.EmployerContributions,
                sum.Tax + line.TaxWithheld, sum.Deductions + line.OtherDeductions, sum.Net + line.NetPay));
        var expectedDebits = totals.Gross + totals.Employer;
        var expectedCredits = totals.Tax + totals.Deductions + totals.Net;
        var discrepancies = new List<string>();
        if (run.AccountingJournalEntryId is not { } journalId)
            return new(run.Id, null, false, expectedDebits, expectedCredits, 0, 0, run.CurrencyCode,
                ["Payroll has no linked Accounting journal."]);
        var journal = await payrollJournalPoster.GetPayrollJournalAsync(journalId, cancellationToken);
        if (journal is null) discrepancies.Add("The linked Accounting journal could not be found in this company.");
        else
        {
            if (journal.State != "Posted") discrepancies.Add($"Journal state is {journal.State}, expected Posted.");
            if (journal.CurrencyCode != run.CurrencyCode) discrepancies.Add("Payroll and journal currencies differ.");
            if (decimal.Round(journal.TotalDebit, 2) != decimal.Round(expectedDebits, 2)) discrepancies.Add("Journal debits differ from payroll expense totals.");
            if (decimal.Round(journal.TotalCredit, 2) != decimal.Round(expectedCredits, 2)) discrepancies.Add("Journal credits differ from payroll liability totals.");
            if (decimal.Round(journal.TotalDebit, 2) != decimal.Round(journal.TotalCredit, 2)) discrepancies.Add("The linked journal is not balanced.");
        }
        return new(run.Id, run.AccountingJournalEntryId, discrepancies.Count == 0, expectedDebits, expectedCredits,
            journal?.TotalDebit ?? 0, journal?.TotalCredit ?? 0, journal?.CurrencyCode ?? run.CurrencyCode, discrepancies);
    }

    private async Task<PayrollRunDto> ChangePayrollReviewAsync(Guid id, PayrollReviewRequest request,
        PayrollRunStatus target, CancellationToken cancellationToken)
    {
        if (request.Comment?.Length > 1000) throw Validation("comment", "Comments are limited to 1000 characters.");
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        await LockPayrollRunAsync(id, cancellationToken);
        var run = await database.PayrollRuns.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Payroll run not found.");
        if (run.ConcurrencyStamp != request.ConcurrencyStamp) throw new HcmConcurrencyException();
        var allowed = target == PayrollRunStatus.Submitted
            ? run.Status is PayrollRunStatus.Draft or PayrollRunStatus.Rejected
            : run.Status == PayrollRunStatus.Submitted;
        if (!allowed) throw new HcmConflictException("The payroll run is not in a state that permits this review action.");
        if (string.IsNullOrWhiteSpace(run.CalculationSnapshotJson) ||
            string.IsNullOrWhiteSpace(run.CalculationRevision) ||
            !StringComparer.Ordinal.Equals(run.CalculationRevision, CalculationRevision(run)))
            throw new HcmConflictException("The stored payroll snapshot is missing or no longer matches its approved revision.");
        var actor = CurrentUserId();
        if (target == PayrollRunStatus.Approved && (actor is null || actor == run.CreatedByUserId))
            throw new HcmConflictException("A payroll run must be approved by a different authenticated user than its creator.");
        if (target == PayrollRunStatus.Submitted && (run.Lines.Count == 0 ||
            string.IsNullOrWhiteSpace(run.CalculationRuleVersion) || string.IsNullOrWhiteSpace(run.CalculationSnapshotJson) ||
            run.Lines.Any(line => line.GrossPay <= 0 || line.TaxWithheld + line.OtherDeductions > line.GrossPay ||
                line.NetPay != line.GrossPay - line.TaxWithheld - line.OtherDeductions)))
            throw new HcmConflictException("Payroll validation failed; a complete reproducible calculation is required before submission.");
        var before = ToDto(run);
        run.Status = target;
        run.UpdatedAtUtc = clock.UtcNow;
        run.ConcurrencyStamp = NewStamp();
        database.PayrollApprovalEvents.Add(new PayrollApprovalEvent
        {
            Id = Guid.NewGuid(),
            TenantId = run.TenantId,
            CompanyId = run.CompanyId,
            PayrollRunId = run.Id,
            Status = target,
            Comment = request.Comment?.Trim(),
            CalculationRuleVersion = run.CalculationRuleVersion,
            CalculationRevision = run.CalculationRevision,
            ActorUserId = actor,
            OccurredAtUtc = clock.UtcNow
        });
        await SaveAsync(cancellationToken);
        var result = ToDto(run);
        await AuditAsync($"hcm.payroll.{target.ToString().ToLowerInvariant()}", nameof(PayrollRun), run.Id,
            before, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private Guid? CurrentUserId()
    {
        var value = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? httpContextAccessor.HttpContext?.User.FindFirstValue("sub");
        return Guid.TryParse(value, out var userId) ? userId : null;
    }

    private static string CalculationRevision(PayrollRun run) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(run.CalculationSnapshotJson ?? string.Empty)));

    public async Task<PayrollAdjustmentDto> CreatePayrollAdjustmentAsync(PayrollAdjustmentRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.Category)) throw Validation("category", "Choose a supported payroll correction category.");
        var reason = Required(request.Reason, "reason", 1000);
        var key = Required(request.IdempotencyKey, "idempotencyKey", 100);
        var evidence = string.IsNullOrWhiteSpace(request.EvidenceReference) ? null : Required(request.EvidenceReference, "evidenceReference", 300);
        if (request.Amount == 0) throw Validation("amount", "A payroll correction amount cannot be zero.");
        var creatorId = CurrentUserId() ?? throw new HcmConflictException("An authenticated user identity is required to create a payroll adjustment.");
        if (request.Category == PayrollAdjustmentCategory.OverpaymentRecovery &&
            (request.Amount < 0 || string.IsNullOrWhiteSpace(evidence)))
            throw Validation("recovery", "Overpayment recovery must be a positive amount with an evidence reference. Posting recognizes a receivable and does not deduct future pay.");
        var existing = await database.PayrollAdjustments.SingleOrDefaultAsync(x => x.IdempotencyKey == key, cancellationToken);
        if (existing is not null)
        {
            if (existing.PayrollRunId != request.PayrollRunId || existing.EmployeeId != request.EmployeeId ||
                existing.Category != request.Category || existing.Amount != request.Amount || existing.EffectiveDate != request.EffectiveDate ||
                existing.Reason != reason || existing.EvidenceReference != evidence)
                throw new HcmConflictException("The adjustment idempotency key is already assigned to different correction details.");
            return ToPayrollAdjustmentDto(existing);
        }
        var run = await database.PayrollRuns.Include(x => x.Lines)
            .SingleOrDefaultAsync(x => x.Id == request.PayrollRunId, cancellationToken)
            ?? throw new KeyNotFoundException("Original payroll run not found.");
        if (run.Status is not (PayrollRunStatus.Approved or PayrollRunStatus.Posted or PayrollRunStatus.Paid))
            throw new HcmConflictException("Adjustments can be created only for approved, posted, or paid payroll runs.");
        var line = run.Lines.SingleOrDefault(x => x.EmployeeId == request.EmployeeId)
            ?? throw Validation("employeeId", "The employee has no line in the selected original payroll run.");
        if (request.EffectiveDate < run.PeriodStart || request.EffectiveDate > run.PeriodEnd)
            throw Validation("effectiveDate", "The correction effective date must be within the original payroll period.");
        var snapshot = PayrollSnapshotJson.Deserialize(run.CalculationSnapshotJson)
            ?? throw new HcmConflictException("The original payroll calculation snapshot is unavailable; a reliable correction cannot be created.");
        var revision = run.CalculationRevision ?? CalculationRevision(run);
        if (revision != CalculationRevision(run) || snapshot.TenantId != RequireTenant() || snapshot.CompanyId != RequireCompany())
            throw new HcmConflictException("The original payroll snapshot failed integrity or company-scope validation.");
        var sourceEmployee = snapshot.Employees.SingleOrDefault(x => x.EmployeeId == line.EmployeeId);
        if (sourceEmployee is null || sourceEmployee.Calculation.GrossPay != line.GrossPay ||
            sourceEmployee.Calculation.TaxWithheld != line.TaxWithheld ||
            sourceEmployee.Calculation.OtherDeductions != line.OtherDeductions ||
            sourceEmployee.Calculation.EmployerContributions != line.EmployerContributions ||
            sourceEmployee.Calculation.NetPay != line.NetPay)
            throw new HcmConflictException("The original payroll line does not match its immutable calculation snapshot.");
        var rules = snapshot.PayrollRules;
        var amount = decimal.Round(request.Amount, rules.CurrencyPrecision,
            rules.RoundingMode == PayrollRoundingMode.MidpointToEven ? MidpointRounding.ToEven : MidpointRounding.AwayFromZero);
        if (amount == 0 || decimal.Abs(amount - request.Amount) > 0.0000001m)
            throw Validation("amount", $"Amount must be nonzero and rounded to {rules.CurrencyPrecision} currency decimal places.");
        PayrollAccountMappingType categoryMapping;
        try { categoryMapping = PayrollAdjustmentJournalBuilder.RequiredComponentMapping(request.Category); }
        catch (ArgumentException exception) { throw Validation("category", exception.Message); }
        var now = clock.UtcNow;
        var entity = new PayrollAdjustmentRecord
        {
            Id = Guid.NewGuid(), TenantId = RequireTenant(), CompanyId = RequireCompany(), PayrollRunId = run.Id,
            EmployeeId = line.EmployeeId, OriginalPeriodStart = run.PeriodStart, OriginalPeriodEnd = run.PeriodEnd,
            EmployeeNumber = line.EmployeeNumber, EmployeeName = line.EmployeeName, Category = request.Category,
            CalculationMode = PayrollAdjustmentCalculationMode.Manual,
            Status = PayrollAdjustmentStatus.Draft, Reason = reason, EffectiveDate = request.EffectiveDate,
            CurrencyCode = run.CurrencyCode, Amount = amount, EvidenceReference = evidence,
            SnapshotJson = "{}", SnapshotRevision = string.Empty, IdempotencyKey = key, CreatedByUserId = creatorId,
            CreatedAtUtc = now, UpdatedAtUtc = now, ConcurrencyStamp = NewStamp()
        };
        var adjustmentSnapshot = new PayrollAdjustmentSnapshot("1", entity.Id, run.Id, line.EmployeeId,
            run.PeriodStart, run.PeriodEnd, revision, run.Status, request.Category, categoryMapping, request.EffectiveDate, run.CurrencyCode,
            line.GrossPay, line.TaxWithheld, line.OtherDeductions, line.NetPay, amount, reason, evidence,
            rules.CurrencyPrecision, rules.RoundingMode);
        entity.SnapshotJson = JsonSerializer.Serialize(adjustmentSnapshot);
        entity.SnapshotRevision = PayrollAdjustmentSnapshotIntegrity.ComputeRevision(entity.SnapshotJson);
        database.PayrollAdjustments.Add(entity);
        AddAdjustmentEvent(entity, PayrollAdjustmentStatus.Draft, reason);
        await SaveAsync(cancellationToken);
        await AuditAsync("hcm.payroll.adjustment.created", nameof(PayrollAdjustmentRecord), entity.Id, null,
            ToPayrollAdjustmentDto(entity), cancellationToken);
        return ToPayrollAdjustmentDto(entity);
    }

    public async Task<PayrollAdjustmentDto> CreateRecalculatedPayrollAdjustmentAsync(
        PayrollRecalculatedAdjustmentRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.Category)) throw Validation("category", "Choose a supported recalculated correction category.");
        var reason = Required(request.Reason, "reason", 1000);
        var key = Required(request.IdempotencyKey, "idempotencyKey", 100);
        var evidence = string.IsNullOrWhiteSpace(request.EvidenceReference) ? null : Required(request.EvidenceReference, "evidenceReference", 300);
        var correction = request.Correction ?? throw Validation("correction", "Corrected source values are required for recalculation.");
        var creatorId = CurrentUserId() ?? throw new HcmConflictException("An authenticated user identity is required to create a payroll adjustment.");
        var prior = await database.PayrollAdjustments.SingleOrDefaultAsync(x => x.IdempotencyKey == key, cancellationToken);
        if (prior is not null)
        {
            var priorSnapshot = JsonSerializer.Deserialize<PayrollAdjustmentSnapshot>(prior.SnapshotJson);
            if (prior.CalculationMode != PayrollAdjustmentCalculationMode.Recalculated ||
                prior.PayrollRunId != request.PayrollRunId || prior.EmployeeId != request.EmployeeId ||
                prior.Category != request.Category || prior.EffectiveDate != request.EffectiveDate ||
                prior.Reason != reason || prior.EvidenceReference != evidence || priorSnapshot?.CorrectionInput != correction)
                throw new HcmConflictException("The adjustment idempotency key is already assigned to different correction details.");
            return ToPayrollAdjustmentDto(prior);
        }
        var run = await database.PayrollRuns.Include(x => x.Lines)
            .SingleOrDefaultAsync(x => x.Id == request.PayrollRunId, cancellationToken)
            ?? throw new KeyNotFoundException("Original payroll run not found.");
        if (run.Status is not (PayrollRunStatus.Approved or PayrollRunStatus.Posted or PayrollRunStatus.Paid))
            throw new HcmConflictException("Adjustments can be created only for approved, posted, or paid payroll runs.");
        var line = run.Lines.SingleOrDefault(x => x.EmployeeId == request.EmployeeId)
            ?? throw Validation("employeeId", "The employee has no line in the selected original payroll run.");
        if (request.EffectiveDate < run.PeriodStart || request.EffectiveDate > run.PeriodEnd)
            throw Validation("effectiveDate", "The correction effective date must be within the original payroll period.");
        var source = PayrollSnapshotJson.Deserialize(run.CalculationSnapshotJson)
            ?? throw new HcmConflictException("The original payroll calculation snapshot is unavailable.");
        var revision = run.CalculationRevision ?? CalculationRevision(run);
        if (revision != CalculationRevision(run) || source.TenantId != RequireTenant() || source.CompanyId != RequireCompany())
            throw new HcmConflictException("The original payroll snapshot failed integrity or company-scope validation.");
        var employeeSnapshot = source.Employees.SingleOrDefault(x => x.EmployeeId == line.EmployeeId)
            ?? throw new HcmConflictException("The employee calculation is missing from the original payroll snapshot.");
        if (employeeSnapshot.Calculation.GrossPay != line.GrossPay || employeeSnapshot.Calculation.TaxWithheld != line.TaxWithheld ||
            employeeSnapshot.Calculation.OtherDeductions != line.OtherDeductions ||
            employeeSnapshot.Calculation.EmployerContributions != line.EmployerContributions ||
            employeeSnapshot.Calculation.NetPay != line.NetPay)
            throw new HcmConflictException("The original payroll line does not match its immutable calculation snapshot.");
        RecalculatedPayrollAdjustment recalculated;
        try
        {
            recalculated = PayrollRecalculationEngine.Recalculate(source, line.EmployeeId, request.Category,
                request.EffectiveDate, correction);
        }
        catch (ArgumentException exception)
        {
            throw Validation("correction", exception.Message);
        }
        var rules = source.PayrollRules;
        var amount = decimal.Round(recalculated.Amount, rules.CurrencyPrecision,
            rules.RoundingMode == PayrollRoundingMode.MidpointToEven ? MidpointRounding.ToEven : MidpointRounding.AwayFromZero);
        var now = clock.UtcNow;
        var entity = new PayrollAdjustmentRecord
        {
            Id = Guid.NewGuid(), TenantId = RequireTenant(), CompanyId = RequireCompany(), PayrollRunId = run.Id,
            EmployeeId = line.EmployeeId, OriginalPeriodStart = run.PeriodStart, OriginalPeriodEnd = run.PeriodEnd,
            EmployeeNumber = line.EmployeeNumber, EmployeeName = line.EmployeeName, Category = request.Category,
            CalculationMode = PayrollAdjustmentCalculationMode.Recalculated,
            Status = PayrollAdjustmentStatus.Draft, Reason = reason, EffectiveDate = request.EffectiveDate,
            CurrencyCode = run.CurrencyCode, Amount = amount, EvidenceReference = evidence,
            SnapshotJson = "{}", SnapshotRevision = string.Empty, IdempotencyKey = key, CreatedByUserId = creatorId,
            CreatedAtUtc = now, UpdatedAtUtc = now, ConcurrencyStamp = NewStamp()
        };
        var adjustmentSnapshot = new PayrollAdjustmentSnapshot("1", entity.Id, run.Id, line.EmployeeId,
            run.PeriodStart, run.PeriodEnd, revision, run.Status, request.Category,
            PayrollAdjustmentJournalBuilder.RequiredComponentMapping(request.Category), request.EffectiveDate,
            run.CurrencyCode, line.GrossPay, line.TaxWithheld, line.OtherDeductions, line.NetPay, amount, reason,
            evidence, rules.CurrencyPrecision, rules.RoundingMode, PayrollAdjustmentCalculationMode.Recalculated,
            correction, employeeSnapshot.Calculation, recalculated.CorrectedCalculation, recalculated.CorrectedSegments);
        entity.SnapshotJson = JsonSerializer.Serialize(adjustmentSnapshot);
        entity.SnapshotRevision = PayrollAdjustmentSnapshotIntegrity.ComputeRevision(entity.SnapshotJson);
        database.PayrollAdjustments.Add(entity);
        AddAdjustmentEvent(entity, PayrollAdjustmentStatus.Draft, reason);
        await SaveAsync(cancellationToken);
        await AuditAsync("hcm.payroll.adjustment.recalculated", nameof(PayrollAdjustmentRecord), entity.Id,
            null, ToPayrollAdjustmentDto(entity), cancellationToken);
        return ToPayrollAdjustmentDto(entity);
    }

    public async Task<IReadOnlyList<PayrollAdjustmentDto>> ListPayrollAdjustmentsAsync(Guid? payrollRunId, Guid? employeeId,
        CancellationToken cancellationToken) => (await database.PayrollAdjustments.AsNoTracking()
            .Where(x => (!payrollRunId.HasValue || x.PayrollRunId == payrollRunId) &&
                (!employeeId.HasValue || x.EmployeeId == employeeId)).OrderByDescending(x => x.CreatedAtUtc)
            .Take(500).ToListAsync(cancellationToken)).Select(ToPayrollAdjustmentDto).ToArray();

    public async Task<PayrollAdjustmentDto?> GetPayrollAdjustmentAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await database.PayrollAdjustments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return entity is null ? null : ToPayrollAdjustmentDto(entity);
    }

    public async Task<PayrollAdjustmentDto> SubmitPayrollAdjustmentAsync(Guid id, PayrollAdjustmentReviewRequest request,
        CancellationToken cancellationToken)
    {
        var entity = await database.PayrollAdjustments.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Payroll adjustment not found.");
        if (!PayrollAdjustmentLifecyclePolicy.CanTransition(entity.Status, PayrollAdjustmentStatus.Submitted))
            throw new HcmConflictException("Only draft or rejected adjustments can be submitted.");
        VerifyAdjustmentSnapshot(entity);
        await EnsureAdjustmentSourceUnchangedAsync(entity, cancellationToken);
        entity.Status = PayrollAdjustmentStatus.Submitted;
        entity.UpdatedAtUtc = clock.UtcNow;
        entity.ConcurrencyStamp = NewStamp();
        AddAdjustmentEvent(entity, entity.Status, Required(request.Reason, "reason", 1000));
        await SaveAsync(cancellationToken);
        await AuditAsync("hcm.payroll.adjustment.submitted", nameof(PayrollAdjustmentRecord), entity.Id, null,
            ToPayrollAdjustmentDto(entity), cancellationToken);
        return ToPayrollAdjustmentDto(entity);
    }

    public async Task<PayrollAdjustmentDto> ReviewPayrollAdjustmentAsync(Guid id, bool approve,
        PayrollAdjustmentReviewRequest request, CancellationToken cancellationToken)
    {
        var entity = await database.PayrollAdjustments.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Payroll adjustment not found.");
        if (!PayrollAdjustmentLifecyclePolicy.CanTransition(entity.Status,
                approve ? PayrollAdjustmentStatus.Approved : PayrollAdjustmentStatus.Rejected))
            throw new HcmConflictException("Only submitted adjustments can be approved or rejected.");
        if (request.ConcurrencyStamp != entity.ConcurrencyStamp) throw new HcmConcurrencyException();
        var actor = CurrentUserId();
        if (approve && !PayrollAdjustmentLifecyclePolicy.CanApprove(actor, entity.CreatedByUserId))
            throw new HcmConflictException("The adjustment creator cannot approve their own correction.");
        VerifyAdjustmentSnapshot(entity);
        await EnsureAdjustmentSourceUnchangedAsync(entity, cancellationToken);
        var reason = Required(request.Reason, "reason", 1000);
        entity.Status = approve ? PayrollAdjustmentStatus.Approved : PayrollAdjustmentStatus.Rejected;
        entity.ApprovedByUserId = approve ? actor : null;
        entity.UpdatedAtUtc = clock.UtcNow;
        entity.ConcurrencyStamp = NewStamp();
        AddAdjustmentEvent(entity, entity.Status, reason);
        await SaveAsync(cancellationToken);
        await AuditAsync($"hcm.payroll.adjustment.{entity.Status.ToString().ToLowerInvariant()}",
            nameof(PayrollAdjustmentRecord), entity.Id, null, ToPayrollAdjustmentDto(entity), cancellationToken);
        return ToPayrollAdjustmentDto(entity);
    }

    public async Task<PayrollPostingStatusDto> PostPayrollAdjustmentAsync(Guid id,
        PayrollAdjustmentPostingRequest request, CancellationToken cancellationToken)
    {
        var entity = await database.PayrollAdjustments.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Payroll adjustment not found.");
        if (entity.Status == PayrollAdjustmentStatus.Posted)
            return new(entity.AccountingJournalEntryId, entity.AccountingJournalNumber, entity.PostingError,
                entity.PostedAtUtc, PayrollPostingStatus.Posted);
        if (!PayrollAdjustmentLifecyclePolicy.CanTransition(entity.Status, PayrollAdjustmentStatus.Posted))
            throw new HcmConflictException("Only approved adjustments can be posted.");
        var requestedKey = Required(request.IdempotencyKey, "idempotencyKey", 100);
        if (request.ConcurrencyStamp != entity.ConcurrencyStamp && entity.PostingIdempotencyKey != requestedKey)
            throw new HcmConcurrencyException();
        VerifyAdjustmentSnapshot(entity);
        await EnsureAdjustmentSourceUnchangedAsync(entity, cancellationToken);
        var run = await database.PayrollRuns.SingleAsync(x => x.Id == entity.PayrollRunId, cancellationToken);
        if (run.Status is not (PayrollRunStatus.Posted or PayrollRunStatus.Paid) || run.AccountingJournalEntryId is null)
            throw new HcmConflictException("Post the original payroll journal before posting its separate correction journal.");
        if (entity.Category == PayrollAdjustmentCategory.OverpaymentRecovery)
            throw Validation("recovery", "Accounting does not yet provide an employee receivable subledger or recovery settlement allocation. The recovery is retained for review and must not be posted until that traceable settlement workflow exists.");
        var key = requestedKey;
        if (entity.PostingIdempotencyKey is not null && entity.PostingIdempotencyKey != key)
            throw new HcmConflictException("A retry must use the original adjustment posting idempotency key.");
        if (request.BusinessDate == default || (entity.PostingBusinessDate is not null && entity.PostingBusinessDate != request.BusinessDate))
            throw new HcmConflictException("A posting retry must use the original nondefault Accounting business date.");
        var originalJournal = await payrollJournalPoster.GetPayrollJournalAsync(run.AccountingJournalEntryId.Value, cancellationToken);
        if (originalJournal is null || originalJournal.State != "Posted" || originalJournal.CurrencyCode != entity.CurrencyCode)
            throw new HcmConflictException("The original payroll journal is missing, not posted, or has a different currency.");
        var adjustmentSnapshot = JsonSerializer.Deserialize<PayrollAdjustmentSnapshot>(entity.SnapshotJson)
            ?? throw new HcmConflictException("The adjustment snapshot cannot be read.");
        if (adjustmentSnapshot.OriginalCalculationRevision != run.CalculationRevision ||
            run.CalculationRevision != CalculationRevision(run))
            throw new HcmConflictException("The original payroll calculation no longer matches the adjustment source snapshot.");
        var mappings = await database.PayrollAccountMappings.AsNoTracking()
            .Where(x => x.EffectiveFrom <= request.BusinessDate && (x.EffectiveTo == null || x.EffectiveTo >= request.BusinessDate) &&
                (x.CurrencyCode == null || x.CurrencyCode == entity.CurrencyCode))
            .OrderByDescending(x => x.CurrencyCode != null).ToListAsync(cancellationToken);
        var mappingByType = mappings.GroupBy(x => x.Type).ToDictionary(x => x.Key, x => x.First());
        IReadOnlyList<PayrollJournalLineRequest> financialLines;
        if (entity.CalculationMode == PayrollAdjustmentCalculationMode.Manual)
        {
            var componentType = PayrollAdjustmentJournalBuilder.RequiredComponentMapping(entity.Category);
            var componentMapping = mappingByType.GetValueOrDefault(componentType)
                ?? throw Validation("accountMappings", $"Configure an effective {componentType} account mapping for {entity.CurrencyCode}.");
            var offsetType = PayrollAccountMappingType.NetSalaryPayable;
            var offsetMapping = mappingByType.GetValueOrDefault(offsetType)
                ?? throw Validation("accountMappings", $"Configure an effective {offsetType} mapping for {entity.CurrencyCode}.");
            financialLines = PayrollAdjustmentJournalBuilder.Build(entity.Category, entity.Amount,
                adjustmentSnapshot.CurrencyPrecision, componentMapping.AccountId, offsetMapping.AccountId).Lines;
            if (run.Status == PayrollRunStatus.Paid &&
                financialLines.Any(x => x.AccountId == offsetMapping.AccountId && x.Debit > 0))
                throw Validation("amount", "This correction would reduce net pay after the original payroll was marked paid. An authorized recovery workflow is required.");
        }
        else
        {
            if (adjustmentSnapshot.OriginalCalculation is null || adjustmentSnapshot.CorrectedCalculation is null)
                throw new HcmConflictException("The recalculated adjustment snapshot lacks both calculation results.");
            financialLines = PayrollAdjustmentJournalBuilder.BuildCalculationDelta(adjustmentSnapshot.OriginalCalculation,
                adjustmentSnapshot.CorrectedCalculation, adjustmentSnapshot.CurrencyPrecision,
                mappingByType.ToDictionary(x => x.Key, x => x.Value.AccountId));
            var netPayableAccount = mappingByType.GetValueOrDefault(PayrollAccountMappingType.NetSalaryPayable)?.AccountId;
            if (run.Status == PayrollRunStatus.Paid && netPayableAccount is { } payable &&
                financialLines.Any(x => x.AccountId == payable && x.Debit > 0))
                throw Validation("amount", "This recalculated correction would reduce net pay after the original payroll was marked paid. An authorized recovery workflow is required.");
        }
        if (financialLines.Count != 2 || financialLines.Sum(x => x.Debit) <= 0 ||
            decimal.Round(financialLines.Sum(x => x.Debit), 4) != decimal.Round(financialLines.Sum(x => x.Credit), 4))
            throw new HcmConflictException("Adjustment journal lines are invalid or unbalanced; Accounting was not called.");

        foreach (var accountId in financialLines.Select(x => x.AccountId).Distinct())
        {
            var mappingTypes = mappingByType.Where(x => x.Value.AccountId == accountId).Select(x => x.Key).ToArray();
            if (mappingTypes.Length == 0)
                throw Validation("accountMappings", "An adjustment journal line no longer has a matching account mapping.");
            foreach (var mappingType in mappingTypes)
                if (!await accountMappingValidator.IsEligibleAsync(accountId, mappingType.ToString(), cancellationToken))
                    throw Validation("accountMappings", $"The {mappingType} account mapping is no longer eligible for payroll posting.");
        }

        if (entity.PostingIdempotencyKey is null)
        {
            entity.PostingIdempotencyKey = key;
            entity.PostingBusinessDate = request.BusinessDate;
            entity.UpdatedAtUtc = clock.UtcNow;
            entity.ConcurrencyStamp = NewStamp();
            await SaveAsync(cancellationToken);
        }

        // Accounting is an external side effect relative to this HCM unit of work. Its source/revision key
        // makes a retry after HCM persistence failure return the same journal instead of duplicating it.
        PayrollJournalResult posted;
        try
        {
            posted = await payrollJournalPoster.PostPayrollAsync(new PayrollJournalRequest(entity.Id,
                $"{run.Number}-ADJ-{entity.Id:N}", entity.SnapshotRevision, request.BusinessDate,
                entity.CurrencyCode, key, financialLines), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            entity.PostingError = exception.Message.Length <= 1000 ? exception.Message : exception.Message[..1000];
            entity.UpdatedAtUtc = clock.UtcNow;
            entity.ConcurrencyStamp = NewStamp();
            await SaveAsync(cancellationToken);
            throw;
        }
        entity.AccountingJournalEntryId = posted.JournalEntryId;
        entity.AccountingJournalNumber = posted.Number;
        entity.Status = PayrollAdjustmentStatus.Posted;
        entity.PostingError = null;
        entity.PostedAtUtc = clock.UtcNow;
        entity.UpdatedAtUtc = clock.UtcNow;
        entity.ConcurrencyStamp = NewStamp();
        AddAdjustmentEvent(entity, entity.Status, "Accounting journal posted.");
        await SaveAsync(cancellationToken);
        await AuditAsync("hcm.payroll.adjustment.posted", nameof(PayrollAdjustmentRecord), entity.Id, null,
            ToPayrollAdjustmentDto(entity), cancellationToken);
        return new(entity.AccountingJournalEntryId, entity.AccountingJournalNumber, null, entity.PostedAtUtc,
            PayrollPostingStatus.Posted);
    }

    public async Task<IReadOnlyList<PayrollAdjustmentEventDto>> GetPayrollAdjustmentHistoryAsync(Guid id,
        CancellationToken cancellationToken)
    {
        if (!await database.PayrollAdjustments.AnyAsync(x => x.Id == id, cancellationToken))
            throw new KeyNotFoundException("Payroll adjustment not found.");
        return await database.PayrollAdjustmentEvents.AsNoTracking().Where(x => x.PayrollAdjustmentId == id)
            .OrderBy(x => x.OccurredAtUtc).Select(x => new PayrollAdjustmentEventDto(x.Id, x.Status,
                x.ActorUserId, x.Reason, x.OccurredAtUtc, x.SnapshotRevision)).ToArrayAsync(cancellationToken);
    }

    public async Task<PayrollAdjustmentReversalDto> ReversePayrollAdjustmentAsync(Guid id,
        PayrollAdjustmentReversalRequest request, CancellationToken cancellationToken)
    {
        var entity = await database.PayrollAdjustments.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Payroll adjustment not found.");
        var reason = Required(request.Reason, "reason", 500);
        var key = Required(request.IdempotencyKey, "idempotencyKey", 100);
        if (entity.Status == PayrollAdjustmentStatus.Reversed)
        {
            if (entity.ReversalIdempotencyKey != key || entity.ReversalReason != reason || entity.ReversalBusinessDate != request.BusinessDate)
                throw new HcmConflictException("A reversal retry must exactly match the original reason, date, and idempotency key.");
            return new(entity.Id, entity.AccountingJournalEntryId!.Value, entity.ReversalJournalEntryId!.Value,
                entity.ReversalJournalNumber!, reason, request.BusinessDate, key);
        }
        if (!PayrollAdjustmentLifecyclePolicy.CanTransition(entity.Status, PayrollAdjustmentStatus.Reversed) ||
            entity.AccountingJournalEntryId is not { } originalId)
            throw new HcmConflictException("Only a posted payroll adjustment can be reversed.");
        if (request.ConcurrencyStamp != entity.ConcurrencyStamp && entity.ReversalIdempotencyKey != key)
            throw new HcmConcurrencyException();
        if (entity.ReversalIdempotencyKey is not null && (entity.ReversalIdempotencyKey != key ||
            entity.ReversalReason != reason || entity.ReversalBusinessDate != request.BusinessDate))
            throw new HcmConflictException("A reversal retry must exactly match the original reason, date, and idempotency key.");
        var journal = await payrollJournalPoster.GetPayrollJournalAsync(originalId, cancellationToken);
        if (journal is null || journal.State != "Posted" || journal.CurrencyCode != entity.CurrencyCode)
            throw new HcmConflictException("The adjustment journal is missing, not posted, or has a different currency.");
        if (entity.ReversalIdempotencyKey is null)
        {
            entity.ReversalIdempotencyKey = key;
            entity.ReversalReason = reason;
            entity.ReversalBusinessDate = request.BusinessDate;
            entity.UpdatedAtUtc = clock.UtcNow;
            entity.ConcurrencyStamp = NewStamp();
            await SaveAsync(cancellationToken);
        }
        var reversal = await payrollJournalPoster.ReversePayrollAsync(originalId, request.BusinessDate, reason, key, cancellationToken);
        entity.ReversalJournalEntryId = reversal.JournalEntryId;
        entity.ReversalJournalNumber = reversal.Number;
        entity.Status = PayrollAdjustmentStatus.Reversed;
        entity.UpdatedAtUtc = clock.UtcNow;
        entity.ConcurrencyStamp = NewStamp();
        AddAdjustmentEvent(entity, entity.Status, reason);
        await SaveAsync(cancellationToken);
        await AuditAsync("hcm.payroll.adjustment.reversed", nameof(PayrollAdjustmentRecord), entity.Id, null,
            ToPayrollAdjustmentDto(entity), cancellationToken);
        return new(entity.Id, originalId, reversal.JournalEntryId, reversal.Number, reason, request.BusinessDate, key);
    }

    private static void VerifyAdjustmentSnapshot(PayrollAdjustmentRecord entity)
    {
        if (!PayrollAdjustmentSnapshotIntegrity.Matches(entity.SnapshotJson, entity.SnapshotRevision))
            throw new HcmConflictException("The immutable payroll adjustment snapshot failed integrity validation.");
        var snapshot = JsonSerializer.Deserialize<PayrollAdjustmentSnapshot>(entity.SnapshotJson);
        if (snapshot is null || snapshot.AdjustmentId != entity.Id || snapshot.OriginalPayrollRunId != entity.PayrollRunId ||
            snapshot.EmployeeId != entity.EmployeeId || snapshot.Category != entity.Category ||
            snapshot.CalculationMode != entity.CalculationMode ||
            snapshot.FinancialMapping != PayrollAdjustmentJournalBuilder.RequiredComponentMapping(entity.Category) ||
            snapshot.EffectiveDate != entity.EffectiveDate || snapshot.CurrencyCode != entity.CurrencyCode ||
            snapshot.AdjustmentAmount != entity.Amount || snapshot.Reason != entity.Reason ||
            snapshot.EvidenceReference != entity.EvidenceReference ||
            snapshot.OriginalPeriodStart > entity.EffectiveDate || snapshot.OriginalPeriodEnd < entity.EffectiveDate)
            throw new HcmConflictException("Adjustment fields do not match the immutable calculation snapshot.");
    }

    private async Task EnsureAdjustmentSourceUnchangedAsync(PayrollAdjustmentRecord entity, CancellationToken cancellationToken)
    {
        var source = JsonSerializer.Deserialize<PayrollAdjustmentSnapshot>(entity.SnapshotJson)
            ?? throw new HcmConflictException("The adjustment source snapshot cannot be read.");
        var run = await database.PayrollRuns.SingleOrDefaultAsync(x => x.Id == entity.PayrollRunId, cancellationToken)
            ?? throw new HcmConflictException("The original payroll run is no longer available.");
        if (run.TenantId != entity.TenantId || run.CompanyId != entity.CompanyId ||
            source.OriginalCalculationRevision != run.CalculationRevision || run.CalculationRevision != CalculationRevision(run))
            throw new HcmConflictException("The original payroll calculation no longer matches the immutable adjustment source revision.");
        if (entity.CalculationMode == PayrollAdjustmentCalculationMode.Recalculated)
        {
            var original = PayrollSnapshotJson.Deserialize(run.CalculationSnapshotJson)
                ?? throw new HcmConflictException("The original payroll calculation snapshot cannot be replayed.");
            if (source.CorrectionInput is null || source.CorrectedCalculation is null || source.CorrectedSegments is null)
                throw new HcmConflictException("The recalculated adjustment snapshot is missing corrected inputs or results.");
            var replay = PayrollRecalculationEngine.Recalculate(original, entity.EmployeeId, entity.Category,
                entity.EffectiveDate, source.CorrectionInput);
            if (replay.Amount != entity.Amount ||
                JsonSerializer.Serialize(replay.CorrectedCalculation) != JsonSerializer.Serialize(source.CorrectedCalculation) ||
                JsonSerializer.Serialize(replay.CorrectedSegments) != JsonSerializer.Serialize(source.CorrectedSegments))
                throw new HcmConflictException("The corrected result cannot be reproduced from the original snapshot and stored inputs.");
        }
    }

    private void AddAdjustmentEvent(PayrollAdjustmentRecord entity, PayrollAdjustmentStatus status, string reason) =>
        database.PayrollAdjustmentEvents.Add(new PayrollAdjustmentEvent
        {
            Id = Guid.NewGuid(), TenantId = entity.TenantId, CompanyId = entity.CompanyId,
            PayrollAdjustmentId = entity.Id, Status = status, ActorUserId = CurrentUserId(), Reason = reason,
            SnapshotRevision = entity.SnapshotRevision, OccurredAtUtc = clock.UtcNow
        });

    private static PayrollAdjustmentDto ToPayrollAdjustmentDto(PayrollAdjustmentRecord x) => new(x.Id,
        x.PayrollRunId, x.EmployeeId, x.EmployeeNumber, x.EmployeeName, x.Category, x.Status, x.Reason,
        x.EffectiveDate, x.CurrencyCode, x.Amount, x.EvidenceReference, x.SnapshotRevision,
        x.AccountingJournalEntryId, x.AccountingJournalNumber, x.CreatedByUserId, x.CreatedAtUtc, x.ConcurrencyStamp,
        x.PostingError, x.CalculationMode);

    public async Task<IReadOnlyList<PayrollAccountMappingDto>> ListPayrollAccountMappingsAsync(CancellationToken cancellationToken) =>
        (await database.PayrollAccountMappings.AsNoTracking().OrderBy(x => x.Type).ThenBy(x => x.EffectiveFrom)
            .ToListAsync(cancellationToken)).Select(ToPayrollAccountMappingDto).ToArray();

    public async Task<PayrollAccountMappingDto> SavePayrollAccountMappingAsync(PayrollAccountMappingRequest request,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.Type) || request.AccountId == Guid.Empty)
            throw Validation("mapping", "Choose a supported payroll mapping type and account.");
        ValidateDate(request.EffectiveFrom, "effectiveFrom");
        ValidateDateRange(request.EffectiveFrom, request.EffectiveTo, "effectiveTo");
        var currency = string.IsNullOrWhiteSpace(request.CurrencyCode) ? null : request.CurrencyCode.Trim().ToUpperInvariant();
        if (currency is not null && currency.Length != 3) throw Validation("currencyCode", "Currency codes must contain three letters.");
        if (!await accountMappingValidator.IsEligibleAsync(request.AccountId, request.Type.ToString(), cancellationToken))
            throw Validation("accountId", "The account does not exist in this company, is inactive, or has the wrong account type.");
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var lockKey = $"{RequireTenant():N}:{RequireCompany():N}:payroll-mapping:{request.Type}:{currency ?? "*"}";
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);
        var overlap = await database.PayrollAccountMappings.AnyAsync(x => x.Type == request.Type && x.CurrencyCode == currency &&
            x.EffectiveFrom <= (request.EffectiveTo ?? DateOnly.MaxValue) && (x.EffectiveTo == null || x.EffectiveTo >= request.EffectiveFrom), cancellationToken);
        if (overlap) throw new HcmConflictException("An effective mapping already exists for this type and currency over the requested dates.");
        var userId = CurrentUserId();
        if (userId is null) throw new HcmConflictException("An authenticated user is required to configure payroll accounts.");
        var entity = new PayrollAccountMapping
        {
            Id = Guid.NewGuid(),
            TenantId = RequireTenant(),
            CompanyId = RequireCompany(),
            Type = request.Type,
            AccountId = request.AccountId,
            CurrencyCode = currency,
            EffectiveFrom = request.EffectiveFrom,
            EffectiveTo = request.EffectiveTo,
            CreatedByUserId = userId.Value,
            CreatedAtUtc = clock.UtcNow
        };
        database.PayrollAccountMappings.Add(entity);
        await SaveAsync(cancellationToken);
        var dto = ToPayrollAccountMappingDto(entity);
        await AuditAsync("hcm.payroll.account_mapping.created", nameof(PayrollAccountMapping), entity.Id, null, dto, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return dto;
    }

    public async Task<EmployeeReference?> FindAsync(Guid employeeId, CancellationToken cancellationToken)
    {
        var value = await database.Employees.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == employeeId, cancellationToken);
        return value is null ? null : ToReference(value);
    }

    public async Task<IReadOnlyDictionary<Guid, EmployeeReference>> FindAsync(
        IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken)
    {
        if (employeeIds.Count == 0)
            return new Dictionary<Guid, EmployeeReference>();
        return await database.Employees.AsNoTracking().Where(x => employeeIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ToReference, cancellationToken);
    }

    private async Task<Employee> FindEmployeeAsync(Guid id, CancellationToken token) =>
        await database.Employees.SingleOrDefaultAsync(x => x.Id == id, token)
        ?? throw new KeyNotFoundException("Employee not found.");

    private async Task SaveAsync(CancellationToken token)
    {
        try { await database.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { throw new HcmConcurrencyException(); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation })
        { throw new HcmConflictException("A conflicting HCM record already exists in this company."); }
    }

    private async Task LockEmployeeLeaveAsync(Guid employeeId, CancellationToken token)
    {
        var key = $"{RequireTenant():N}:{RequireCompany():N}:{employeeId:N}";
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", token);
    }

    private static Guid NewStamp() => Guid.NewGuid();

    private async Task AuditAsync(string action, string type, Guid id, object? before, object after, CancellationToken token)
    {
        audit.Append(new(action, type, id.ToString(), null,
            before is null ? null : JsonSerializer.Serialize(before), JsonSerializer.Serialize(after)));
        await audit.FlushAsync(token);
    }

    private Guid RequireTenant() => tenantContext.TenantId?.Value
        ?? throw new InvalidOperationException("An active tenant is required.");
    private Guid RequireCompany() => companyContext.CompanyId?.Value
        ?? throw new InvalidOperationException("An active company is required.");

    private static string Required(string? value, string field, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > maxLength)
            throw Validation(field, $"A value between 1 and {maxLength} characters is required.");
        return normalized;
    }

    private static string? NormalizeEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var email = value.Trim();
        if (email.Length > 254 || !System.Net.Mail.MailAddress.TryCreate(email, out var address) ||
            !string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase))
            throw Validation("email", "Enter a valid email address no longer than 254 characters.");
        return email;
    }

    private static string? Optional(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Length > maxLength)
            throw Validation(field, $"This value must be no longer than {maxLength} characters.");
        return normalized;
    }

    private static (string Title, string? Provider, string? CertificateReference) ValidateTraining(
        string? title, string? provider, DateOnly completedOn, DateOnly? expiresOn, string? certificateReference)
    {
        ValidateDate(completedOn, "completedOn");
        ValidateDateRange(completedOn, expiresOn, "expiresOn");
        return (Required(title, "title", 200), Optional(provider, "provider", 200),
            Optional(certificateReference, "certificateReference", 200));
    }

    private static (string ReviewerName, string Summary, string? Strengths, string? DevelopmentGoals) ValidateReview(
        string? reviewerName, DateOnly periodStart, DateOnly periodEnd, string? summary,
        string? strengths, string? developmentGoals)
    {
        ValidateDate(periodStart, "periodStart");
        ValidateDate(periodEnd, "periodEnd");
        if (periodStart > periodEnd)
            throw Validation("periodStart", "The review period must start on or before its end date.");
        return (Required(reviewerName, "reviewerName", 200), Required(summary, "summary", 4000),
            Optional(strengths, "strengths", 4000), Optional(developmentGoals, "developmentGoals", 4000));
    }

    private static void ValidateDate(DateOnly date, string field)
    {
        if (date == default) throw Validation(field, "A date is required.");
    }

    private static void ValidateDateRange(DateOnly startDate, DateOnly? endDate, string field)
    {
        if (endDate.HasValue && endDate.Value < startDate)
            throw Validation(field, "The end date must be on or after the start date.");
    }

    private static PayrollComponentInput[] ValidatePayrollComponents(
        IReadOnlyList<PayrollComponentInput>? values, string field)
    {
        var components = values ?? [];
        if (components.Count > 100 || components.Any(x => string.IsNullOrWhiteSpace(x.Code) ||
                x.Code.Trim().Length > 50 || x.Amount < 0 || decimal.Round(x.Amount, 2) != x.Amount) ||
            components.Select(x => x.Code.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != components.Count)
            throw Validation(field, "Components require unique names up to 50 characters and nonnegative two-decimal amounts.");
        return components.Select(x => new PayrollComponentInput(x.Code.Trim(), x.Amount)).ToArray();
    }

    private static void ValidateHireDate(DateOnly date)
    {
        if (date == default) throw Validation("hireDate", "A hire date is required.");
    }

    private static HcmValidationException Validation(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });

    private static EmployeeDto ToDto(Employee x) =>
        new(x.Id, x.EmployeeNumber, x.FullName, x.Email, x.HireDate, x.Status, x.CreatedAtUtc, x.UpdatedAtUtc,
            x.EmploymentEndDate);
    private static EmployeeReference ToReference(Employee x) =>
        new(x.Id, x.EmployeeNumber, x.FullName, x.Email, x.Status);
    private static LeaveRequestDto ToDto(LeaveRequestRecord x) =>
        new(x.Id, x.EmployeeId, x.EmployeeName, x.StartDate, x.EndDate, x.Reason, x.Status, x.SubmittedAtUtc, x.DecidedAtUtc);
    private static AttendanceRecordDto ToDto(AttendanceRecord x) =>
        new(x.Id, x.EmployeeId, x.EmployeeName, x.WorkDate, x.CheckedInAtUtc, x.CheckedOutAtUtc,
            x.ApprovalStatus, x.Disposition, x.ApprovedOvertimeHours,
            JsonSerializer.Deserialize<AttendanceBreakInput[]>(x.BreaksJson ?? "[]") ?? []);
    private static EmployeeBenefitDto ToDto(EmployeeBenefitRecord x) =>
        new(x.Id, x.EmployeeId, x.EmployeeName, x.BenefitName, x.Provider, x.EnrollmentDate, x.EndDate, x.Status);
    private static TrainingRecordDto ToDto(TrainingRecord x) =>
        new(x.Id, x.EmployeeId, x.EmployeeName, x.Title, x.Provider, x.CompletedOn, x.ExpiresOn, x.CertificateReference);
    private static PerformanceReviewDto ToDto(PerformanceReview x) =>
        new(x.Id, x.EmployeeId, x.EmployeeName, x.ReviewerName, x.PeriodStart, x.PeriodEnd,
            x.Summary, x.Strengths, x.DevelopmentGoals, x.CreatedAtUtc, x.UpdatedAtUtc);
    private static PayrollRunDto ToDto(PayrollRun x) => new(x.Id, x.Number, x.PeriodStart, x.PeriodEnd,
        x.PayDate, x.CurrencyCode, x.Status, x.CreatedAtUtc, x.UpdatedAtUtc, x.ConcurrencyStamp,
        x.Lines.OrderBy(line => line.EmployeeName).Select(line => new PayrollLineDto(line.EmployeeId,
            line.EmployeeNumber, line.EmployeeName, line.GrossPay, line.TaxWithheld, line.OtherDeductions,
            line.EmployerContributions, line.NetPay, line.AttendanceHours,
            JsonSerializer.Deserialize<PayrollCalculationDetailDto[]>(line.CalculationDetailsJson ?? "[]") ?? []))
            .ToArray(), x.PayrollPeriodId, x.CalculationRuleVersion, x.AccountingJournalEntryId,
        x.AccountingJournalNumber, x.PostingError, x.PostedAtUtc);
    private static PayrollPeriodDto ToPayrollPeriodDto(PayrollPeriod x) => new(
        x.Id, x.Number, x.Type, x.StartDate, x.EndDate, x.PayDate, x.JurisdictionCode, x.Status,
        JsonSerializer.Deserialize<DateOnly[]>(x.ExpectedWorkDatesJson) ?? [], x.CreatedAtUtc);
    private static PayrollAccountMappingDto ToPayrollAccountMappingDto(PayrollAccountMapping x) =>
        new(x.Id, x.Type, x.AccountId, x.CurrencyCode, x.EffectiveFrom, x.EffectiveTo, x.CreatedAtUtc);
    private static PayrollRuleConfigurationDto ToPayrollRuleDto(PayrollRuleConfiguration x) => new(
        x.Id, x.JurisdictionCode, x.Version, x.IsSupported, x.EffectiveFrom, x.EffectiveTo,
        x.StandardHoursPerPeriod, x.OvertimeMultiplier,
        JsonSerializer.Deserialize<PayrollTaxBandInput[]>(x.TaxBandsJson) ?? [], x.PeriodType,
        JsonSerializer.Deserialize<PayrollEmployerContributionInput[]>(x.EmployerContributionsJson) ?? [],
        x.ProrationMethod, x.HireDateTreatment, x.TerminationDateTreatment, x.UnpaidLeaveTreatment,
        x.CurrencyPrecision, x.RoundingMode);
    private static EmployeeCompensationDto ToCompensationDto(EmployeeCompensation x) => new(
        x.Id, x.EmployeeId, x.CurrencyCode, x.EffectiveFrom, x.EffectiveTo, x.BasePayForPeriod,
        JsonSerializer.Deserialize<PayrollComponentInput[]>(x.AllowancesJson) ?? [],
        JsonSerializer.Deserialize<PayrollComponentInput[]>(x.DeductionsJson) ?? [], x.PeriodType,
        x.ScheduledHoursPerPeriod);
}
