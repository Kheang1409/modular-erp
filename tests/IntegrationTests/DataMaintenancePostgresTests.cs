using System.Security.Claims;
using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Operations;
using Erp.Infrastructure.Auditing;
using Erp.Infrastructure.Operations;
using Erp.Infrastructure.Persistence;
using Erp.SharedKernel;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Erp.IntegrationTests;

public sealed class DataMaintenancePostgresTests
{
    [Fact]
    public async Task CleanupRequiresUnchangedConfirmedScopeAndPreservesBusinessRecoveryAndDuplicatePreventionHistory()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("erp_maintenance_tests").WithUsername("erp").WithPassword("integration_only_password").Build();
        await postgres.StartAsync(CancellationToken.None);
        var options = new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(postgres.GetConnectionString()).Options;
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var sameTenantCompanyId = Guid.NewGuid();
        var otherTenantCompanyId = Guid.NewGuid();
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        var old = clock.UtcNow.AddDays(-100);
        var recent = clock.UtcNow.AddDays(-5);
        var eligibleJob = Job(tenantId, companyId, old);
        var eligibleOutbox = Message(tenantId, companyId, old);

        await using (var seed = new PlatformDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync(CancellationToken.None);
            seed.Tenants.AddRange(Tenant(tenantId, "one"), Tenant(otherTenantId, "two"));
            seed.Companies.AddRange(Company(tenantId, companyId, "MAIN"),
                Company(tenantId, sameTenantCompanyId, "SECOND"), Company(otherTenantId, otherTenantCompanyId, "OTHER"));
            seed.BackgroundJobs.AddRange(eligibleJob, Job(tenantId, companyId, recent),
                Job(tenantId, companyId, old, idempotencyKey: "keep-duplicate-prevention"),
                Job(tenantId, companyId, old, state: "dead_letter"), Job(tenantId, companyId, old, state: "queued"),
                Job(tenantId, companyId, old, state: "running"), Job(tenantId, companyId, old, lockedUntil: clock.UtcNow.AddMinutes(5)),
                Job(tenantId, companyId, old, lastError: "Keep recovery detail"),
                Job(tenantId, sameTenantCompanyId, old), Job(otherTenantId, otherTenantCompanyId, old));
            seed.OutboxMessages.AddRange(eligibleOutbox, Message(tenantId, companyId, recent),
                Message(tenantId, companyId, null), Message(tenantId, companyId, old, deadLettered: old),
                Message(tenantId, companyId, old, lockedUntil: clock.UtcNow.AddMinutes(5)),
                Message(tenantId, companyId, old, lastError: "Keep recovery detail"),
                Message(tenantId, sameTenantCompanyId, old), Message(otherTenantId, otherTenantCompanyId, old));
            seed.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                CompanyId = companyId,
                Action = "business.created",
                EntityType = "Test",
                EntityId = "keep-history",
                TimestampUtc = old,
                CorrelationId = "seed"
            });
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        var tenant = new FixedTenantContext(new TenantId(tenantId));
        var company = new MutableCompanyContext(new CompanyId(companyId));
        var protection = new EphemeralDataProtectionProvider();
        var requestContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"))
        };
        await using var database = new PlatformDbContext(options, tenant);
        var audit = new AuditLogService(database, tenant, company, new HttpContextAccessor { HttpContext = requestContext }, clock);
        var maintenance = new DataMaintenanceService(database, tenant, company, clock, audit, protection);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => maintenance.PreviewAsync(29, CancellationToken.None));
        var preview = await maintenance.PreviewAsync(90, CancellationToken.None);
        Assert.Equal(companyId, preview.CompanyId);
        Assert.Equal(1, preview.SuccessfulJobCount);
        Assert.Equal(1, preview.ProcessedOutboxCount);
        Assert.Equal("CLEAN MAIN", preview.ConfirmationText);
        await Assert.ThrowsAsync<ArgumentException>(() => maintenance.ExecuteAsync(new(preview.PreviewToken, "CLEAN SECOND"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => maintenance.ExecuteAsync(new("tampered" + preview.PreviewToken, preview.ConfirmationText), CancellationToken.None));
        company.CompanyId = new CompanyId(sameTenantCompanyId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => maintenance.ExecuteAsync(new(preview.PreviewToken, preview.ConfirmationText), CancellationToken.None));
        company.CompanyId = new CompanyId(companyId);
        clock.UtcNow = clock.UtcNow.AddMinutes(31);
        await Assert.ThrowsAsync<InvalidOperationException>(() => maintenance.ExecuteAsync(new(preview.PreviewToken, preview.ConfirmationText), CancellationToken.None));
        clock.UtcNow = clock.UtcNow.AddMinutes(-31);

        // Equal counts still require identical records: a stale preview cannot approve a substituted selection.
        await using (var change = new PlatformDbContext(options))
        {
            await change.BackgroundJobs.IgnoreQueryFilters().Where(item => item.Id == eligibleJob.Id).ExecuteDeleteAsync(CancellationToken.None);
            eligibleJob = Job(tenantId, companyId, old);
            change.BackgroundJobs.Add(eligibleJob);
            await change.SaveChangesAsync(CancellationToken.None);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => maintenance.ExecuteAsync(new(preview.PreviewToken, preview.ConfirmationText), CancellationToken.None));

        // Deletion and its audit entry are atomic, including when auditing fails.
        preview = await maintenance.PreviewAsync(90, CancellationToken.None);
        var failingMaintenance = new DataMaintenanceService(database, tenant, company, clock, new FailingAudit(), protection);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failingMaintenance.ExecuteAsync(new(preview.PreviewToken, preview.ConfirmationText), CancellationToken.None));
        Assert.True(await database.BackgroundJobs.AnyAsync(item => item.Id == eligibleJob.Id, CancellationToken.None));
        Assert.True(await database.OutboxMessages.AnyAsync(item => item.Id == eligibleOutbox.Id, CancellationToken.None));

        var result = await maintenance.ExecuteAsync(new(preview.PreviewToken, preview.ConfirmationText), CancellationToken.None);
        Assert.Equal(1, result.DeletedJobCount);
        Assert.Equal(1, result.DeletedOutboxCount);
        Assert.False(await database.BackgroundJobs.AnyAsync(item => item.Id == eligibleJob.Id, CancellationToken.None));
        Assert.False(await database.OutboxMessages.AnyAsync(item => item.Id == eligibleOutbox.Id, CancellationToken.None));
        await using var verify = new PlatformDbContext(options);
        Assert.Equal(9, await verify.BackgroundJobs.IgnoreQueryFilters().CountAsync(CancellationToken.None));
        Assert.Equal(7, await verify.OutboxMessages.IgnoreQueryFilters().CountAsync(CancellationToken.None));
        Assert.Equal(3, await verify.Companies.IgnoreQueryFilters().CountAsync(CancellationToken.None));
        Assert.True(await verify.AuditLogs.IgnoreQueryFilters().AnyAsync(item => item.EntityId == "keep-history", CancellationToken.None));
        var cleanupAudit = await verify.AuditLogs.IgnoreQueryFilters().SingleAsync(item => item.Action == "platform.data_maintenance.completed", CancellationToken.None);
        Assert.Equal(companyId, cleanupAudit.CompanyId);
        using var cleanupDetails = JsonDocument.Parse(cleanupAudit.AfterJson!);
        Assert.Equal(1, cleanupDetails.RootElement.GetProperty("DeletedJobCount").GetInt32());
        Assert.Equal(1, cleanupDetails.RootElement.GetProperty("DeletedOutboxCount").GetInt32());
        await Assert.ThrowsAsync<InvalidOperationException>(() => maintenance.ExecuteAsync(new(preview.PreviewToken, preview.ConfirmationText), CancellationToken.None));
    }

    private static Tenant Tenant(Guid id, string slug) => new()
    {
        Id = id,
        Name = slug,
        Slug = slug,
        TimeZoneId = "UTC",
        IsActive = true,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    private static Company Company(Guid tenantId, Guid id, string code) => new()
    {
        Id = id,
        TenantId = tenantId,
        Name = code,
        Code = code,
        BaseCurrencyCode = "USD",
        TimeZoneId = "UTC",
        IsActive = true
    };

    private static BackgroundJob Job(Guid tenantId, Guid companyId, DateTimeOffset completed, string state = "succeeded",
        string? idempotencyKey = null, DateTimeOffset? lockedUntil = null, string? lastError = null) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CompanyId = companyId,
            Name = "test.job",
            State = state,
            CreatedAtUtc = completed.AddDays(-10),
            ExecuteAfterUtc = completed.AddDays(-10),
            CompletedAtUtc = completed,
            PayloadJson = "{}",
            CorrelationId = "test",
            MaxAttempts = 3,
            IdempotencyKey = idempotencyKey,
            LockedUntilUtc = lockedUntil,
            LastError = lastError
        };

    private static OutboxMessage Message(Guid tenantId, Guid companyId, DateTimeOffset? processed,
        DateTimeOffset? deadLettered = null, DateTimeOffset? lockedUntil = null, string? lastError = null) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CompanyId = companyId,
            EventType = "test.event",
            PayloadJson = "{}",
            CorrelationId = "test",
            OccurredAtUtc = processed?.AddDays(-10) ?? DateTimeOffset.UtcNow,
            NextAttemptAtUtc = processed ?? DateTimeOffset.UtcNow,
            ProcessedAtUtc = processed,
            DeadLetteredAtUtc = deadLettered,
            LockedUntilUtc = lockedUntil,
            LastError = lastError
        };

    private sealed class FixedTenantContext(TenantId tenantId) : ITenantContext { public TenantId? TenantId { get; } = tenantId; }
    private sealed class MutableCompanyContext(CompanyId companyId) : ICompanyContext { public CompanyId? CompanyId { get; set; } = companyId; }
    private sealed class MutableClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow { get; set; } = now; }
    private sealed class FailingAudit : IAuditLogWriter
    {
        public void Append(AuditWriteEntry entry) { }
        public Task FlushAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Test audit failure");
    }
}
