using System.Reflection;
using Erp.Abstractions.Modules;
using Erp.Abstractions.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Erp.Modules.Contacts.Contracts;

namespace Erp.Modules.Contacts;

public sealed class ContactsModule : IErpModule
{
    public ModuleManifest Manifest { get; } = new(
        "erp.contacts", "Contacts", "0.1.0", "Tenant-safe customer and supplier contacts.",
        "ERP Community", "Apache-2.0", "0.1.0", [],
        [ContactsPermissions.Read, ContactsPermissions.Manage, ContactsPermissions.Archive, ContactsPermissions.Import, ContactsPermissions.Export]);

    public Assembly ComponentAssembly => typeof(ContactsModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ContactsDbContext>((provider, options) => options.UseNpgsql(
            provider.GetRequiredService<IRelationalDatabaseSession>().Connection,
            postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", "contacts")));
        services.AddScoped<IContactService, ContactService>();
        services.AddScoped<IContactDirectory, ContactDirectory>();
        services.AddScoped<ICustomerDocumentDirectory, CustomerDocumentDirectory>();
        services.AddScoped<ISupplierDocumentDirectory, SupplierDocumentDirectory>();
        services.AddScoped<IModuleDatabaseInitializer, ContactsDatabaseInitializer>();
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapContactsEndpoints();

    public void ConfigurePermissions(IPermissionRegistry permissions)
    {
        permissions.Add(new(ContactsPermissions.Read, "View contacts", Manifest.Id));
        permissions.Add(new(ContactsPermissions.Manage, "Create and manage contacts", Manifest.Id));
        permissions.Add(new(ContactsPermissions.Archive, "Archive and reactivate contacts", Manifest.Id));
        permissions.Add(new(ContactsPermissions.Import, "Import contacts from CSV", Manifest.Id));
        permissions.Add(new(ContactsPermissions.Export, "Export contacts to CSV", Manifest.Id));
    }

    public void ConfigureNavigation(INavigationRegistry navigation) =>
        navigation.Add(new("Contacts", "/contacts", "Master data", 30, ContactsPermissions.Read));

}

public static class ContactsPermissions
{
    public const string Read = "contacts.read";
    public const string Manage = "contacts.manage";
    public const string Archive = "contacts.archive";
    public const string Import = "contacts.import";
    public const string Export = "contacts.export";
}

internal sealed class ContactsDatabaseInitializer(ContactsDbContext database) : IModuleDatabaseInitializer
{
    public string ModuleId => "erp.contacts";
    public Task MigrateAsync(CancellationToken cancellationToken) => database.Database.MigrateAsync(cancellationToken);
}
