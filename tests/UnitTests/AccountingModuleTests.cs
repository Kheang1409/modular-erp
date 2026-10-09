using Erp.Abstractions.Modules;
using Erp.Abstractions.Events;
using Erp.Modules.Accounting;
using Erp.ModuleSystem;

namespace Erp.UnitTests;

public sealed class AccountingModuleTests
{
    [Fact]
    public void ManifestAndPermissionsDescribeAccountingBoundary()
    {
        var module = new AccountingModule();
        var permissions = new PermissionRegistry();
        var events = new Registry();

        module.ConfigurePermissions(permissions);
        module.ConfigureIntegrationEvents(events);

        Assert.Equal("erp.accounting", module.Manifest.Id);
        Assert.Equal(["erp.sales", "erp.purchasing"], module.Manifest.Dependencies.Select(x => x.ModuleId));
        Assert.Equal(14, permissions.Permissions.Count);
        Assert.All(permissions.Permissions, permission => Assert.Equal(module.Manifest.Id, permission.ModuleId));
        Assert.Contains(permissions.Permissions, permission => permission.Name == AccountingPermissions.Post);
        Assert.Contains(permissions.Permissions, permission => permission.Name == AccountingPermissions.ClosePeriods);
        Assert.Contains(permissions.Permissions, permission => permission.Name == AccountingPermissions.InvoicePost);
        Assert.Contains(permissions.Permissions, permission => permission.Name == AccountingPermissions.BillPost);
        Assert.Contains(permissions.Permissions, permission => permission.Name == AccountingPermissions.PaymentPost);
        Assert.Contains(permissions.Permissions, permission => permission.Name == AccountingPermissions.Reconcile);
        Assert.Equal(6, events.Events.Count);
    }

    [Fact]
    public void GeneralLedgerRejectsUnbalancedEntries() =>
        Assert.Throws<AccountingValidationException>(() => GeneralLedgerService.ValidateBalance([(10m, 0m), (0m, 9m)]));

    private sealed class Registry : IIntegrationEventRegistry
    {
        private readonly List<IntegrationEventDefinition> _events = [];
        public IReadOnlyCollection<IntegrationEventDefinition> Events => _events;
        public void Add<TEvent>(string eventType) where TEvent : IIntegrationEvent => _events.Add(new(eventType, typeof(TEvent)));
    }
}
