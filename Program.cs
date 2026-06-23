using ApprovalPO.Authorization;
using ApprovalPO.Configuration;
using ApprovalPO.Hosting;
using ApprovalPO.Services.Ocr;
using ApprovalPO.Models;
using ApprovalPO.Options;
using ApprovalPO.Services;
using ApprovalPO.Helpers;
using Amazon;
using Amazon.SecretsManager;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using System.Security.Cryptography.X509Certificates;

// Optional .env: TENANT_CODE, FIREBIRD_PASSWORD (see .env.example). Other defaults in appsettings.json.
// Prefer project folder .env before publish/bin — dotnet exec runs from repo root but BaseDirectory is bin\.
foreach (var envPath in new[]
         {
             Path.Combine(Directory.GetCurrentDirectory(), ".env"),
             Path.Combine(AppContext.BaseDirectory, ".env"),
         })
{
    if (File.Exists(envPath))
    {
        DotNetEnv.Env.Load(envPath);
        break;
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

// Configure request size limits to prevent large upload attacks and DoS
builder.ConfigureRequestSizeLimits();

// Tenant id: use TENANT_CODE from environment (.env) only — always wins over appsettings when set.
var tenantFromEnv = (Environment.GetEnvironmentVariable("TENANT_CODE") ?? builder.Configuration["TENANT_CODE"])?.Trim();
var bootstrapFromEnv = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
if (!string.IsNullOrEmpty(tenantFromEnv))
    bootstrapFromEnv["TenantBootstrap:TenantCode"] = tenantFromEnv;

var fbPassFromEnv = (Environment.GetEnvironmentVariable("FIREBIRD_PASSWORD")
    ?? Environment.GetEnvironmentVariable("TenantBootstrap__FirebirdPassword"))?.Trim();
if (!string.IsNullOrEmpty(fbPassFromEnv))
    bootstrapFromEnv["TenantBootstrap:FirebirdPassword"] = fbPassFromEnv;

if (bootstrapFromEnv.Count > 0)
    builder.Configuration.AddInMemoryCollection(bootstrapFromEnv);

// Empty process env vars (TenantBootstrap__*) override appsettings with blanks — restore from appsettings*.json.
TenantBootstrapJsonReinstate.Apply(builder.Configuration, builder.Environment.ContentRootPath);

ProductionKestrelEndpoints.Apply(builder);

// Phone testing: run run-lan.ps1 (sets APPROVALPO_LISTEN_LAN=true). Binds HTTP+HTTPS on all interfaces, not localhost-only.
if (builder.Environment.IsDevelopment()
    && string.Equals(Environment.GetEnvironmentVariable("APPROVALPO_LISTEN_LAN"), "true", StringComparison.OrdinalIgnoreCase))
{
    var lanRoot = builder.Environment.ContentRootPath;
    var lanPem = (Environment.GetEnvironmentVariable("APPROVALPO_TLS_PEM") ?? Path.Combine(lanRoot, ".certs", "dev.pem")).Trim();
    var lanKey = (Environment.GetEnvironmentVariable("APPROVALPO_TLS_KEY") ?? Path.Combine(lanRoot, ".certs", "dev.key")).Trim();

    var lanHttp = ApprovalKestrelPorts.Http(builder.Configuration);
    var lanHttps = ApprovalKestrelPorts.Https(builder.Configuration);
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(lanHttp);
        options.ListenAnyIP(lanHttps, listen =>
        {
            if (File.Exists(lanPem) && File.Exists(lanKey))
                listen.UseHttps(X509Certificate2.CreateFromPemFile(lanPem, lanKey));
            else
                listen.UseHttps();
        });
    });
    builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
    builder.WebHost.PreferHostingUrls(false);
    Console.WriteLine($"[LAN] Listening on all interfaces: http://*:{lanHttp}  https://*:{lanHttps} (use https on your phone for camera).");
}

// Development: optional "trusted" HTTPS for LAN IPs — place mkcert PEM here or set APPROVALPO_TLS_PEM / APPROVALPO_TLS_KEY.
//   mkdir .certs && cd .certs && mkcert 192.168.x.x localhost 127.0.0.1
//   copy the generated cert to dev.pem and key to dev.key (or set env paths). Install mkcert root CA on the phone once.
if (builder.Environment.IsDevelopment())
{
    var root = builder.Environment.ContentRootPath;
    var pemPath = (Environment.GetEnvironmentVariable("APPROVALPO_TLS_PEM") ?? Path.Combine(root, ".certs", "dev.pem")).Trim();
    var keyPath = (Environment.GetEnvironmentVariable("APPROVALPO_TLS_KEY") ?? Path.Combine(root, ".certs", "dev.key")).Trim();
    if (File.Exists(pemPath) && File.Exists(keyPath))
    {
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ConfigureHttpsDefaults(https =>
            {
                try
                {
                    https.ServerCertificate = X509Certificate2.CreateFromPemFile(pemPath, keyPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[HTTPS] Could not load custom PEM ({pemPath} / {keyPath}): {ex.Message}. Falling back to the ASP.NET Core dev certificate.");
                }
            });
        });
        Console.WriteLine($"[HTTPS] Development: custom TLS from PEM files (see Program.cs comment for mkcert).");
    }
}

builder.Services.AddHttpClient(TenantDbConnectionResolver.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<TenantDbConnectionResolver>();
builder.Services.AddSingleton<IAmazonSecretsManager>(_ =>
{
    var regionName = builder.Configuration["AWS:Region"]
        ?? builder.Configuration["TenantBootstrap:SecretsManagerRegion"]
        ?? Environment.GetEnvironmentVariable("AWS_REGION")
        ?? "ap-southeast-1";
    return new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(regionName));
});
builder.Services.AddScoped<ISyUserLoginValidator, SyUserLoginValidator>();
builder.Services.AddHttpClient(nameof(ScanQrLinkResolver), client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ApprovalPO-ScanResolver/1.0");
});
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IScanQrLinkResolver, ScanQrLinkResolver>();
builder.Services.AddSingleton<IScanPoSubmitStore, ScanPoSubmitFileStore>();
builder.Services.AddScoped<IPurchaseOrderCatalog, PurchaseOrderCatalogService>();
builder.Services.AddScoped<ApprovalPO.Services.Orders.IPurchaseOrderScanQuery, ApprovalPO.Services.Orders.PurchaseOrderScanQueryService>();
builder.Services.AddScoped<ISalesOrderCatalog, SalesOrderCatalogService>();
builder.Services.AddScoped<IGoodsReceiptCatalog, GoodsReceiptCatalogService>();
builder.Services.AddHttpClient<ApprovalPO.Services.SqlApi.ISqlAccountingApi, ApprovalPO.Services.SqlApi.SqlAccountingApiClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddScoped<ApprovalPO.Services.Orders.PoToGoodsReceiptFirebirdTransferService>();
builder.Services.AddScoped<ApprovalPO.Services.Orders.IGoodsReceivedTransfer, ApprovalPO.Services.Orders.GoodsReceivedTransferService>();
builder.Services.AddScoped<ApprovalPO.Services.MaintenanceScanner.IMaintenanceScannerService, ApprovalPO.Services.MaintenanceScanner.MaintenanceScannerService>();
builder.Services.AddScoped<ApprovalPO.Services.Ocr.IOcrCaptureService, ApprovalPO.Services.Ocr.OcrCaptureService>();
builder.Services.AddScoped<ApprovalPO.Services.Ocr.IOcrEmailSender, ApprovalPO.Services.Ocr.OcrEmailSender>();
builder.Services.AddScoped<ApprovalPO.Services.Stock.IStockItemLookup, ApprovalPO.Services.Stock.StockItemLookupService>();
builder.Services.AddScoped<ApprovalPO.Services.Ocr.OcrScanEnrichmentService>();
builder.Services.AddHttpClient<ApprovalPO.Services.Ocr.IOpenAiVisionService, ApprovalPO.Services.Ocr.OpenAiVisionService>(client =>
{
    var seconds = builder.Configuration.GetValue<int?>("OpenAi:TimeoutSeconds") ?? 120;
    client.Timeout = TimeSpan.FromSeconds(seconds <= 0 ? 120 : seconds);
});
builder.Services.AddScoped<IUserRoleResolver, UserRoleResolver>();
builder.Services.AddScoped<IModuleAccessService, ModuleAccessService>();

builder.Services.Configure<Microsoft.AspNetCore.Antiforgery.AntiforgeryOptions>(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
});

builder.Services.Configure<ApprovalOptions>(builder.Configuration.GetSection(ApprovalOptions.SectionName));
builder.Services.Configure<WebPushOptions>(builder.Configuration.GetSection(WebPushOptions.SectionName));
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));
builder.Services.Configure<OpenAiOptions>(builder.Configuration.GetSection(OpenAiOptions.SectionName));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});
builder.Services.AddSingleton<OtpSessionStore>();
builder.Services.AddSingleton<LoginIpThrottle>();
builder.Services.AddSingleton<IOtpEmailSender, SmtpOtpEmailSender>();
builder.Services.AddSingleton<WebPushSubscriptionFileStore>();
builder.Services.AddSingleton<WebPushPendingCursorStore>();
builder.Services.AddHostedService<PendingOrderWebPushWorker>();

var razorPages = builder.Services.AddRazorPages(options =>
{
    // All pages under /Pages require authentication unless explicitly allowed below.
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Index");
    options.Conventions.AllowAnonymousToPage("/Login");
    options.Conventions.AllowAnonymousToPage("/Logout");
    options.Conventions.AllowAnonymousToPage("/Error");

    // Module access is enforced dynamically from tenant dashboard-module flags
    // (adminDashboardModules / maintenanceDashboardModules) in middleware.
    options.Conventions.AuthorizePage("/MaintenanceScanner/Index", PolicyNames.MaintenanceAccess);
});
if (builder.Environment.IsDevelopment())
    razorPages.AddRazorRuntimeCompilation();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        var sessionHours = builder.Configuration.GetValue<int?>("Approval:SessionHours") ?? 2;
        options.LoginPath = "/Login";
        options.LogoutPath = "/Logout";
        options.AccessDeniedPath = "/Login";
        options.ExpireTimeSpan = TimeSpan.FromHours(Math.Clamp(sessionHours, 1, 24));
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        var cookieSecureAlways = builder.Configuration.GetValue("Approval:CookieSecureAlways", false);
        options.Cookie.SecurePolicy = cookieSecureAlways
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
    });

builder.Services.AddAuthorization(options =>
{
    // Admin gets access to all business pages.
    options.AddPolicy(PolicyNames.AdminOnly, p =>
        p.RequireAuthenticatedUser().RequireRole(ApprovalRoles.Admin));

    // Admin OR Maintenance can use the Maintenance Scanner.
    options.AddPolicy(PolicyNames.MaintenanceAccess, p =>
        p.RequireAuthenticatedUser().RequireRole(ApprovalRoles.Admin, ApprovalRoles.Maintenance));
});

var app = builder.Build();

app.UseForwardedHeaders();

// Apply strict request validation (size limits, method validation, malicious user agents)
app.UseApprovalRequestValidation();

app.UseApprovalSecurityHeaders();

// Development: phone camera needs HTTPS — redirect LAN http → https (same path).
if (app.Environment.IsDevelopment())
{
    var redirectHttpPort = ApprovalKestrelPorts.Http(app.Configuration);
    var redirectHttpsPort = ApprovalKestrelPorts.Https(app.Configuration);
    app.Use(async (ctx, next) =>
    {
        if (HttpMethods.IsGet(ctx.Request.Method)
            && string.Equals(ctx.Request.Scheme, "http", StringComparison.OrdinalIgnoreCase)
            && ctx.Request.Host.Port == redirectHttpPort)
        {
            var host = ctx.Request.Host.Host;
            if (host is not "localhost"
                && !host.StartsWith("127.", StringComparison.Ordinal)
                && host.Contains('.', StringComparison.Ordinal))
            {
                var path = ctx.Request.Path.Value ?? "/";
                var qs = ctx.Request.QueryString.Value ?? "";
                // Use 301 (Permanent) redirect for GET requests - better security and caching
                ctx.Response.Redirect($"https://{host}:{redirectHttpsPort}{path}{qs}", permanent: true);
                return;
            }
        }
        await next();
    });
}

// Production defaults for UseHsts / UseHttpsRedirection come from appsettings.Production.json (off for HTTP-only LAN publish).
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    if (app.Configuration.GetValue("Approval:UseHsts", true))
        app.UseHsts();
    if (app.Configuration.GetValue("Approval:UseHttpsRedirection", true))
        app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();

app.Use(async (ctx, next) =>
{
    if (ctx.User.Identity?.IsAuthenticated == true)
    {
        var access = ctx.RequestServices.GetRequiredService<IModuleAccessService>();
        var allowed = await access.IsAllowedAsync(ctx.User, ctx.Request.Path, ctx.RequestAborted).ConfigureAwait(false);
        if (!allowed)
        {
            ctx.Response.Redirect("/Dashboard");
            return;
        }
    }
    await next();
});

app.UseAuthorization();
app.MapRazorPages();
// Legacy bookmark / old JS: /ScanPO/Detail?docKey= → /ScanPODetail?docKey=
app.MapGet("/ScanPO/Detail", (HttpContext ctx) =>
{
    var docKey = ctx.Request.Query["docKey"].ToString();
    return string.IsNullOrWhiteSpace(docKey)
        ? Results.Redirect("/ScanPO")
        : Results.Redirect($"/ScanPODetail?docKey={Uri.EscapeDataString(docKey)}");
});
app.MapWebPushEndpoints();

app.MapGet("/ocr-captures/{fileName}", (string fileName, HttpContext ctx, IWebHostEnvironment env) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var path = OcrCaptureService.ResolveAuthorizedFilePath(env, fileName);
    if (path is null)
        return Results.NotFound();

    var ext = Path.GetExtension(path).ToLowerInvariant();
    var contentType = ext switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "image/png"
    };
    return Results.File(path, contentType);
}).RequireAuthorization();

async Task WarmupTenantConnectionStringAsync()
{
    var tenant = (app.Configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
    if (string.IsNullOrEmpty(tenant))
        return;

    await using var scope = app.Services.CreateAsyncScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        var resolver = scope.ServiceProvider.GetRequiredService<TenantDbConnectionResolver>();
        var connStr = await resolver.GetConnectionStringForTenantAsync(tenant).ConfigureAwait(false);
        await using (var conn = new FirebirdSql.Data.FirebirdClient.FbConnection(connStr))
        {
            await conn.OpenAsync().ConfigureAwait(false);
            await PhPoSchemaBootstrap.EnsureUdfPoStatusColumnAsync(conn, logger).ConfigureAwait(false);
        }
        logger.LogInformation(
            "Tenant Firebird connection string cached for {Tenant} in {ElapsedMs} ms (startup warmup).",
            tenant,
            sw.ElapsedMilliseconds);
    }
    catch (Exception ex)
    {
        logger.LogWarning(
            ex,
            "Tenant connection warmup failed after {ElapsedMs} ms; the first PO list load may wait on the tenant-config API and Firebird.",
            sw.ElapsedMilliseconds);
    }
}

// Fire warmup after the server is already listening — does not block startup.
app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(WarmupTenantConnectionStringAsync));
app.Run();
