using Erp.Modules.Hcm;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Erp.IntegrationTests;

public sealed class HcmPayrollAdjustmentsPostgresTests
{
    [Fact]
    public async Task PayrollAdjustmentMigrationsCreateScopedTablesAndIdempotencyIndexes()
    {
        await using var postgres = await IntegrationContainers.StartPostgresAsync("hcm_payroll_adjustments_tests");
        var options = new DbContextOptionsBuilder<HcmDbContext>().UseNpgsql(postgres.GetConnectionString(),
            npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "hcm")).Options;
        await using (var database = new HcmDbContext(options))
        {
            var snapshot = database.GetService<IModelRuntimeInitializer>()
                .Initialize(database.GetService<IMigrationsAssembly>().ModelSnapshot!.Model);
            var differences = database.GetService<IMigrationsModelDiffer>()
                .GetDifferences(snapshot.GetRelationalModel(), database.GetService<IDesignTimeModel>().Model.GetRelationalModel());
            Assert.True(differences.Count == 0,
                $"Snapshot entities: {snapshot.GetEntityTypes().Count()}, current entities: {database.GetService<IDesignTimeModel>().Model.GetEntityTypes().Count()}. " +
                string.Join(Environment.NewLine, differences.Select(x => x.GetType().Name)));
            await database.Database.MigrateAsync();
        }
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT to_regclass('hcm.payroll_adjustments') IS NOT NULL,
                   to_regclass('hcm.payroll_adjustment_events') IS NOT NULL,
                   EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'hcm' AND tablename = 'payroll_adjustments'
                       AND indexdef LIKE '%IdempotencyKey%' AND indexdef LIKE '%UNIQUE%'),
                   EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'hcm'
                       AND table_name = 'payroll_adjustments' AND column_name = 'SnapshotJson')
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
    }
}
