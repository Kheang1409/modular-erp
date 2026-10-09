using Erp.SharedKernel;

namespace Erp.Abstractions.Jobs;

public interface IBackgroundJobScheduler
{
    Task<Guid> EnqueueAsync<TPayload>(
        string jobName,
        TPayload payload,
        DateTimeOffset? executeAfterUtc = null,
        string? idempotencyKey = null,
        int maxAttempts = 5,
        CancellationToken cancellationToken = default);
}

public interface IBackgroundJobHandler<TPayload>
{
    Task ExecuteAsync(TPayload payload, BackgroundJobExecutionContext context, CancellationToken cancellationToken);
}

public interface IBackgroundJobRegistry
{
    IReadOnlyCollection<BackgroundJobDefinition> Jobs { get; }
    void Add<THandler, TPayload>(string name)
        where THandler : class, IBackgroundJobHandler<TPayload>;
}

public sealed record BackgroundJobDefinition(string Name, Type HandlerType, Type PayloadType);

public sealed record BackgroundJobExecutionContext(
    Guid JobId,
    TenantId TenantId,
    CompanyId CompanyId,
    int Attempt,
    string CorrelationId);
