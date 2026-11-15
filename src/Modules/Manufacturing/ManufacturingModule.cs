using System.Reflection;
using Erp.Abstractions.Modules;
using Erp.Abstractions.Persistence;
using Erp.Modules.Manufacturing.Contracts;
using Erp.Modules.Inventory.Contracts;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Manufacturing;

public sealed class ManufacturingModule : IErpModule
{
    public ModuleManifest Manifest { get; } = new(
        "erp.manufacturing", "Manufacturing", "0.1.0",
        "Bills of materials, production work orders, and quality inspection records.",
        "ERP Community", "Apache-2.0", "0.1.0",
        [new("erp.products", ">=0.1.0 <1.0.0"), new("erp.inventory", ">=0.1.0 <1.0.0")],
        [ManufacturingPermissions.Read, ManufacturingPermissions.BillOfMaterialManage,
            ManufacturingPermissions.WorkOrderManage, ManufacturingPermissions.WorkOrderTransition,
            ManufacturingPermissions.QualityManage]);

    public Assembly ComponentAssembly => typeof(ManufacturingModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ManufacturingDbContext>((provider, options) => options.UseNpgsql(
            provider.GetRequiredService<IRelationalDatabaseSession>().Connection,
            postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", "manufacturing")));
        services.AddHttpContextAccessor();
        services.AddScoped<IManufacturingService, ManufacturingService>();
        services.AddScoped<IModuleDatabaseInitializer, ManufacturingDatabaseInitializer>();
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapManufacturingEndpoints();

    public void ConfigurePermissions(IPermissionRegistry permissions)
    {
        permissions.Add(new(ManufacturingPermissions.Read, "View manufacturing records", Manifest.Id));
        permissions.Add(new(ManufacturingPermissions.BillOfMaterialManage, "Manage bills of materials", Manifest.Id));
        permissions.Add(new(ManufacturingPermissions.WorkOrderManage, "Create work orders", Manifest.Id));
        permissions.Add(new(ManufacturingPermissions.WorkOrderTransition, "Transition production work orders", Manifest.Id));
        permissions.Add(new(ManufacturingPermissions.QualityManage, "Record quality inspections", Manifest.Id));
    }

    public void ConfigureNavigation(INavigationRegistry navigation)
    {
        navigation.Add(new("Overview", "/manufacturing", "Manufacturing", 49, ManufacturingPermissions.Read));
        navigation.Add(new("Bills of materials", "/manufacturing/bills", "Manufacturing", 50, ManufacturingPermissions.Read));
        navigation.Add(new("Work orders", "/manufacturing/work-orders", "Manufacturing", 51, ManufacturingPermissions.Read));
        navigation.Add(new("Capacity", "/manufacturing/capacity", "Manufacturing", 52, ManufacturingPermissions.Read));
    }
}

public static class ManufacturingPermissions
{
    public const string Read = "manufacturing.read";
    public const string BillOfMaterialManage = "manufacturing.bom.manage";
    public const string WorkOrderManage = "manufacturing.workorder.manage";
    public const string WorkOrderTransition = "manufacturing.workorder.transition";
    public const string QualityManage = "manufacturing.quality.manage";
}

internal sealed class ManufacturingDatabaseInitializer(ManufacturingDbContext database) : IModuleDatabaseInitializer
{
    public string ModuleId => "erp.manufacturing";
    public Task MigrateAsync(CancellationToken cancellationToken) => database.Database.MigrateAsync(cancellationToken);
}
