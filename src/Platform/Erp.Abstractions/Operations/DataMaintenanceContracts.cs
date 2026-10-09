namespace Erp.Abstractions.Operations;

public interface IDataMaintenanceService
{
    Task<DataMaintenancePreview> PreviewAsync(int retentionDays, CancellationToken cancellationToken);
    Task<DataMaintenanceResult> ExecuteAsync(ExecuteDataMaintenanceRequest request, CancellationToken cancellationToken);
}

public sealed record DataMaintenancePreview(
    Guid CompanyId,
    string CompanyName,
    string CompanyCode,
    int RetentionDays,
    DateTimeOffset CutoffUtc,
    DateTimeOffset ExpiresAtUtc,
    int SuccessfulJobCount,
    int ProcessedOutboxCount,
    string ConfirmationText,
    string PreviewToken);

public sealed record ExecuteDataMaintenanceRequest(string PreviewToken, string Confirmation);

public sealed record DataMaintenanceResult(
    Guid CompanyId,
    DateTimeOffset CutoffUtc,
    int DeletedJobCount,
    int DeletedOutboxCount);
