using ApprovalPO.Helpers;
using ApprovalPO.Options;
using Amazon.SecretsManager;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ApprovalPO.Services.Ocr;

public sealed class OcrEmailSender : IOcrEmailSender
{
    private readonly SmtpOptions _defaults;
    private readonly TenantDbConnectionResolver _tenants;
    private readonly IConfiguration _configuration;
    private readonly IAmazonSecretsManager _secrets;
    private readonly ILogger<OcrEmailSender> _logger;

    public OcrEmailSender(
        IOptions<SmtpOptions> smtp,
        TenantDbConnectionResolver tenants,
        IConfiguration configuration,
        IAmazonSecretsManager secrets,
        ILogger<OcrEmailSender> logger)
    {
        _defaults = smtp.Value;
        _tenants = tenants;
        _configuration = configuration;
        _secrets = secrets;
        _logger = logger;
    }

    public async Task<(bool Sent, string? Error)> SendAsync(
        string toAddress,
        string subject,
        string bodyText,
        byte[]? attachment,
        string? attachmentFileName,
        string? attachmentContentType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toAddress))
            return (false, "No recipient.");

        var smtp = await SmtpSettingsResolver
            .ResolveAsync(_defaults, _tenants, _configuration, _secrets, _logger, cancellationToken)
            .ConfigureAwait(false);

        if (smtp.Error is not null)
            return (false, smtp.Error);

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("ApprovalPO", smtp.User));
            message.To.Add(MailboxAddress.Parse(toAddress));
            message.Subject = subject;

            var builder = new BodyBuilder { TextBody = bodyText };
            if (attachment is { Length: > 0 })
            {
                var name = string.IsNullOrWhiteSpace(attachmentFileName) ? "ocr-capture.png" : attachmentFileName;
                var ct = string.IsNullOrWhiteSpace(attachmentContentType) ? "image/png" : attachmentContentType;
                builder.Attachments.Add(name, attachment, ContentType.Parse(ct));
            }
            message.Body = builder.ToMessageBody();

            using var client = new SmtpClient();
            var secure = SmtpSettingsResolver.ResolveSecureSocketOptions(smtp.Port, smtp.UseSsl);
            await client.ConnectAsync(smtp.Host, smtp.Port, secure, cancellationToken).ConfigureAwait(false);
            await client.AuthenticateAsync(smtp.User, smtp.Password, cancellationToken).ConfigureAwait(false);
            await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Sent OCR scan email to {To}.", toAddress);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OCR scan email failed: {Message}", ex.Message);
            return (false, ex.Message);
        }
    }
}
