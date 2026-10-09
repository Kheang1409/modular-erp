using Erp.Abstractions.Context;

namespace Erp.Core;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
