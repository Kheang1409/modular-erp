namespace Erp.Abstractions.Files;

public interface IFileStorage
{
    Task WriteAsync(string storageKey, Stream content, string contentType, CancellationToken cancellationToken);
    Task ReadAsync(string storageKey, Stream destination, CancellationToken cancellationToken);
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken);
}

public interface IFileMalwareScanner
{
    bool IsAvailable { get; }
    Task ScanAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken);
}

public interface IAttachmentService
{
    Task<AttachmentInfo> UploadAsync(UploadAttachmentRequest request, Stream content, CancellationToken cancellationToken);
    Task<IReadOnlyList<AttachmentInfo>> ListAsync(string entityType, string entityId, CancellationToken cancellationToken);
    Task<AttachmentDownload?> GetDownloadAsync(Guid attachmentId, CancellationToken cancellationToken);
}

public sealed record UploadAttachmentRequest(
    string FileName,
    string ContentType,
    long Size,
    string EntityType,
    string EntityId);

public sealed record AttachmentInfo(
    Guid Id,
    string FileName,
    string ContentType,
    long Size,
    string EntityType,
    string EntityId,
    Guid UploadedBy,
    DateTimeOffset UploadedAtUtc);

public sealed record AttachmentDownload(AttachmentInfo Attachment, string StorageKey);
