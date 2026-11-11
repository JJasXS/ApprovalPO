using ApprovalPO.Models;

namespace ApprovalPO.Services.Databases;

public interface IActiveCompanyContext
{
    const string CookieName = "ApprovalPO.ActiveDbId";

    string? ActiveCompanyId { get; }

    Task<CompanyDatabaseEntry?> GetActiveCompanyAsync(CancellationToken cancellationToken = default);

    Task SetActiveCompanyAsync(string companyId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CompanyDatabaseEntry>> ListCompaniesAsync(CancellationToken cancellationToken = default);
}
