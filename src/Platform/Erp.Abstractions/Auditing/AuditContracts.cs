namespace Erp.Abstractions.Auditing;

public interface IAuditLogWriter
{
    void Append(AuditWriteEntry entry);
    Task FlushAsync(CancellationToken cancellationToken);
}

public interface IAuditLogReader
{
    Task<AuditPage> ReadAsync(int offset, int limit, CancellationToken cancellationToken);
}

public interface IAuthenticationAuditService
{
    Task<bool> RecordSignInAsync(Guid userId, bool succeeded, bool lockedOut, CancellationToken cancellationToken);
    Task<bool> RecordLogoutAsync(Guid userId, CancellationToken cancellationToken);
}

public sealed record AuditWriteEntry(
    string Action,
    string EntityType,
    string EntityId,
    string? MetadataJson,
    string? BeforeJson,
    string? AfterJson);

public sealed record AuditEntry(
    Guid Id,
    Guid? CompanyId,
    Guid? UserId,
    string Action,
    string EntityType,
    string EntityId,
    DateTimeOffset TimestampUtc,
    string CorrelationId,
    string? IpAddress,
    string? MetadataJson,
    string? BeforeJson,
    string? AfterJson);

public sealed record AuditPage(int Offset, int Limit, int Total, IReadOnlyList<AuditEntry> Items);
