using Erp.Abstractions.Files;

namespace Erp.Infrastructure.Files;

public sealed class LocalFileStorage : IFileStorage
{
    private readonly string _rootPath;

    public LocalFileStorage(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_rootPath);
    }

    public async Task WriteAsync(string storageKey, Stream content, string contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        _ = contentType;
        var path = ResolvePath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                await content.CopyToAsync(output, cancellationToken);
            File.Move(temporaryPath, path, false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public async Task ReadAsync(string storageKey, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        await using var input = new FileStream(ResolvePath(storageKey), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        await input.CopyToAsync(destination, cancellationToken);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(ResolvePath(storageKey));
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(ResolvePath(storageKey)));
    }

    private string ResolvePath(string storageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        if (Path.IsPathRooted(storageKey)) throw new ArgumentException("Storage keys must be relative.", nameof(storageKey));
        var normalized = storageKey.Replace('/', Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(_rootPath, normalized));
        var rootPrefix = _rootPath.EndsWith(Path.DirectorySeparatorChar) ? _rootPath : _rootPath + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Storage key escapes the configured root.", nameof(storageKey));
        return path;
    }
}
