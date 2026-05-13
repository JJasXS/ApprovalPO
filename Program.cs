using ApprovalPO.Configuration;
using ApprovalPO.Hosting;
using ApprovalPO.Options;
using ApprovalPO.Services;
using ApprovalPO.Helpers;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using System.Security.Cryptography.X509Certificates;

// Optional .env: typically only TENANT_CODE (see appsettings / .env.example). All other config lives in appsettings.json.
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

// Tenant id: use TENANT_CODE from environment (.env) only — always wins over appsettings when set.
var tenantFromEnv = (Environment.GetEnvironmentVariable("TENANT_CODE") ?? builder.Configuration["TENANT_CODE"])?.Trim();
if (!string.IsNullOrEmpty(tenantFromEnv))
{
    builder.Configuration.AddInMemoryCollection(
        new Dictionary<string, string?> { ["TenantBootstrap:TenantCode"] = tenantFromEnv });
}

// Empty process env vars (TenantBootstrap__*) override appsettings with blanks — restore from appsettings*.json.
TenantBootstrapJsonReinstate.Apply(builder.Configuration, builder.Environment.ContentRootPath);

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
builder.Services.AddScoped<ISyUserLoginValidator, SyUserLoginValidator>();
builder.Services.AddScoped<IPurchaseOrderCatalog, PurchaseOrderCatalogService>();

builder.Services.Configure<Microsoft.AspNetCore.Antiforgery.AntiforgeryOptions>(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
});

builder.Services.Configure<ApprovalOptions>(builder.Configuration.GetSection(ApprovalOptions.SectionName));
builder.Services.Configure<WebPushOptions>(builder.Configuration.GetSection(WebPushOptions.SectionName));
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));
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

builder.Services.AddRazorPages(options =>
{
    // All pages under /Pages require authentication unless explicitly allowed below.
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Index");
    options.Conventions.AllowAnonymousToPage("/Login");
    options.Conventions.AllowAnonymousToPage("/Logout");
    options.Conventions.AllowAnonymousToPage("/Error");
});

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
        if (!builder.Environment.IsDevelopment())
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    });

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseForwardedHeaders();

app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "SAMEORIGIN");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    await next();
});

// In Development, skip HTTPS redirection so LAN/mobile testing via http://192.168.x.x:5288 works
// (otherwise browsers may redirect to https:// with no dev cert). Production uses HTTPS below.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.MapWebPushEndpoints();

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
        await resolver.GetConnectionStringForTenantAsync(tenant).ConfigureAwait(false);
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

await WarmupTenantConnectionStringAsync();
app.Run();
