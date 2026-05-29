using ApprovalPO.Helpers;
using ApprovalPO.Models;
using ApprovalPO.Options;
using Amazon.SecretsManager;
using MailKit.Security;

namespace ApprovalPO.Services.Email;

/// <summary>Resolved SMTP connection values after merging appsettings + tenant overrides + Secrets Manager.</summary>
public sealed record ResolvedSmtp(
    string Host,
    int Port,
    string User,
    string Password,
    bool UseSsl,
    TenantEmailOverride? TenantEmail,
    string? Error);

/// <summary>
/// Shared SMTP credential resolution (appsettings <c>Smtp</c> + tenant <c>email</c>/<c>proaccEmail</c> +
/// optional Secrets Manager bundle / app-password ref), mirroring <see cref="SmtpOtpEmailSender"/>.
/// </summary>
public static class SmtpSettingsResolver
{
    public static async Task<ResolvedSmtp> ResolveAsync(
        SmtpOptions defaults,
        TenantDbConnectionResolver tenants,
        IConfiguration configuration,
        IAmazonSecretsManager secrets,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var tenantCode = (configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        TenantEmailOverride? tenantEmail = null;
        if (!string.IsNullOrWhiteSpace(tenantCode))
        {
            try
            {
                tenantEmail = await tenants.GetTenantEmailOverrideAsync(tenantCode, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[SMTP] Could not load tenant email overrides for {Tenant}.", tenantCode);
            }
        }

        var host = (defaults.Host ?? "smtp.gmail.com").Trim();
        var port = defaults.Port <= 0 ? 587 : defaults.Port;
        var user = (defaults.User ?? "").Trim();
        var passRaw = defaults.Password ?? "";

        if (tenantEmail is not null)
        {
            if (!string.IsNullOrWhiteSpace(tenantEmail.SmtpHost))
                host = tenantEmail.SmtpHost.Trim();
            if (tenantEmail.SmtpPort is > 0)
                port = tenantEmail.SmtpPort.Value;
            if (!string.IsNullOrWhiteSpace(tenantEmail.SmtpSenderEmail))
                user = tenantEmail.SmtpSenderEmail.Trim();
        }

        TenantSmtpCredentialsSecretPayload? bundle = null;
        if (!string.IsNullOrWhiteSpace(tenantEmail?.SmtpCredentialsSecretRef))
        {
            bundle = await TenantSmtpSecretResolver
                .ResolveSmtpCredentialsSecretAsync(secrets, tenantEmail.SmtpCredentialsSecretRef, cancellationToken)
                .ConfigureAwait(false);
            if (bundle is not null)
            {
                if (!string.IsNullOrWhiteSpace(bundle.Host))
                    host = bundle.Host.Trim();
                if (bundle.Port is > 0)
                    port = bundle.Port.Value;
                if (!string.IsNullOrWhiteSpace(bundle.User))
                    user = bundle.User.Trim();
            }
        }

        string pass;
        if (!string.IsNullOrWhiteSpace(bundle?.Password))
            pass = bundle.Password;
        else
        {
            pass = (await TenantSmtpSecretResolver.ResolveSmtpPasswordAsync(
                    secrets,
                    tenantEmail?.SmtpAppPasswordSecretRef,
                    passRaw,
                    cancellationToken)
                .ConfigureAwait(false) ?? "").Replace(" ", "").Trim();
        }

        if (string.IsNullOrWhiteSpace(host))
            return new ResolvedSmtp(host, port, user, pass, defaults.UseSsl, tenantEmail, "SMTP is not configured.");

        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
            return new ResolvedSmtp(host, port, user, pass, defaults.UseSsl, tenantEmail,
                "SMTP user or password empty after tenant merge. Set tenant proaccEmail/email smtpCredentialsSecretRef, or smtpAppPasswordSecretRef + smtpSenderEmail, or Smtp:User/Smtp:Password.");

        return new ResolvedSmtp(host, port, user, pass, defaults.UseSsl, tenantEmail, null);
    }

    /// <summary>465 = implicit TLS; 587 + UseSsl → STARTTLS when available (ProAccScanner parity).</summary>
    public static SecureSocketOptions ResolveSecureSocketOptions(int port, bool enableSsl)
    {
        if (port == 465)
            return SecureSocketOptions.SslOnConnect;
        if (enableSsl)
            return SecureSocketOptions.StartTlsWhenAvailable;
        return SecureSocketOptions.None;
    }
}
