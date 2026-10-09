using Erp.Abstractions.Modules;

namespace Erp.ModuleSystem;

public sealed class ModuleRegistry : IModuleRegistry
{
    private readonly List<IErpModule> _modules = [];
    public IReadOnlyList<IErpModule> Modules => _modules;

    public void Add(IErpModule erpModule)
    {
        ArgumentNullException.ThrowIfNull(erpModule);
        if (_modules.Any(existing => string.Equals(existing.Manifest.Id, erpModule.Manifest.Id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"A module with ID '{erpModule.Manifest.Id}' is already registered.");
        }

        _modules.Add(erpModule);
    }
}

public sealed class PermissionRegistry : IPermissionRegistry
{
    private readonly Dictionary<string, PermissionDefinition> _permissions = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<PermissionDefinition> Permissions => _permissions.Values;

    public void Add(PermissionDefinition permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        if (!_permissions.TryAdd(permission.Name, permission))
        {
            throw new InvalidOperationException($"Permission '{permission.Name}' is already registered.");
        }
    }
}

public sealed class NavigationRegistry : INavigationRegistry
{
    private readonly List<NavigationItem> _items = [];
    public IReadOnlyCollection<NavigationItem> Items => _items.OrderBy(item => item.Order).ToArray();

    public void Add(NavigationItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_items.Any(existing => string.Equals(existing.Href, item.Href, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Navigation path '{item.Href}' is already registered.");
        }

        _items.Add(item);
    }
}
