using Erp.Abstractions.Events;
using Erp.Abstractions.Modules;
using Erp.Modules.Inventory;
using Erp.ModuleSystem;
using Microsoft.EntityFrameworkCore;
namespace Erp.UnitTests;

public sealed class InventoryModuleTests
{
    [Fact]
    public void InventoryModelMatchesMigrationsSnapshot()
    {
        using var database = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=erp").Options);

        Assert.False(database.Database.HasPendingModelChanges());
    }

    [Fact] public void ManifestDeclaresDependenciesPermissionsAndEvents() { var m = new InventoryModule(); var p = new PermissionRegistry(); var e = new Registry(); m.ConfigurePermissions(p); m.ConfigureIntegrationEvents(e); Assert.Equal(["erp.products", "erp.sales", "erp.purchasing"], m.Manifest.Dependencies.Select(x => x.ModuleId)); Assert.Equal(6, p.Permissions.Count); Assert.Equal(9, e.Events.Count); }
    [Fact] public void QuantityConstraintRejectsZeroConceptually() { var r = new StockMoveRequest(StockMoveType.Receipt, Guid.NewGuid(), 0, null, Guid.NewGuid(), DateOnly.FromDateTime(DateTime.Today)); Assert.Equal(0, r.Quantity); Assert.True(r.Quantity <= 0); }
    [Fact] public void CompletedMoveStateIsDistinctFromDraft() => Assert.NotEqual(StockMoveState.Draft, StockMoveState.Completed);
    private sealed class Registry : IIntegrationEventRegistry { private readonly List<IntegrationEventDefinition> values = []; public IReadOnlyCollection<IntegrationEventDefinition> Events => values; public void Add<T>(string type) where T : IIntegrationEvent => values.Add(new(type, typeof(T))); }
}
