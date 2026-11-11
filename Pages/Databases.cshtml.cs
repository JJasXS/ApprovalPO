using System.ComponentModel.DataAnnotations;
using ApprovalPO.Helpers;
using ApprovalPO.Models;
using ApprovalPO.Services.Databases;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApprovalPO.Pages;

public class DatabasesModel : PageModel
{
    private readonly IConfiguration _configuration;
    private readonly IApprovalDatabaseStore _store;
    private readonly IActiveCompanyContext _activeCompany;
    private readonly TenantDbConnectionResolver _tenantResolver;

    public DatabasesModel(
        IConfiguration configuration,
        IApprovalDatabaseStore store,
        IActiveCompanyContext activeCompany,
        TenantDbConnectionResolver tenantResolver)
    {
        _configuration = configuration;
        _store = store;
        _activeCompany = activeCompany;
        _tenantResolver = tenantResolver;
    }

    public string TenantCode { get; private set; } = "";
    public string SecretId { get; private set; } = "";
    public int CompanyCount { get; private set; }
    public string? ActiveCompanyId { get; private set; }
    public IReadOnlyList<CompanyDatabaseEntry> Companies { get; private set; } = Array.Empty<CompanyDatabaseEntry>();
    public string? StatusMessage { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool IsEditing { get; private set; }

    [BindProperty]
    public CompanyInput Input { get; set; } = new();

    public class CompanyInput
    {
        public string? Id { get; set; }

        [Required, Display(Name = "Display name")]
        public string DisplayName { get; set; } = "";

        [Required, Display(Name = "Host")]
        public string FbHost { get; set; } = "";

        [Required, Display(Name = "Database path")]
        public string FbPath { get; set; } = "";

        [Required, Display(Name = "User")]
        public string FbUser { get; set; } = "SYSDBA";

        [Display(Name = "Password")]
        public string? FbPassword { get; set; }

        public int FbPort { get; set; } = 3050;
        public int FbDialect { get; set; } = 3;
        public string FbCharset { get; set; } = "UTF8";
    }

    public async Task OnGetAsync(string? edit, CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(edit))
            BeginEdit(edit);
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!ModelState.IsValid)
        {
            IsEditing = true;
            return Page();
        }

        var bundle = await _store.GetBundleAsync(cancellationToken).ConfigureAwait(false);
        var id = (Input.Id ?? "").Trim();
        CompanyDatabaseEntry? existing = null;
        if (!string.IsNullOrWhiteSpace(id))
            existing = bundle.Companies.FirstOrDefault(c =>
                string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

        var password = (Input.FbPassword ?? "").Trim();
        if (string.IsNullOrWhiteSpace(password) && existing is not null)
            password = existing.FbPassword;

        if (string.IsNullOrWhiteSpace(password))
        {
            ModelState.AddModelError("Input.FbPassword", "Password is required for new companies.");
            IsEditing = true;
            return Page();
        }

        if (existing is null)
        {
            existing = new CompanyDatabaseEntry
            {
                Id = string.IsNullOrWhiteSpace(id) ? "cmp_" + Guid.NewGuid().ToString("N")[..10] : id,
            };
            bundle.Companies.Add(existing);
        }

        existing.DisplayName = Input.DisplayName.Trim();
        existing.FbHost = Input.FbHost.Trim();
        existing.FbPath = Input.FbPath.Trim();
        existing.FbUser = string.IsNullOrWhiteSpace(Input.FbUser) ? "SYSDBA" : Input.FbUser.Trim();
        existing.FbPassword = password;
        existing.FbPort = Input.FbPort > 0 ? Input.FbPort : 3050;
        existing.FbDialect = Input.FbDialect > 0 ? Input.FbDialect : 3;
        existing.FbCharset = string.IsNullOrWhiteSpace(Input.FbCharset) ? "UTF8" : Input.FbCharset.Trim();
        existing.UseTenantBootstrap = false;

        // Drop ephemeral bootstrap-only placeholder once a real company is saved.
        bundle.Companies.RemoveAll(c =>
            c.UseTenantBootstrap
            && string.Equals(c.Id, "bootstrap", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(c.Id, existing.Id, StringComparison.OrdinalIgnoreCase));

        try
        {
            await _store.SaveBundleAsync(bundle, cancellationToken).ConfigureAwait(false);
            await _activeCompany.SetActiveCompanyAsync(existing.Id, cancellationToken).ConfigureAwait(false);
            StatusMessage = $"Saved “{existing.DisplayName}”. Secret now has {bundle.Companies.Count} company database(s).";
            Input = new CompanyInput();
            IsEditing = false;
        }
        catch (Exception ex)
        {
            ErrorMessage = "Could not save to Secrets Manager: " + ex.Message;
            IsEditing = true;
        }

        await LoadAsync(cancellationToken).ConfigureAwait(false);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id, CancellationToken cancellationToken)
    {
        id = (id ?? "").Trim();
        var bundle = await _store.GetBundleAsync(cancellationToken).ConfigureAwait(false);
        var removed = bundle.Companies.RemoveAll(c =>
            string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            ErrorMessage = "Company not found.";
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            return Page();
        }

        try
        {
            await _store.SaveBundleAsync(bundle, cancellationToken).ConfigureAwait(false);
            StatusMessage = $"Removed company. Secret now has {bundle.Companies.Count} company database(s).";
        }
        catch (Exception ex)
        {
            ErrorMessage = "Could not update Secrets Manager: " + ex.Message;
        }

        await LoadAsync(cancellationToken).ConfigureAwait(false);
        return Page();
    }

    public async Task<IActionResult> OnPostTestAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        IsEditing = true;

        var password = (Input.FbPassword ?? "").Trim();
        if (string.IsNullOrWhiteSpace(password) && !string.IsNullOrWhiteSpace(Input.Id))
        {
            var existing = await _store.FindAsync(Input.Id!, cancellationToken).ConfigureAwait(false);
            password = existing?.FbPassword ?? "";
        }

        var probe = new CompanyDatabaseEntry
        {
            Id = Input.Id ?? "probe",
            DisplayName = Input.DisplayName,
            FbHost = Input.FbHost,
            FbPath = Input.FbPath,
            FbUser = Input.FbUser,
            FbPassword = password,
            FbPort = Input.FbPort,
            FbDialect = Input.FbDialect,
            FbCharset = Input.FbCharset,
            UseTenantBootstrap = false,
        };

        try
        {
            var cs = TenantDbConnectionResolver.BuildCompanyConnectionString(probe);
            await using var conn = new FbConnection(cs);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            StatusMessage = "Connection OK.";
        }
        catch (Exception ex)
        {
            ErrorMessage = "Connection failed: " + ex.Message;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostPersistBootstrapAsync(CancellationToken cancellationToken)
    {
        var bundle = await _store.GetBundleAsync(cancellationToken).ConfigureAwait(false);
        var bootstrap = bundle.Companies.FirstOrDefault(c => c.UseTenantBootstrap)
            ?? bundle.Companies.FirstOrDefault();
        if (bootstrap is null)
        {
            ErrorMessage = "Nothing to persist.";
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            return Page();
        }

        try
        {
            if (bootstrap.UseTenantBootstrap
                || string.IsNullOrWhiteSpace(bootstrap.FbHost)
                || string.IsNullOrWhiteSpace(bootstrap.FbPath))
            {
                var tenant = TenantConfigurationHelper.RequireTenantCode(_configuration);
                var cs = await _tenantResolver.GetConnectionStringForTenantAsync(tenant, cancellationToken)
                    .ConfigureAwait(false);
                var csb = new FbConnectionStringBuilder(cs);
                var database = (csb.Database ?? "").Trim();
                var colon = database.IndexOf(':');
                if (colon > 0)
                {
                    bootstrap.FbHost = database[..colon].Trim();
                    bootstrap.FbPath = database[(colon + 1)..].Trim();
                }

                if (string.IsNullOrWhiteSpace(bootstrap.FbUser))
                    bootstrap.FbUser = csb.UserID;
                if (string.IsNullOrWhiteSpace(bootstrap.FbPassword))
                    bootstrap.FbPassword = csb.Password;
                bootstrap.FbPort = csb.Port > 0 ? csb.Port : 3050;
                bootstrap.FbDialect = csb.Dialect > 0 ? csb.Dialect : 3;
                if (!string.IsNullOrWhiteSpace(csb.Charset))
                    bootstrap.FbCharset = csb.Charset;
                bootstrap.UseTenantBootstrap = false;
                if (string.IsNullOrWhiteSpace(bootstrap.DisplayName))
                    bootstrap.DisplayName = tenant;
            }

            await _store.SaveBundleAsync(bundle, cancellationToken).ConfigureAwait(false);
            StatusMessage = $"Persisted current connection into Secrets Manager ({_store.GetSecretId(TenantConfigurationHelper.RequireTenantCode(_configuration))}).";
        }
        catch (Exception ex)
        {
            ErrorMessage = "Could not persist bootstrap: " + ex.Message;
        }

        await LoadAsync(cancellationToken).ConfigureAwait(false);
        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        TenantCode = TenantConfigurationHelper.GetTenantCodeOrEmpty(_configuration);
        SecretId = string.IsNullOrWhiteSpace(TenantCode) ? "" : _store.GetSecretId(TenantCode);
        var bundle = await _store.GetBundleAsync(cancellationToken).ConfigureAwait(false);
        Companies = bundle.Companies;
        CompanyCount = bundle.Companies.Count;
        var active = await _activeCompany.GetActiveCompanyAsync(cancellationToken).ConfigureAwait(false);
        ActiveCompanyId = active?.Id;
    }

    private void BeginEdit(string id)
    {
        var match = Companies.FirstOrDefault(c =>
            string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return;

        IsEditing = true;
        Input = new CompanyInput
        {
            Id = match.Id,
            DisplayName = match.DisplayName,
            FbHost = match.FbHost,
            FbPath = match.FbPath,
            FbUser = match.FbUser,
            FbPassword = "",
            FbPort = match.FbPort,
            FbDialect = match.FbDialect,
            FbCharset = match.FbCharset,
        };
    }
}
