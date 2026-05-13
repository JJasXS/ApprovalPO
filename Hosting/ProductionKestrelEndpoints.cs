using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace ApprovalPO.Hosting;

/// <summary>
/// Non-Development: bind HTTP on configured ports and HTTPS when a certificate is present
/// (PEM pair or PFX next to the published app). Development keeps using launchSettings URLs.
/// Use <c>Approval:BindLoopbackOnly</c> true for localhost-only (127.0.0.1 / ::1); false for LAN (0.0.0.0).
/// </summary>
internal static class ProductionKestrelEndpoints
{
    public static void Apply(WebApplicationBuilder builder)
    {
        if (builder.Environment.IsDevelopment())
            return;

        var httpPort = builder.Configuration.GetValue("Approval:PublicHttpPort", 5288);
        var httpsPort = builder.Configuration.GetValue("Approval:PublicHttpsPort", 7298);
        var loopbackOnly = builder.Configuration.GetValue("Approval:BindLoopbackOnly", false);
        var where = loopbackOnly ? "localhost (loopback only)" : "all interfaces (LAN)";

        builder.WebHost.ConfigureKestrel((_, opts) =>
        {
            ListenHttp(opts, httpPort, loopbackOnly);

            if (!TryLoadCertificate(builder, out var cert) || cert is null)
            {
                Console.WriteLine(
                    $"[Kestrel] HTTP on {where}, port {httpPort}. HTTPS on port {httpsPort} disabled (no cert). " +
                    "Add .certs/site.pem + .certs/site.key, or tls/site.pfx + APPROVALPO_TLS_PFX_PASSWORD, or set APPROVALPO_TLS_PEM / APPROVALPO_TLS_KEY.");
                return;
            }

            ListenHttps(opts, httpsPort, loopbackOnly, cert);
            Console.WriteLine($"[Kestrel] HTTP on {where}, port {httpPort}; HTTPS on {where}, port {httpsPort}.");
        });
    }

    private static void ListenHttp(KestrelServerOptions opts, int port, bool loopbackOnly)
    {
        if (loopbackOnly)
            opts.ListenLocalhost(port);
        else
            opts.ListenAnyIP(port);
    }

    private static void ListenHttps(KestrelServerOptions opts, int port, bool loopbackOnly, X509Certificate2 cert)
    {
        if (loopbackOnly)
            opts.ListenLocalhost(port, lo => lo.UseHttps(cert));
        else
            opts.ListenAnyIP(port, lo => lo.UseHttps(cert));
    }
    private static bool TryLoadCertificate(WebApplicationBuilder builder, out X509Certificate2? cert)
    {
        cert = null;
        var baseDir = AppContext.BaseDirectory;

        var pemEnv = Environment.GetEnvironmentVariable("APPROVALPO_TLS_PEM")?.Trim();
        var keyEnv = Environment.GetEnvironmentVariable("APPROVALPO_TLS_KEY")?.Trim();
        string? pemPath;
        string? keyPath;
        if (!string.IsNullOrEmpty(pemEnv) && !string.IsNullOrEmpty(keyEnv))
        {
            pemPath = Path.IsPathRooted(pemEnv) ? pemEnv : Path.Combine(baseDir, pemEnv.TrimStart('.', '/', '\\'));
            keyPath = Path.IsPathRooted(keyEnv) ? keyEnv : Path.Combine(baseDir, keyEnv.TrimStart('.', '/', '\\'));
        }
        else
        {
            pemPath = Path.Combine(baseDir, ".certs", "site.pem");
            keyPath = Path.Combine(baseDir, ".certs", "site.key");
        }

        if (File.Exists(pemPath) && File.Exists(keyPath))
        {
            cert = X509Certificate2.CreateFromPemFile(pemPath, keyPath);
            return true;
        }

        var pfxRel = (builder.Configuration["Approval:TlsPfxPath"] ?? "tls/site.pfx").Trim();
        var pfxPath = Path.IsPathRooted(pfxRel) ? pfxRel : Path.Combine(baseDir, pfxRel.TrimStart('.', '/', '\\'));
        var pfxPwd = Environment.GetEnvironmentVariable("APPROVALPO_TLS_PFX_PASSWORD") ?? "";

        if (!File.Exists(pfxPath))
            return false;

        cert = new X509Certificate2(pfxPath, pfxPwd, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        return true;
    }
}
