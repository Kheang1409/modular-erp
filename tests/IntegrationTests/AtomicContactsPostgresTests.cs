using System.Security.Claims;
using System.Data.Common;
using System.Text;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Events;
using Erp.Abstractions.Persistence;
using Erp.Infrastructure.Auditing;
using Erp.Infrastructure.Events;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Sequences;
using Erp.ModuleSystem;
using Erp.Modules.Contacts;
using Erp.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Erp.IntegrationTests;

public sealed class AtomicContactsPostgresTests(AtomicContactsPostgresFixture fixture)
    : IClassFixture<AtomicContactsPostgresFixture>
{
    [Fact]
    public async Task ContactAuditAndPendingOutboxCommitTogetherOnOneScopedConnection()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var message = scope.NewEvent();
        scope.Audit.PendingEvent = message;
        Assert.Same(scope.Session.Connection, scope.Platform.Database.GetDbConnection());
        Assert.Same(scope.Session.Connection, scope.Contacts.Database.GetDbConnection());

        var contact = await scope.Service.CreateAsync(Request("First customer"), CancellationToken.None);

        await using var contacts = scope.ReadContacts();
        Assert.Equal(contact.Id, await contacts.Contacts.Select(value => value.Id).SingleAsync());
        Assert.Equal(2, await contacts.ContactEmails.CountAsync());
        await using var platform = scope.ReadPlatform();
        var audit = await platform.AuditLogs.SingleAsync();
        Assert.Equal("contacts.created", audit.Action);
        Assert.Equal(contact.Id.ToString(), audit.EntityId);
        Assert.Equal(scope.CompanyId, audit.CompanyId);
        Assert.Equal(scope.UserId, audit.UserId);
        var persistedMessage = await platform.OutboxMessages.SingleAsync();
        Assert.Equal(message.EventId, persistedMessage.Id);
        Assert.Equal(scope.TenantId, persistedMessage.TenantId);
        Assert.Equal(scope.CompanyId, persistedMessage.CompanyId);
    }

    [Fact]
    public async Task FailureAfterRealPlatformFlushRollsBackAndRetryDoesNotResurrectOldRows()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var failedMessage = scope.NewEvent();
        scope.Audit.PendingEvent = failedMessage;
        scope.Audit.FailAfterFlush = true;

        await Assert.ThrowsAsync<InjectedAuditException>(() => scope.Service.CreateAsync(Request("Rolled back"), CancellationToken.None));

        await AssertCounts(scope, contacts: 0, audits: 0, events: 0);
        Assert.Empty(scope.Contacts.ChangeTracker.Entries());
        Assert.Empty(scope.Platform.ChangeTracker.Entries());
        Assert.Null(scope.Contacts.Database.CurrentTransaction);
        Assert.Null(scope.Platform.Database.CurrentTransaction);

        var successfulMessage = scope.NewEvent();
        scope.Audit.PendingEvent = successfulMessage;
        var contact = await scope.Service.CreateAsync(Request("Retried customer"), CancellationToken.None);
        await AssertCounts(scope, contacts: 1, audits: 1, events: 1);
        await using var verify = scope.ReadPlatform();
        Assert.Equal(successfulMessage.EventId, await verify.OutboxMessages.Select(value => value.Id).SingleAsync());
        Assert.False(await verify.OutboxMessages.AnyAsync(value => value.Id == failedMessage.EventId));
        Assert.Equal(contact.Id.ToString(), await verify.AuditLogs.Select(value => value.EntityId).SingleAsync());
    }

    [Fact]
    public async Task BusinessConstraintFailureBeforeAuditFlushClearsPendingRowsAndAllowsValidRetry()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var failedMessage = scope.NewEvent();
        var invalid = Request("Duplicate communications") with
        {
            EmailAddresses = [new("billing@example.test", true), new("BILLING@example.test")]
        };
        await using (await scope.Transactions.BeginAsync(scope.Contacts, CancellationToken.None))
        {
            scope.Outbox.Add(failedMessage);
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => scope.Service.CreateAsync(invalid, CancellationToken.None));
            Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, Assert.IsType<Npgsql.PostgresException>(failure.InnerException).SqlState);
        }
        Assert.Equal(0, scope.Audit.FlushCalls);
        await AssertCounts(scope, contacts: 0, audits: 0, events: 0);
        await using (var verify = scope.ReadContacts())
        {
            Assert.Empty(await verify.ContactEmails.ToListAsync());
            Assert.Empty(await verify.ContactPhones.ToListAsync());
        }
        Assert.Empty(scope.Contacts.ChangeTracker.Entries());
        Assert.Empty(scope.Platform.ChangeTracker.Entries());
        Assert.Null(scope.Contacts.Database.CurrentTransaction);
        Assert.Null(scope.Platform.Database.CurrentTransaction);

        await scope.Service.CreateAsync(Request("Valid independent retry"), CancellationToken.None);
        await AssertCounts(scope, contacts: 1, audits: 1, events: 0);
        Assert.Equal(1, scope.Audit.FlushCalls);
    }

    [Fact]
    public async Task FailedUpdateRestoresProfileAddressesAndCommunicationChildren()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var original = await scope.Service.CreateAsync(Request("Original customer"), CancellationToken.None);
        var update = Request("Changed customer") with
        {
            ConcurrencyStamp = original.ConcurrencyStamp,
            Email = "changed@example.test",
            Phone = "+85599999999",
            BillingAddress = new("Changed street", null, "Changed city", null, null, "KH"),
            EmailAddresses = [new("changed@example.test", true)],
            PhoneNumbers = [new("+85599999999", true)]
        };
        scope.Audit.FailAfterFlush = true;

        await Assert.ThrowsAsync<InjectedAuditException>(() => scope.Service.UpdateAsync(original.Id, update, CancellationToken.None));

        await using (var verify = scope.ReadContacts())
        {
            var restored = await verify.Contacts.Include(value => value.EmailAddresses).Include(value => value.PhoneNumbers).SingleAsync();
            Assert.Equal(original.DisplayName, restored.DisplayName);
            Assert.Equal(original.ConcurrencyStamp, restored.ConcurrencyStamp);
            Assert.Equal("Original street", restored.BillingAddress.Line1);
            Assert.Equal(["billing@example.test", "orders@example.test"], restored.EmailAddresses.Select(value => value.Address).Order().ToArray());
            Assert.Equal("+85512345678", Assert.Single(restored.PhoneNumbers).Number);
        }
        await AssertCounts(scope, contacts: 1, audits: 1, events: 0);

        var changed = await scope.Service.UpdateAsync(original.Id, update, CancellationToken.None);
        Assert.Equal("Changed customer", changed.DisplayName);
        Assert.Equal("changed@example.test", Assert.Single(changed.EmailAddresses).Address);
        await AssertCounts(scope, contacts: 1, audits: 2, events: 0);
    }

    [Fact]
    public async Task FailedArchiveLeavesOriginalVersionActiveAndCanBeRetried()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var original = await scope.Service.CreateAsync(Request("Active customer"), CancellationToken.None);
        scope.Audit.FailAfterFlush = true;

        await Assert.ThrowsAsync<InjectedAuditException>(() => scope.Service.SetArchivedAsync(original.Id, original.ConcurrencyStamp, true, CancellationToken.None));

        await using (var verify = scope.ReadContacts())
        {
            var restored = await verify.Contacts.SingleAsync();
            Assert.False(restored.IsArchived);
            Assert.Equal(original.ConcurrencyStamp, restored.ConcurrencyStamp);
        }
        await AssertCounts(scope, contacts: 1, audits: 1, events: 0);
        var archived = await scope.Service.SetArchivedAsync(original.Id, original.ConcurrencyStamp, true, CancellationToken.None);
        Assert.True(archived.IsArchived);
        await AssertCounts(scope, contacts: 1, audits: 2, events: 0);
    }

    [Fact]
    public async Task CancellationAfterPlatformFlushRollsBackAndUncancelledOperationStillWorks()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        using var cancellation = new CancellationTokenSource();
        scope.Audit.PendingEvent = scope.NewEvent();
        scope.Audit.AfterFlush = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Service.CreateAsync(Request("Cancelled customer"), cancellation.Token));

        await AssertCounts(scope, contacts: 0, audits: 0, events: 0);
        Assert.Null(scope.Contacts.Database.CurrentTransaction);
        Assert.Null(scope.Platform.Database.CurrentTransaction);
        await scope.Service.CreateAsync(Request("Next customer"), CancellationToken.None);
        await AssertCounts(scope, contacts: 1, audits: 1, events: 0);
    }

    [Fact]
    public async Task NestedServiceCommitDoesNotEscapeAnAbandonedOuterTransaction()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        await using (await scope.Transactions.BeginAsync(scope.Contacts, CancellationToken.None))
        {
            scope.Audit.PendingEvent = scope.NewEvent();
            await scope.Service.CreateAsync(Request("Outer rolled back"), CancellationToken.None);
            // A completed inner command remains invisible to independent readers until its root commits.
            await AssertCounts(scope, contacts: 0, audits: 0, events: 0);
        }
        await AssertCounts(scope, contacts: 0, audits: 0, events: 0);

        await using (var committed = await scope.Transactions.BeginAsync(scope.Contacts, CancellationToken.None))
        {
            scope.Audit.PendingEvent = scope.NewEvent();
            await scope.Service.CreateAsync(Request("Outer committed"), CancellationToken.None);
            await committed.CommitAsync(CancellationToken.None);
        }
        await AssertCounts(scope, contacts: 1, audits: 1, events: 1);
    }

    [Fact]
    public async Task IncompleteInnerScopePreventsOuterCommit()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        await using (var outer = await scope.Transactions.BeginAsync(scope.Contacts, CancellationToken.None))
        {
            await scope.Service.CreateAsync(Request("Rollback-only customer"), CancellationToken.None);
            await using (await scope.Transactions.BeginAsync(scope.Contacts, CancellationToken.None)) { }
            await Assert.ThrowsAsync<InvalidOperationException>(() => outer.CommitAsync(CancellationToken.None));
        }
        await AssertCounts(scope, contacts: 0, audits: 0, events: 0);
    }

    [Fact]
    public async Task OutOfOrderNestedDisposalRollsBackAndDoesNotPoisonTheNextOperation()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        await using var outer = await scope.Transactions.BeginAsync(scope.Contacts, CancellationToken.None);
        await using var middle = await scope.Transactions.BeginAsync(scope.Contacts, CancellationToken.None);
        await using var inner = await scope.Transactions.BeginAsync(scope.Contacts, CancellationToken.None);
        scope.Audit.PendingEvent = scope.NewEvent();
        await scope.Service.CreateAsync(Request("Out-of-order rollback"), CancellationToken.None);

        await middle.DisposeAsync();

        await AssertCounts(scope, contacts: 0, audits: 0, events: 0);
        Assert.Empty(scope.Contacts.ChangeTracker.Entries());
        Assert.Empty(scope.Platform.ChangeTracker.Entries());
        Assert.Null(scope.Contacts.Database.CurrentTransaction);
        Assert.Null(scope.Platform.Database.CurrentTransaction);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => inner.CommitAsync(CancellationToken.None));

        await scope.Service.CreateAsync(Request("Next independent customer"), CancellationToken.None);
        await AssertCounts(scope, contacts: 1, audits: 1, events: 0);
    }

    [Fact]
    public async Task ParticipantOnAnotherConnectionIsRejectedWithoutWriting()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        await using var independent = scope.ReadContacts();
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Transactions.BeginAsync(independent, CancellationToken.None));
        await AssertCounts(scope, contacts: 0, audits: 0, events: 0);
    }

    [Fact]
    public async Task RootRejectsUnrelatedPendingChangesWithoutClearingThemButNestedOwnedChangesAreAllowed()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var pendingAudit = new AuditLog
        {
            Id = Guid.NewGuid(),
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            Action = "contacts.pending.acceptance",
            EntityType = "Contact",
            EntityId = "unrelated-pending",
            TimestampUtc = FixedClock.Now,
            CorrelationId = "atomicity-test"
        };
        scope.Platform.AuditLogs.Add(pendingAudit);

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Transactions.BeginAsync(scope.Contacts, CancellationToken.None));

        Assert.Equal(EntityState.Added, scope.Platform.Entry(pendingAudit).State);
        Assert.Null(scope.Platform.Database.CurrentTransaction);
        Assert.Null(scope.Contacts.Database.CurrentTransaction);
        await AssertCounts(scope, contacts: 0, audits: 0, events: 0);
        scope.Platform.ChangeTracker.Clear();

        var pendingContact = new Contact
        {
            Id = Guid.NewGuid(),
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            Kind = ContactKind.Organization,
            DisplayName = "Outer-owned customer",
            OrganizationName = "Outer-owned customer",
            IsCustomer = true,
            CreatedAtUtc = FixedClock.Now,
            UpdatedAtUtc = FixedClock.Now,
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };
        scope.Contacts.Contacts.Add(pendingContact);

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Transactions.BeginAsync(scope.Contacts, CancellationToken.None));

        Assert.Equal(EntityState.Added, scope.Contacts.Entry(pendingContact).State);
        Assert.Null(scope.Platform.Database.CurrentTransaction);
        Assert.Null(scope.Contacts.Database.CurrentTransaction);
        await AssertCounts(scope, contacts: 0, audits: 0, events: 0);
        scope.Contacts.ChangeTracker.Clear();

        await using (var outer = await scope.Transactions.BeginAsync(scope.Contacts, CancellationToken.None))
        {
            scope.Contacts.Contacts.Add(pendingContact);
            scope.Outbox.Add(scope.NewEvent());
            await scope.Service.CreateAsync(Request("Nested customer"), CancellationToken.None);
            await outer.CommitAsync(CancellationToken.None);
        }
        await AssertCounts(scope, contacts: 2, audits: 1, events: 1);
    }

    [Fact]
    public async Task CleanupFailurePermanentlyFaultsTheCoordinatorEvenAfterTrackersAreCleared()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var participant = new FailingCleanupParticipant(scope.Contacts);
        await using var outer = await scope.Transactions.BeginAsync(participant, CancellationToken.None);
        scope.Audit.PendingEvent = scope.NewEvent();
        await scope.Service.CreateAsync(Request("Cleanup failure customer"), CancellationToken.None);

        var cleanup = await Assert.ThrowsAsync<AggregateException>(() => outer.DisposeAsync().AsTask());

        Assert.IsType<InvalidOperationException>(Assert.Single(cleanup.InnerExceptions));
        await AssertCounts(scope, contacts: 0, audits: 0, events: 0);
        Assert.Empty(scope.Platform.ChangeTracker.Entries());
        Assert.Empty(scope.Contacts.ChangeTracker.Entries());
        Assert.Null(scope.Platform.Database.CurrentTransaction);
        Assert.Null(scope.Contacts.Database.CurrentTransaction);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Transactions.BeginAsync(scope.Contacts, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Service.CreateAsync(Request("Rejected poisoned scope"), CancellationToken.None));
        await AssertCounts(scope, contacts: 0, audits: 0, events: 0);
    }

    [Fact]
    public async Task UnmanagedModuleTransactionIsRejectedWithoutTakingOwnership()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        await using var unmanaged = await scope.Contacts.Database.BeginTransactionAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Transactions.BeginAsync(scope.Contacts, CancellationToken.None));

        Assert.Same(unmanaged, scope.Contacts.Database.CurrentTransaction);
        Assert.Null(scope.Platform.Database.CurrentTransaction);
        await scope.Contacts.Database.ExecuteSqlRawAsync("SELECT 1");
        await unmanaged.CommitAsync();
        await AssertCounts(scope, contacts: 0, audits: 0, events: 0);
    }

    [Fact]
    public async Task SequenceAllocationJoinsOuterRollbackAndStandaloneRetryStartsAtFirstNumber()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var sequence = new PostgresSequenceService(scope.Platform, new TestTenant(scope.TenantId), new TestCompany(scope.CompanyId));
        var request = new Erp.Abstractions.Sequences.SequenceRequest("contacts.atomicity", "ATOMIC-", 6, new(2026, 10, 16));
        await using (await scope.Transactions.BeginAsync(scope.Contacts, CancellationToken.None))
        {
            Assert.Equal("ATOMIC-000001", await sequence.NextAsync(request, CancellationToken.None));
        }
        await using (var rolledBack = scope.ReadPlatform()) Assert.Empty(await rolledBack.DocumentSequences.ToListAsync());

        Assert.Equal("ATOMIC-000001", await sequence.NextAsync(request, CancellationToken.None));
        await using var committed = scope.ReadPlatform();
        Assert.Equal(2, await committed.DocumentSequences.Select(value => value.NextValue).SingleAsync());
    }

    [Fact]
    public async Task CommitFlushesPendingPlatformRowsEvenWithoutAnExplicitAuditFlush()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        var message = scope.NewEvent();
        await using (var transaction = await scope.Transactions.BeginAsync(scope.Contacts, CancellationToken.None))
        {
            scope.Outbox.Add(message);
            scope.Platform.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                Action = "contacts.atomicity.acceptance",
                EntityType = "Contact",
                EntityId = "pending-test",
                TimestampUtc = FixedClock.Now,
                CorrelationId = "atomicity-test"
            });
            await transaction.CommitAsync(CancellationToken.None);
        }
        await AssertCounts(scope, contacts: 0, audits: 1, events: 1);
    }

    [Fact]
    public async Task CsvImportKeepsPerRowPartialSuccessAndAuditsOnlySuccessfulRows()
    {
        await using var scope = await TestScope.CreateAsync(fixture.ConnectionString);
        using var csv = new MemoryStream(Encoding.UTF8.GetBytes(
            "kind,displayName,organizationName,isCustomer,email\r\n" +
            "Organization,First,First,true,first@example.test\r\n" +
            "Unknown,Rejected,Rejected,true,rejected@example.test\r\n" +
            "Organization,Third,Third,true,third@example.test\r\n"));

        var result = await scope.Service.ImportCsvAsync(csv, CancellationToken.None);

        Assert.Equal(2, result.Imported);
        Assert.Single(result.Errors);
        await AssertCounts(scope, contacts: 2, audits: 2, events: 0);
        await using var verify = scope.ReadContacts();
        Assert.Equal(["First", "Third"], await verify.Contacts.OrderBy(value => value.DisplayName).Select(value => value.DisplayName).ToArrayAsync());
    }

    private static ContactRequest Request(string name) => new(
        ContactKind.Organization, name, null, null, name, "billing@example.test", "+85512345678", null, null,
        IsCustomer: true, BillingAddress: new("Original street", null, "Phnom Penh", null, null, "KH"),
        EmailAddresses: [new("billing@example.test", true), new("orders@example.test")],
        PhoneNumbers: [new("+85512345678", true)]);

    private static async Task AssertCounts(TestScope scope, int contacts, int audits, int events)
    {
        await using var contactReader = scope.ReadContacts();
        await using var platformReader = scope.ReadPlatform();
        Assert.Equal(contacts, await contactReader.Contacts.CountAsync());
        Assert.Equal(audits, await platformReader.AuditLogs.CountAsync());
        Assert.Equal(events, await platformReader.OutboxMessages.CountAsync());
    }

    private sealed class TestScope(ServiceProvider provider, AsyncServiceScope services, string connectionString,
        Guid tenantId, Guid companyId, Guid userId) : IAsyncDisposable
    {
        public Guid TenantId { get; } = tenantId;
        public Guid CompanyId { get; } = companyId;
        public Guid UserId { get; } = userId;
        public IRelationalDatabaseSession Session => services.ServiceProvider.GetRequiredService<IRelationalDatabaseSession>();
        public PlatformDbContext Platform => services.ServiceProvider.GetRequiredService<PlatformDbContext>();
        public ContactsDbContext Contacts => services.ServiceProvider.GetRequiredService<ContactsDbContext>();
        public IContactService Service => services.ServiceProvider.GetRequiredService<IContactService>();
        public IBusinessTransactionCoordinator Transactions => services.ServiceProvider.GetRequiredService<IBusinessTransactionCoordinator>();
        public InjectingAudit Audit => services.ServiceProvider.GetRequiredService<InjectingAudit>();
        public IOutboxWriter Outbox => services.ServiceProvider.GetRequiredService<IOutboxWriter>();

        public TestEvent NewEvent() => new(Guid.NewGuid(), services.ServiceProvider.GetRequiredService<IClock>().UtcNow);
        public PlatformDbContext ReadPlatform() => new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId));
        public ContactsDbContext ReadContacts() => new(new DbContextOptionsBuilder<ContactsDbContext>().UseNpgsql(connectionString).Options, new TestTenant(TenantId), new TestCompany(CompanyId));

        public static async Task<TestScope> CreateAsync(string connectionString)
        {
            var tenantId = Guid.NewGuid(); var companyId = Guid.NewGuid(); var userId = Guid.NewGuid();
            var registry = new IntegrationEventRegistry();
            registry.Add<TestEvent>("tests.contacts.atomicity.v1");
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
                    TraceIdentifier = "contacts-atomicity-test"
                }
            });
            registrations.AddScoped<IRelationalDatabaseSession>(_ => new RelationalDatabaseSession(connectionString));
            registrations.AddDbContext<PlatformDbContext>((services, options) => options.UseNpgsql(services.GetRequiredService<IRelationalDatabaseSession>().Connection));
            registrations.AddDbContext<ContactsDbContext>((services, options) => options.UseNpgsql(services.GetRequiredService<IRelationalDatabaseSession>().Connection));
            registrations.AddScoped<IBusinessTransactionCoordinator, BusinessTransactionCoordinator>();
            registrations.AddScoped<AuditLogService>();
            registrations.AddScoped<IOutboxWriter, PostgresOutboxWriter>();
            registrations.AddScoped<InjectingAudit>();
            registrations.AddScoped<IAuditLogWriter>(services => services.GetRequiredService<InjectingAudit>());
            registrations.AddScoped<IContactService, ContactService>();
            var provider = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            var services = provider.CreateAsyncScope();
            var scope = new TestScope(provider, services, connectionString, tenantId, companyId, userId);
            try
            {
                scope.Platform.Tenants.Add(new Tenant { Id = tenantId, Name = "Atomicity test", Slug = $"atomic-{tenantId:N}", TimeZoneId = "UTC", IsActive = true, CreatedAtUtc = FixedClock.Now });
                scope.Platform.Companies.Add(new Company { Id = companyId, TenantId = tenantId, Code = "MAIN", Name = "Atomicity test", BaseCurrencyCode = "USD", TimeZoneId = "UTC", IsActive = true });
                await scope.Platform.SaveChangesAsync();
                scope.Platform.ChangeTracker.Clear();
                return scope;
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
        public bool FailAfterFlush { get; set; }
        public Action? AfterFlush { get; set; }
        public TestEvent? PendingEvent { get; set; }

        public void Append(AuditWriteEntry entry) => realAudit.Append(entry);

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCalls++;
            if (PendingEvent is { } message) { PendingEvent = null; outbox.Add(message); }
            await realAudit.FlushAsync(cancellationToken);
            var afterFlush = AfterFlush; AfterFlush = null; afterFlush?.Invoke();
            if (FailAfterFlush) { FailAfterFlush = false; throw new InjectedAuditException(); }
        }
    }

    private sealed class InjectedAuditException : Exception;
    private sealed class FailingCleanupParticipant(ITransactionParticipant participant) : ITransactionParticipant
    {
        public DbConnection Connection => participant.Connection;
        public bool HasPendingChanges => participant.HasPendingChanges;
        public Task EnlistAsync(DbTransaction? transaction, CancellationToken cancellationToken) => participant.EnlistAsync(transaction, cancellationToken);
        public void DiscardChanges()
        {
            participant.DiscardChanges();
            throw new InvalidOperationException("Injected participant cleanup failure");
        }
    }
    private sealed record TestEvent(Guid EventId, DateTimeOffset OccurredAtUtc) : IIntegrationEvent;
    private sealed class TestHttpContextAccessor : IHttpContextAccessor { public HttpContext? HttpContext { get; set; } }
    private sealed class TestTenant(Guid id) : ITenantContext { public TenantId? TenantId { get; } = new(id); }
    private sealed class TestCompany(Guid id) : ICompanyContext { public CompanyId? CompanyId { get; } = new(id); }
    private sealed class FixedClock : IClock
    {
        public static DateTimeOffset Now { get; } = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => Now;
    }
}

public sealed class AtomicContactsPostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _postgres = await IntegrationContainers.StartPostgresAsync("contacts_atomicity_tests");
        ConnectionString = _postgres.GetConnectionString();
        await using var platform = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(ConnectionString).Options);
        await platform.Database.MigrateAsync();
        await using var contacts = new ContactsDbContext(new DbContextOptionsBuilder<ContactsDbContext>()
            .UseNpgsql(ConnectionString, postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", "contacts")).Options);
        await contacts.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }
}
