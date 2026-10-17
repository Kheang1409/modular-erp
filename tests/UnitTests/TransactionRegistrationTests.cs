using Erp.Abstractions.Persistence;
using Erp.Infrastructure;
using Erp.Infrastructure.Persistence;
using Erp.ModuleSystem;
using Erp.Modules.Contacts;
using Erp.Modules.Products;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.UnitTests;

public sealed class TransactionRegistrationTests
{
    [Fact]
    public async Task RuntimeRegistrationSharesOneConnectionPerScopeAcrossPlatformContactsAndProducts()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=registration_only;Username=erp"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        new ContactsModule().ConfigureServices(services, configuration);
        new ProductsModule().ConfigureServices(services, configuration);
        services.AddErpInfrastructure(configuration, new PermissionRegistry());
        await using var provider = services.BuildServiceProvider();
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        var session = first.ServiceProvider.GetRequiredService<IRelationalDatabaseSession>();
        var platform = first.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var contacts = first.ServiceProvider.GetRequiredService<ContactsDbContext>();
        var products = first.ServiceProvider.GetRequiredService<ProductsDbContext>();

        Assert.Same(session.Connection, platform.Database.GetDbConnection());
        Assert.Same(session.Connection, contacts.Database.GetDbConnection());
        Assert.Same(session.Connection, products.Database.GetDbConnection());
        Assert.Same(products, first.ServiceProvider.GetRequiredService<ProductsDbContext>());
        var coordinator = first.ServiceProvider.GetRequiredService<IBusinessTransactionCoordinator>();
        Assert.IsType<BusinessTransactionCoordinator>(coordinator);
        Assert.Same(coordinator, first.ServiceProvider.GetRequiredService<IBusinessTransactionCoordinator>());

        var nextSession = second.ServiceProvider.GetRequiredService<IRelationalDatabaseSession>();
        Assert.NotSame(session.Connection, nextSession.Connection);
        Assert.Same(nextSession.Connection, second.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.GetDbConnection());
        Assert.Same(nextSession.Connection, second.ServiceProvider.GetRequiredService<ContactsDbContext>().Database.GetDbConnection());
        Assert.Same(nextSession.Connection, second.ServiceProvider.GetRequiredService<ProductsDbContext>().Database.GetDbConnection());
        Assert.NotSame(coordinator, second.ServiceProvider.GetRequiredService<IBusinessTransactionCoordinator>());
    }

    [Fact]
    public void InfrastructureRejectsMissingPostgresConfigurationAfterModulesRegister()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        new ContactsModule().ConfigureServices(services, configuration);
        new ProductsModule().ConfigureServices(services, configuration);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddErpInfrastructure(configuration, new PermissionRegistry()));

        Assert.Equal("ConnectionStrings:Postgres is required.", exception.Message);
    }

    [Fact]
    public void ModuleContextsRequireTheInfrastructureSessionAtResolution()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        new ContactsModule().ConfigureServices(services, configuration);
        new ProductsModule().ConfigureServices(services, configuration);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var contacts = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<ContactsDbContext>());
        var products = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<ProductsDbContext>());

        Assert.Contains(nameof(IRelationalDatabaseSession), contacts.Message);
        Assert.Contains(nameof(IRelationalDatabaseSession), products.Message);
    }
}
