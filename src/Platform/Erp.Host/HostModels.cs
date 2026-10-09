namespace Erp.Host;

public sealed record LoginForm(string Email, string Password)
{
    public string? ReturnUrl { get; init; }
}

internal static partial class HostLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Error, Message = "Unhandled request failure. TraceId: {TraceId}; FailureType: {FailureType}")]
    public static partial void UnhandledRequest(ILogger logger, string traceId, string failureType);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Bootstrap administrator {AdminEmail} was created.")]
    public static partial void BootstrapAdminCreated(ILogger logger, string adminEmail);
}
