using Amazon.S3;
using Amazon.S3.Model;
using Erp.Abstractions.Files;

namespace Erp.Infrastructure.Files;

public sealed class S3CompatibleFileStorage(IAmazonS3 client, string bucket) : IFileStorage
{
    private readonly string _bucket = string.IsNullOrWhiteSpace(bucket)
        ? throw new ArgumentException("An S3 bucket is required.", nameof(bucket))
        : bucket;

    public async Task WriteAsync(string storageKey, Stream content, string contentType, CancellationToken cancellationToken)
    {
        ValidateKey(storageKey);
        ArgumentNullException.ThrowIfNull(content);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = storageKey,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false
        }, cancellationToken);
    }

    public async Task ReadAsync(string storageKey, Stream destination, CancellationToken cancellationToken)
    {
        ValidateKey(storageKey);
        ArgumentNullException.ThrowIfNull(destination);
        using var response = await client.GetObjectAsync(_bucket, storageKey, cancellationToken);
        await response.ResponseStream.CopyToAsync(destination, cancellationToken);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        ValidateKey(storageKey);
        return client.DeleteObjectAsync(_bucket, storageKey, cancellationToken);
    }

    public async Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken)
    {
        ValidateKey(storageKey);
        try
        {
            _ = await client.GetObjectMetadataAsync(_bucket, storageKey, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static void ValidateKey(string storageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        if (storageKey.StartsWith('/') || storageKey.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Storage key is invalid.", nameof(storageKey));
    }
}
