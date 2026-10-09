using System.Reflection;
using Erp.Abstractions.Modules;
using Erp.Modules.Crm.Contracts;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Crm;

public sealed class CrmModule : IErpModule
{
    public ModuleManifest Manifest { get; } = new("erp.crm", "CRM", "0.1.0", "Tenant-safe leads and pipeline foundation.",
        "ERP Community", "Apache-2.0", "0.1.0", [new("erp.contacts", ">=0.1.0 <1.0.0")],
        [CrmPermissions.Read, CrmPermissions.Manage, CrmPermissions.Assign, CrmPermissions.Configure, CrmPermissions.Archive]);
    public Assembly ComponentAssembly => typeof(CrmModule).Assembly;
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Postgres") ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
        services.AddDbContext<CrmDbContext>(options => options.UseNpgsql(connection, postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", "crm")));
        services.AddScoped<ILeadService, LeadService>(); services.AddScoped<IOpportunityService, OpportunityService>(); services.AddScoped<IActivityService, ActivityService>(); services.AddScoped<ICrmLeadDirectory, CrmLeadDirectory>();
        services.AddScoped<IModuleDatabaseInitializer, CrmDatabaseInitializer>();
    }
    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapCrmEndpoints();
    public void ConfigurePermissions(IPermissionRegistry permissions)
    { permissions.Add(new(CrmPermissions.Read, "View CRM", Manifest.Id)); permissions.Add(new(CrmPermissions.Manage, "Manage CRM leads and opportunities", Manifest.Id)); permissions.Add(new(CrmPermissions.Assign, "Assign CRM work", Manifest.Id)); permissions.Add(new(CrmPermissions.Configure, "Configure CRM pipelines", Manifest.Id)); permissions.Add(new(CrmPermissions.Archive, "Archive CRM records", Manifest.Id)); }
    public void ConfigureNavigation(INavigationRegistry navigation) => navigation.Add(new("Leads and opportunities", "/crm/leads", "CRM", 50, CrmPermissions.Read));
}
public static class CrmPermissions { public const string Read = "crm.read"; public const string Manage = "crm.manage"; public const string Assign = "crm.assign"; public const string Configure = "crm.configure"; public const string Archive = "crm.archive"; }
internal sealed class CrmDatabaseInitializer(CrmDbContext database) : IModuleDatabaseInitializer { public string ModuleId => "erp.crm"; public Task MigrateAsync(CancellationToken token) => database.Database.MigrateAsync(token); }
