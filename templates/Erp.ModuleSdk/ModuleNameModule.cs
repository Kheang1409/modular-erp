using System.Reflection;
using Erp.Abstractions.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ModuleName;

public sealed class ModuleNameModule : IErpModule
{
    public ModuleManifest Manifest { get; } = new(
        "community.modulename", "ModuleName", "0.1.0", "A community Open ERP module.",
        "Community", "Apache-2.0", "0.1.0", [], [ModuleNamePermissions.Read]);

    public Assembly ComponentAssembly => typeof(ModuleNameModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        => services.AddScoped<ModuleNameService>();

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints)
        => endpoints.MapGet("/api/v1/community.modulename", (ModuleNameService service) =>
                Results.Ok(new { message = service.GetMessage() }))
            .WithTags("ModuleName")
            .RequirePermission(ModuleNamePermissions.Read);

    public void ConfigurePermissions(IPermissionRegistry permissions)
        => permissions.Add(new(ModuleNamePermissions.Read, "View ModuleName", Manifest.Id));

    public void ConfigureNavigation(INavigationRegistry navigation)
        => navigation.Add(new("ModuleName", "/community.modulename", "extension", 100, ModuleNamePermissions.Read));
}

public static class ModuleNamePermissions
{
    public const string Read = "community.modulename.read";
}

public sealed class ModuleNameService
{
    private readonly string _message = "ModuleName is loaded through the Open ERP module contract.";
    public string GetMessage() => _message;
}
