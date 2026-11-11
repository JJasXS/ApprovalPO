using ApprovalPO.Models;

namespace ApprovalPO.Services.Databases;

public interface IApprovalDatabaseStore
{
    string GetSecretId(string tenantCode);

    /// <summary>Loads companies from Secrets Manager; seeds an in-memory bootstrap entry when empty.</summary>
    Task<ApprovalDatabasesBundle> GetBundleAsync(CancellationToken cancellationToken = default);

    Task SaveBundleAsync(ApprovalDatabasesBundle bundle, CancellationToken cancellationToken = default);

    Task<CompanyDatabaseEntry?> FindAsync(string companyId, CancellationToken cancellationToken = default);

    Task<int> CountAsync(CancellationToken cancellationToken = default);

    void InvalidateCache();
}
