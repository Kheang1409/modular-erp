using Erp.Abstractions.Context;
using Erp.Abstractions.Auditing;
using Erp.Modules.Contacts;
using Erp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Microsoft.AspNetCore.Http;

namespace Erp.IntegrationTests;

public sealed class ContactsPostgresTests
{
    [Fact]
    public async Task ContactsAreCompanyIsolatedAndConcurrencyIsEnforced()
    {
        PostgreSqlContainer postgres;
        try
        {
            postgres = new PostgreSqlBuilder("postgres:18-alpine").WithDatabase("contacts_tests")
                .WithUsername("erp").WithPassword("integration_only_password").Build();
            await postgres.StartAsync(CancellationToken.None);
        }
        catch (Exception) when (!string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        await using (postgres)
        {
            var tenant = Guid.NewGuid(); var companyA = Guid.NewGuid(); var companyB = Guid.NewGuid();
            var options = new DbContextOptionsBuilder<ContactsDbContext>().UseNpgsql(postgres.GetConnectionString()).Options;
            await using (var migration = new ContactsDbContext(options)) await migration.Database.MigrateAsync();
            var contactId = Guid.NewGuid();
            await using (var write = new ContactsDbContext(options, new TenantContext(tenant), new CompanyContext(companyA)))
            {
                write.Contacts.Add(ContactFor(contactId, tenant, companyA));
                await write.SaveChangesAsync();
            }
            await using var visible = new ContactsDbContext(options, new TenantContext(tenant), new CompanyContext(companyA));
            await using var hidden = new ContactsDbContext(options, new TenantContext(tenant), new CompanyContext(companyB));
            Assert.Equal(contactId, await visible.Contacts.Select(x => x.Id).SingleAsync());
            Assert.Equal("billing@example.test", await visible.ContactEmails.Select(x => x.Address).SingleAsync());
            Assert.Empty(await hidden.Contacts.ToListAsync());
            Assert.Empty(await hidden.ContactEmails.ToListAsync());

            var audit = new RecordingAudit();
            await using (var serviceContext = new ContactsDbContext(options, new TenantContext(tenant), new CompanyContext(companyA)))
            {
                var service = new ContactService(serviceContext, new TenantContext(tenant), new CompanyContext(companyA),
                    new FixedClock(), audit, new HttpContextAccessor());
                var supplier = await service.CreateAsync(new(ContactKind.Organization, "Supplier One", null, null,
                    "Supplier One", "primary@supplier.test", null, null, null, IsSupplier: true,
                    EmailAddresses: [new("primary@supplier.test", true), new("orders@supplier.test")]), CancellationToken.None);
                Assert.True(supplier.IsSupplier);
                Assert.Equal(2, supplier.EmailAddresses.Count);
                Assert.Equal(supplier.Id, Assert.Single((await service.SearchAsync(new(IsSupplier: true), CancellationToken.None)).Items).Id);
                var changed = await service.UpdateAsync(supplier.Id, new(ContactKind.Organization, "Supplier Updated", null,
                    null, "Supplier One", supplier.Email, null, null, null, supplier.ConcurrencyStamp, IsSupplier: true), CancellationToken.None);
                var archived = await service.SetArchivedAsync(changed.Id, changed.ConcurrencyStamp, true, CancellationToken.None);
                Assert.True(archived.IsArchived);
                Assert.Equal(["contacts.created", "contacts.updated", "contacts.archived"], audit.Entries.Select(value => value.Action));
            }

            var first = await visible.Contacts.SingleAsync(value => value.Id == contactId);
            await using (var competing = new ContactsDbContext(options, new TenantContext(tenant), new CompanyContext(companyA)))
            {
                var second = await competing.Contacts.SingleAsync(value => value.Id == contactId); second.DisplayName = "Second writer"; second.ConcurrencyStamp = Guid.NewGuid().ToString("N");
                await competing.SaveChangesAsync();
            }
            first.DisplayName = "Stale writer"; first.ConcurrencyStamp = Guid.NewGuid().ToString("N");
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => visible.SaveChangesAsync());
        }
    }

    private static Contact ContactFor(Guid id, Guid tenant, Guid company) => new()
    {
        Id = id,
        TenantId = tenant,
        CompanyId = company,
        Kind = ContactKind.Organization,
        DisplayName = "Acme",
        OrganizationName = "Acme",
        IsCustomer = true,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
        ConcurrencyStamp = Guid.NewGuid().ToString("N")
        ,
        EmailAddresses = [new ContactEmailAddress { Id = Guid.NewGuid(), TenantId = tenant, CompanyId = company,
            ContactId = id, Address = "billing@example.test", IsPrimary = true }]
    };
    private sealed class TenantContext(Guid id) : ITenantContext { public TenantId? TenantId { get; } = new(id); }
    private sealed class CompanyContext(Guid id) : ICompanyContext { public CompanyId? CompanyId { get; } = new(id); }
    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => new(2026, 9, 14, 18, 30, 0, TimeSpan.Zero); }
    private sealed class RecordingAudit : IAuditLogWriter
    {
        public List<AuditWriteEntry> Entries { get; } = [];
        public void Append(AuditWriteEntry entry) => Entries.Add(entry);
        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
