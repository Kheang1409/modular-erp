namespace Erp.Infrastructure.Jobs;

public sealed class BackgroundJobOptions
{
    public const string SectionName = "BackgroundJobs";
    public bool Enabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 2;
    public int LeaseSeconds { get; set; } = 60;
}
