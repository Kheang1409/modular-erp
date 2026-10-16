using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using Erp.Abstractions.Files;
using Erp.Infrastructure.Files;

namespace Erp.IntegrationTests;

public sealed class FileStorageProviderTests
{
    [Fact]
    public async Task S3CompatibleProviderSatisfiesStorageContractAgainstMinio()
    {
        const string accessKey = "erp_integration";
        const string secretKey = "integration_only_secret";
        var container = await IntegrationContainers.StartAsync(
            new ContainerBuilder("quay.io/minio/minio@sha256:a1ea29fa28355559ef137d71fc570e508a214ec84ff8083e39bc5428980b015e")
                .WithEnvironment("MINIO_ROOT_USER", accessKey)
                .WithEnvironment("MINIO_ROOT_PASSWORD", secretKey)
                .WithCommand("server", "/data")
                .WithPortBinding(9000, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request =>
                    request.ForPort(9000).ForPath("/minio/health/ready")))
                .Build());

        await using (container)
        {
            using var client = new AmazonS3Client(
                new BasicAWSCredentials(accessKey, secretKey),
                new AmazonS3Config
                {
                    ServiceURL = $"http://{container.Hostname}:{container.GetMappedPublicPort(9000)}",
                    ForcePathStyle = true,
                    AuthenticationRegion = "us-east-1"
                });
            var bucket = $"erp-storage-{Guid.NewGuid():N}";
            await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, CancellationToken.None);

            await AssertStorageContractAsync(new S3CompatibleFileStorage(client, bucket));
        }
    }

#pragma warning disable CA1859 // Contract tests intentionally exercise providers through IFileStorage.
    private static async Task AssertStorageContractAsync(IFileStorage storage)
    {
        const string key = "tenant/company/attachment.txt";
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("stored safely"));
        await storage.WriteAsync(key, content, "text/plain", CancellationToken.None);
        Assert.True(await storage.ExistsAsync(key, CancellationToken.None));

        await using var downloaded = new MemoryStream();
        await storage.ReadAsync(key, downloaded, CancellationToken.None);
        Assert.Equal("stored safely", Encoding.UTF8.GetString(downloaded.ToArray()));

        await Assert.ThrowsAsync<ArgumentException>(() => storage.WriteAsync(
            "../escape", new MemoryStream([]), "application/octet-stream", CancellationToken.None));

        await storage.DeleteAsync(key, CancellationToken.None);
        Assert.False(await storage.ExistsAsync(key, CancellationToken.None));
    }
#pragma warning restore CA1859
}
