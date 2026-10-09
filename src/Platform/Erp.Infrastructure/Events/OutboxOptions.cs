namespace Erp.Infrastructure.Events;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";
    public bool Enabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 2;
    public int LeaseSeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 10;
}
