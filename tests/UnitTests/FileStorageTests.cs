using System.Text;
using Erp.Infrastructure.Files;

namespace Erp.UnitTests;

public sealed class FileStorageTests
{
    [Fact]
    public async Task LocalStorageRoundTripsContentAndRejectsTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "erp-storage-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new LocalFileStorage(root);
            await using var content = new MemoryStream(Encoding.UTF8.GetBytes("stored safely"));
            await storage.WriteAsync("tenant/company/file", content, "text/plain", CancellationToken.None);
            Assert.True(await storage.ExistsAsync("tenant/company/file", CancellationToken.None));

            await using var downloaded = new MemoryStream();
            await storage.ReadAsync("tenant/company/file", downloaded, CancellationToken.None);
            Assert.Equal("stored safely", Encoding.UTF8.GetString(downloaded.ToArray()));
            await Assert.ThrowsAsync<ArgumentException>(() => storage.WriteAsync(
                "../escape", new MemoryStream([]), "application/octet-stream", CancellationToken.None));

            await storage.DeleteAsync("tenant/company/file", CancellationToken.None);
            Assert.False(await storage.ExistsAsync("tenant/company/file", CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
