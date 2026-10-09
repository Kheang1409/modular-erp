using System.Security.Claims;
using Erp.Abstractions.Context;
using Erp.Abstractions.Events;
using Erp.Infrastructure.Events;
using Erp.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.UnitTests;

public sealed class DomainEventDispatcherTests
{
    [Fact]
    public async Task DispatcherRunsHandlersInOrderAndPropagatesExecutionContext()
    {
        var tenantId = TenantId.New();
        var companyId = new CompanyId(Guid.NewGuid());
        var userId = Guid.NewGuid();
        var recorder = new EventRecorder();
        var httpContext = new DefaultHttpContext { TraceIdentifier = "event-correlation" };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = httpContext });
        services.AddSingleton<ITenantContext>(new FixedTenantContext(tenantId));
        services.AddSingleton<ICompanyContext>(new FixedCompanyContext(companyId));
        services.AddSingleton(recorder);
        services.AddScoped<IDomainEventConsumer<TestEvent>, FirstHandler>();
        services.AddScoped<IDomainEventConsumer<TestEvent>, SecondHandler>();
        services.AddScoped<IDomainEventDispatcher, InProcessDomainEventDispatcher>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>()
            .DispatchAsync(new TestEvent(Guid.NewGuid(), DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.Equal(["first", "second"], recorder.Calls);
        Assert.Equal(tenantId, recorder.Context!.TenantId);
        Assert.Equal(companyId, recorder.Context.CompanyId);
        Assert.Equal(new UserId(userId), recorder.Context.UserId);
        Assert.Equal("event-correlation", recorder.Context.CorrelationId);
    }

    [Fact]
    public async Task DispatcherPropagatesHandlerFailure()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddSingleton<ITenantContext>(new FixedTenantContext(TenantId.New()));
        services.AddSingleton<ICompanyContext>(new FixedCompanyContext(new CompanyId(Guid.NewGuid())));
        services.AddScoped<IDomainEventConsumer<TestEvent>, FailingHandler>();
        services.AddScoped<IDomainEventDispatcher, InProcessDomainEventDispatcher>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>()
                .DispatchAsync(new TestEvent(Guid.NewGuid(), DateTimeOffset.UtcNow), CancellationToken.None));
        Assert.Equal("handler failed", exception.Message);
    }

    private sealed record TestEvent(Guid EventId, DateTimeOffset OccurredAtUtc) : IDomainEvent;
    private sealed class EventRecorder
    {
        public List<string> Calls { get; } = [];
        public DomainEventContext? Context { get; set; }
    }
    private sealed class FirstHandler(EventRecorder recorder) : IDomainEventConsumer<TestEvent>
    {
        public Task HandleAsync(TestEvent domainEvent, DomainEventContext context, CancellationToken cancellationToken)
        {
            recorder.Calls.Add("first");
            recorder.Context = context;
            return Task.CompletedTask;
        }
    }
    private sealed class SecondHandler(EventRecorder recorder) : IDomainEventConsumer<TestEvent>
    {
        public Task HandleAsync(TestEvent domainEvent, DomainEventContext context, CancellationToken cancellationToken)
        {
            recorder.Calls.Add("second");
            return Task.CompletedTask;
        }
    }
    private sealed class FailingHandler : IDomainEventConsumer<TestEvent>
    {
        public Task HandleAsync(TestEvent domainEvent, DomainEventContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("handler failed");
    }
    private sealed class FixedTenantContext(TenantId tenantId) : ITenantContext
    {
        public TenantId? TenantId { get; } = tenantId;
    }
    private sealed class FixedCompanyContext(CompanyId companyId) : ICompanyContext
    {
        public CompanyId? CompanyId { get; } = companyId;
    }
}
