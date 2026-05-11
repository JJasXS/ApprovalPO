using ApprovalPO.Configuration;
using ApprovalPO.Hosting;
using ApprovalPO.Options;
using ApprovalPO.Services;
using ApprovalPO.Helpers;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;

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

// Tenant id: use TENANT_CODE from environment (.env) only — always wins over appsettings when set.
var tenantFromEnv = (Environment.GetEnvironmentVariable("TENANT_CODE") ?? builder.Configuration["TENANT_CODE"])?.Trim();
if (!string.IsNullOrEmpty(tenantFromEnv))
{
    builder.Configuration.AddInMemoryCollection(
        new Dictionary<string, string?> { ["TenantBootstrap:TenantCode"] = tenantFromEnv });
}

// Empty process env vars (TenantBootstrap__*) override appsettings with blanks — restore from appsettings*.json.
TenantBootstrapJsonReinstate.Apply(builder.Configuration, builder.Environment.ContentRootPath);

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

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.MapWebPushEndpoints();

app.Run();
