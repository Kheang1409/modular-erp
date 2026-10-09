using Erp.Abstractions.Jobs;

namespace Erp.ModuleSystem;

public sealed class BackgroundJobRegistry : IBackgroundJobRegistry
{
    private readonly Dictionary<string, BackgroundJobDefinition> _jobs = new(StringComparer.Ordinal);
    public IReadOnlyCollection<BackgroundJobDefinition> Jobs => _jobs.Values;

    public void Add<THandler, TPayload>(string name)
        where THandler : class, IBackgroundJobHandler<TPayload>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 150 || !name.Contains('.', StringComparison.Ordinal) || name.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
            throw new ArgumentException("Job names must be dot-qualified ASCII identifiers up to 150 characters.", nameof(name));
        if (!_jobs.TryAdd(name, new(name, typeof(THandler), typeof(TPayload))))
            throw new InvalidOperationException($"Background job '{name}' is already registered.");
    }
}
