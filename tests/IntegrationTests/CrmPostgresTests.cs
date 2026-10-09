using Erp.Abstractions.Context;
using Erp.Modules.Crm;
using Erp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Erp.IntegrationTests;

public sealed class CrmPostgresTests
{
    [Fact]
    public async Task LeadsAreCompanyIsolatedAndConcurrencyIsEnforced()
    {
        PostgreSqlContainer postgres;
        try { postgres = new PostgreSqlBuilder("postgres:18-alpine").WithDatabase("crm_tests").WithUsername("erp").WithPassword("integration_only_password").Build(); await postgres.StartAsync(); }
        catch (Exception) when (!string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)) { return; }
        await using (postgres)
        {
            var tenant = Guid.NewGuid(); var companyA = Guid.NewGuid(); var companyB = Guid.NewGuid();
            var options = new DbContextOptionsBuilder<CrmDbContext>().UseNpgsql(postgres.GetConnectionString()).Options;
            await using (var migration = new CrmDbContext(options)) await migration.Database.MigrateAsync();
            var id = Guid.NewGuid(); var opportunityId = Guid.NewGuid(); var activityId = Guid.NewGuid();
            await using (var write = new CrmDbContext(options, new TenantContext(tenant), new CompanyContext(companyA)))
            {
                var stageId = Guid.NewGuid();
                write.PipelineStages.Add(new PipelineStage { Id = stageId, TenantId = tenant, CompanyId = companyA, Name = "Open", Sequence = 10, DefaultProbability = 25, IsActive = true, ConcurrencyStamp = Guid.NewGuid().ToString("N") });
                write.Leads.Add(NewLead(id, tenant, companyA));
                write.Opportunities.Add(new Opportunity { Id = opportunityId, TenantId = tenant, CompanyId = companyA, StageId = stageId, Title = "Opportunity", ExpectedRevenue = 100, Probability = 25, State = OpportunityState.Open, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow, ConcurrencyStamp = Guid.NewGuid().ToString("N") });
                write.Activities.Add(new CrmActivity { Id = activityId, TenantId = tenant, CompanyId = companyA, OpportunityId = opportunityId, Type = ActivityType.Task, Subject = "Follow up", CreatedAtUtc = DateTimeOffset.UtcNow, ConcurrencyStamp = Guid.NewGuid().ToString("N") });
                await write.SaveChangesAsync();
            }
            await using var visible = new CrmDbContext(options, new TenantContext(tenant), new CompanyContext(companyA));
            await using var hidden = new CrmDbContext(options, new TenantContext(tenant), new CompanyContext(companyB));
            Assert.Equal(id, await visible.Leads.Select(x => x.Id).SingleAsync()); Assert.Equal(activityId, await visible.Activities.Select(x => x.Id).SingleAsync()); Assert.Empty(await hidden.Leads.ToListAsync()); Assert.Empty(await hidden.Opportunities.ToListAsync()); Assert.Empty(await hidden.Activities.ToListAsync());
            var first = await visible.Leads.SingleAsync();
            await using (var competing = new CrmDbContext(options, new TenantContext(tenant), new CompanyContext(companyA)))
            { var second = await competing.Leads.SingleAsync(); second.Title = "Second"; second.ConcurrencyStamp = Guid.NewGuid().ToString("N"); await competing.SaveChangesAsync(); }
            first.Title = "Stale"; first.ConcurrencyStamp = Guid.NewGuid().ToString("N");
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => visible.SaveChangesAsync());
        }
    }
    private static Lead NewLead(Guid id, Guid tenant, Guid company) => new() { Id = id, TenantId = tenant, CompanyId = company, Title = "Lead", State = LeadState.New, Probability = 10, ExpectedRevenue = 100, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow, ConcurrencyStamp = Guid.NewGuid().ToString("N") };
    private sealed class TenantContext(Guid id) : ITenantContext { public TenantId? TenantId { get; } = new(id); }
    private sealed class CompanyContext(Guid id) : ICompanyContext { public CompanyId? CompanyId { get; } = new(id); }
}
