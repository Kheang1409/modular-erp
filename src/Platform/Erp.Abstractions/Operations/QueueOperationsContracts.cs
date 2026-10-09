namespace Erp.Abstractions.Operations;

public interface IQueueOperationsService
{
    Task<QueueOperationsSnapshot> GetAsync(int limit, CancellationToken cancellationToken);
    Task<bool> ReplayOutboxAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> ReplayJobAsync(Guid id, CancellationToken cancellationToken);
}

public sealed record QueueOperationsSnapshot(
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<DeadLetterOutboxItem> Outbox,
    IReadOnlyList<DeadLetterJobItem> Jobs);

public sealed record DeadLetterOutboxItem(Guid Id, string EventType, int RetryCount, DateTimeOffset OccurredAtUtc, string CorrelationId);
public sealed record DeadLetterJobItem(Guid Id, string Name, int Attempts, int MaxAttempts, DateTimeOffset CreatedAtUtc, string CorrelationId);
