using Erp.Abstractions.Persistence;
using Erp.Infrastructure;
using Erp.Infrastructure.Persistence;
using Erp.ModuleSystem;
using Erp.Modules.Contacts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.UnitTests;

public sealed class TransactionRegistrationTests
{
    [Fact]
    public async Task RuntimeRegistrationSharesOneConnectionPerScopeAcrossPlatformAndContacts()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=registration_only;Username=erp"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        new ContactsModule().ConfigureServices(services, configuration);
        services.AddErpInfrastructure(configuration, new PermissionRegistry());
        await using var provider = services.BuildServiceProvider();
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        var session = first.ServiceProvider.GetRequiredService<IRelationalDatabaseSession>();
        var platform = first.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var contacts = first.ServiceProvider.GetRequiredService<ContactsDbContext>();

        Assert.Same(session.Connection, platform.Database.GetDbConnection());
        Assert.Same(session.Connection, contacts.Database.GetDbConnection());
        Assert.IsType<BusinessTransactionCoordinator>(first.ServiceProvider.GetRequiredService<IBusinessTransactionCoordinator>());
        Assert.NotSame(session.Connection, second.ServiceProvider.GetRequiredService<IRelationalDatabaseSession>().Connection);
    }
}
