using ApprovalPO.Helpers;
using ApprovalPO.Models;
using ApprovalPO.Options;
using Amazon.SecretsManager;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ApprovalPO.Services;

/// <summary>
/// Sends OTP via MailKit, merging <c>Smtp</c> appsettings with tenant <c>email</c> from the same API as Firebird
/// and optional AWS Secrets Manager app password (same pattern as ProAccScanner <c>EmailHelper</c>).
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

        var host = (tenantEmail?.SmtpHost ?? _defaults.Host ?? "smtp.gmail.com").Trim();
        var port = tenantEmail?.SmtpPort is > 0 and var p ? p : (_defaults.Port <= 0 ? 587 : _defaults.Port);
        var user = (tenantEmail?.SmtpSenderEmail ?? _defaults.User ?? "").Trim();
        var passRaw = _defaults.Password ?? "";
        var pass = (await TenantSmtpSecretResolver.ResolveSmtpPasswordAsync(
                _secrets,
                tenantEmail?.SmtpAppPasswordSecretRef,
                passRaw,
                cancellationToken)
            .ConfigureAwait(false) ?? "").Replace(" ", "").Trim();

        if (string.IsNullOrWhiteSpace(host))
            return (false, "SMTP is not configured.");

        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            return (false,
                "SMTP user or password empty after tenant merge. Set tenant email.smtpSenderEmail + smtpAppPasswordSecretRef (Secrets Manager) or Smtp:User/Smtp:Password.");
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
                "Gmail SMTP hint: use an App Password (2FA), or tenant smtpAppPasswordSecretRef + Secrets Manager; see ProAccScanner EmailHelper logs for the same hints.");
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
