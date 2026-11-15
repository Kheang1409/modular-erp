using System.Security.Cryptography;
using System.Text;

namespace Erp.Modules.Hcm;

public static class PayrollAdjustmentSnapshotIntegrity
{
    public static string ComputeRevision(string serializedSnapshot)
    {
        ArgumentNullException.ThrowIfNull(serializedSnapshot);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serializedSnapshot)));
    }

    public static bool Matches(string serializedSnapshot, string revision) =>
        !string.IsNullOrWhiteSpace(revision) &&
        string.Equals(ComputeRevision(serializedSnapshot), revision, StringComparison.Ordinal);
}
