using System.Security.Claims;
using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Events;
using Erp.Abstractions.Organization;
using Erp.Abstractions.Persistence;
using Erp.Infrastructure.Auditing;
using Erp.Infrastructure.Events;
using Erp.Infrastructure.Persistence;
using Erp.ModuleSystem;
using Erp.Modules.Contacts;
using Erp.Modules.Crm;
using Erp.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Erp.IntegrationTests;

public sealed class AtomicCrmPostgresTests(AtomicCrmPostgresFixture fixture)
    : IClassFixture<AtomicCrmPostgresFixture>
{
    [Theory]
    [InlineData("lead-create")]
    [InlineData("lead-update")]
    [InlineData("lead-qualify")]
    [InlineData("lead-lose")]
    [InlineData("lead-reopen")]
    [InlineData("lead-assign")]
    [InlineData("lead-archive")]
    [InlineData("lead-reactivate")]
    [InlineData("opportunity-create")]
    [InlineData("opportunity-update")]
    [InlineData("opportunity-move")]
    [InlineData("opportunity-win")]
    [InlineData("opportunity-lose")]
    [InlineData("opportunity-reopen")]
    [InlineData("opportunity-assign")]
    [InlineData("opportunity-archive")]
    [InlineData("opportunity-reactivate")]
    [InlineData("stage-create")]
    [InlineData("stage-update")]
    [InlineData("stage-deactivate")]
    [InlineData("stage-reactivate")]
    [InlineData("activity-create-lead")]
    [InlineData("activity-create-opportunity")]
    [InlineData("activity-complete")]
    [InlineData("activity-reopen")]
    public async Task FailureAfterRealAuditFlushRollsBackEveryWriteFamilyAndAllowsSameScopeCommit(string operation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var mutation = await PrepareMutation(scope, operation);
        var original = await Snapshot(scope);
        var failedEvent = scope.NewEvent();
        scope.Audit.PendingEvents.Enqueue(failedEvent);
        scope.Audit.FailOnFlush = 1;

        await Assert.ThrowsAsync<InjectedAuditException>(() => mutation.Execute(CancellationToken.None));

        Assert.Equal(1, scope.Audit.FlushCalls);
        Assert.Equal(original, await Snapshot(scope));
        await AssertPlatformCounts(scope, 0, 0);
        AssertClean(scope);
        var committedEvent = scope.NewEvent();
        scope.Audit.PendingEvents.Enqueue(committedEvent);

        var committed = await mutation.Execute(CancellationToken.None);

        AssertMutationOutcome(scope, operation, committed);
        Assert.NotEqual(original, await Snapshot(scope));
        await AssertDurableResult(scope, committed);
        await AssertPlatformCounts(scope, 1, 1);
        await using var platform = scope.ReadPlatform();
        var audit = await platform.AuditLogs.SingleAsync(entry => entry.CompanyId == scope.CompanyId);
        Assert.Equal(mutation.Action, audit.Action);
        Assert.Equal(scope.TenantId, audit.TenantId);
        Assert.Equal(scope.CompanyId, audit.CompanyId);
        Assert.Equal(scope.UserId, audit.UserId);
        Assert.Equal("crm-atomicity-test", audit.CorrelationId);
        Assert.Equal(FixedClock.Now, audit.TimestampUtc);
        using var after = JsonDocument.Parse(audit.AfterJson!);
        Assert.Equal(audit.EntityId, after.RootElement.GetProperty("Id").GetGuid().ToString());
        var (expectedId, expectedStamp) = committed switch
        {
            LeadDto item => (item.Id, item.ConcurrencyStamp),
            OpportunityDto item => (item.Id, item.ConcurrencyStamp),
            PipelineStageDto item => (item.Id, item.ConcurrencyStamp),
            ActivityDto item => (item.Id, item.ConcurrencyStamp),
            _ => throw new InvalidOperationException("The CRM mutation returned an unexpected result type.")
        };
        Assert.Equal(expectedId.ToString(), audit.EntityId);
        Assert.Equal(expectedStamp, after.RootElement.GetProperty("ConcurrencyStamp").GetString());
        var message = await platform.OutboxMessages.SingleAsync(entry => entry.CompanyId == scope.CompanyId);
        Assert.Equal(committedEvent.EventId, message.Id);
        Assert.Equal("tests.crm.atomicity.v1", message.EventType);
        Assert.Equal(scope.TenantId, message.TenantId);
        Assert.Equal(scope.CompanyId, message.CompanyId);
        Assert.False(await platform.OutboxMessages.AnyAsync(entry => entry.Id == failedEvent.EventId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversionSecondRealAuditFlushFailureOrCancellationRestoresQualifiedLeadAndAllowsRetry(bool cancel)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        scope.Crm.Leads.Attach(scope.SeedLead);
        scope.SeedLead.State = LeadState.Qualified;
        await scope.Crm.SaveChangesAsync();
        scope.Crm.ChangeTracker.Clear();
        var original = await Snapshot(scope);
        using var cancellation = new CancellationTokenSource();
        var failedEvents = new[] { scope.NewEvent(), scope.NewEvent() };
        foreach (var message in failedEvents) scope.Audit.PendingEvents.Enqueue(message);
        if (cancel) scope.Audit.AfterFlush = call => { if (call == 2) cancellation.Cancel(); };
        else scope.Audit.FailOnFlush = 2;
        var request = new ConvertLeadRequest(scope.SeedLead.ConcurrencyStamp, scope.OpenStage.Id, "Converted sale");

        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Leads.ConvertAsync(scope.SeedLead.Id, request, cancellation.Token));
        else
            await Assert.ThrowsAsync<InjectedAuditException>(() => scope.Leads.ConvertAsync(scope.SeedLead.Id, request, CancellationToken.None));

        Assert.Equal(2, scope.Audit.FlushCalls);
        Assert.Equal(original, await Snapshot(scope));
        await AssertPlatformCounts(scope, 0, 0);
        AssertClean(scope);
        await using (var reader = scope.ReadCrm())
        {
            var lead = await reader.Leads.SingleAsync();
            Assert.Equal(LeadState.Qualified, lead.State);
            Assert.Equal(scope.SeedLead.ConcurrencyStamp, lead.ConcurrencyStamp);
            Assert.False(await reader.Opportunities.AnyAsync(item => item.SourceLeadId == lead.Id));
        }
        scope.Audit.AfterFlush = null;
        var successfulEvents = new[] { scope.NewEvent(), scope.NewEvent() };
        foreach (var message in successfulEvents) scope.Audit.PendingEvents.Enqueue(message);

        var converted = await scope.Leads.ConvertAsync(scope.SeedLead.Id, request, CancellationToken.None);

        await AssertDurableResult(scope, converted);
        await AssertPlatformCounts(scope, 2, 2);
        await using var persistedCrm = scope.ReadCrm();
        Assert.Equal(LeadState.Converted, (await persistedCrm.Leads.SingleAsync()).State);
        Assert.Equal(scope.SeedLead.Id, converted.SourceLeadId);
        Assert.Equal(scope.ContactId, converted.ContactId);
        Assert.Equal("Atomic customer", converted.ContactName);
        Assert.Equal(scope.SeedLead.ExpectedRevenue, converted.ExpectedRevenue);
        Assert.Equal(scope.SeedLead.Probability, converted.Probability);
        Assert.Equal(scope.UserId, converted.AssignedUserId);
        await using var platform = scope.ReadPlatform();
        var audits = await platform.AuditLogs.Where(entry => entry.CompanyId == scope.CompanyId).ToDictionaryAsync(entry => entry.Action);
        Assert.Equal(scope.SeedLead.Id.ToString(), audits["crm.lead.converted"].EntityId);
        Assert.Equal(converted.Id.ToString(), audits["crm.opportunity.created"].EntityId);
        var messages = await platform.OutboxMessages.Where(entry => entry.CompanyId == scope.CompanyId).Select(entry => entry.Id).ToArrayAsync();
        Assert.Equal(successfulEvents.Select(message => message.EventId).Order(), messages.Order());
        Assert.DoesNotContain(messages, id => failedEvents.Any(message => message.EventId == id));
    }

    [Theory]
    [InlineData("lead-create")]
    [InlineData("opportunity-create")]
    [InlineData("stage-create")]
    [InlineData("activity-create-lead")]
    public async Task CancellationAfterRealAuditFlushRollsBackAndAllowsUncancelledRetry(string operation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var mutation = await PrepareMutation(scope, operation);
        var original = await Snapshot(scope);
        using var cancellation = new CancellationTokenSource();
        scope.Audit.PendingEvents.Enqueue(scope.NewEvent());
        scope.Audit.AfterFlush = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation.Execute(cancellation.Token));

        Assert.Equal(original, await Snapshot(scope));
        await AssertPlatformCounts(scope, 0, 0);
        AssertClean(scope);
        scope.Audit.AfterFlush = null;
        await AssertDurableResult(scope, await mutation.Execute(CancellationToken.None));
        await AssertPlatformCounts(scope, 1, 0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RealStageNameOrSequenceUniqueViolationDiscardsBusinessAndStagedPlatformRows(bool update, bool duplicateSequence)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var original = await Snapshot(scope);
        await using (await scope.Transactions.BeginAsync(scope.Crm, CancellationToken.None))
        {
            scope.Outbox.Add(scope.NewEvent());
            var request = new PipelineStageRequest(duplicateSequence ? "Unique name" : scope.OpenStage.Name,
                duplicateSequence ? scope.OpenStage.Sequence : 99, 20, false, false,
                ConcurrencyStamp: update ? scope.NextStage.ConcurrencyStamp : null);

            var failure = await Assert.ThrowsAsync<LeadValidationException>(() => scope.Opportunities.SaveStageAsync(
                update ? scope.NextStage.Id : null, request, CancellationToken.None));

            Assert.Contains("conflict", failure.Errors.Keys);
        }

        var databaseFailure = Assert.IsType<DbUpdateException>(scope.SaveHook.LastFailure);
        var postgres = Assert.IsType<PostgresException>(databaseFailure.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal(duplicateSequence ? "IX_pipeline_stages_TenantId_CompanyId_Sequence" : "IX_pipeline_stages_TenantId_CompanyId_Name", postgres.ConstraintName);
        Assert.Equal(0, scope.Audit.FlushCalls);
        Assert.Equal(original, await Snapshot(scope));
        AssertClean(scope);
        await AssertPlatformCounts(scope, 0, 0);
        await scope.Opportunities.SaveStageAsync(update ? scope.NextStage.Id : null,
            new("Valid retry", 99, 20, false, false, ConcurrencyStamp: update ? scope.NextStage.ConcurrencyStamp : null), CancellationToken.None);
        await AssertPlatformCounts(scope, 1, 0);
    }

    [Theory]
    [InlineData("lead")]
    [InlineData("opportunity")]
    [InlineData("stage")]
    [InlineData("activity")]
    public async Task StaleVersionsLeaveNoWritesAndAllowCurrentVersionRetry(string family)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var original = await Snapshot(scope);
        var stale = Guid.NewGuid().ToString("N");

        await Assert.ThrowsAsync<LeadConcurrencyException>(() => VersionedMutation(scope, family, stale));

        Assert.Equal(original, await Snapshot(scope));
        AssertClean(scope);
        await AssertPlatformCounts(scope, 0, 0);
        var stamp = family switch
        {
            "lead" => scope.SeedLead.ConcurrencyStamp,
            "opportunity" => scope.SeedOpportunity.ConcurrencyStamp,
            "stage" => scope.OpenStage.ConcurrencyStamp,
            _ => scope.SeedActivity.ConcurrencyStamp
        };
        await AssertDurableResult(scope, await VersionedMutation(scope, family, stamp));
        await AssertPlatformCounts(scope, 1, 0);
    }

    [Theory]
    [InlineData("lead")]
    [InlineData("opportunity")]
    [InlineData("stage")]
    [InlineData("activity")]
    public async Task RealConcurrentDatabaseWriterRollsBackLosingMutationAndStagedOutboxThenAllowsRetry(string family)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var winnerStamp = Guid.NewGuid().ToString("N");
        scope.SaveHook.BeforeSave = async (_, token) =>
        {
            await using var competing = scope.ReadCrm();
            switch (family)
            {
                case "lead":
                    var lead = await competing.Leads.SingleAsync(token);
                    lead.Title = "Concurrent winner"; lead.ConcurrencyStamp = winnerStamp;
                    break;
                case "opportunity":
                    var opportunity = await competing.Opportunities.SingleAsync(token);
                    opportunity.Title = "Concurrent winner"; opportunity.ConcurrencyStamp = winnerStamp;
                    break;
                case "stage":
                    var stage = await competing.PipelineStages.SingleAsync(item => item.Id == scope.OpenStage.Id, token);
                    stage.Name = "Concurrent winner"; stage.ConcurrencyStamp = winnerStamp;
                    break;
                default:
                    var activity = await competing.Activities.SingleAsync(token);
                    activity.Subject = "Concurrent winner"; activity.ConcurrencyStamp = winnerStamp;
                    break;
            }
            await competing.SaveChangesAsync(token);
        };
        var stamp = family switch
        {
            "lead" => scope.SeedLead.ConcurrencyStamp,
            "opportunity" => scope.SeedOpportunity.ConcurrencyStamp,
            "stage" => scope.OpenStage.ConcurrencyStamp,
            _ => scope.SeedActivity.ConcurrencyStamp
        };
        await using (await scope.Transactions.BeginAsync(scope.Crm, CancellationToken.None))
        {
            scope.Outbox.Add(scope.NewEvent());
            await Assert.ThrowsAsync<LeadConcurrencyException>(() => VersionedMutation(scope, family, stamp));
        }

        Assert.True(scope.SaveHook.Executed);
        Assert.IsType<DbUpdateConcurrencyException>(scope.SaveHook.LastFailure);
        AssertClean(scope);
        await AssertPlatformCounts(scope, 0, 0);
        await using (var reader = scope.ReadCrm())
        {
            var persistedStamp = family switch
            {
                "lead" => (await reader.Leads.SingleAsync()).ConcurrencyStamp,
                "opportunity" => (await reader.Opportunities.SingleAsync()).ConcurrencyStamp,
                "stage" => (await reader.PipelineStages.SingleAsync(item => item.Id == scope.OpenStage.Id)).ConcurrencyStamp,
                _ => (await reader.Activities.SingleAsync()).ConcurrencyStamp
            };
            Assert.Equal(winnerStamp, persistedStamp);
        }
        await AssertDurableResult(scope, await VersionedMutation(scope, family, winnerStamp));
        await AssertPlatformCounts(scope, 1, 0);
    }

    [Theory]
    [InlineData("opportunity-assign")]
    [InlineData("opportunity-archive")]
    [InlineData("opportunity-reactivate")]
    public async Task InactiveStageDetectedAfterAuditFlushRollsBackTheMutation(string operation)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var mutation = await PrepareMutation(scope, operation);
        await using (var setup = scope.ReadCrm())
        {
            var stage = await setup.PipelineStages.SingleAsync(item => item.Id == scope.OpenStage.Id);
            stage.IsActive = false;
            await setup.SaveChangesAsync();
        }
        var original = await Snapshot(scope);
        scope.Audit.PendingEvents.Enqueue(scope.NewEvent());

        var failure = await Assert.ThrowsAsync<LeadValidationException>(() => mutation.Execute(CancellationToken.None));

        Assert.Contains("stageId", failure.Errors.Keys);
        Assert.Equal(1, scope.Audit.FlushCalls);
        Assert.Equal(original, await Snapshot(scope));
        AssertClean(scope);
        await AssertPlatformCounts(scope, 0, 0);
        await using (var setup = scope.ReadCrm())
        {
            var stage = await setup.PipelineStages.SingleAsync(item => item.Id == scope.OpenStage.Id);
            stage.IsActive = true;
            await setup.SaveChangesAsync();
        }
        await AssertDurableResult(scope, await mutation.Execute(CancellationToken.None));
        await AssertPlatformCounts(scope, 1, 0);
    }

    [Fact]
    public async Task RealContactAndCompanyMembershipDirectoriesRejectInvalidReferencesWithoutWrites()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        await using var otherCompany = await TestScope.CreateAsync(fixture.ConnectionString, scope.TenantId);
        await using var otherTenant = await TestScope.CreateAsync(fixture.ConnectionString);
        var currentUser = await scope.Users.FindAsync(scope.UserId, CancellationToken.None);
        Assert.NotNull(currentUser);
        Assert.Equal(scope.UserId, currentUser.Id);
        Assert.Equal($"atomic-{scope.UserId:N}", currentUser.DisplayName);
        Assert.Equal($"{scope.UserId:N}@example.test", currentUser.Email);
        Assert.Equal(currentUser, Assert.Single(await scope.Users.ListAsync(CancellationToken.None)));
        Assert.Null(await scope.Users.FindAsync(otherCompany.UserId, CancellationToken.None));
        Assert.Null(await scope.Users.FindAsync(otherTenant.UserId, CancellationToken.None));
        var archived = NewContact(scope.TenantId, scope.CompanyId);
        archived.IsArchived = true;
        scope.Contacts.Contacts.Add(archived);
        await scope.Contacts.SaveChangesAsync();
        scope.Contacts.ChangeTracker.Clear();
        var original = await Snapshot(scope);
        foreach (var contact in new[] { Guid.NewGuid(), archived.Id, otherCompany.ContactId, otherTenant.ContactId })
        {
            await Reject("contactId", () => scope.Leads.CreateAsync(LeadRequest(scope, "Invalid contact") with { ContactId = contact }, CancellationToken.None));
            await Reject("contactId", () => scope.Leads.UpdateAsync(scope.SeedLead.Id, LeadRequest(scope, "Invalid contact", scope.SeedLead.ConcurrencyStamp) with { ContactId = contact }, CancellationToken.None));
            await Reject("contactId", () => scope.Opportunities.CreateAsync(OpportunityRequest(scope, "Invalid contact") with { ContactId = contact }, CancellationToken.None));
            await Reject("contactId", () => scope.Opportunities.UpdateAsync(scope.SeedOpportunity.Id, OpportunityRequest(scope, "Invalid contact", scope.SeedOpportunity.ConcurrencyStamp) with { ContactId = contact }, CancellationToken.None));
        }
        foreach (var user in new[] { Guid.NewGuid(), otherCompany.UserId, otherTenant.UserId })
        {
            await Reject("assignedUserId", () => scope.Leads.AssignAsync(scope.SeedLead.Id, new(scope.SeedLead.ConcurrencyStamp, user), CancellationToken.None));
            await Reject("assignedUserId", () => scope.Opportunities.AssignAsync(scope.SeedOpportunity.Id, new(scope.SeedOpportunity.ConcurrencyStamp, user), CancellationToken.None));
            await Reject("assignedUserId", () => scope.Activities.CreateAsync(new(scope.SeedLead.Id, null, ActivityType.Task, "Invalid assignee", null, null, user), CancellationToken.None));
        }
        foreach (var hidden in new[] { otherCompany, otherTenant })
        {
            await Reject("stageId", () => scope.Opportunities.CreateAsync(OpportunityRequest(scope, "Hidden stage") with { StageId = hidden.OpenStage.Id }, CancellationToken.None));
            await Reject("leadId", () => scope.Activities.CreateAsync(new(hidden.SeedLead.Id, null, ActivityType.Task, "Hidden parent", null, null, null), CancellationToken.None));
            await Reject("opportunityId", () => scope.Activities.CreateAsync(new(null, hidden.SeedOpportunity.Id, ActivityType.Task, "Hidden parent", null, null, null), CancellationToken.None));
        }
        Assert.Equal(0, scope.Audit.FlushCalls);
        Assert.Equal(original, await Snapshot(scope));
        await AssertPlatformCounts(scope, 0, 0);
        var assigned = await scope.Leads.AssignAsync(scope.SeedLead.Id, new(scope.SeedLead.ConcurrencyStamp, scope.UserId), CancellationToken.None);
        Assert.Equal(scope.UserId, assigned.AssignedUserId);
        await AssertDurableResult(scope, assigned);
        await AssertPlatformCounts(scope, 1, 0);

        async Task Reject(string field, Func<Task> action)
        {
            var failure = await Assert.ThrowsAsync<LeadValidationException>(action);
            Assert.Contains(field, failure.Errors.Keys);
            AssertClean(scope);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceReadsAndMutationsPreserveTenantAndCompanyIsolation(bool differentTenant)
    {
        await using var owner = await TestScope.CreateAsync(fixture.ConnectionString);
        await using var hidden = await TestScope.CreateAsync(fixture.ConnectionString, differentTenant ? null : owner.TenantId);
        var original = await Snapshot(owner);
        Assert.Null(await hidden.Leads.GetAsync(owner.SeedLead.Id, CancellationToken.None));
        Assert.Null(await hidden.Opportunities.GetAsync(owner.SeedOpportunity.Id, CancellationToken.None));
        Assert.DoesNotContain((await hidden.Leads.SearchAsync(new(), CancellationToken.None)).Items, item => item.Id == owner.SeedLead.Id);
        Assert.DoesNotContain((await hidden.Opportunities.SearchAsync(new(), CancellationToken.None)).Items, item => item.Id == owner.SeedOpportunity.Id);
        Assert.DoesNotContain(await hidden.Opportunities.GetStagesAsync(true, CancellationToken.None), item => item.Id == owner.OpenStage.Id);
        Assert.Empty(await hidden.Activities.ListAsync(owner.SeedLead.Id, null, CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => hidden.Leads.SetArchivedAsync(owner.SeedLead.Id, owner.SeedLead.ConcurrencyStamp, true, CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => hidden.Opportunities.SetArchivedAsync(owner.SeedOpportunity.Id, owner.SeedOpportunity.ConcurrencyStamp, true, CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => hidden.Opportunities.SaveStageAsync(owner.OpenStage.Id,
            new("Forbidden", 10, 20, false, false, ConcurrencyStamp: owner.OpenStage.ConcurrencyStamp), CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => hidden.Activities.SetCompletedAsync(owner.SeedActivity.Id, owner.SeedActivity.ConcurrencyStamp, true, CancellationToken.None));
        AssertClean(hidden);
        Assert.Equal(original, await Snapshot(owner));
        await AssertPlatformCounts(hidden, 0, 0);
        await AssertPlatformCounts(owner, 0, 0);
        await using (await hidden.Transactions.BeginAsync(hidden.Crm, CancellationToken.None))
        {
            hidden.Crm.Leads.Add(NewLead(owner.TenantId, owner.CompanyId, owner.ContactId));
            await Assert.ThrowsAsync<InvalidOperationException>(() => hidden.Crm.SaveChangesAsync());
        }
        AssertClean(hidden);
    }

    [Fact]
    public async Task StagesCanBeCreatedReadUpdatedDeactivatedAndReactivatedWithoutSchemaChanges()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var created = await scope.Opportunities.SaveStageAsync(null, new(" Custom stage ", 50, 60, false, false), CancellationToken.None);
        Assert.Equal("Custom stage", created.Name);
        var updated = await scope.Opportunities.SaveStageAsync(created.Id,
            new("Renamed stage", 60, 75, false, false, ConcurrencyStamp: created.ConcurrencyStamp), CancellationToken.None);
        var inactive = await scope.Opportunities.SaveStageAsync(updated.Id,
            new(updated.Name, 60, 75, false, false, false, updated.ConcurrencyStamp), CancellationToken.None);
        Assert.DoesNotContain(await scope.Opportunities.GetStagesAsync(false, CancellationToken.None), item => item.Id == created.Id);
        Assert.Contains(await scope.Opportunities.GetStagesAsync(true, CancellationToken.None), item => item == inactive);
        var active = await scope.Opportunities.SaveStageAsync(inactive.Id,
            new(inactive.Name, 60, 75, false, false, true, inactive.ConcurrencyStamp), CancellationToken.None);
        Assert.Equal(4, new[] { created.ConcurrencyStamp, updated.ConcurrencyStamp, inactive.ConcurrencyStamp, active.ConcurrencyStamp }.Distinct().Count());
        await AssertDurableResult(scope, active);
        await AssertPlatformCounts(scope, 4, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrmAndContactsComposeUnderOneOuterTransactionAndAbandonmentAllowsRetry(bool commit)
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        Assert.Same(scope.Session.Connection, scope.Platform.Database.GetDbConnection());
        Assert.Same(scope.Session.Connection, scope.Crm.Database.GetDbConnection());
        Assert.Same(scope.Session.Connection, scope.Contacts.Database.GetDbConnection());
        var original = await Snapshot(scope);
        Guid contactId;
        Guid leadId;
        await using (var outer = await scope.Transactions.BeginAsync(scope.Crm, CancellationToken.None))
        {
            scope.Audit.PendingEvents.Enqueue(scope.NewEvent());
            var contact = await scope.ContactService.CreateAsync(new(ContactKind.Organization, "Composed customer", null, null,
                "Composed customer", null, null, null, null, IsCustomer: true), CancellationToken.None);
            scope.Audit.PendingEvents.Enqueue(scope.NewEvent());
            var lead = await scope.Leads.CreateAsync(LeadRequest(scope, "Composed lead") with { ContactId = contact.Id }, CancellationToken.None);
            Assert.Equal("Composed customer", lead.ContactName);
            contactId = contact.Id;
            leadId = lead.Id;
            Assert.Equal(original, await Snapshot(scope));
            await using (var contacts = scope.ReadContacts()) Assert.False(await contacts.Contacts.AnyAsync(item => item.Id == contactId));
            await AssertPlatformCounts(scope, 0, 0);
            if (commit) await outer.CommitAsync(CancellationToken.None);
        }

        await AssertPlatformCounts(scope, commit ? 2 : 0, commit ? 2 : 0);
        await using (var reader = scope.ReadCrm()) Assert.Equal(commit, await reader.Leads.AnyAsync(item => item.Id == leadId && item.ContactId == contactId));
        await using (var contacts = scope.ReadContacts()) Assert.Equal(commit, await contacts.Contacts.AnyAsync(item => item.Id == contactId));
        if (!commit)
        {
            AssertClean(scope);
            Assert.Empty(scope.Contacts.ChangeTracker.Entries());
            Assert.Null(scope.Contacts.Database.CurrentTransaction);
            Assert.Equal(original, await Snapshot(scope));
            await scope.Leads.CreateAsync(LeadRequest(scope, "Independent retry"), CancellationToken.None);
            await AssertPlatformCounts(scope, 1, 0);
            await using var contacts = scope.ReadContacts();
            Assert.False(await contacts.Contacts.AnyAsync(item => item.Id == contactId));
        }
    }

    private sealed record Mutation(string Action, Func<CancellationToken, Task<object>> Execute);

    private static async Task<Mutation> PrepareMutation(TestScope scope, string operation)
    {
        // Raw setup isolates the operation's own audit/outbox rows and preserves its original version for retry.
        await using (var setup = scope.ReadCrm())
        {
            if (operation == "lead-reactivate") (await setup.Leads.SingleAsync()).IsArchived = true;
            if (operation == "lead-reopen")
            {
                var lead = await setup.Leads.SingleAsync(); lead.State = LeadState.Lost; lead.LostReason = "Original loss";
            }
            if (operation == "opportunity-reactivate") (await setup.Opportunities.SingleAsync()).IsArchived = true;
            if (operation == "opportunity-reopen")
            {
                var opportunity = await setup.Opportunities.SingleAsync(); opportunity.State = OpportunityState.Lost;
                opportunity.StageId = scope.LostStage.Id; opportunity.Probability = 0; opportunity.LostReason = "Original loss";
            }
            if (operation == "activity-reopen") (await setup.Activities.SingleAsync()).CompletedAtUtc = FixedClock.Now;
            if (operation == "stage-reactivate") (await setup.PipelineStages.SingleAsync(item => item.Id == scope.OpenStage.Id)).IsActive = false;
            await setup.SaveChangesAsync();
        }
        var leadStamp = scope.SeedLead.ConcurrencyStamp;
        var opportunityStamp = scope.SeedOpportunity.ConcurrencyStamp;
        var stageStamp = scope.OpenStage.ConcurrencyStamp;
        var activityStamp = scope.SeedActivity.ConcurrencyStamp;
        return operation switch
        {
            "lead-create" => new("crm.lead.created", token => Box(scope.Leads.CreateAsync(LeadRequest(scope, "Created lead"), token))),
            "lead-update" => new("crm.lead.updated", token => Box(scope.Leads.UpdateAsync(scope.SeedLead.Id, LeadRequest(scope, "Updated lead", leadStamp), token))),
            "lead-qualify" => new("crm.lead.qualified", token => Box(scope.Leads.TransitionAsync(scope.SeedLead.Id, LeadState.Qualified, new(leadStamp), token))),
            "lead-lose" => new("crm.lead.lost", token => Box(scope.Leads.TransitionAsync(scope.SeedLead.Id, LeadState.Lost, new(leadStamp, "Lost sale"), token))),
            "lead-reopen" => new("crm.lead.new", token => Box(scope.Leads.TransitionAsync(scope.SeedLead.Id, LeadState.New, new(leadStamp), token))),
            "lead-assign" => new("crm.lead.assigned", token => Box(scope.Leads.AssignAsync(scope.SeedLead.Id, new(leadStamp, null), token))),
            "lead-archive" or "lead-reactivate" => new(operation == "lead-archive" ? "crm.lead.archived" : "crm.lead.reactivated",
                token => Box(scope.Leads.SetArchivedAsync(scope.SeedLead.Id, leadStamp, operation == "lead-archive", token))),
            "opportunity-create" => new("crm.opportunity.created", token => Box(scope.Opportunities.CreateAsync(OpportunityRequest(scope, "Created opportunity"), token))),
            "opportunity-update" => new("crm.opportunity.updated", token => Box(scope.Opportunities.UpdateAsync(scope.SeedOpportunity.Id, OpportunityRequest(scope, "Updated opportunity", opportunityStamp), token))),
            "opportunity-move" or "opportunity-reopen" => new("crm.opportunity.open", token => Box(scope.Opportunities.MoveAsync(scope.SeedOpportunity.Id, new(opportunityStamp, scope.NextStage.Id), token))),
            "opportunity-win" => new("crm.opportunity.won", token => Box(scope.Opportunities.MoveAsync(scope.SeedOpportunity.Id, new(opportunityStamp, scope.WonStage.Id), token))),
            "opportunity-lose" => new("crm.opportunity.lost", token => Box(scope.Opportunities.MoveAsync(scope.SeedOpportunity.Id, new(opportunityStamp, scope.LostStage.Id, "Lost sale"), token))),
            "opportunity-assign" => new("crm.opportunity.assigned", token => Box(scope.Opportunities.AssignAsync(scope.SeedOpportunity.Id, new(opportunityStamp, null), token))),
            "opportunity-archive" or "opportunity-reactivate" => new(operation == "opportunity-archive" ? "crm.opportunity.archived" : "crm.opportunity.reactivated",
                token => Box(scope.Opportunities.SetArchivedAsync(scope.SeedOpportunity.Id, opportunityStamp, operation == "opportunity-archive", token))),
            "stage-create" => new("crm.stage.created", token => Box(scope.Opportunities.SaveStageAsync(null, new("Created stage", 50, 60, false, false), token))),
            "stage-update" or "stage-deactivate" or "stage-reactivate" => new("crm.stage.updated", token => Box(scope.Opportunities.SaveStageAsync(scope.OpenStage.Id,
                new("Updated stage", 11, 35, false, false, operation != "stage-deactivate", stageStamp), token))),
            "activity-create-lead" or "activity-create-opportunity" => new("crm.activity.created", token => Box(scope.Activities.CreateAsync(
                new(operation == "activity-create-lead" ? scope.SeedLead.Id : null, operation == "activity-create-opportunity" ? scope.SeedOpportunity.Id : null,
                    ActivityType.Call, "Created activity", "Activity notes", FixedClock.Now.AddDays(1), scope.UserId), token))),
            "activity-complete" or "activity-reopen" => new(operation == "activity-complete" ? "crm.activity.completed" : "crm.activity.reopened",
                token => Box(scope.Activities.SetCompletedAsync(scope.SeedActivity.Id, activityStamp, operation == "activity-complete", token))),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    private static Task<object> VersionedMutation(TestScope scope, string family, string stamp) => family switch
    {
        "lead" => Box(scope.Leads.UpdateAsync(scope.SeedLead.Id, LeadRequest(scope, "Retried lead", stamp), CancellationToken.None)),
        "opportunity" => Box(scope.Opportunities.UpdateAsync(scope.SeedOpportunity.Id, OpportunityRequest(scope, "Retried opportunity", stamp), CancellationToken.None)),
        "stage" => Box(scope.Opportunities.SaveStageAsync(scope.OpenStage.Id, new("Retried stage", 11, 30, false, false, ConcurrencyStamp: stamp), CancellationToken.None)),
        _ => Box(scope.Activities.SetCompletedAsync(scope.SeedActivity.Id, stamp, true, CancellationToken.None))
    };

    private static void AssertMutationOutcome(TestScope scope, string operation, object result)
    {
        switch (result)
        {
            case LeadDto lead:
                Assert.Equal(scope.ContactId, lead.ContactId);
                if (operation is "lead-create" or "lead-update")
                {
                    Assert.Equal(operation == "lead-create" ? "Created lead" : "Updated lead", lead.Title);
                    Assert.Equal("Atomic customer", lead.ContactName);
                    Assert.Equal(123.4567m, lead.ExpectedRevenue);
                    Assert.Equal(45, lead.Probability);
                    Assert.Equal("sales@example.test", lead.Email);
                }
                if (operation == "lead-qualify") Assert.Equal(LeadState.Qualified, lead.State);
                if (operation == "lead-lose") { Assert.Equal(LeadState.Lost, lead.State); Assert.Equal("Lost sale", lead.LostReason); }
                if (operation == "lead-reopen") { Assert.Equal(LeadState.New, lead.State); Assert.Null(lead.LostReason); }
                if (operation == "lead-assign") Assert.Null(lead.AssignedUserId);
                if (operation is "lead-archive" or "lead-reactivate") Assert.Equal(operation == "lead-archive", lead.IsArchived);
                Assert.NotEqual(scope.SeedLead.ConcurrencyStamp, lead.ConcurrencyStamp);
                break;
            case OpportunityDto opportunity:
                Assert.Equal(scope.ContactId, opportunity.ContactId);
                if (operation is "opportunity-create" or "opportunity-update")
                {
                    Assert.Equal(operation == "opportunity-create" ? "Created opportunity" : "Updated opportunity", opportunity.Title);
                    Assert.Equal("Atomic customer", opportunity.ContactName);
                    Assert.Equal(234.5678m, opportunity.ExpectedRevenue);
                    Assert.Equal(25, opportunity.Probability);
                }
                if (operation is "opportunity-move" or "opportunity-reopen")
                {
                    Assert.Equal(scope.NextStage.Id, opportunity.StageId); Assert.Equal(OpportunityState.Open, opportunity.State);
                    Assert.Equal(70, opportunity.Probability); Assert.Null(opportunity.LostReason);
                }
                if (operation == "opportunity-win") { Assert.Equal(OpportunityState.Won, opportunity.State); Assert.Equal(100, opportunity.Probability); }
                if (operation == "opportunity-lose")
                {
                    Assert.Equal(OpportunityState.Lost, opportunity.State); Assert.Equal(0, opportunity.Probability);
                    Assert.Equal("Lost sale", opportunity.LostReason);
                }
                if (operation == "opportunity-assign") Assert.Null(opportunity.AssignedUserId);
                if (operation is "opportunity-archive" or "opportunity-reactivate") Assert.Equal(operation == "opportunity-archive", opportunity.IsArchived);
                Assert.NotEqual(scope.SeedOpportunity.ConcurrencyStamp, opportunity.ConcurrencyStamp);
                break;
            case PipelineStageDto stage:
                Assert.Equal(operation == "stage-create" ? "Created stage" : "Updated stage", stage.Name);
                Assert.Equal(operation != "stage-deactivate", stage.IsActive);
                Assert.NotEqual(scope.OpenStage.ConcurrencyStamp, stage.ConcurrencyStamp);
                break;
            case ActivityDto activity:
                if (operation is "activity-create-lead" or "activity-create-opportunity")
                {
                    Assert.Equal("Created activity", activity.Subject);
                    Assert.Equal(ActivityType.Call, activity.Type);
                    Assert.Equal(scope.UserId, activity.AssignedUserId);
                    Assert.Equal(operation == "activity-create-lead" ? scope.SeedLead.Id : (Guid?)null, activity.LeadId);
                    Assert.Equal(operation == "activity-create-opportunity" ? scope.SeedOpportunity.Id : (Guid?)null, activity.OpportunityId);
                }
                if (operation == "activity-complete") Assert.Equal(FixedClock.Now, activity.CompletedAtUtc);
                if (operation == "activity-reopen") Assert.Null(activity.CompletedAtUtc);
                Assert.NotEqual(scope.SeedActivity.ConcurrencyStamp, activity.ConcurrencyStamp);
                break;
        }
    }

    private static async Task<object> Box<T>(Task<T> task) => (await task)!;
    private static LeadRequest LeadRequest(TestScope scope, string title, string? stamp = null) =>
        new(title, scope.ContactId, "sales@example.test", "555-0100", "Referral", 123.4567m, 45, null,
            new DateOnly(2026, 12, 1), "Lead notes", stamp);
    private static OpportunityRequest OpportunityRequest(TestScope scope, string title, string? stamp = null) =>
        new(title, scope.ContactId, scope.OpenStage.Id, 234.5678m, null, new DateOnly(2026, 12, 2), "Opportunity notes", stamp);

    private static async Task AssertDurableResult(TestScope scope, object result)
    {
        await using var reader = scope.ReadCrm();
        switch (result)
        {
            case LeadDto expected:
                var lead = await reader.Leads.SingleAsync(item => item.Id == expected.Id);
                Assert.Equal(expected, new LeadDto(lead.Id, lead.Title, lead.ContactId, expected.ContactName, lead.Email, lead.Phone,
                    lead.Source, lead.ExpectedRevenue, lead.Probability, lead.AssignedUserId, lead.PlannedCloseDate, lead.State,
                    lead.LostReason, lead.Notes, lead.IsArchived, lead.CreatedAtUtc, lead.UpdatedAtUtc, lead.ConcurrencyStamp));
                break;
            case OpportunityDto expected:
                var opportunity = await reader.Opportunities.SingleAsync(item => item.Id == expected.Id);
                var stage = await reader.PipelineStages.SingleAsync(item => item.Id == opportunity.StageId);
                Assert.Equal(expected, new OpportunityDto(opportunity.Id, opportunity.SourceLeadId, opportunity.ContactId, expected.ContactName,
                    stage.Id, stage.Name, opportunity.Title, opportunity.ExpectedRevenue, opportunity.Probability, opportunity.AssignedUserId,
                    opportunity.PlannedCloseDate, opportunity.State, opportunity.LostReason, opportunity.Notes, opportunity.IsArchived,
                    opportunity.CreatedAtUtc, opportunity.UpdatedAtUtc, opportunity.ConcurrencyStamp));
                break;
            case PipelineStageDto expected:
                var pipeline = await reader.PipelineStages.SingleAsync(item => item.Id == expected.Id);
                Assert.Equal(expected, new PipelineStageDto(pipeline.Id, pipeline.Name, pipeline.Sequence, pipeline.DefaultProbability,
                    pipeline.IsWon, pipeline.IsLost, pipeline.IsActive, pipeline.ConcurrencyStamp));
                break;
            case ActivityDto expected:
                var activity = await reader.Activities.SingleAsync(item => item.Id == expected.Id);
                Assert.Equal(expected, new ActivityDto(activity.Id, activity.LeadId, activity.OpportunityId, activity.Type,
                    activity.Subject, activity.Details, activity.DueAtUtc, activity.CompletedAtUtc, activity.AssignedUserId,
                    activity.CreatedAtUtc, activity.ConcurrencyStamp));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(result));
        }
    }

    private static async Task<string> Snapshot(TestScope scope)
    {
        await using var reader = scope.ReadCrm();
        return JsonSerializer.Serialize(new
        {
            Leads = await reader.Leads.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            Opportunities = await reader.Opportunities.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            Stages = await reader.PipelineStages.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            Activities = await reader.Activities.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync()
        });
    }

    private static async Task AssertPlatformCounts(TestScope scope, int audits, int events)
    {
        await using var reader = scope.ReadPlatform();
        Assert.Equal(audits, await reader.AuditLogs.CountAsync(item => item.CompanyId == scope.CompanyId));
        Assert.Equal(events, await reader.OutboxMessages.CountAsync(item => item.CompanyId == scope.CompanyId));
    }

    private static void AssertClean(TestScope scope)
    {
        Assert.Empty(scope.Crm.ChangeTracker.Entries());
        Assert.Empty(scope.Platform.ChangeTracker.Entries());
        Assert.Null(scope.Crm.Database.CurrentTransaction);
        Assert.Null(scope.Platform.Database.CurrentTransaction);
    }

    private static Contact NewContact(Guid tenantId, Guid companyId) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        CompanyId = companyId,
        Kind = ContactKind.Organization,
        DisplayName = "Atomic customer",
        OrganizationName = "Atomic customer",
        IsCustomer = true,
        CreatedAtUtc = FixedClock.Now,
        UpdatedAtUtc = FixedClock.Now,
        ConcurrencyStamp = Guid.NewGuid().ToString("N")
    };

    private static Lead NewLead(Guid tenantId, Guid companyId, Guid contactId) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        CompanyId = companyId,
        ContactId = contactId,
        Title = "Original lead",
        ExpectedRevenue = 100.1234m,
        Probability = 40,
        State = LeadState.New,
        Notes = "Original notes",
        CreatedAtUtc = FixedClock.Now,
        UpdatedAtUtc = FixedClock.Now,
        ConcurrencyStamp = Guid.NewGuid().ToString("N")
    };

    private sealed class TestScope(ServiceProvider provider, AsyncServiceScope services, string connectionString,
        Guid tenantId, Guid companyId, Guid userId) : IAsyncDisposable
    {
        public Guid TenantId { get; } = tenantId;
        public Guid CompanyId { get; } = companyId;
        public Guid UserId { get; } = userId;
        public Guid ContactId { get; private set; }
        public Lead SeedLead { get; private set; } = null!;
        public Opportunity SeedOpportunity { get; private set; } = null!;
        public CrmActivity SeedActivity { get; private set; } = null!;
        public PipelineStage OpenStage { get; private set; } = null!;
        public PipelineStage NextStage { get; private set; } = null!;
        public PipelineStage WonStage { get; private set; } = null!;
        public PipelineStage LostStage { get; private set; } = null!;
        public IRelationalDatabaseSession Session => services.ServiceProvider.GetRequiredService<IRelationalDatabaseSession>();
        public PlatformDbContext Platform => services.ServiceProvider.GetRequiredService<PlatformDbContext>();
        public CrmDbContext Crm => services.ServiceProvider.GetRequiredService<CrmDbContext>();
        public ContactsDbContext Contacts => services.ServiceProvider.GetRequiredService<ContactsDbContext>();
        public ILeadService Leads => services.ServiceProvider.GetRequiredService<ILeadService>();
        public IOpportunityService Opportunities => services.ServiceProvider.GetRequiredService<IOpportunityService>();
        public IActivityService Activities => services.ServiceProvider.GetRequiredService<IActivityService>();
        public ICompanyUserDirectory Users => services.ServiceProvider.GetRequiredService<ICompanyUserDirectory>();
        public IContactService ContactService => services.ServiceProvider.GetRequiredService<IContactService>();
        public IBusinessTransactionCoordinator Transactions => services.ServiceProvider.GetRequiredService<IBusinessTransactionCoordinator>();
        public InjectingAudit Audit => services.ServiceProvider.GetRequiredService<InjectingAudit>();
        public IOutboxWriter Outbox => services.ServiceProvider.GetRequiredService<IOutboxWriter>();
        public OneShotSaveHook SaveHook => services.ServiceProvider.GetRequiredService<OneShotSaveHook>();
        public TestEvent NewEvent() => new(Guid.NewGuid(), services.ServiceProvider.GetRequiredService<IClock>().UtcNow);
        public PlatformDbContext ReadPlatform() => new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId));
        public CrmDbContext ReadCrm() => new(new DbContextOptionsBuilder<CrmDbContext>().UseNpgsql(connectionString).Options,
            new TestTenant(TenantId), new TestCompany(CompanyId));
        public ContactsDbContext ReadContacts() => new(new DbContextOptionsBuilder<ContactsDbContext>().UseNpgsql(connectionString).Options,
            new TestTenant(TenantId), new TestCompany(CompanyId));

        public static async Task<TestScope> CreateAsync(string connectionString, Guid? existingTenantId = null)
        {
            var tenantId = existingTenantId ?? Guid.NewGuid();
            var companyId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var registry = new IntegrationEventRegistry();
            // Only the test stages this event. CRM does not gain a production event contract.
            registry.Add<TestEvent>("tests.crm.atomicity.v1");
            var registrations = new ServiceCollection();
            registrations.AddSingleton<ITenantContext>(new TestTenant(tenantId));
            registrations.AddSingleton<ICompanyContext>(new TestCompany(companyId));
            registrations.AddSingleton<IClock>(new FixedClock());
            registrations.AddSingleton<IIntegrationEventRegistry>(registry);
            registrations.AddSingleton<IHttpContextAccessor>(new TestHttpContextAccessor
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")),
                    TraceIdentifier = "crm-atomicity-test"
                }
            });
            registrations.AddScoped<IRelationalDatabaseSession>(_ => new RelationalDatabaseSession(connectionString));
            registrations.AddDbContext<PlatformDbContext>((provider, options) => options.UseNpgsql(provider.GetRequiredService<IRelationalDatabaseSession>().Connection));
            var configuration = new ConfigurationBuilder().Build();
            new ContactsModule().ConfigureServices(registrations, configuration);
            new CrmModule().ConfigureServices(registrations, configuration);
            registrations.AddScoped<OneShotSaveHook>();
            registrations.AddDbContext<CrmDbContext>((provider, options) => options.AddInterceptors(provider.GetRequiredService<OneShotSaveHook>()));
            registrations.AddScoped<ICompanyUserDirectory, CompanyUserDirectory>();
            registrations.AddScoped<IBusinessTransactionCoordinator, BusinessTransactionCoordinator>();
            registrations.AddScoped<AuditLogService>();
            registrations.AddScoped<IOutboxWriter, PostgresOutboxWriter>();
            registrations.AddScoped<InjectingAudit>();
            registrations.AddScoped<IAuditLogWriter>(provider => provider.GetRequiredService<InjectingAudit>());
            var provider = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            var services = provider.CreateAsyncScope();
            var scope = new TestScope(provider, services, connectionString, tenantId, companyId, userId);
            try
            {
                if (existingTenantId is null)
                    scope.Platform.Tenants.Add(new Tenant { Id = tenantId, Name = "CRM atomicity", Slug = $"atomic-{tenantId:N}", TimeZoneId = "UTC", IsActive = true, CreatedAtUtc = FixedClock.Now });
                scope.Platform.Companies.Add(new Company { Id = companyId, TenantId = tenantId, Code = companyId.ToString("N")[..20], Name = "CRM atomicity", BaseCurrencyCode = "USD", TimeZoneId = "UTC", IsActive = true });
                scope.Platform.Users.Add(new ApplicationUser { Id = userId, UserName = $"atomic-{userId:N}", NormalizedUserName = $"ATOMIC-{userId:N}", Email = $"{userId:N}@example.test", CreatedAtUtc = FixedClock.Now });
                scope.Platform.UserCompanyMemberships.Add(new UserCompanyMembership { Id = Guid.NewGuid(), TenantId = tenantId, CompanyId = companyId, UserId = userId, IsDefault = true });
                await scope.Platform.SaveChangesAsync();
                scope.Platform.ChangeTracker.Clear();
                var contact = NewContact(tenantId, companyId);
                scope.ContactId = contact.Id;
                scope.Contacts.Contacts.Add(contact);
                await scope.Contacts.SaveChangesAsync();
                scope.Contacts.ChangeTracker.Clear();
                scope.OpenStage = Stage("Open", 10, 25);
                scope.NextStage = Stage("Negotiation", 20, 70);
                scope.WonStage = Stage("Won", 30, 100, won: true);
                scope.LostStage = Stage("Lost", 40, 0, lost: true);
                scope.Crm.PipelineStages.AddRange(scope.OpenStage, scope.NextStage, scope.WonStage, scope.LostStage);
                scope.SeedLead = NewLead(tenantId, companyId, contact.Id);
                scope.SeedLead.AssignedUserId = userId;
                scope.SeedLead.CreatedBy = userId;
                scope.SeedLead.UpdatedBy = userId;
                scope.SeedOpportunity = new Opportunity
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    CompanyId = companyId,
                    ContactId = contact.Id,
                    StageId = scope.OpenStage.Id,
                    Title = "Original opportunity",
                    ExpectedRevenue = 200.1234m,
                    Probability = 25,
                    AssignedUserId = userId,
                    State = OpportunityState.Open,
                    CreatedAtUtc = FixedClock.Now,
                    UpdatedAtUtc = FixedClock.Now,
                    CreatedBy = userId,
                    UpdatedBy = userId,
                    ConcurrencyStamp = Guid.NewGuid().ToString("N")
                };
                scope.SeedActivity = new CrmActivity
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    CompanyId = companyId,
                    LeadId = scope.SeedLead.Id,
                    Type = ActivityType.Task,
                    Subject = "Original activity",
                    AssignedUserId = userId,
                    CreatedAtUtc = FixedClock.Now,
                    CreatedBy = userId,
                    ConcurrencyStamp = Guid.NewGuid().ToString("N")
                };
                scope.Crm.Leads.Add(scope.SeedLead);
                scope.Crm.Opportunities.Add(scope.SeedOpportunity);
                scope.Crm.Activities.Add(scope.SeedActivity);
                await scope.Crm.SaveChangesAsync();
                scope.Crm.ChangeTracker.Clear();
                return scope;

                PipelineStage Stage(string name, int sequence, int probability, bool won = false, bool lost = false) => new()
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    CompanyId = companyId,
                    Name = name,
                    Sequence = sequence,
                    DefaultProbability = probability,
                    IsWon = won,
                    IsLost = lost,
                    IsActive = true,
                    ConcurrencyStamp = Guid.NewGuid().ToString("N")
                };
            }
            catch
            {
                await scope.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

    private sealed class InjectingAudit(AuditLogService realAudit, IOutboxWriter outbox) : IAuditLogWriter
    {
        public int FlushCalls { get; private set; }
        public int? FailOnFlush { get; set; }
        public Action<int>? AfterFlush { get; set; }
        public Queue<TestEvent> PendingEvents { get; } = new();
        public void Append(AuditWriteEntry entry) => realAudit.Append(entry);

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCalls++;
            if (PendingEvents.TryDequeue(out var message)) outbox.Add(message);
            await realAudit.FlushAsync(cancellationToken);
            AfterFlush?.Invoke(FlushCalls);
            if (FailOnFlush == FlushCalls) { FailOnFlush = null; throw new InjectedAuditException(); }
        }
    }

    private sealed class OneShotSaveHook : SaveChangesInterceptor
    {
        public Func<CrmDbContext, CancellationToken, Task>? BeforeSave { get; set; }
        public Exception? LastFailure { get; private set; }
        public bool Executed { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (BeforeSave is { } callback)
            {
                BeforeSave = null;
                Executed = true;
                await callback((CrmDbContext)eventData.Context!, cancellationToken);
            }
            return result;
        }

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            LastFailure = eventData.Exception;
            return Task.CompletedTask;
        }

        public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(ConcurrencyExceptionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            LastFailure = eventData.Exception;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class InjectedAuditException : Exception;
    private sealed record TestEvent(Guid EventId, DateTimeOffset OccurredAtUtc) : IIntegrationEvent;
    private sealed class TestHttpContextAccessor : IHttpContextAccessor { public HttpContext? HttpContext { get; set; } }
    private sealed class TestTenant(Guid id) : ITenantContext { public TenantId? TenantId { get; } = new(id); }
    private sealed class TestCompany(Guid id) : ICompanyContext { public CompanyId? CompanyId { get; } = new(id); }
    private sealed class FixedClock : IClock
    {
        public static DateTimeOffset Now { get; } = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => Now;
    }
}

public sealed class AtomicCrmPostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _postgres = await IntegrationContainers.StartPostgresAsync("crm_atomicity_tests");
        ConnectionString = _postgres.GetConnectionString();
        await using var platform = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(ConnectionString).Options);
        await platform.Database.MigrateAsync();
        await using var contacts = new ContactsDbContext(new DbContextOptionsBuilder<ContactsDbContext>()
            .UseNpgsql(ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "contacts")).Options);
        await contacts.Database.MigrateAsync();
        await using var crm = new CrmDbContext(new DbContextOptionsBuilder<CrmDbContext>()
            .UseNpgsql(ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "crm")).Options);
        await crm.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }
}
