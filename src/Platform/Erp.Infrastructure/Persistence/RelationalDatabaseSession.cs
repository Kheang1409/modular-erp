using System.Data.Common;
using Erp.Abstractions.Persistence;
using Npgsql;

namespace Erp.Infrastructure.Persistence;

/// <summary>Owns the connection; participating DbContexts must not dispose it.</summary>
public sealed class RelationalDatabaseSession : IRelationalDatabaseSession, IDisposable, IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;

    public RelationalDatabaseSession(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connection = new NpgsqlConnection(connectionString);
    }

    public DbConnection Connection => _connection;

    public void Dispose() => _connection.Dispose();
    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
