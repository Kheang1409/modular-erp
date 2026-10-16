using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using Erp.Abstractions.Files;
using Erp.Infrastructure.Files;
using Microsoft.Extensions.Logging;

namespace Erp.IntegrationTests;

public sealed class FileStorageProviderTests
{
    [Fact]
    public async Task S3CompatibleProviderSatisfiesStorageContractAgainstMinio()
    {
        const string accessKey = "erp_integration";
        const string secretKey = "integration_only_secret";
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Minio");
        using var buildLoggerFactory = LoggerFactory.Create(logging =>
            logging.AddSimpleConsole(options => options.SingleLine = true).SetMinimumLevel(LogLevel.Debug));
        await using var image = new BuildKitImageFromDockerfileBuilder(
                "docker:29.8.1-cli@sha256:018edbc908e08fcc9dbf029c812c34251e9b4719e6f71ca0e5eae2a987d014ca")
            .WithContextDirectory(fixtureDirectory)
            .WithDockerfileDirectory(fixtureDirectory)
            .WithLogger(buildLoggerFactory.CreateLogger("MinioFixtureBuild"))
            .Build();
        using (var buildTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(15)))
        {
            await image.CreateAsync(buildTimeout.Token);
        }

        var container = await IntegrationContainers.StartAsync(
            new ContainerBuilder(image)
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
