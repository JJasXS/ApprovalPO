namespace ApprovalPO.Services.Email;

public interface IOtpEmailSender
{
    Task<(bool Sent, string? ErrorMessage)> SendOtpAsync(string toAddress, string otpCode, CancellationToken cancellationToken = default);
}
