using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Files;
using Erp.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.Files;

public sealed partial class AttachmentService(
    PlatformDbContext database,
    ITenantContext tenantContext,
    ICompanyContext companyContext,
    IHttpContextAccessor httpContextAccessor,
    IClock clock,
    IFileStorage storage,
    IOptions<FileStorageOptions> options,
    IAuditLogWriter auditLog,
    IFileMalwareScanner? malwareScanner = null) : IAttachmentService
{
    private readonly long _maximumSize = options.Value.MaximumFileSizeBytes > 0
        ? options.Value.MaximumFileSizeBytes
        : throw new InvalidOperationException("FileStorage:MaximumFileSizeBytes must be positive.");
    private readonly bool _requireMalwareScanner = options.Value.RequireMalwareScanner;

    public async Task<AttachmentInfo> UploadAsync(UploadAttachmentRequest request, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(content);
        var (tenantId, companyId, userId) = RequireContext();
        var fileName = ValidateFileName(request.FileName);
        var contentType = ValidateContentType(request.ContentType);
        var entityType = ValidateReference(request.EntityType, nameof(request.EntityType));
        var entityId = ValidateReference(request.EntityId, nameof(request.EntityId));
        if (request.Size is < 0 || request.Size > _maximumSize)
            throw new ArgumentOutOfRangeException(nameof(request), $"File size must be between 0 and {_maximumSize} bytes.");

        var id = Guid.NewGuid();
        var storageKey = $"{tenantId:N}/{companyId:N}/{id:N}";
        if (_requireMalwareScanner && malwareScanner?.IsAvailable != true)
            throw new InvalidOperationException("File uploads require a configured malware scanner.");
        if (malwareScanner?.IsAvailable == true)
        {
            var temporaryPath = Path.Combine(Path.GetTempPath(), $"erp-upload-{Guid.NewGuid():N}.tmp");
            try
            {
                await using var temporary = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                var counted = new CountingReadStream(content, _maximumSize);
                await counted.CopyToAsync(temporary, cancellationToken);
                EnsureDeclaredSize(counted.BytesRead, request.Size);
                temporary.Position = 0;
                await malwareScanner.ScanAsync(temporary, fileName, contentType, cancellationToken);
                temporary.Position = 0;
                await storage.WriteAsync(storageKey, temporary, contentType, cancellationToken);
            }
            finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
        }
        else
        {
            var counted = new CountingReadStream(content, _maximumSize);
            await storage.WriteAsync(storageKey, counted, contentType, cancellationToken);
            if (counted.BytesRead != request.Size)
            {
                await storage.DeleteAsync(storageKey, cancellationToken);
                throw new InvalidDataException("Declared file size does not match the uploaded content.");
            }
        }

        var attachment = new Attachment
        {
            Id = id,
            TenantId = tenantId,
            CompanyId = companyId,
            FileName = fileName,
            ContentType = contentType,
            Size = request.Size,
            StorageKey = storageKey,
            EntityType = entityType,
            EntityId = entityId,
            UploadedBy = userId,
            UploadedAtUtc = clock.UtcNow
        };
        database.Attachments.Add(attachment);
        auditLog.Append(new("attachment.uploaded", "Attachment", id.ToString(), null, null,
            JsonSerializer.Serialize(new { attachment.FileName, attachment.ContentType, attachment.Size, entityType, entityId })));
        try { await database.SaveChangesAsync(cancellationToken); }
        catch
        {
            await storage.DeleteAsync(storageKey, CancellationToken.None);
            throw;
        }
        return ToInfo(attachment);
    }

    private static void EnsureDeclaredSize(long actual, long declared)
    {
        if (actual != declared) throw new InvalidDataException("Declared file size does not match the uploaded content.");
    }

    public async Task<IReadOnlyList<AttachmentInfo>> ListAsync(string entityType, string entityId, CancellationToken cancellationToken)
    {
        var (_, companyId, _) = RequireContext();
        entityType = ValidateReference(entityType, nameof(entityType));
        entityId = ValidateReference(entityId, nameof(entityId));
        return await database.Attachments.AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.EntityType == entityType && x.EntityId == entityId)
            .OrderByDescending(x => x.UploadedAtUtc)
            .Select(x => new AttachmentInfo(x.Id, x.FileName, x.ContentType, x.Size, x.EntityType, x.EntityId, x.UploadedBy, x.UploadedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<AttachmentDownload?> GetDownloadAsync(Guid attachmentId, CancellationToken cancellationToken)
    {
        var (_, companyId, _) = RequireContext();
        var attachment = await database.Attachments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == attachmentId && x.CompanyId == companyId, cancellationToken);
        if (attachment is null || !await storage.ExistsAsync(attachment.StorageKey, cancellationToken)) return null;
        return new(ToInfo(attachment), attachment.StorageKey);
    }

    private (Guid TenantId, Guid CompanyId, Guid UserId) RequireContext()
    {
        var identifier = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(identifier, out var userId)) throw new InvalidOperationException("An authenticated user is required.");
        return (
            tenantContext.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required."),
            companyContext.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required."),
            userId);
    }

    private static string ValidateFileName(string value)
    {
        var fileName = value?.Trim() ?? string.Empty;
        if (fileName.Length is < 1 or > 255 || fileName != Path.GetFileName(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("File name is invalid.", nameof(value));
        return fileName;
    }

    private static string ValidateContentType(string value)
    {
        var contentType = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (contentType.Length is < 3 or > 200 || !ContentTypePattern().IsMatch(contentType))
            throw new ArgumentException("Content type is invalid.", nameof(value));
        return contentType;
    }

    private static string ValidateReference(string value, string parameterName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > 200 || normalized.Any(char.IsControl))
            throw new ArgumentException("Attachment reference must contain between 1 and 200 printable characters.", parameterName);
        return normalized;
    }

    private static AttachmentInfo ToInfo(Attachment value) => new(
        value.Id, value.FileName, value.ContentType, value.Size, value.EntityType, value.EntityId, value.UploadedBy, value.UploadedAtUtc);

    [GeneratedRegex("^[a-z0-9][a-z0-9!#$&^_.+-]+/[a-z0-9][a-z0-9!#$&^_.+-]+$")]
    private static partial Regex ContentTypePattern();

    private sealed class CountingReadStream(Stream inner, long maximumBytes) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Track(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Track(inner.Read(buffer));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Track(await inner.ReadAsync(buffer, cancellationToken));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Track(int count)
        {
            BytesRead += count;
            if (BytesRead > maximumBytes) throw new InvalidDataException($"File exceeds the {maximumBytes}-byte limit.");
            return count;
        }
    }
}
