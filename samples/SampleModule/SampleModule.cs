using System.Reflection;
using Erp.Abstractions.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Erp.Abstractions.Jobs;
using Microsoft.Extensions.Logging;
using Erp.Abstractions.Context;
using Erp.Abstractions.Events;

namespace Erp.Modules.Sample;

public sealed class SampleModule : IErpModule
{
    public ModuleManifest Manifest { get; } = new(
        "erp.sample", "Sample", "0.1.0", "Proves the public ERP module contracts.",
        "ERP Community", "Apache-2.0", "0.1.0", [],
        [SamplePermissions.Read, SamplePermissions.EventsPublish]);

    public Assembly ComponentAssembly => typeof(SampleModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<SampleGreetingService>();
        services.AddScoped<SampleHeartbeatJob>();
        services.AddScoped<IDomainEventConsumer<SampleGreetingRequested>, SampleGreetingRequestedHandler>();
        services.AddScoped<IIntegrationEventConsumer<SampleGreetingPublished>, SampleGreetingPublishedConsumer>();
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/sample", (SampleGreetingService service) =>
                Results.Ok(new SampleResponse(service.GetMessage())))
            .WithName("GetSample")
            .WithTags("Sample")
            .RequirePermission(SamplePermissions.Read);
        endpoints.MapPost("/api/v1/sample/events/greeting-requested", async (
                SampleEventRequest request,
                IDomainEventDispatcher dispatcher,
                IClock clock,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 200)
                    return Results.BadRequest(new { error = "Message must contain between 1 and 200 characters." });
                var domainEvent = new SampleGreetingRequested(Guid.NewGuid(), clock.UtcNow, request.Message);
                await dispatcher.DispatchAsync(domainEvent, cancellationToken);
                return Results.Accepted(value: new { domainEvent.EventId });
            })
            .WithName("PublishSampleGreetingRequested")
            .WithTags("Sample")
            .RequirePermission(SamplePermissions.EventsPublish);
    }

    public void ConfigurePermissions(IPermissionRegistry permissions)
    {
        permissions.Add(new(SamplePermissions.Read, "View the sample module", Manifest.Id));
        permissions.Add(new(SamplePermissions.EventsPublish, "Publish the sample domain event", Manifest.Id));
    }

    public void ConfigureNavigation(INavigationRegistry navigation)
        => navigation.Add(new("Sample", "/sample", "🧩", 100, SamplePermissions.Read));

    public void ConfigureBackgroundJobs(IBackgroundJobRegistry jobs)
        => jobs.Add<SampleHeartbeatJob, SampleHeartbeatPayload>("erp.sample.heartbeat");

    public void ConfigureIntegrationEvents(IIntegrationEventRegistry events)
        => events.Add<SampleGreetingPublished>("sample.greeting.published.v1");
}

public static class SamplePermissions
{
    public const string Read = "sample.read";
    public const string EventsPublish = "sample.events.publish";
}

public sealed class SampleGreetingService
{
    private readonly string _message = "The SampleModule is running through the ERP module contract.";
    public string GetMessage() => _message;
}

public sealed record SampleResponse(string Message);

public sealed record SampleEventRequest(string Message);

public sealed record SampleGreetingRequested(Guid EventId, DateTimeOffset OccurredAtUtc, string Message) : IDomainEvent;

public sealed record SampleGreetingPublished(Guid EventId, DateTimeOffset OccurredAtUtc, string Message) : IIntegrationEvent;

public sealed partial class SampleGreetingRequestedHandler(ILogger<SampleGreetingRequestedHandler> logger)
    : IDomainEventConsumer<SampleGreetingRequested>
{
    public Task HandleAsync(
        SampleGreetingRequested domainEvent,
        DomainEventContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GreetingObserved(logger, domainEvent.EventId, context.TenantId.Value, context.CorrelationId, domainEvent.Message);
        return Task.CompletedTask;
    }

    [LoggerMessage(4101, LogLevel.Information, "Sample greeting event {EventId} observed for tenant {TenantId} with correlation {CorrelationId}: {Message}")]
    private static partial void GreetingObserved(ILogger logger, Guid eventId, Guid tenantId, string correlationId, string message);
}

public sealed partial class SampleGreetingPublishedConsumer(ILogger<SampleGreetingPublishedConsumer> logger)
    : IIntegrationEventConsumer<SampleGreetingPublished>
{
    public Task HandleAsync(
        SampleGreetingPublished integrationEvent,
        IntegrationEventContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PublishedConsumed(logger, integrationEvent.EventId, context.TenantId.Value, context.Attempt);
        return Task.CompletedTask;
    }

    [LoggerMessage(4102, LogLevel.Information, "Sample published greeting {EventId} consumed for tenant {TenantId} on attempt {Attempt}")]
    private static partial void PublishedConsumed(ILogger logger, Guid eventId, Guid tenantId, int attempt);
}

public sealed record SampleHeartbeatPayload(string Message);

public sealed partial class SampleHeartbeatJob(ILogger<SampleHeartbeatJob> logger)
    : IBackgroundJobHandler<SampleHeartbeatPayload>
{
    public Task ExecuteAsync(
        SampleHeartbeatPayload payload,
        BackgroundJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HeartbeatExecuted(logger, context.JobId, context.TenantId.Value, payload.Message);
        return Task.CompletedTask;
    }

    [LoggerMessage(4100, LogLevel.Information, "Sample heartbeat job {JobId} executed for tenant {TenantId}: {Message}")]
    private static partial void HeartbeatExecuted(ILogger logger, Guid jobId, Guid tenantId, string message);
}
