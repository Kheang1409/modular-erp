namespace Erp.Abstractions.Sequences;

public interface ISequenceService
{
    Task<string> NextAsync(SequenceRequest request, CancellationToken cancellationToken);
}

public sealed record SequenceRequest(
    string Key,
    string PrefixTemplate,
    int Padding,
    DateOnly BusinessDate);
