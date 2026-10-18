using Erp.Abstractions.Events;
using Erp.Abstractions.Modules;
using Erp.ModuleSystem;
using Erp.Modules.Sales;
using Microsoft.EntityFrameworkCore;

namespace Erp.UnitTests;

public sealed class SalesModuleTests
{
    [Fact]
    public void SalesModelMatchesMigrationsSnapshot()
    {
        using var database = new SalesDbContext(new DbContextOptionsBuilder<SalesDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=erp").Options);

        Assert.False(database.Database.HasPendingModelChanges());
    }

    [Fact]
    public void ManifestDeclaresDependenciesPermissionsAndEvents()
    {
        var module = new SalesModule(); var permissions = new PermissionRegistry(); var events = new TestEventRegistry();
        module.ConfigurePermissions(permissions); module.ConfigureIntegrationEvents(events);
        Assert.Equal(["erp.contacts", "erp.products"], module.Manifest.Dependencies.Select(x => x.ModuleId));
        Assert.Equal(7, permissions.Permissions.Count); Assert.Equal(5, events.Events.Count);
    }

    [Fact]
    public void LineTotalsAreCalculatedAndRoundedByServer()
    {
        var value = QuotationService.CalculateLine(2m, 10m, 10m, 5m);
        Assert.Equal((20m, 2m, 0.9m, 18.9m), value);
    }

    [Theory]
    [InlineData(0, 10, 0, 0)]
    [InlineData(1, -1, 0, 0)]
    [InlineData(1, 1, 101, 0)]
    public void InvalidLineValuesAreRejected(decimal quantity, decimal price, decimal discount, decimal tax) =>
        Assert.Throws<SalesValidationException>(() => QuotationService.CalculateLine(quantity, price, discount, tax));

    private sealed class TestEventRegistry : IIntegrationEventRegistry
    {
        private readonly List<IntegrationEventDefinition> _events = [];
        public IReadOnlyCollection<IntegrationEventDefinition> Events => _events;
        public void Add<TEvent>(string eventType) where TEvent : IIntegrationEvent => _events.Add(new(eventType, typeof(TEvent)));
    }
}
