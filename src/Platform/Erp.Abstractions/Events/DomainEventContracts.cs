using Erp.SharedKernel;

namespace Erp.Abstractions.Events;

public interface IDomainEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredAtUtc { get; }
}

public interface IDomainEventConsumer<in TEvent>
    where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, DomainEventContext context, CancellationToken cancellationToken);
}

public interface IDomainEventDispatcher
{
    Task DispatchAsync<TEvent>(TEvent domainEvent, CancellationToken cancellationToken = default)
        where TEvent : IDomainEvent;
}

public sealed record DomainEventContext(
    TenantId TenantId,
    CompanyId CompanyId,
    UserId? UserId,
    string CorrelationId);
