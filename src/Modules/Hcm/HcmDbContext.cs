using System.Data.Common;
using Erp.Abstractions.Context;
using Erp.Abstractions.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage;

namespace Erp.Modules.Hcm;

public sealed class HcmDbContext(
    DbContextOptions<HcmDbContext> options,
    ITenantContext? tenantContext = null,
    ICompanyContext? companyContext = null) : DbContext(options), ITransactionParticipant
{
    private readonly ITenantContext? _tenantContext = tenantContext;
    private readonly ICompanyContext? _companyContext = companyContext;
    private Guid TenantId => _tenantContext?.TenantId?.Value ?? Guid.Empty;
    private Guid CompanyId => _companyContext?.CompanyId?.Value ?? Guid.Empty;

    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<LeaveRequestRecord> LeaveRequests => Set<LeaveRequestRecord>();
    public DbSet<AttendanceRecord> AttendanceRecords => Set<AttendanceRecord>();
    public DbSet<EmployeeBenefitRecord> EmployeeBenefits => Set<EmployeeBenefitRecord>();
    public DbSet<TrainingRecord> TrainingRecords => Set<TrainingRecord>();
    public DbSet<PerformanceReview> PerformanceReviews => Set<PerformanceReview>();
    public DbSet<PayrollRun> PayrollRuns => Set<PayrollRun>();
    public DbSet<PayrollApprovalEvent> PayrollApprovalEvents => Set<PayrollApprovalEvent>();
    public DbSet<PayrollAdjustmentRecord> PayrollAdjustments => Set<PayrollAdjustmentRecord>();
    public DbSet<PayrollAdjustmentEvent> PayrollAdjustmentEvents => Set<PayrollAdjustmentEvent>();
    public DbSet<PayrollAccountMapping> PayrollAccountMappings => Set<PayrollAccountMapping>();
    public DbSet<PayrollLine> PayrollLines => Set<PayrollLine>();
    public DbSet<PayrollPeriod> PayrollPeriods => Set<PayrollPeriod>();
    public DbSet<PayrollRuleConfiguration> PayrollRuleConfigurations => Set<PayrollRuleConfiguration>();
    public DbSet<EmployeeCompensation> EmployeeCompensations => Set<EmployeeCompensation>();

    DbConnection ITransactionParticipant.Connection => Database.GetDbConnection();
    bool ITransactionParticipant.HasPendingChanges => ChangeTracker.HasChanges();

    async Task ITransactionParticipant.EnlistAsync(DbTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null && Database.CurrentTransaction is { } current &&
            !ReferenceEquals(current.GetDbTransaction(), transaction))
            throw new InvalidOperationException("An unrelated HCM transaction is already active.");
        _ = await Database.UseTransactionAsync(transaction, cancellationToken);
    }

    void ITransactionParticipant.DiscardChanges() => ChangeTracker.Clear();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("hcm");
        modelBuilder.Entity<Employee>(entity =>
        {
            entity.ToTable("employees", table => table.HasCheckConstraint(
                "CK_hcm_employees_employment_dates", "\"EmploymentEndDate\" IS NULL OR \"EmploymentEndDate\" >= \"HireDate\""));
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.Id });
            entity.Property(x => x.EmployeeNumber).HasMaxLength(50);
            entity.Property(x => x.FullName).HasMaxLength(200);
            entity.Property(x => x.Email).HasMaxLength(254);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.EmploymentEndDate });
            entity.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.EmployeeNumber }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.FullName });
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
        });
        modelBuilder.Entity<LeaveRequestRecord>(entity =>
        {
            entity.ToTable("leave_requests", table => table.HasCheckConstraint(
                "CK_hcm_leave_requests_DateRange", "\"StartDate\" <= \"EndDate\""));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EmployeeName).HasMaxLength(200);
            entity.Property(x => x.Reason).HasMaxLength(2000);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.EmployeeId, x.StartDate, x.EndDate });
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Status, x.SubmittedAtUtc });
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.EmployeeId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<AttendanceRecord>(entity =>
        {
            entity.ToTable("attendance_records", table =>
            {
                table.HasCheckConstraint("CK_hcm_attendance_checkout", "\"CheckedInAtUtc\" IS NULL OR \"CheckedOutAtUtc\" IS NULL OR \"CheckedOutAtUtc\" >= \"CheckedInAtUtc\"");
                table.HasCheckConstraint("CK_hcm_attendance_overtime", "\"ApprovedOvertimeHours\" >= 0");
                table.HasCheckConstraint("CK_hcm_attendance_finalized", "\"ApprovalStatus\" <> 'Finalized' OR (\"Disposition\" IS NOT NULL AND \"FinalizedAtUtc\" IS NOT NULL AND (\"Disposition\" <> 'Worked' OR (\"CheckedInAtUtc\" IS NOT NULL AND \"CheckedOutAtUtc\" IS NOT NULL)))");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EmployeeName).HasMaxLength(200);
            entity.Property(x => x.ApprovalStatus).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.Disposition).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.ApprovedOvertimeHours).HasPrecision(8, 2);
            entity.Property(x => x.BreaksJson).HasColumnType("jsonb");
            entity.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.EmployeeId, x.WorkDate }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.WorkDate });
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.EmployeeId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<EmployeeBenefitRecord>(entity =>
        {
            entity.ToTable("employee_benefits", table => table.HasCheckConstraint(
                "CK_hcm_employee_benefits_end_date", "\"EndDate\" IS NULL OR \"EndDate\" >= \"EnrollmentDate\""));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EmployeeName).HasMaxLength(200);
            entity.Property(x => x.BenefitName).HasMaxLength(200);
            entity.Property(x => x.Provider).HasMaxLength(200);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.EmployeeId, x.EnrollmentDate });
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.EmployeeId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<TrainingRecord>(entity =>
        {
            entity.ToTable("training_records", table => table.HasCheckConstraint(
                "CK_hcm_training_records_expiry", "\"ExpiresOn\" IS NULL OR \"ExpiresOn\" >= \"CompletedOn\""));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EmployeeName).HasMaxLength(200);
            entity.Property(x => x.Title).HasMaxLength(200);
            entity.Property(x => x.Provider).HasMaxLength(200);
            entity.Property(x => x.CertificateReference).HasMaxLength(200);
            entity.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.EmployeeId, x.CompletedOn });
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.EmployeeId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<PerformanceReview>(entity =>
        {
            entity.ToTable("performance_reviews", table => table.HasCheckConstraint(
                "CK_hcm_performance_reviews_period", "\"PeriodStart\" <= \"PeriodEnd\""));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EmployeeName).HasMaxLength(200);
            entity.Property(x => x.ReviewerName).HasMaxLength(200);
            entity.Property(x => x.Summary).HasMaxLength(4000);
            entity.Property(x => x.Strengths).HasMaxLength(4000);
            entity.Property(x => x.DevelopmentGoals).HasMaxLength(4000);
            entity.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.EmployeeId, x.PeriodStart, x.PeriodEnd });
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.EmployeeId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<PayrollRun>(entity =>
        {
            entity.ToTable("payroll_runs", table =>
            {
                table.HasCheckConstraint("CK_hcm_payroll_period", "\"PeriodStart\" <= \"PeriodEnd\"");
                table.HasCheckConstraint("CK_hcm_payroll_pay_date", "\"PayDate\" >= \"PeriodEnd\"");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.Id });
            entity.Property(x => x.Number).HasMaxLength(50);
            entity.Property(x => x.CurrencyCode).HasMaxLength(3).IsFixedLength();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.CalculationRuleVersion).HasMaxLength(50);
            entity.Property(x => x.CalculationRevision).HasMaxLength(64);
            entity.Property(x => x.CalculationSnapshotJson).HasColumnType("jsonb");
            entity.Property(x => x.IdempotencyKey).HasMaxLength(100);
            entity.Property(x => x.AccountingJournalNumber).HasMaxLength(50);
            entity.Property(x => x.ReversalJournalNumber).HasMaxLength(50);
            entity.Property(x => x.ReversalIdempotencyKey).HasMaxLength(100);
            entity.Property(x => x.ReversalReason).HasMaxLength(1000);
            entity.Property(x => x.PostingError).HasMaxLength(1000);
            entity.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Number }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.PayrollPeriodId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.ReversalIdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.ReversalJournalEntryId }).IsUnique()
                .HasFilter("\"ReversalJournalEntryId\" IS NOT NULL");
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.PeriodStart, x.PeriodEnd });
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
            entity.HasOne<PayrollPeriod>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.PayrollPeriodId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<PayrollLine>(entity =>
        {
            entity.ToTable("payroll_lines", table =>
            {
                table.HasCheckConstraint("CK_hcm_payroll_gross", "\"GrossPay\" >= 0");
                table.HasCheckConstraint("CK_hcm_payroll_tax", "\"TaxWithheld\" >= 0");
                table.HasCheckConstraint("CK_hcm_payroll_deductions", "\"OtherDeductions\" >= 0");
                table.HasCheckConstraint("CK_hcm_payroll_employer_contributions", "\"EmployerContributions\" >= 0");
                table.HasCheckConstraint("CK_hcm_payroll_net", "\"NetPay\" >= 0");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EmployeeNumber).HasMaxLength(50);
            entity.Property(x => x.EmployeeName).HasMaxLength(200);
            entity.Property(x => x.GrossPay).HasPrecision(18, 2);
            entity.Property(x => x.TaxWithheld).HasPrecision(18, 2);
            entity.Property(x => x.OtherDeductions).HasPrecision(18, 2);
            entity.Property(x => x.EmployerContributions).HasPrecision(18, 2);
            entity.Property(x => x.NetPay).HasPrecision(18, 2);
            entity.Property(x => x.AttendanceHours).HasPrecision(10, 2);
            entity.Property(x => x.CalculationDetailsJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.PayrollRunId, x.EmployeeId }).IsUnique();
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
            entity.HasOne<PayrollRun>().WithMany(x => x.Lines)
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.PayrollRunId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Employee>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.EmployeeId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<PayrollApprovalEvent>(entity =>
        {
            entity.ToTable("payroll_approval_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.ActorUserId);
            entity.Property(x => x.Comment).HasMaxLength(1000);
            entity.Property(x => x.CalculationRuleVersion).HasMaxLength(50);
            entity.Property(x => x.CalculationRevision).HasMaxLength(64);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.PayrollRunId, x.OccurredAtUtc });
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
            entity.HasOne<PayrollRun>().WithMany(x => x.ApprovalHistory)
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.PayrollRunId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<PayrollAdjustmentRecord>(entity =>
        {
            entity.ToTable("payroll_adjustments", table =>
            {
                table.HasCheckConstraint("CK_hcm_payroll_adjustments_amount", "\"Amount\" <> 0");
                table.HasCheckConstraint("CK_hcm_payroll_adjustments_effective_date", "\"EffectiveDate\" >= \"OriginalPeriodStart\" AND \"EffectiveDate\" <= \"OriginalPeriodEnd\"");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.Id });
            entity.Property(x => x.EmployeeNumber).HasMaxLength(50);
            entity.Property(x => x.EmployeeName).HasMaxLength(200);
            entity.Property(x => x.Category).HasConversion<string>().HasMaxLength(48);
            entity.Property(x => x.CalculationMode).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.Reason).HasMaxLength(1000);
            entity.Property(x => x.CurrencyCode).HasMaxLength(3).IsFixedLength();
            entity.Property(x => x.Amount).HasPrecision(18, 4);
            entity.Property(x => x.EvidenceReference).HasMaxLength(300);
            entity.Property(x => x.SnapshotJson).HasColumnType("jsonb");
            entity.Property(x => x.SnapshotRevision).HasMaxLength(64);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(100);
            entity.Property(x => x.PostingIdempotencyKey).HasMaxLength(100);
            entity.Property(x => x.PostingError).HasMaxLength(1000);
            entity.Property(x => x.AccountingJournalNumber).HasMaxLength(50);
            entity.Property(x => x.ReversalJournalNumber).HasMaxLength(50);
            entity.Property(x => x.ReversalIdempotencyKey).HasMaxLength(100);
            entity.Property(x => x.ReversalReason).HasMaxLength(1000);
            entity.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.PayrollRunId, x.EmployeeId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.AccountingJournalEntryId }).IsUnique()
                .HasFilter("\"AccountingJournalEntryId\" IS NOT NULL");
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.ReversalJournalEntryId }).IsUnique()
                .HasFilter("\"ReversalJournalEntryId\" IS NOT NULL");
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.ReversalIdempotencyKey }).IsUnique();
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
            entity.HasOne<PayrollRun>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.PayrollRunId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.EmployeeId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<PayrollAdjustmentEvent>(entity =>
        {
            entity.ToTable("payroll_adjustment_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.Reason).HasMaxLength(1000);
            entity.Property(x => x.SnapshotRevision).HasMaxLength(64);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.PayrollAdjustmentId, x.OccurredAtUtc });
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
            entity.HasOne<PayrollAdjustmentRecord>().WithMany(x => x.History)
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.PayrollAdjustmentId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<PayrollAccountMapping>(entity =>
        {
            entity.ToTable("payroll_account_mappings", table => table.HasCheckConstraint(
                "CK_hcm_payroll_account_mapping_dates", "\"EffectiveTo\" IS NULL OR \"EffectiveTo\" >= \"EffectiveFrom\""));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Type).HasConversion<string>().HasMaxLength(40);
            entity.Property(x => x.CurrencyCode).HasMaxLength(3).IsFixedLength();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Type, x.CurrencyCode, x.EffectiveFrom }).IsUnique();
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
        });
        modelBuilder.Entity<PayrollRuleConfiguration>(entity =>
        {
            entity.ToTable("payroll_rule_configurations", table =>
            {
                table.HasCheckConstraint("CK_hcm_payroll_rules_effective_dates", "\"EffectiveTo\" IS NULL OR \"EffectiveTo\" >= \"EffectiveFrom\"");
                table.HasCheckConstraint("CK_hcm_payroll_rules_hours", "\"StandardHoursPerPeriod\" > 0 AND \"OvertimeMultiplier\" >= 1");
                table.HasCheckConstraint("CK_hcm_payroll_rules_currency_precision", "\"CurrencyPrecision\" BETWEEN 0 AND 4");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.JurisdictionCode).HasMaxLength(20);
            entity.Property(x => x.Version).HasMaxLength(50);
            entity.Property(x => x.PeriodType).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.StandardHoursPerPeriod).HasPrecision(10, 2);
            entity.Property(x => x.OvertimeMultiplier).HasPrecision(8, 4);
            entity.Property(x => x.TaxBandsJson).HasColumnType("jsonb");
            entity.Property(x => x.EmployerContributionsJson).HasColumnType("jsonb");
            entity.Property(x => x.ProrationMethod).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.HireDateTreatment).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.TerminationDateTreatment).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.UnpaidLeaveTreatment).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.RoundingMode).HasConversion<string>().HasMaxLength(32);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.JurisdictionCode, x.Version, x.PeriodType }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.JurisdictionCode, x.PeriodType, x.EffectiveFrom });
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
        });
        modelBuilder.Entity<EmployeeCompensation>(entity =>
        {
            entity.ToTable("employee_compensations", table =>
            {
                table.HasCheckConstraint("CK_hcm_compensation_effective_dates", "\"EffectiveTo\" IS NULL OR \"EffectiveTo\" >= \"EffectiveFrom\"");
                table.HasCheckConstraint("CK_hcm_compensation_base_pay", "\"BasePayForPeriod\" >= 0");
                table.HasCheckConstraint("CK_hcm_compensation_schedule_hours", "\"ScheduledHoursPerPeriod\" IS NULL OR \"ScheduledHoursPerPeriod\" > 0");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CurrencyCode).HasMaxLength(3).IsFixedLength();
            entity.Property(x => x.PeriodType).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.BasePayForPeriod).HasPrecision(18, 2);
            entity.Property(x => x.ScheduledHoursPerPeriod).HasPrecision(10, 2);
            entity.Property(x => x.AllowancesJson).HasColumnType("jsonb");
            entity.Property(x => x.DeductionsJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.EmployeeId, x.PeriodType, x.EffectiveFrom }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.EmployeeId, x.PeriodType, x.EffectiveTo });
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
            entity.HasOne<Employee>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.EmployeeId })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<PayrollPeriod>(entity =>
        {
            entity.ToTable("payroll_periods", table =>
            {
                table.HasCheckConstraint("CK_hcm_payroll_period_dates", "\"StartDate\" <= \"EndDate\"");
                table.HasCheckConstraint("CK_hcm_payroll_period_pay_date", "\"PayDate\" >= \"EndDate\"");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.Id });
            entity.Property(x => x.Number).HasMaxLength(50);
            entity.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.JurisdictionCode).HasMaxLength(20);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.ExpectedWorkDatesJson).HasColumnType("jsonb");
            entity.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Number }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.StartDate, x.EndDate }).IsUnique();
            entity.HasQueryFilter(x => x.TenantId == TenantId && x.CompanyId == CompanyId);
        });
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var changes = ChangeTracker.Entries()
            .Where(x => x.Entity is Employee or LeaveRequestRecord or AttendanceRecord or EmployeeBenefitRecord or TrainingRecord or PerformanceReview or PayrollRun or PayrollLine or PayrollApprovalEvent or PayrollAdjustmentRecord or PayrollAdjustmentEvent or PayrollAccountMapping or PayrollRuleConfiguration or EmployeeCompensation or PayrollPeriod)
            .Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToArray();
        if (changes.Length > 0)
        {
            var tenant = _tenantContext?.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required.");
            var company = _companyContext?.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required.");
            if (changes.Any(x => Scope(x.Entity) != (tenant, company)))
                throw new InvalidOperationException("Cross-tenant or cross-company HCM writes are not permitted.");
        }
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private static (Guid Tenant, Guid Company) Scope(object entity) => entity switch
    {
        Employee value => (value.TenantId, value.CompanyId),
        LeaveRequestRecord value => (value.TenantId, value.CompanyId),
        AttendanceRecord value => (value.TenantId, value.CompanyId),
        EmployeeBenefitRecord value => (value.TenantId, value.CompanyId),
        TrainingRecord value => (value.TenantId, value.CompanyId),
        PerformanceReview value => (value.TenantId, value.CompanyId),
        PayrollRun value => (value.TenantId, value.CompanyId),
        PayrollLine value => (value.TenantId, value.CompanyId),
        PayrollApprovalEvent value => (value.TenantId, value.CompanyId),
        PayrollAdjustmentRecord value => (value.TenantId, value.CompanyId),
        PayrollAdjustmentEvent value => (value.TenantId, value.CompanyId),
        PayrollAccountMapping value => (value.TenantId, value.CompanyId),
        PayrollRuleConfiguration value => (value.TenantId, value.CompanyId),
        EmployeeCompensation value => (value.TenantId, value.CompanyId),
        PayrollPeriod value => (value.TenantId, value.CompanyId),
        _ => default
    };
}

public sealed class HcmDesignTimeFactory : IDesignTimeDbContextFactory<HcmDbContext>
{
    public HcmDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<HcmDbContext>()
            .UseNpgsql("Host=localhost;Database=erp_design;Username=erp")
            .Options);
}
