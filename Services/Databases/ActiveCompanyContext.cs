using ApprovalPO.Models;
using Microsoft.AspNetCore.Http;

namespace ApprovalPO.Services.Databases;

public sealed class ActiveCompanyContext : IActiveCompanyContext
{
    private readonly IHttpContextAccessor _http;
    private readonly IApprovalDatabaseStore _store;

    public ActiveCompanyContext(IHttpContextAccessor http, IApprovalDatabaseStore store)
    {
        _http = http;
        _store = store;
    }

    public string? ActiveCompanyId
    {
        get
        {
            var ctx = _http.HttpContext;
            if (ctx is null)
                return null;

            if (ctx.Request.Cookies.TryGetValue(IActiveCompanyContext.CookieName, out var raw)
                && !string.IsNullOrWhiteSpace(raw))
                return raw.Trim();

            return null;
        }
    }

    public async Task<IReadOnlyList<CompanyDatabaseEntry>> ListCompaniesAsync(
        CancellationToken cancellationToken = default)
    {
        var bundle = await _store.GetBundleAsync(cancellationToken).ConfigureAwait(false);
        return bundle.Companies;
    }

    public async Task<CompanyDatabaseEntry?> GetActiveCompanyAsync(CancellationToken cancellationToken = default)
    {
        var companies = await ListCompaniesAsync(cancellationToken).ConfigureAwait(false);
        if (companies.Count == 0)
            return null;

        var wanted = ActiveCompanyId;
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            var match = companies.FirstOrDefault(c =>
                string.Equals(c.Id, wanted, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return companies[0];
    }

    public async Task SetActiveCompanyAsync(string companyId, CancellationToken cancellationToken = default)
    {
        companyId = (companyId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company id is required.", nameof(companyId));

        var found = await _store.FindAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (found is null)
            throw new InvalidOperationException($"Unknown company '{companyId}'.");

        var ctx = _http.HttpContext
            ?? throw new InvalidOperationException("No HTTP context to set active company cookie.");

        ctx.Response.Cookies.Append(
            IActiveCompanyContext.CookieName,
            found.Id,
            new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Secure = ctx.Request.IsHttps,
                Path = "/",
                MaxAge = TimeSpan.FromDays(180),
            });
    }
}
