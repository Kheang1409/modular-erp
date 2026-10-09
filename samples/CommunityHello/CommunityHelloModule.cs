using System.Reflection;
using Erp.Abstractions.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CommunityHello;

public sealed class CommunityHelloModule : IErpModule
{
    public ModuleManifest Manifest { get; } = new(
        "community.hello",
        "Community Hello",
        "0.1.0",
        "An installable reference module built only against the public ERP module contract.",
        "Open ERP Community",
        "Apache-2.0",
        "0.1.0",
        [],
        [CommunityHelloPermissions.Read]);

    public Assembly ComponentAssembly => typeof(CommunityHelloModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        => services.AddScoped<CommunityHelloService>();

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints)
        => endpoints.MapGet("/api/v1/community/hello", (CommunityHelloService service) =>
                Results.Ok(new { message = service.GetMessage() }))
            .WithName("GetCommunityHello")
            .WithTags("Community Hello")
            .RequirePermission(CommunityHelloPermissions.Read);

    public void ConfigurePermissions(IPermissionRegistry permissions)
        => permissions.Add(new(CommunityHelloPermissions.Read, "View Community Hello", Manifest.Id));

    public void ConfigureNavigation(INavigationRegistry navigation)
        => navigation.Add(new("Community Hello", "/community/hello", "extension", 100, CommunityHelloPermissions.Read));
}

public static class CommunityHelloPermissions
{
    public const string Read = "community.hello.read";
}

public sealed class CommunityHelloService
{
    private readonly string _message = "Community Hello was loaded from a verified .erpmod package.";

    public string GetMessage() => _message;
}
