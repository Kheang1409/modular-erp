using System.Data;
using Erp.Abstractions.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Erp.Infrastructure.Persistence;

/// <summary>
/// Coordinates one local PostgreSQL transaction across explicitly enlisted contexts.
/// Operations in the same scope must be sequential and must not perform external side effects.
/// </summary>
public sealed class BusinessTransactionCoordinator(PlatformDbContext database) : IBusinessTransactionCoordinator
{
    private readonly HashSet<ITransactionParticipant> _participants = new(ReferenceEqualityComparer.Instance);
    private readonly List<BusinessTransactionScope> _scopes = [];
    private IDbContextTransaction? _transaction;
    private bool _rollbackOnly;
    private bool _faulted;

    public async Task<IBusinessTransaction> BeginAsync(
        ITransactionParticipant participant,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(participant);
        cancellationToken.ThrowIfCancellationRequested();
        if (_faulted)
            throw new InvalidOperationException("Transaction cleanup failed. Start the next operation in a fresh service scope.");
        if (!ReferenceEquals(participant.Connection, database.Database.GetDbConnection()))
            throw new InvalidOperationException("Transaction participants must share the same scoped database connection.");
        if (_rollbackOnly)
            throw new InvalidOperationException("The current operation must roll back before another transaction can begin.");
        if (_transaction is null && _scopes.Count != 0)
            throw new InvalidOperationException("Dispose the completed transaction scope before starting another operation.");

        var isRoot = _scopes.Count == 0;
        if (isRoot)
        {
            // A new operation must not accidentally save or discard another operation's staged work.
            // Changes staged within an explicitly started outer scope are owned by that operation.
            if (database.ChangeTracker.HasChanges() || participant.HasPendingChanges)
                throw new InvalidOperationException("Begin the business transaction before staging persistence changes.");
            if (database.Database.CurrentTransaction is not null)
                throw new InvalidOperationException("An unmanaged platform transaction is already active.");
            _transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            _participants.Add(database);
        }

        try
        {
            if (_participants.Add(participant))
                await participant.EnlistAsync(_transaction!.GetDbTransaction(), cancellationToken);
            var scope = new BusinessTransactionScope(this, isRoot);
            _scopes.Add(scope);
            return scope;
        }
        catch (Exception exception)
        {
            _rollbackOnly = true;
            if (isRoot)
            {
                await RollbackAfterFailureAsync(exception);
                _rollbackOnly = false;
            }
            throw;
        }
    }

    private async Task CommitAsync(BusinessTransactionScope scope, CancellationToken cancellationToken)
    {
        EnsureActive(scope);
        if (scope.Completed)
            throw new InvalidOperationException("This transaction scope has already completed.");
        if (!ReferenceEquals(_scopes[^1], scope))
            throw new InvalidOperationException("Complete and dispose nested transaction scopes before committing their parent.");
        if (_rollbackOnly)
            throw new InvalidOperationException("An unfinished nested transaction requires the entire operation to roll back.");
        cancellationToken.ThrowIfCancellationRequested();

        if (!scope.IsRoot)
        {
            scope.Completed = true;
            return;
        }

        try
        {
            // Outbox events must persist even when the operation has no explicit audit flush.
            await database.SaveChangesAsync(cancellationToken);
            await _transaction!.CommitAsync(cancellationToken);
            scope.Completed = true;
        }
        catch (Exception exception)
        {
            _rollbackOnly = true;
            await RollbackAfterFailureAsync(exception);
            throw;
        }

        await FinishAsync(rollback: false);
    }

    private async ValueTask DisposeAsync(BusinessTransactionScope scope)
    {
        if (!_scopes.Contains(scope)) return;

        if (scope.IsRoot || !ReferenceEquals(_scopes[^1], scope))
        {
            // Out-of-order disposal always rolls back and cannot leave a connection enlisted.
            try
            {
                if (_transaction is not null)
                    await FinishAsync(rollback: !scope.IsRoot || !scope.Completed || _rollbackOnly);
            }
            finally
            {
                _scopes.Clear();
                _rollbackOnly = false;
            }
            return;
        }

        if (!scope.Completed) _rollbackOnly = true;
        _scopes.RemoveAt(_scopes.Count - 1);
    }

    private void EnsureActive(BusinessTransactionScope scope)
    {
        ObjectDisposedException.ThrowIf(!_scopes.Contains(scope) || _transaction is null, scope);
    }

    private async Task RollbackAfterFailureAsync(Exception originalException)
    {
        try { await FinishAsync(rollback: true); }
        catch (Exception cleanupException)
        {
            // Preserve the business failure while retaining evidence of a failed cleanup.
            originalException.Data["TransactionCleanupException"] = cleanupException;
        }
    }

    private async Task FinishAsync(bool rollback)
    {
        var transaction = _transaction;
        if (transaction is null) return;
        _transaction = null;
        var participants = _participants.ToArray();
        _participants.Clear();
        var errors = new List<Exception>();

        if (rollback)
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch (Exception exception) { errors.Add(exception); }
        }

        foreach (var participant in participants.Where(participant => !ReferenceEquals(participant, database)))
        {
            try { await participant.EnlistAsync(null, CancellationToken.None); }
            catch (Exception exception) { errors.Add(exception); }
        }
        try { await transaction.DisposeAsync(); }
        catch (Exception exception) { errors.Add(exception); }
        try { await ((ITransactionParticipant)database).EnlistAsync(null, CancellationToken.None); }
        catch (Exception exception) { errors.Add(exception); }

        if (rollback)
        {
            foreach (var participant in participants)
            {
                try { participant.DiscardChanges(); }
                catch (Exception exception) { errors.Add(exception); }
            }
        }

        if (errors.Count != 0)
        {
            _faulted = true;
            throw new AggregateException("One or more database transaction cleanup operations failed.", errors);
        }
    }

    private sealed class BusinessTransactionScope(BusinessTransactionCoordinator coordinator, bool isRoot) : IBusinessTransaction
    {
        public bool IsRoot { get; } = isRoot;
        public bool Completed { get; set; }

        public Task CommitAsync(CancellationToken cancellationToken) => coordinator.CommitAsync(this, cancellationToken);
        public ValueTask DisposeAsync() => coordinator.DisposeAsync(this);
    }
}
