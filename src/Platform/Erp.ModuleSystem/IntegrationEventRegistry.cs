using System.Text.RegularExpressions;
using Erp.Abstractions.Events;

namespace Erp.ModuleSystem;

public sealed partial class IntegrationEventRegistry : IIntegrationEventRegistry
{
    private readonly Dictionary<string, IntegrationEventDefinition> _events = new(StringComparer.Ordinal);
    public IReadOnlyCollection<IntegrationEventDefinition> Events => _events.Values;

    public void Add<TEvent>(string eventType)
        where TEvent : IIntegrationEvent
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        if (eventType.Length > 200 || !EventTypePattern().IsMatch(eventType))
            throw new ArgumentException("Integration event types must be lowercase dot-qualified names ending in a version such as '.v1'.", nameof(eventType));
        if (_events.Values.Any(x => x.PayloadType == typeof(TEvent)))
            throw new InvalidOperationException($"Integration event payload '{typeof(TEvent).FullName}' is already registered.");
        if (!_events.TryAdd(eventType, new(eventType, typeof(TEvent))))
            throw new InvalidOperationException($"Integration event type '{eventType}' is already registered.");
    }

    [GeneratedRegex("^[a-z0-9]+(?:[._-][a-z0-9]+)*\\.v[1-9][0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EventTypePattern();
}
