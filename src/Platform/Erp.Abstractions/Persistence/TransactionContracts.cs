using System.Data.Common;

namespace Erp.Abstractions.Persistence;

/// <summary>A scoped relational connection shared by explicitly participating contexts.</summary>
public interface IRelationalDatabaseSession
{
    DbConnection Connection { get; }
}

/// <summary>Enlists a context without exposing its persistence model to the coordinator.</summary>
public interface ITransactionParticipant
{
    DbConnection Connection { get; }
    bool HasPendingChanges { get; }
    Task EnlistAsync(DbTransaction? transaction, CancellationToken cancellationToken);
    void DiscardChanges();
}

public interface IBusinessTransactionCoordinator
{
    Task<IBusinessTransaction> BeginAsync(
        ITransactionParticipant participant,
        CancellationToken cancellationToken);
}

/// <summary>
/// Commits the outermost operation only. Disposing any unfinished nested scope
/// prevents the entire operation from committing.
/// </summary>
public interface IBusinessTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
