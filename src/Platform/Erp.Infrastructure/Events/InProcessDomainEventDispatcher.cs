using System.Security.Claims;
using Erp.Abstractions.Context;
using Erp.Abstractions.Events;
using Erp.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Erp.Infrastructure.Events;

public sealed class InProcessDomainEventDispatcher(
    IServiceProvider serviceProvider,
    ITenantContext tenantContext,
    ICompanyContext companyContext,
    IHttpContextAccessor httpContextAccessor,
    ILogger<InProcessDomainEventDispatcher> logger) : IDomainEventDispatcher
{
    public async Task DispatchAsync<TEvent>(TEvent domainEvent, CancellationToken cancellationToken = default)
        where TEvent : IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var tenantId = tenantContext.TenantId
            ?? throw new InvalidOperationException("An active tenant is required to dispatch a domain event.");
        var companyId = companyContext.CompanyId
            ?? throw new InvalidOperationException("An active company is required to dispatch a domain event.");
        var httpContext = httpContextAccessor.HttpContext;
        var userIdentifier = httpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        UserId? userId = Guid.TryParse(userIdentifier, out var parsedUserId) ? new UserId(parsedUserId) : null;
        var context = new DomainEventContext(
            tenantId, companyId, userId,
            httpContext?.TraceIdentifier ?? Guid.NewGuid().ToString("N"));
        var handlers = serviceProvider.GetServices<IDomainEventConsumer<TEvent>>().ToArray();
        DomainEventLog.Dispatching(logger, typeof(TEvent).FullName ?? typeof(TEvent).Name, domainEvent.EventId, handlers.Length);
        foreach (var handler in handlers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await handler.HandleAsync(domainEvent, context, cancellationToken);
        }
    }
}

internal static partial class DomainEventLog
{
    [LoggerMessage(3200, LogLevel.Debug, "Dispatching domain event {EventType} ({EventId}) to {HandlerCount} handlers.")]
    public static partial void Dispatching(ILogger logger, string eventType, Guid eventId, int handlerCount);
}
