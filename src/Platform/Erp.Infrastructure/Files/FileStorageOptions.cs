namespace Erp.Infrastructure.Files;

public sealed class FileStorageOptions
{
    public const string SectionName = "FileStorage";
    public string Provider { get; set; } = "Local";
    public string LocalRootPath { get; set; } = ".erp/files";
    public long MaximumFileSizeBytes { get; set; } = 25 * 1024 * 1024;
    public bool RequireMalwareScanner { get; set; }
    public string? ServiceUrl { get; set; }
    public string? Region { get; set; }
    public string? Bucket { get; set; }
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public bool ForcePathStyle { get; set; } = true;
}
