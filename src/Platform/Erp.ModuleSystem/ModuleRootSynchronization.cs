using System.Security.Cryptography;
using System.Text;

namespace Erp.ModuleSystem;

internal static class ModuleRootSynchronization
{
    private static readonly TimeSpan AcquisitionTimeout = TimeSpan.FromSeconds(30);

    internal static IDisposable Acquire(string rootPath)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (OperatingSystem.IsWindows()) canonicalRoot = canonicalRoot.ToUpperInvariant();
        var rootHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRoot)));
        var name = (OperatingSystem.IsWindows() ? "Global\\" : "") + "Erp.ModuleRoot." + rootHash;
        var mutex = new Mutex(initiallyOwned: false, name);
        try
        {
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(AcquisitionTimeout);
            }
            catch (AbandonedMutexException exception)
            {
                mutex.ReleaseMutex();
                throw new InvalidOperationException(
                    "A previous module operation was interrupted. Inspect the module packages, state, and hidden backups before retrying.",
                    exception);
            }

            if (!acquired)
                throw new InvalidOperationException("Another module operation is in progress. Retry after it has finished.");
            return new Lease(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private sealed class Lease(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;

        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref _mutex, null);
            if (owned is null) return;
            try { owned.ReleaseMutex(); }
            finally { owned.Dispose(); }
        }
    }
}
