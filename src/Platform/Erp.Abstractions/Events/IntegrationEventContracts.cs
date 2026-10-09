using Erp.SharedKernel;

namespace Erp.Abstractions.Events;

public interface IIntegrationEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredAtUtc { get; }
}

public interface IIntegrationEventConsumer<in TEvent>
    where TEvent : IIntegrationEvent
{
    Task HandleAsync(TEvent integrationEvent, IntegrationEventContext context, CancellationToken cancellationToken);
}

public interface IIntegrationEventRegistry
{
    IReadOnlyCollection<IntegrationEventDefinition> Events { get; }
    void Add<TEvent>(string eventType)
        where TEvent : IIntegrationEvent;
}

public interface IOutboxWriter
{
    void Add<TEvent>(TEvent integrationEvent)
        where TEvent : IIntegrationEvent;
}

public sealed record IntegrationEventDefinition(string EventType, Type PayloadType);

public sealed record IntegrationEventContext(
    Guid MessageId,
    TenantId TenantId,
    CompanyId CompanyId,
    int Attempt,
    string CorrelationId);
