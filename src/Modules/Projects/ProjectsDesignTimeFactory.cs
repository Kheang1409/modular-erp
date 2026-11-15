using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Erp.Modules.Projects;

internal sealed class ProjectsDesignTimeFactory : IDesignTimeDbContextFactory<ProjectsDbContext>
{
    public ProjectsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("PROJECTS_DESIGN_CONNECTION")
            ?? throw new InvalidOperationException("Set PROJECTS_DESIGN_CONNECTION to scaffold Projects migrations.");
        var options = new DbContextOptionsBuilder<ProjectsDbContext>()
            .UseNpgsql(connectionString, postgres =>
                postgres.MigrationsHistoryTable("__EFMigrationsHistory", "projects"))
            .Options;
        return new ProjectsDbContext(options);
    }
}
