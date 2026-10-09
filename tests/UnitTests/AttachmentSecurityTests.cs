using System.Security.Claims;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Files;
using Erp.Infrastructure.Files;
using Erp.Infrastructure.Persistence;
using Erp.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Erp.UnitTests;

public sealed class AttachmentSecurityTests
{
    [Fact]
    public async Task RequiredMalwareScannerFailsClosedBeforeStorageWrite()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var company = new CompanyId(Guid.NewGuid());
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        await using var database = new PlatformDbContext(options, new TenantContext(tenant));
        var storage = new RecordingStorage();
        var userId = Guid.NewGuid();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
        };
        var service = new AttachmentService(
            database, new TenantContext(tenant), new CompanyContext(company),
            new HttpContextAccessor { HttpContext = http }, new Clock(), storage,
            Options.Create(new FileStorageOptions { MaximumFileSizeBytes = 1024, RequireMalwareScanner = true }),
            new AuditWriter(), new UnavailableFileMalwareScanner());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UploadAsync(
            new("document.txt", "text/plain", 4, "Document", "1"),
            new MemoryStream("test"u8.ToArray()), CancellationToken.None));
        Assert.False(storage.Written);
    }

    private sealed record TenantContext(TenantId Value) : ITenantContext { public TenantId? TenantId => Value; }
    private sealed record CompanyContext(CompanyId Value) : ICompanyContext { public CompanyId? CompanyId => Value; }
    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
    private sealed class AuditWriter : IAuditLogWriter
    {
        public void Append(AuditWriteEntry entry) { }
        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class RecordingStorage : IFileStorage
    {
        public bool Written { get; private set; }
        public Task WriteAsync(string storageKey, Stream content, string contentType, CancellationToken cancellationToken) { Written = true; return Task.CompletedTask; }
        public Task ReadAsync(string storageKey, Stream destination, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
