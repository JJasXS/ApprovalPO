namespace ApprovalPO.Services.MaintenanceScanner;

public interface IMaintenanceScannerService
{
    Task<MaintenanceScanValidateResult> ValidateCodeAsync(string code, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetLocationDescriptionsAsync(CancellationToken cancellationToken = default);

    Task InsertScanDetailAsync(
        MaintenanceScanInsertRequest request,
        string operatorDisplayName,
        CancellationToken cancellationToken = default);
}
