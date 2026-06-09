using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using ApprovalPO.Helpers;
using ApprovalPO.Services.SqlApi;
using FirebirdSql.Data.FirebirdClient;

namespace ApprovalPO.Services.Stock;

public interface IStockItemLookup
{
    /// <summary>Canonical description for a stock item code (SQL API catalog, then Firebird ST_ITEM).</summary>
    Task<StockItemLookupResult?> LookupByCodeAsync(string code, CancellationToken cancellationToken = default);
}

public sealed record StockItemLookupResult(string Code, string Description, string Source);

/// <summary>Resolves item descriptions by code for OCR scan validation.</summary>
public sealed class StockItemLookupService : IStockItemLookup
{
    private const string DefaultStockListPath = "/stockitem";

    private readonly ISqlAccountingApi _sqlApi;
    private readonly TenantDbConnectionResolver _tenantResolver;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StockItemLookupService> _logger;

    private readonly ConcurrentDictionary<string, CatalogCache> _catalogByTenant = new(StringComparer.OrdinalIgnoreCase);

    public StockItemLookupService(
        ISqlAccountingApi sqlApi,
        TenantDbConnectionResolver tenantResolver,
        IConfiguration configuration,
        ILogger<StockItemLookupService> logger)
    {
        _sqlApi = sqlApi;
        _tenantResolver = tenantResolver;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<StockItemLookupResult?> LookupByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var trimmed = (code ?? "").Trim();
        if (trimmed.Length == 0)
            return null;

        var key = trimmed.ToUpperInvariant();
        var catalog = await GetCatalogIndexAsync(cancellationToken).ConfigureAwait(false);
        if (catalog is not null && catalog.TryGetValue(key, out var fromCatalog))
            return new StockItemLookupResult(trimmed, fromCatalog, "sqlApi");

        var fromDetail = await LookupDirectApiAsync(trimmed, cancellationToken).ConfigureAwait(false);
        if (fromDetail is not null)
            return fromDetail;

        var fromDb = await LookupFirebirdAsync(trimmed, cancellationToken).ConfigureAwait(false);
        return fromDb is null ? null : new StockItemLookupResult(trimmed, fromDb, "firebird");
    }

    /// <summary>GET <c>/stockitem/{code}</c> — includes UDFs and works when the list endpoint is empty.</summary>
    private async Task<StockItemLookupResult?> LookupDirectApiAsync(string code, CancellationToken cancellationToken)
    {
        if (!await _sqlApi.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var basePath = (_configuration["SqlApi:StockItemListPath"] ?? DefaultStockListPath).Trim().TrimEnd('/');
        if (!basePath.StartsWith('/'))
            basePath = "/" + basePath;

        var path = $"{basePath}/{Uri.EscapeDataString(code)}";
        var resp = await _sqlApi.SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccess)
        {
            _logger.LogDebug("SQL API stock detail failed for {Code} ({Status}).", code, resp.Status);
            return null;
        }

        if (!TryParseStockDetail(resp.Body, out var canonicalCode, out var description))
            return null;

        return new StockItemLookupResult(canonicalCode, description, "sqlApi");
    }

    private static bool TryParseStockDetail(string body, out string code, out string description)
    {
        code = "";
        description = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var row = UnwrapStockRow(doc.RootElement);
            if (row is null) return false;

            code = PickString(row.Value, "code", "CODE", "itemcode", "ITEMCODE");
            description = PickString(row.Value, "description", "DESCRIPTION");
            if (string.IsNullOrWhiteSpace(code)) return false;
            if (string.IsNullOrWhiteSpace(description))
                description = code.Trim();
            code = code.Trim();
            description = description.Trim();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static JsonElement? UnwrapStockRow(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("data", out var data))
            {
                if (data.ValueKind == JsonValueKind.Object) return data;
                if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0) return data[0];
            }

            if (PickString(root, "code", "CODE", "itemcode", "ITEMCODE").Length > 0)
                return root;
        }

        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            return root[0];

        return null;
    }

    private async Task<Dictionary<string, string>?> GetCatalogIndexAsync(CancellationToken cancellationToken)
    {
        var tenant = TenantConfigurationHelper.RequireTenantCode(_configuration);
        if (_catalogByTenant.TryGetValue(tenant, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            return cached.ByCode;

        if (!await _sqlApi.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var path = (_configuration["SqlApi:StockItemListPath"] ?? DefaultStockListPath).Trim();
        if (!path.StartsWith('/'))
            path = "/" + path;

        var resp = await _sqlApi.SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccess)
        {
            _logger.LogDebug("SQL API stock list failed ({Status}).", resp.Status);
            return null;
        }

        var index = ParseStockList(resp.Body);
        _catalogByTenant[tenant] = new CatalogCache(index, DateTime.UtcNow.AddMinutes(10));
        return index;
    }

    private static Dictionary<string, string> ParseStockList(string body)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            JsonElement list;
            if (root.ValueKind == JsonValueKind.Array)
                list = root;
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                list = data;
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                list = items;
            else
                return index;

            foreach (var row in list.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                var code = PickString(row, "code", "CODE", "itemcode", "ITEMCODE");
                var desc = PickString(row, "description", "DESCRIPTION");
                if (string.IsNullOrWhiteSpace(code)) continue;
                if (string.IsNullOrWhiteSpace(desc))
                    desc = code;
                index[code.Trim().ToUpperInvariant()] = desc.Trim();
            }
        }
        catch (Exception)
        {
            // ignore parse errors
        }

        return index;
    }

    private static string PickString(JsonElement row, params string[] names)
    {
        foreach (var name in names)
        {
            if (!row.TryGetProperty(name, out var el)) continue;
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
            else if (el.ValueKind == JsonValueKind.Number)
            {
                return el.GetRawText();
            }
        }

        foreach (var prop in row.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                return prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString()?.Trim() ?? "",
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    _ => ""
                };
            }
        }

        return "";
    }

    private async Task<string?> LookupFirebirdAsync(string code, CancellationToken cancellationToken)
    {
        try
        {
            await using var conn = await _tenantResolver.OpenConnectionAsync(_configuration, cancellationToken: cancellationToken).ConfigureAwait(false);
            await using var cmd = new FbCommand(
                """
                SELECT FIRST 1 TRIM(COALESCE(I.DESCRIPTION, ''))
                FROM ST_ITEM I
                WHERE UPPER(TRIM(I.CODE)) = @C
                """,
                conn);
            cmd.Parameters.Add("@C", FbDbType.VarChar).Value = code.ToUpperInvariant();
            var obj = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (obj is null or DBNull) return null;
            var desc = obj.ToString()?.Trim();
            return string.IsNullOrEmpty(desc) ? null : desc;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ST_ITEM lookup failed for {Code}.", code);
            return null;
        }
    }
    private sealed record CatalogCache(Dictionary<string, string> ByCode, DateTime ExpiresAt);
}
