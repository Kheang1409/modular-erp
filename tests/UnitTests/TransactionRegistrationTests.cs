using Erp.Abstractions.Persistence;
using Erp.Infrastructure;
using Erp.Infrastructure.Persistence;
using Erp.ModuleSystem;
using Erp.Modules.Contacts;
using Erp.Modules.Crm;
using Erp.Modules.Products;
using Erp.Modules.Purchasing;
using Erp.Modules.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.UnitTests;

public sealed class TransactionRegistrationTests
{
    [Fact]
    public async Task RuntimeRegistrationSharesOneConnectionPerScopeAcrossPlatformContactsProductsCrmSalesAndPurchasing()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=registration_only;Username=erp"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        new ContactsModule().ConfigureServices(services, configuration);
        new ProductsModule().ConfigureServices(services, configuration);
        new CrmModule().ConfigureServices(services, configuration);
        new SalesModule().ConfigureServices(services, configuration);
        new PurchasingModule().ConfigureServices(services, configuration);
        services.AddErpInfrastructure(configuration, new PermissionRegistry());
        await using var provider = services.BuildServiceProvider();
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        var session = first.ServiceProvider.GetRequiredService<IRelationalDatabaseSession>();
        var platform = first.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var contacts = first.ServiceProvider.GetRequiredService<ContactsDbContext>();
        var products = first.ServiceProvider.GetRequiredService<ProductsDbContext>();
        var crm = first.ServiceProvider.GetRequiredService<CrmDbContext>();
        var sales = first.ServiceProvider.GetRequiredService<SalesDbContext>();
        var purchasing = first.ServiceProvider.GetRequiredService<PurchasingDbContext>();

        Assert.Same(session.Connection, platform.Database.GetDbConnection());
        Assert.Same(session.Connection, contacts.Database.GetDbConnection());
        Assert.Same(session.Connection, products.Database.GetDbConnection());
        Assert.Same(session.Connection, crm.Database.GetDbConnection());
        Assert.Same(session.Connection, sales.Database.GetDbConnection());
        Assert.Same(session.Connection, purchasing.Database.GetDbConnection());
        Assert.IsAssignableFrom<ITransactionParticipant>(purchasing);
        Assert.Same(purchasing, first.ServiceProvider.GetRequiredService<PurchasingDbContext>());
        Assert.IsAssignableFrom<ITransactionParticipant>(sales);
        Assert.Same(sales, first.ServiceProvider.GetRequiredService<SalesDbContext>());
        Assert.IsAssignableFrom<ITransactionParticipant>(crm);
        Assert.Same(crm, first.ServiceProvider.GetRequiredService<CrmDbContext>());
        Assert.Same(products, first.ServiceProvider.GetRequiredService<ProductsDbContext>());
        var coordinator = first.ServiceProvider.GetRequiredService<IBusinessTransactionCoordinator>();
        Assert.IsType<BusinessTransactionCoordinator>(coordinator);
        Assert.Same(coordinator, first.ServiceProvider.GetRequiredService<IBusinessTransactionCoordinator>());

        var nextSession = second.ServiceProvider.GetRequiredService<IRelationalDatabaseSession>();
        Assert.NotSame(session.Connection, nextSession.Connection);
        Assert.Same(nextSession.Connection, second.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.GetDbConnection());
        Assert.Same(nextSession.Connection, second.ServiceProvider.GetRequiredService<ContactsDbContext>().Database.GetDbConnection());
        Assert.Same(nextSession.Connection, second.ServiceProvider.GetRequiredService<ProductsDbContext>().Database.GetDbConnection());
        var nextCrm = second.ServiceProvider.GetRequiredService<CrmDbContext>();
        Assert.Same(nextSession.Connection, nextCrm.Database.GetDbConnection());
        Assert.NotSame(crm, nextCrm);
        var nextSales = second.ServiceProvider.GetRequiredService<SalesDbContext>();
        Assert.Same(nextSession.Connection, nextSales.Database.GetDbConnection());
        Assert.NotSame(sales, nextSales);
        var nextPurchasing = second.ServiceProvider.GetRequiredService<PurchasingDbContext>();
        Assert.Same(nextSession.Connection, nextPurchasing.Database.GetDbConnection());
        Assert.NotSame(purchasing, nextPurchasing);
        Assert.NotSame(coordinator, second.ServiceProvider.GetRequiredService<IBusinessTransactionCoordinator>());
    }

    [Fact]
    public void InfrastructureRejectsMissingPostgresConfigurationAfterModulesRegister()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        new ContactsModule().ConfigureServices(services, configuration);
        new ProductsModule().ConfigureServices(services, configuration);
        new CrmModule().ConfigureServices(services, configuration);
        new SalesModule().ConfigureServices(services, configuration);
        new PurchasingModule().ConfigureServices(services, configuration);

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
        new CrmModule().ConfigureServices(services, configuration);
        new SalesModule().ConfigureServices(services, configuration);
        new PurchasingModule().ConfigureServices(services, configuration);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var contacts = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<ContactsDbContext>());
        var products = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<ProductsDbContext>());
        var crm = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<CrmDbContext>());
        var sales = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<SalesDbContext>());
        var purchasing = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<PurchasingDbContext>());

        Assert.Contains(nameof(IRelationalDatabaseSession), contacts.Message);
        Assert.Contains(nameof(IRelationalDatabaseSession), products.Message);
        Assert.Contains(nameof(IRelationalDatabaseSession), crm.Message);
        Assert.Contains(nameof(IRelationalDatabaseSession), sales.Message);
        Assert.Contains(nameof(IRelationalDatabaseSession), purchasing.Message);
    }
}
