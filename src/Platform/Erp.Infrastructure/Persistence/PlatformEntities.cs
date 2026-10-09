using Microsoft.AspNetCore.Identity;

namespace Erp.Infrastructure.Persistence;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class ApplicationRole : IdentityRole<Guid>, ITenantOwned
{
    public Guid TenantId { get; set; }
    public bool IsSystem { get; set; }
}

public sealed class RolePermissionAssignment : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid RoleId { get; set; }
    public required string Permission { get; set; }
}

public sealed class AuditLog : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? UserId { get; set; }
    public required string Action { get; set; }
    public required string EntityType { get; set; }
    public required string EntityId { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public required string CorrelationId { get; set; }
    public string? IpAddress { get; set; }
    public string? MetadataJson { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
}

public sealed class DocumentSequence : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public required string Key { get; set; }
    public required string PrefixTemplate { get; set; }
    public int Padding { get; set; }
    public long NextValue { get; set; }
}

public sealed class CompanyCurrency : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public required string Symbol { get; set; }
    public int MinorUnits { get; set; }
    public bool IsActive { get; set; }
    public required string ConcurrencyStamp { get; set; }
}

public sealed class CompanyExchangeRate : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public required string FromCurrencyCode { get; set; }
    public required string ToCurrencyCode { get; set; }
    public decimal Rate { get; set; }
    public DateOnly EffectiveDate { get; set; }
}

public sealed class Attachment : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; }
    public required string StorageKey { get; set; }
    public required string EntityType { get; set; }
    public required string EntityId { get; set; }
    public Guid UploadedBy { get; set; }
    public DateTimeOffset UploadedAtUtc { get; set; }
}

public sealed class BackgroundJob : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public required string Name { get; set; }
    public required string PayloadJson { get; set; }
    public required string State { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; }
    public DateTimeOffset ExecuteAfterUtc { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public required string CorrelationId { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? LastError { get; set; }
}

public sealed class OutboxMessage : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public required string EventType { get; set; }
    public required string PayloadJson { get; set; }
    public required string CorrelationId { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
    public DateTimeOffset? DeadLetteredAtUtc { get; set; }
    public DateTimeOffset NextAttemptAtUtc { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
}

public sealed class Tenant
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public required string TimeZoneId { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public interface ITenantOwned
{
    Guid TenantId { get; }
}

public sealed class Company : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public required string Code { get; set; }
    public required string BaseCurrencyCode { get; set; }
    public required string TimeZoneId { get; set; }
    public bool IsActive { get; set; }
}

public sealed class Branch : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public required string Name { get; set; }
    public required string Code { get; set; }
    public required string TimeZoneId { get; set; }
    public bool IsActive { get; set; }
}

public sealed class UserCompanyMembership : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid CompanyId { get; set; }
    public bool IsDefault { get; set; }
}

public sealed class InstalledModule
{
    public Guid Id { get; set; }
    public required string ModuleId { get; set; }
    public required string Version { get; set; }
    public DateTimeOffset InstalledAtUtc { get; set; }
    public Guid? InstalledBy { get; set; }
    public bool Enabled { get; set; }
    public required string Checksum { get; set; }
}
