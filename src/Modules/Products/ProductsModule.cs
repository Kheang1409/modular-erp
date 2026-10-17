using System.Reflection;
using Erp.Abstractions.Modules;
using Erp.Abstractions.Persistence;
using Erp.Modules.Products.Contracts;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Products;

public sealed class ProductsModule : IErpModule
{
    public ModuleManifest Manifest { get; } = new(
        "erp.products", "Products", "0.1.0", "Tenant-safe products, services, prices, and units.",
        "ERP Community", "Apache-2.0", "0.1.0", [],
        [ProductsPermissions.Read, ProductsPermissions.Manage, ProductsPermissions.Archive,
            ProductsPermissions.Import, ProductsPermissions.Export]);

    public Assembly ComponentAssembly => typeof(ProductsModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ProductsDbContext>((provider, options) => options.UseNpgsql(
            provider.GetRequiredService<IRelationalDatabaseSession>().Connection,
            postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", "products")));
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<IProductDirectory, ProductDirectory>();
        services.AddScoped<IPurchasingProductDirectory, PurchasingProductDirectory>();
        services.AddScoped<IModuleDatabaseInitializer, ProductsDatabaseInitializer>();
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapProductEndpoints();

    public void ConfigurePermissions(IPermissionRegistry permissions)
    {
        permissions.Add(new(ProductsPermissions.Read, "View products", Manifest.Id));
        permissions.Add(new(ProductsPermissions.Manage, "Create and manage products", Manifest.Id));
        permissions.Add(new(ProductsPermissions.Archive, "Archive and reactivate products", Manifest.Id));
        permissions.Add(new(ProductsPermissions.Import, "Import products from CSV", Manifest.Id));
        permissions.Add(new(ProductsPermissions.Export, "Export products to CSV", Manifest.Id));
    }

    public void ConfigureNavigation(INavigationRegistry navigation) =>
        navigation.Add(new("Products", "/products", "Master data", 40, ProductsPermissions.Read));
}

public static class ProductsPermissions
{
    public const string Read = "products.read";
    public const string Manage = "products.manage";
    public const string Archive = "products.archive";
    public const string Import = "products.import";
    public const string Export = "products.export";
}

internal sealed class ProductsDatabaseInitializer(ProductsDbContext database) : IModuleDatabaseInitializer
{
    public string ModuleId => "erp.products";
    public Task MigrateAsync(CancellationToken cancellationToken) => database.Database.MigrateAsync(cancellationToken);
}
