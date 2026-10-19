using Erp.Abstractions.Events;
using Erp.ModuleSystem;
using Erp.Modules.Purchasing;
using Microsoft.EntityFrameworkCore;

namespace Erp.UnitTests;

public sealed class PurchasingModuleTests
{
    [Fact]
    public void PurchasingModelMatchesMigrationsSnapshot()
    {
        using var database = new PurchasingDbContext(new DbContextOptionsBuilder<PurchasingDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=erp").Options);

        Assert.False(database.Database.HasPendingModelChanges());
    }

    [Fact]
    public void ManifestDeclaresDependenciesPermissionsAndEvents()
    {
        var module = new PurchasingModule();
        var permissions = new PermissionRegistry();
        var events = new Registry();
        module.ConfigurePermissions(permissions);
        module.ConfigureIntegrationEvents(events);
        Assert.Equal(["erp.contacts", "erp.products"], module.Manifest.Dependencies.Select(dependency => dependency.ModuleId));
        Assert.Equal(5, permissions.Permissions.Count);
        Assert.Equal(3, events.Events.Count);
    }

    [Fact]
    public void ServerCalculatesPurchaseLineTotals() =>
        Assert.Equal((20m, 2m, 0.9m, 18.9m), PurchaseOrderService.CalculateLine(2, 10, 10, 5));

    [Fact]
    public void InvalidLineIsRejected() =>
        Assert.Throws<PurchasingValidationException>(() => PurchaseOrderService.CalculateLine(0, 1, 0, 0));

    private sealed class Registry : IIntegrationEventRegistry
    {
        private readonly List<IntegrationEventDefinition> _events = [];
        public IReadOnlyCollection<IntegrationEventDefinition> Events => _events;
        public void Add<T>(string type) where T : IIntegrationEvent => _events.Add(new(type, typeof(T)));
    }
}
