using System.Text.Json;
using Erp.Abstractions.Context;
using Erp.Abstractions.Events;
using Erp.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;

namespace Erp.Infrastructure.Events;

public sealed class PostgresOutboxWriter(
    PlatformDbContext database,
    ITenantContext tenantContext,
    ICompanyContext companyContext,
    IIntegrationEventRegistry registry,
    IHttpContextAccessor httpContextAccessor) : IOutboxWriter
{
    public void Add<TEvent>(TEvent integrationEvent)
        where TEvent : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        if (integrationEvent.EventId == Guid.Empty) throw new ArgumentException("Integration event ID cannot be empty.", nameof(integrationEvent));
        if (integrationEvent.OccurredAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Integration event time must be UTC.", nameof(integrationEvent));
        var definition = registry.Events.SingleOrDefault(x => x.PayloadType == typeof(TEvent))
            ?? throw new InvalidOperationException($"Integration event '{typeof(TEvent).FullName}' is not registered.");
        database.OutboxMessages.Add(new OutboxMessage
        {
            Id = integrationEvent.EventId,
            TenantId = tenantContext.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required."),
            CompanyId = companyContext.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required."),
            OccurredAtUtc = integrationEvent.OccurredAtUtc,
            EventType = definition.EventType,
            PayloadJson = JsonSerializer.Serialize(integrationEvent),
            CorrelationId = httpContextAccessor.HttpContext?.TraceIdentifier ?? Guid.NewGuid().ToString("N"),
            NextAttemptAtUtc = integrationEvent.OccurredAtUtc
        });
    }
}
