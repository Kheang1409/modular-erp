using System.Reflection;
using Erp.Abstractions.Modules;
using Erp.Abstractions.Persistence;
using Erp.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Projects;

public sealed class ProjectsModule : IErpModule
{
    public ModuleManifest Manifest { get; } = new(
        "erp.projects", "Projects", "0.1.0", "Tenant-safe project planning, task tracking, and time capture.",
        "ERP Community", "Apache-2.0", "0.1.0", [],
        [ProjectsPermissions.Read, ProjectsPermissions.Manage]);

    public Assembly ComponentAssembly => typeof(ProjectsModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ProjectsDbContext>((provider, options) => options.UseNpgsql(
            provider.GetRequiredService<IRelationalDatabaseSession>().Connection,
            postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", "projects")));
        services.AddScoped<IProjectsService, ProjectsService>();
        services.AddScoped<IModuleDatabaseInitializer, ProjectsDatabaseInitializer>();
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapProjectsEndpoints();

    public void ConfigurePermissions(IPermissionRegistry permissions)
    {
        permissions.Add(new(ProjectsPermissions.Read, "View projects, tasks, milestones, and time entries", Manifest.Id));
        permissions.Add(new(ProjectsPermissions.Manage, "Create and manage projects, tasks, milestones, and time entries", Manifest.Id));
    }

    public void ConfigureNavigation(INavigationRegistry navigation)
    {
        navigation.Add(new("Portfolio", "/projects", "Projects", 60, ProjectsPermissions.Read));
        navigation.Add(new("Milestones", "/projects/milestones", "Projects", 61, ProjectsPermissions.Read));
        navigation.Add(new("Tasks", "/projects/tasks", "Projects", 62, ProjectsPermissions.Read));
        navigation.Add(new("Time entries", "/projects/time", "Projects", 63, ProjectsPermissions.Read));
    }
}

public static class ProjectsPermissions
{
    public const string Read = "projects.read";
    public const string Manage = "projects.manage";
}

internal sealed class ProjectsDatabaseInitializer(ProjectsDbContext database) : IModuleDatabaseInitializer
{
    public string ModuleId => "erp.projects";
    public Task MigrateAsync(CancellationToken cancellationToken) => database.Database.MigrateAsync(cancellationToken);
}
