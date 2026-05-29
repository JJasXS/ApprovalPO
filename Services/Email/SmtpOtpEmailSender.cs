using ApprovalPO.Helpers;
using ApprovalPO.Models;
using ApprovalPO.Options;
using Amazon.SecretsManager;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ApprovalPO.Services.Email;

/// <summary>
/// Sends OTP via MailKit: <c>Smtp</c> appsettings + tenant <c>email</c> / <c>proaccEmail</c> from the tenant API,
/// optional <c>smtpCredentialsSecretRef</c> (full JSON bundle), then <c>smtpAppPasswordSecretRef</c> or <c>Smtp:Password</c>
/// (same merge order as ProAccScanner / ABS_System <c>EmailHelper</c>).
/// </summary>
public sealed class SmtpOtpEmailSender : IOtpEmailSender
{
    private readonly SmtpOptions _defaults;
    private readonly TenantDbConnectionResolver _tenants;
    private readonly IConfiguration _configuration;
    private readonly IAmazonSecretsManager _secrets;
    private readonly ILogger<SmtpOtpEmailSender> _logger;

    public SmtpOtpEmailSender(
        IOptions<SmtpOptions> smtp,
        TenantDbConnectionResolver tenants,
        IConfiguration configuration,
        IAmazonSecretsManager secrets,
        ILogger<SmtpOtpEmailSender> logger)
    {
        _defaults = smtp.Value;
        _tenants = tenants;
        _configuration = configuration;
        _secrets = secrets;
        _logger = logger;
    }

    public async Task<(bool Sent, string? ErrorMessage)> SendOtpAsync(
        string toAddress,
        string otpCode,
        CancellationToken cancellationToken = default)
    {
        var tenantCode = (_configuration["TenantBootstrap:TenantCode"] ?? "").Trim();
        TenantEmailOverride? tenantEmail = null;
        if (!string.IsNullOrWhiteSpace(tenantCode))
        {
            try
            {
                tenantEmail = await _tenants.GetTenantEmailOverrideAsync(tenantCode, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SMTP] Could not load tenant email overrides for {Tenant}.", tenantCode);
            }
        }

        if (tenantEmail?.OtpEnabled == false)
        {
            _logger.LogInformation("[SMTP] Tenant email.otpEnabled is false; skipping send.");
            return (false, "OTP email is disabled for this tenant.");
        }

        var host = (_defaults.Host ?? "smtp.gmail.com").Trim();
        var port = _defaults.Port <= 0 ? 587 : _defaults.Port;
        var user = (_defaults.User ?? "").Trim();
        var passRaw = _defaults.Password ?? "";

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
                .ResolveSmtpCredentialsSecretAsync(_secrets, tenantEmail.SmtpCredentialsSecretRef, cancellationToken)
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
                    _secrets,
                    tenantEmail?.SmtpAppPasswordSecretRef,
                    passRaw,
                    cancellationToken)
                .ConfigureAwait(false) ?? "").Replace(" ", "").Trim();
        }

        if (string.IsNullOrWhiteSpace(host))
            return (false, "SMTP is not configured.");

        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            return (false,
                "SMTP user or password empty after tenant merge. Set tenant proaccEmail/email smtpCredentialsSecretRef, or smtpAppPasswordSecretRef + smtpSenderEmail, or Smtp:User/Smtp:Password.");
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("ApprovalPO", user));
            message.To.Add(MailboxAddress.Parse(toAddress));
            message.Subject = "Your approval login code";
            message.Body = new TextPart("plain")
            {
                Text = $"Your one-time code is: {otpCode}\n\nIt expires in a few minutes."
            };

            using var client = new SmtpClient();
            var secure = ResolveSecureSocketOptions(port, _defaults.UseSsl);
            await client.ConnectAsync(host, port, secure, cancellationToken).ConfigureAwait(false);
            await client.AuthenticateAsync(user, pass, cancellationToken).ConfigureAwait(false);
            await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            return (true, null);
        }
        catch (Exception ex)
        {
            LogSmtpFailure(ex);
            return (false, ex.Message);
        }
    }

    private void LogSmtpFailure(Exception ex)
    {
        var message = ex.Message;
        _logger.LogWarning(ex, "SMTP send failed: {Message}", message);
        var low = message.ToLowerInvariant();
        if (low.Contains("5.7.9") || low.Contains("534") || low.Contains("webloginrequired"))
        {
            _logger.LogInformation(
                "Gmail SMTP hint: use an App Password (2FA), or tenant smtpCredentialsSecretRef / smtpAppPasswordSecretRef + Secrets Manager (same as ProAccScanner).");
        }

        if (low.Contains("5.7.0") && low.Contains("authentication"))
        {
            _logger.LogInformation(
                "SMTP auth rejected: check tenant email + secret ref or Smtp:User/Smtp:Password; port 465 uses SSL, 587 uses STARTTLS.");
        }
    }

    /// <summary>465 = implicit TLS; 587 + UseSsl → STARTTLS when available (ProAccScanner parity).</summary>
    private static SecureSocketOptions ResolveSecureSocketOptions(int port, bool enableSsl)
    {
        if (port == 465)
            return SecureSocketOptions.SslOnConnect;
        if (enableSsl)
            return SecureSocketOptions.StartTlsWhenAvailable;
        return SecureSocketOptions.None;
    }
}
