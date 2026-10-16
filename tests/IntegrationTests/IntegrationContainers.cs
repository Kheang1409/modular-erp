using DotNet.Testcontainers.Containers;
using Testcontainers.PostgreSql;

namespace Erp.IntegrationTests;

internal static class IntegrationContainers
{
    public static Task<PostgreSqlContainer> StartPostgresAsync(string database) => StartAsync(
        new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase(database)
            .WithUsername("erp")
            .WithPassword("integration_only_password")
            .Build());

    public static async Task<TContainer> StartAsync<TContainer>(TContainer container) where TContainer : IContainer
    {
        // An unavailable Docker engine must fail the integration suite, never produce a passing no-op.
        // Each test owns a fresh disposable database; application connection strings are never used.
        using var startupTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await container.StartAsync(startupTimeout.Token);
            return container;
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }
    }
}
