using System.Net;
using System.Net.Mail;
using ApprovalPO.Options;
using Microsoft.Extensions.Options;

namespace ApprovalPO.Services;

public class SmtpOtpEmailSender : IOtpEmailSender
{
    private readonly SmtpOptions _smtp;

    public SmtpOtpEmailSender(IOptions<SmtpOptions> smtp) => _smtp = smtp.Value;

    public async Task<(bool Sent, string? ErrorMessage)> SendOtpAsync(
        string toAddress,
        string otpCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_smtp.Host))
            return (false, "SMTP is not configured.");

        if (string.IsNullOrWhiteSpace(_smtp.From))
            return (false, "SMTP From address is not configured.");

        try
        {
            using var client = new SmtpClient(_smtp.Host, _smtp.Port)
            {
                EnableSsl = _smtp.UseSsl
            };
            if (!string.IsNullOrEmpty(_smtp.User))
                client.Credentials = new NetworkCredential(_smtp.User, _smtp.Password);

            using var msg = new MailMessage(_smtp.From, toAddress, "Your approval login code", $"Your one-time code is: {otpCode}\n\nIt expires in a few minutes.")
            {
                IsBodyHtml = false
            };

            await client.SendMailAsync(msg, cancellationToken).ConfigureAwait(false);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
