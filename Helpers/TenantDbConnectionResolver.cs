using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using ApprovalPO.Models;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.AspNetCore.WebUtilities;

namespace ApprovalPO.Helpers;

/// <summary>
/// Resolves Firebird connection strings from the AWS tenant-config API (DynamoDB JSON or plain JSON) and caches per tenant code.
/// Also parses optional <c>email</c> + <c>proaccEmail</c> SMTP overrides from the same payload (aligned with ProAccScanner).
/// The API is called with query <c>tenantCode</c> (same as the browser/Postman URL for this API).
/// </summary>
public sealed class TenantDbConnectionResolver
{
    /// <summary>Named <see cref="IHttpClientFactory"/> client for tenant-config HTTP calls.</summary>
    public const string HttpClientName = "TenantBootstrapConfig";

    /// <summary>Query parameter name required by <c>proacc-tenant-config-api</c> (camelCase).</summary>
    public const string TenantQueryParameter = "tenantCode";

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ConcurrentDictionary<string, TenantResolvedPayload> _cache = new(StringComparer.OrdinalIgnoreCase);

    public TenantDbConnectionResolver(IConfiguration configuration, IHttpClientFactory httpFactory)
    {
        _configuration = configuration;
        _httpFactory = httpFactory;
    }

    public async Task<string> GetConnectionStringForTenantAsync(string tenantCode, CancellationToken cancellationToken = default)
    {
        tenantCode = (tenantCode ?? "").Trim();
        if (string.IsNullOrWhiteSpace(tenantCode))
            throw new InvalidOperationException("Tenant code is required.");

        if (_cache.TryGetValue(tenantCode, out var cached))
            return cached.ConnectionString;

        var baseUrl = (_configuration["TenantBootstrap:AwsApiBaseUrl"] ?? "").Trim();
        var apiKey = (_configuration["TenantBootstrap:AwsApiKey"] ?? "").Trim();
        var fbUser = (_configuration["TenantBootstrap:FirebirdUser"] ?? "").Trim();
        var fbPass = (_configuration["TenantBootstrap:FirebirdPassword"] ?? "").Trim();

        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("TenantBootstrap:AwsApiBaseUrl is required.");
        if (string.IsNullOrWhiteSpace(fbUser))
            throw new InvalidOperationException("TenantBootstrap:FirebirdUser is required.");
        if (string.IsNullOrWhiteSpace(fbPass))
            throw new InvalidOperationException("TenantBootstrap:FirebirdPassword is required.");

        var requestUri = QueryHelpers.AddQueryString(baseUrl, TenantQueryParameter, tenantCode);

        var http = _httpFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, requestUri);
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.TryAddWithoutValidation("x-api-key", apiKey);

        using var res = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var raw = (await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
        if (raw.Length > 0 && raw[0] == '\uFEFF')
            raw = raw.Substring(1);

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Tenant AWS call failed ({(int)res.StatusCode}): {Truncate(raw, 2000)}");

        using var json = JsonDocument.Parse(raw);
        JsonDocument? innerDoc = null;
        try
        {
            var root = ResolveTenantPayload(json.RootElement, ref innerDoc);
            if (!TryFindDatabaseAttribute(root, out var databaseAttr))
            {
                var healthHint = LooksLikeApiHealthEnvelope(raw, root)
                    ? "This URL returned a health/status JSON (e.g. status + service name only), not the tenant record with Firebird settings. Point TenantBootstrap:AwsApiBaseUrl at the invoke URL that returns the full tenant payload (the JSON that includes database / dbHost / dbPath). "
                    : "";
                throw new InvalidOperationException(
                    healthHint +
                    $"Tenant JSON missing 'database' (after unwrapping API Gateway / nested objects). " +
                    $"Request used ?{TenantQueryParameter}=. Raw: {Truncate(raw, 900)}");
            }

            var db = UnwrapDynamoMap(databaseAttr);
            if (!TryReadDatabaseFields(db, out var dbPath, out var dbHost, out var dbCharset, out var dbPort, out var dbDialect))
                throw new InvalidOperationException("Tenant JSON missing required database fields.");

            if (string.IsNullOrWhiteSpace(dbPath) || string.IsNullOrWhiteSpace(dbHost) || string.IsNullOrWhiteSpace(dbCharset) || dbPort <= 0 || dbDialect <= 0)
                throw new InvalidOperationException("Tenant JSON missing required database fields.");

            var csb = new FbConnectionStringBuilder
            {
                UserID = fbUser,
                Password = fbPass,
                Database = $"{dbHost}:{dbPath}",
                Port = dbPort,
                Dialect = dbDialect,
                Charset = dbCharset,
                Pooling = true,
            };
            var conn = csb.ConnectionString;
            var email = TryParseTenantEmailFromRoot(root);
            var dashboardModules = TryParseDashboardModulesFromRoot(root);
            var adminDashboardModules = TryParseNamedDashboardModulesFromRoot(root, "adminDashboardModules");
            var maintenanceDashboardModules = TryParseNamedDashboardModulesFromRoot(root, "maintenanceDashboardModules");
            var userRoles = TryParseUserRolesFromRoot(root);
            var openAiSecretRef = TryParseOpenAiSecretRefFromRoot(root);
            var sqlApi = TryParseSqlApiFromRoot(root);
            _cache[tenantCode] = new TenantResolvedPayload
            {
                ConnectionString = conn,
                Email = email,
                DashboardModules = dashboardModules,
                AdminDashboardModules = adminDashboardModules,
                MaintenanceDashboardModules = maintenanceDashboardModules,
                UserRoles = userRoles,
                OpenAiApiKeySecretRef = openAiSecretRef,
                SqlApi = sqlApi
            };
            return conn;
        }
        finally
        {
            innerDoc?.Dispose();
        }
    }

    /// <summary>SMTP-related overrides from the last successful tenant fetch (same cache as connection string).</summary>
    public async Task<TenantEmailOverride?> GetTenantEmailOverrideAsync(string tenantCode, CancellationToken cancellationToken = default)
    {
        tenantCode = (tenantCode ?? "").Trim();
        if (string.IsNullOrWhiteSpace(tenantCode))
            return null;

        if (_cache.TryGetValue(tenantCode, out var hit))
            return hit.Email;

        await GetConnectionStringForTenantAsync(tenantCode, cancellationToken).ConfigureAwait(false);
        return _cache.TryGetValue(tenantCode, out var hit2) ? hit2.Email : null;
    }

    /// <summary>Secrets Manager id/ARN for the OpenAI API key from the tenant payload (<c>openai.openaiApiKeySecretRef</c>), if present.</summary>
    public async Task<string?> GetOpenAiApiKeySecretRefAsync(string tenantCode, CancellationToken cancellationToken = default)
    {
        tenantCode = (tenantCode ?? "").Trim();
        if (string.IsNullOrWhiteSpace(tenantCode))
            return null;

        if (_cache.TryGetValue(tenantCode, out var hit))
            return hit.OpenAiApiKeySecretRef;

        await GetConnectionStringForTenantAsync(tenantCode, cancellationToken).ConfigureAwait(false);
        return _cache.TryGetValue(tenantCode, out var hit2) ? hit2.OpenAiApiKeySecretRef : null;
    }

    /// <summary>Optional SQL Accounting HTTP API config from the tenant payload (<c>sqlApi</c>), if present.</summary>
    public async Task<TenantSqlApiConfig?> GetTenantSqlApiAsync(string tenantCode, CancellationToken cancellationToken = default)
    {
        tenantCode = (tenantCode ?? "").Trim();
        if (string.IsNullOrWhiteSpace(tenantCode))
            return null;

        if (_cache.TryGetValue(tenantCode, out var hit))
            return hit.SqlApi;

        await GetConnectionStringForTenantAsync(tenantCode, cancellationToken).ConfigureAwait(false);
        return _cache.TryGetValue(tenantCode, out var hit2) ? hit2.SqlApi : null;
    }

    /// <summary>Dashboard module visibility flags from tenant payload, if present.</summary>
    public async Task<TenantDashboardModules?> GetTenantDashboardModulesAsync(string tenantCode, CancellationToken cancellationToken = default)
    {
        tenantCode = (tenantCode ?? "").Trim();
        if (string.IsNullOrWhiteSpace(tenantCode))
            return null;

        if (_cache.TryGetValue(tenantCode, out var hit))
            return hit.DashboardModules;

        await GetConnectionStringForTenantAsync(tenantCode, cancellationToken).ConfigureAwait(false);
        return _cache.TryGetValue(tenantCode, out var hit2) ? hit2.DashboardModules : null;
    }

    /// <summary>
    /// Returns role-specific dashboard module flags when present:
    /// - maintenance -> <c>maintenanceDashboardModules</c> fallback to <c>dashboardModules</c>
    /// - admin/other -> <c>adminDashboardModules</c> fallback to <c>dashboardModules</c>
    /// </summary>
    public async Task<TenantDashboardModules?> GetTenantDashboardModulesForRoleAsync(
        string tenantCode,
        bool isMaintenance,
        CancellationToken cancellationToken = default)
    {
        tenantCode = (tenantCode ?? "").Trim();
        if (string.IsNullOrWhiteSpace(tenantCode))
            return null;

        if (!_cache.TryGetValue(tenantCode, out var hit))
        {
            await GetConnectionStringForTenantAsync(tenantCode, cancellationToken).ConfigureAwait(false);
            _cache.TryGetValue(tenantCode, out hit);
        }

        if (hit is null)
            return null;

        if (isMaintenance)
            return hit.MaintenanceDashboardModules ?? hit.DashboardModules;

        return hit.AdminDashboardModules ?? hit.DashboardModules;
    }

    /// <summary>
    /// Per-tenant role allowlists. Returns null when the tenant payload has no <c>userRoles</c>
    /// block at all (i.e. legacy mode: caller should treat every signed-in user as Admin).
    /// </summary>
    public async Task<TenantUserRoles?> GetTenantUserRolesAsync(string tenantCode, CancellationToken cancellationToken = default)
    {
        tenantCode = (tenantCode ?? "").Trim();
        if (string.IsNullOrWhiteSpace(tenantCode))
            return null;

        if (_cache.TryGetValue(tenantCode, out var hit))
            return hit.UserRoles;

        await GetConnectionStringForTenantAsync(tenantCode, cancellationToken).ConfigureAwait(false);
        return _cache.TryGetValue(tenantCode, out var hit2) ? hit2.UserRoles : null;
    }

    /// <summary>
    /// API Gateway/Lambda often returns <c>{ "statusCode": 200, "body": "{\"database\":...}" }</c>.
    /// DynamoDB GetItem style APIs may wrap rows in <c>Item</c>.
    /// </summary>
    private static JsonElement ResolveTenantPayload(JsonElement root, ref JsonDocument? innerDoc)
    {
        if (TryGetJsonPropertyIgnoreCase(root, "body", out var bodyEl))
        {
            if (bodyEl.ValueKind == JsonValueKind.String)
            {
                var s = bodyEl.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    var text = MaybeDecodeApiGatewayBody(root, s.Trim());
                    innerDoc = JsonDocument.Parse(text);
                    root = innerDoc.RootElement;
                }
            }
            else if (bodyEl.ValueKind == JsonValueKind.Object)
            {
                root = bodyEl;
            }
        }

        if (TryGetJsonPropertyIgnoreCase(root, "Item", out var item) && item.ValueKind == JsonValueKind.Object)
            root = item;

        if (TryGetJsonPropertyIgnoreCase(root, "data", out var dataEl) && dataEl.ValueKind == JsonValueKind.String)
        {
            var s = dataEl.GetString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                innerDoc?.Dispose();
                innerDoc = JsonDocument.Parse(s.Trim());
                root = innerDoc.RootElement;
            }
        }

        if (TryGetJsonPropertyIgnoreCase(root, "data", out var dataObj) && dataObj.ValueKind == JsonValueKind.Object)
            root = dataObj;

        return root;
    }

    private static string MaybeDecodeApiGatewayBody(JsonElement envelopeRoot, string bodyText)
    {
        if (!TryGetJsonPropertyIgnoreCase(envelopeRoot, "isBase64Encoded", out var b64))
            return bodyText;
        if (b64.ValueKind != JsonValueKind.True && b64.ValueKind != JsonValueKind.False)
            return bodyText;
        if (b64.ValueKind != JsonValueKind.True)
            return bodyText;
        try
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(bodyText));
        }
        catch
        {
            return bodyText;
        }
    }

    private static bool TryGetJsonPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var p in obj.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }

        return false;
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max)
            return s;
        return s[..max] + "…";
    }

    /// <summary>
    /// Finds <c>database</c> on the root or inside common wrappers (<c>tenant</c>, <c>payload</c>, stringified JSON values, etc.).
    /// </summary>
    private static bool TryFindDatabaseAttribute(JsonElement root, out JsonElement databaseAttr) =>
        TryFindSectionRecursive(root, "database", depth: 0, maxDepth: 8, out databaseAttr);

    private static TenantEmailOverride? TryParseTenantEmailFromRoot(JsonElement root)
    {
        TenantEmailOverride? merged = null;

        if (TryFindSectionRecursive(root, "email", depth: 0, maxDepth: 8, out var emailAttr))
            merged = ParseTenantEmailMap(UnwrapDynamoMap(emailAttr));

        if (TryFindSectionRecursive(root, "proaccEmail", depth: 0, maxDepth: 8, out var proaccAttr))
            merged = MergeTenantEmail(merged, ParseTenantEmailMap(UnwrapDynamoMap(proaccAttr)));

        if (merged is null || !TenantEmailOverrideHasAnyValue(merged))
            return null;

        return merged;
    }

    /// <summary>
    /// Reads the optional <c>sqlApi</c> section (host/region/service/useTls + inline accessKey/secretKey
    /// and/or <c>sqlApiCredentialsSecretRef</c>). Returns null when the tenant has no SQL API block at all.
    /// </summary>
    private static TenantSqlApiConfig? TryParseSqlApiFromRoot(JsonElement root)
    {
        if (!TryFindSectionRecursive(root, "sqlApi", depth: 0, maxDepth: 8, out var attr)
            && !TryFindSectionRecursive(root, "sqlapi", depth: 0, maxDepth: 8, out attr))
            return null;

        var map = UnwrapDynamoMap(attr);
        if (map.ValueKind != JsonValueKind.Object)
            return null;

        string? First(params string[] names)
        {
            foreach (var n in names)
            {
                if (TryGetScalar(map, n, out var v) && !string.IsNullOrWhiteSpace(v))
                    return v.Trim();
            }
            return null;
        }

        var cfg = new TenantSqlApiConfig
        {
            Host = First("host", "sqlApiHost"),
            Region = First("region", "sqlApiRegion"),
            Service = First("service", "sqlApiService"),
            AccessKey = First("accessKey", "sqlApiAccessKey", "SQL_API_ACCESS_KEY"),
            SecretKey = First("secretKey", "sqlApiSecretKey", "SQL_API_SECRET_KEY"),
            CredentialsSecretRef = First("sqlApiCredentialsSecretRef", "credentialsSecretRef", "secretRef"),
        };

        var useTls = First("useTls", "sqlApiUseTls");
        if (useTls is not null)
        {
            var t = useTls.ToLowerInvariant();
            cfg.UseTls = t is "1" or "true" or "yes" or "on";
        }

        // Treat an empty/uninformative block as "no SQL API".
        if (string.IsNullOrWhiteSpace(cfg.Host)
            && !cfg.HasInlineKeys
            && string.IsNullOrWhiteSpace(cfg.CredentialsSecretRef))
            return null;

        return cfg;
    }

    private static string? TryParseOpenAiSecretRefFromRoot(JsonElement root)
    {
        if (TryFindSectionRecursive(root, "openai", depth: 0, maxDepth: 8, out var openAiAttr))
        {
            var map = UnwrapDynamoMap(openAiAttr);
            if (TryGetScalar(map, "openaiApiKeySecretRef", out var secretRef) && !string.IsNullOrWhiteSpace(secretRef))
                return secretRef.Trim();
        }
        return null;
    }

    private static TenantEmailOverride MergeTenantEmail(TenantEmailOverride? baseline, TenantEmailOverride next)
    {
        baseline ??= new TenantEmailOverride();
        if (next.SmtpHost != null)
            baseline.SmtpHost = next.SmtpHost;
        if (next.SmtpPort != null)
            baseline.SmtpPort = next.SmtpPort;
        if (next.SmtpSenderEmail != null)
            baseline.SmtpSenderEmail = next.SmtpSenderEmail;
        if (next.SmtpAppPasswordSecretRef != null)
            baseline.SmtpAppPasswordSecretRef = next.SmtpAppPasswordSecretRef;
        if (next.SmtpCredentialsSecretRef != null)
            baseline.SmtpCredentialsSecretRef = next.SmtpCredentialsSecretRef;
        if (next.OtpEnabled != null)
            baseline.OtpEnabled = next.OtpEnabled;
        return baseline;
    }

    private static bool TenantEmailOverrideHasAnyValue(TenantEmailOverride o) =>
        o.SmtpHost is not null || o.SmtpPort is not null || o.SmtpSenderEmail is not null ||
        o.SmtpAppPasswordSecretRef is not null || o.SmtpCredentialsSecretRef is not null || o.OtpEnabled is not null;

    private static TenantEmailOverride ParseTenantEmailMap(JsonElement map)
    {
        var o = new TenantEmailOverride();

        if (TryGetScalar(map, "smtpHost", out var host) && !string.IsNullOrWhiteSpace(host))
            o.SmtpHost = host.Trim();

        if (TryGetScalar(map, "smtpPort", out var portText) && int.TryParse(portText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0)
            o.SmtpPort = port;

        if (TryGetScalar(map, "smtpSenderEmail", out var sender) && !string.IsNullOrWhiteSpace(sender))
            o.SmtpSenderEmail = sender.Trim();

        if (TryGetScalar(map, "smtpAppPasswordSecretRef", out var secretRef) && !string.IsNullOrWhiteSpace(secretRef))
            o.SmtpAppPasswordSecretRef = secretRef.Trim();

        if (TryGetScalar(map, "smtpCredentialsSecretRef", out var credRef) && !string.IsNullOrWhiteSpace(credRef))
            o.SmtpCredentialsSecretRef = credRef.Trim();

        if (TryGetScalar(map, "otpEnabled", out var otpText))
            o.OtpEnabled = otpText.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || otpText.Trim() == "1";

        return o;
    }

    private static TenantDashboardModules? TryParseDashboardModulesFromRoot(JsonElement root)
    {
        TenantDashboardModules? parsed = null;

        // 1) Preferred clean JSON: dashboardModules.{...}
        if (TryFindSectionRecursive(root, "dashboardModules", depth: 0, maxDepth: 8, out var directAttr))
        {
            parsed = ParseDashboardModulesMap(UnwrapDynamoMap(directAttr));
        }

        // 2) Nested under features: features.dashboardModules.{...}
        if (parsed is null && TryFindSectionRecursive(root, "features", depth: 0, maxDepth: 8, out var featuresAttrForNested))
        {
            var featuresMap = UnwrapDynamoMap(featuresAttrForNested);
            if (TryGetJsonPropertyIgnoreCase(featuresMap, "dashboardModules", out var nestedAttr))
            {
                parsed = ParseDashboardModulesMap(UnwrapDynamoMap(nestedAttr));
            }
        }

        // 3) Fallback for MaintenanceScanner only: tenant features.enableScanner (legacy ProAccScanner flag).
        if ((parsed is null || parsed.MaintenanceScanner is null)
            && TryFindSectionRecursive(root, "features", depth: 0, maxDepth: 8, out var featuresAttrForLegacy))
        {
            var featuresMap = UnwrapDynamoMap(featuresAttrForLegacy);
            if (TryGetJsonPropertyIgnoreCase(featuresMap, "enableScanner", out var enableScannerEl)
                && TryCoerceDynamoScalar(enableScannerEl, out var rawEnable))
            {
                var enable = ParseBoolOrNull(rawEnable);
                if (enable is not null)
                {
                    parsed = new TenantDashboardModules
                    {
                        PurchaseApproval = parsed?.PurchaseApproval,
                        SalesApproval = parsed?.SalesApproval,
                        ScanPo = parsed?.ScanPo,
                        ReceivedGoods = parsed?.ReceivedGoods,
                        MaintenanceScanner = enable,
                    };
                }
            }
        }

        return parsed;
    }

    private static TenantDashboardModules? TryParseNamedDashboardModulesFromRoot(JsonElement root, string sectionName)
    {
        if (TryFindSectionRecursive(root, sectionName, depth: 0, maxDepth: 8, out var directAttr))
            return ParseDashboardModulesMap(UnwrapDynamoMap(directAttr));

        if (TryFindSectionRecursive(root, "features", depth: 0, maxDepth: 8, out var featuresAttr))
        {
            var featuresMap = UnwrapDynamoMap(featuresAttr);
            if (TryGetJsonPropertyIgnoreCase(featuresMap, sectionName, out var nestedAttr))
                return ParseDashboardModulesMap(UnwrapDynamoMap(nestedAttr));
        }
        return null;
    }

    private static bool? ParseBoolOrNull(string? raw)
    {
        var t = (raw ?? "").Trim();
        if (t.Length == 0) return null;
        if (bool.TryParse(t, out var b)) return b;
        if (t is "1" or "yes" or "YES" or "on" or "ON") return true;
        if (t is "0" or "no" or "NO" or "off" or "OFF") return false;
        return null;
    }

    /// <summary>
    /// Reads optional <c>userRoles.admin / userRoles.maintenance</c> allowlists from the tenant JSON.
    /// Accepts plain JSON arrays of strings, DynamoDB <c>L/S</c> lists, or comma-separated strings.
    /// Returns <c>null</c> when the tenant payload has no <c>userRoles</c> block at all (legacy mode).
    /// </summary>
    private static TenantUserRoles? TryParseUserRolesFromRoot(JsonElement root)
    {
        if (!TryFindSectionRecursive(root, "userRoles", depth: 0, maxDepth: 8, out var userRolesAttr))
            return null;

        var map = UnwrapDynamoMap(userRolesAttr);
        if (map.ValueKind != JsonValueKind.Object)
            return null;

        var admins = ParseEmailListFromUserRoles(map, "admin", "admins", "administrator", "administrators");
        var maint = ParseEmailListFromUserRoles(map, "maintenance", "maintenanceTeam", "maintenance_team", "scanner", "scanners");

        return new TenantUserRoles
        {
            Admin = admins,
            Maintenance = maint,
        };
    }

    private static IReadOnlyList<string> ParseEmailListFromUserRoles(JsonElement userRolesMap, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!TryGetJsonPropertyIgnoreCase(userRolesMap, key, out var attr))
                continue;

            var unwrapped = UnwrapDynamoMap(attr);

            if (unwrapped.ValueKind == JsonValueKind.Array)
                return ReadStringArray(unwrapped);

            // DynamoDB list form: { "L": [ { "S": "a@x.com" }, ... ] }
            if (unwrapped.ValueKind == JsonValueKind.Object
                && TryGetJsonPropertyIgnoreCase(unwrapped, "L", out var listAttr)
                && listAttr.ValueKind == JsonValueKind.Array)
                return ReadStringArray(listAttr);

            // Comma-separated string fallback ("a@x.com, b@x.com").
            if (TryCoerceDynamoScalar(unwrapped, out var csv) && !string.IsNullOrWhiteSpace(csv))
                return SplitCommaSeparated(csv);
        }
        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement arr)
    {
        var list = new List<string>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
        {
            string? value = null;
            if (item.ValueKind == JsonValueKind.String)
                value = item.GetString();
            else if (item.ValueKind == JsonValueKind.Object
                     && TryCoerceDynamoScalar(item, out var raw))
                value = raw;

            value = (value ?? "").Trim();
            if (value.Length > 0)
                list.Add(value);
        }
        return list;
    }

    private static IReadOnlyList<string> SplitCommaSeparated(string csv)
    {
        var parts = csv.Split(new[] { ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var list = new List<string>(parts.Length);
        foreach (var p in parts)
        {
            var t = p.Trim();
            if (t.Length > 0)
                list.Add(t);
        }
        return list;
    }

    private static TenantDashboardModules? ParseDashboardModulesMap(JsonElement map)
    {
        if (map.ValueKind != JsonValueKind.Object)
            return null;

        bool? ReadBool(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (!TryGetJsonPropertyIgnoreCase(map, key, out var el))
                    continue;
                if (!TryCoerceDynamoScalar(el, out var raw))
                    continue;
                var t = (raw ?? "").Trim();
                if (t.Length == 0)
                    continue;
                if (bool.TryParse(t, out var b))
                    return b;
                if (t is "1" or "yes" or "YES" or "on" or "ON")
                    return true;
                if (t is "0" or "no" or "NO" or "off" or "OFF")
                    return false;
            }
            return null;
        }

        var modules = new TenantDashboardModules
        {
            PurchaseApproval = ReadBool("purchaseApproval", "approvalPO", "approvalPo"),
            SalesApproval = ReadBool("salesApproval", "approvalSO", "approvalSo"),
            ScanPo = ReadBool("scanPO", "scanPo"),
            ReceivedGoods = ReadBool("receivedGoods", "receivedGood"),
            MaintenanceScanner = ReadBool("maintenanceScanner", "stockScanner", "scanner"),
        };

        return modules.PurchaseApproval is null
               && modules.SalesApproval is null
               && modules.ScanPo is null
               && modules.ReceivedGoods is null
               && modules.MaintenanceScanner is null
            ? null
            : modules;
    }

    private static bool TryFindSectionRecursive(JsonElement e, string sectionName, int depth, int maxDepth, out JsonElement sectionAttr)
    {
        sectionAttr = default;
        if (depth > maxDepth || e.ValueKind != JsonValueKind.Object)
            return false;

        if (TryGetJsonPropertyIgnoreCase(e, sectionName, out sectionAttr))
            return true;

        foreach (var p in e.EnumerateObject())
        {
            switch (p.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    if (TryFindSectionRecursive(p.Value, sectionName, depth + 1, maxDepth, out sectionAttr))
                        return true;
                    break;
                case JsonValueKind.String:
                    var s = p.Value.GetString();
                    if (string.IsNullOrWhiteSpace(s))
                        break;
                    var t = s.Trim();
                    if (!t.StartsWith('{'))
                        break;
                    try
                    {
                        using var nested = JsonDocument.Parse(t);
                        if (TryFindSectionRecursive(nested.RootElement, sectionName, depth + 1, maxDepth, out sectionAttr))
                            return true;
                    }
                    catch
                    {
                        /* ignore invalid embedded JSON */
                    }

                    break;
            }
        }

        return false;
    }

    /// <summary>Detects minimal JSON like <c>{"status":"ok","service":"proacc-tenant-config-api"}</c> with no tenant payload.</summary>
    private static bool LooksLikeApiHealthEnvelope(string raw, JsonElement root)
    {
        if (raw.Contains("\"database\"", StringComparison.OrdinalIgnoreCase))
            return false;
        if (root.ValueKind != JsonValueKind.Object)
            return false;
        var hasStatus = TryGetJsonPropertyIgnoreCase(root, "status", out _);
        var hasService = TryGetJsonPropertyIgnoreCase(root, "service", out _);
        return hasStatus && hasService;
    }

    /// <summary>DynamoDB AttributeValue maps use a single <c>M</c> property wrapping the record.</summary>
    private static JsonElement UnwrapDynamoMap(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty("M", out var inner) &&
            inner.ValueKind == JsonValueKind.Object)
            return inner;

        return el;
    }

    private static bool TryReadDatabaseFields(
        JsonElement db,
        out string dbPath,
        out string dbHost,
        out string dbCharset,
        out int dbPort,
        out int dbDialect)
    {
        dbPath = "";
        dbHost = "";
        dbCharset = "";
        dbPort = 0;
        dbDialect = 0;

        if (!TryGetScalar(db, "dbPath", out dbPath))
            return false;
        if (!TryGetScalar(db, "dbHost", out dbHost))
            return false;
        if (!TryGetScalar(db, "dbCharset", out dbCharset))
            return false;
        if (!TryGetScalar(db, "dbPort", out var portText) || !int.TryParse(portText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out dbPort))
            return false;
        if (!TryGetScalar(db, "dbDialect", out var dialectText) || !int.TryParse(dialectText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out dbDialect))
            return false;

        dbPath = dbPath.Trim();
        dbHost = dbHost.Trim();
        dbCharset = dbCharset.Trim();
        return true;
    }

    private static bool TryGetScalar(JsonElement parent, string name, out string value)
    {
        value = "";
        if (!TryGetJsonPropertyIgnoreCase(parent, name, out var el))
            return false;
        return TryCoerceDynamoScalar(el, out value);
    }

    /// <summary>Reads plain JSON scalars or DynamoDB wrappers (<c>S</c>, <c>N</c>, <c>BOOL</c>).</summary>
    private static bool TryCoerceDynamoScalar(JsonElement el, out string value)
    {
        value = "";
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                value = el.GetString() ?? "";
                return true;
            case JsonValueKind.Number:
                value = el.GetRawText();
                return true;
            case JsonValueKind.True:
                value = "true";
                return true;
            case JsonValueKind.False:
                value = "false";
                return true;
            case JsonValueKind.Object:
                if (el.TryGetProperty("S", out var s))
                {
                    value = s.ValueKind == JsonValueKind.String ? (s.GetString() ?? "") : s.GetRawText();
                    return true;
                }

                if (el.TryGetProperty("N", out var n))
                {
                    value = n.ValueKind == JsonValueKind.String ? (n.GetString() ?? "") : n.GetRawText();
                    return true;
                }

                if (el.TryGetProperty("BOOL", out var b))
                {
                    value = b.ValueKind switch
                    {
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        _ => b.GetBoolean() ? "true" : "false",
                    };
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>Opens a tenant Firebird connection (caller disposes).</summary>
    public async Task<FbConnection> OpenConnectionAsync(
        IConfiguration configuration,
        string? operation = null,
        CancellationToken cancellationToken = default)
    {
        var tenant = TenantConfigurationHelper.RequireTenantCode(configuration, operation);
        var connStr = await GetConnectionStringForTenantAsync(tenant, cancellationToken).ConfigureAwait(false);
        var conn = new FbConnection(connStr);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        return conn;
    }
}
