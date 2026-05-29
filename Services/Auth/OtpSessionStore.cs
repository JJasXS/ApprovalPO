using ApprovalPO.Options;
using Microsoft.Extensions.Options;

namespace ApprovalPO.Services.Auth;

/// <summary>In-memory OTP with expiry, send rate limit, and verify lockout.</summary>
public class OtpSessionStore
{
    private readonly IOptions<ApprovalOptions> _options;
    private readonly Dictionary<string, OtpEntry> _entries = new();
    private readonly object _global = new();

    public OtpSessionStore(IOptions<ApprovalOptions> options) => _options = options;

    private static string Normalize(string loginId) => (loginId ?? "").Trim().ToUpperInvariant();

    private sealed class OtpEntry
    {
        public readonly object LockObj = new();
        public string? Otp;
        public DateTimeOffset? ExpiresAt;
        public int FailedVerifications;
        public DateTimeOffset? LockedUntil;
        public readonly List<DateTimeOffset> SendTimestamps = new();
    }

    public (bool Ok, string Message, string? OtpPlainForDev) TryBeginSend(string loginId)
    {
        var opt = _options.Value;
        var key = Normalize(loginId);
        if (string.IsNullOrEmpty(key))
            return (false, "Login id is required.", null);

        lock (_global)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new OtpEntry();
                _entries[key] = entry;
            }

            lock (entry.LockObj)
            {
                var now = DateTimeOffset.UtcNow;
                if (entry.LockedUntil.HasValue && entry.LockedUntil.Value > now)
                {
                    var mins = Math.Ceiling((entry.LockedUntil.Value - now).TotalMinutes);
                    return (false, $"Too many attempts. Try again in {(int)mins} minute(s).", null);
                }

                if (opt.OtpMaxSendsPerWindow > 0)
                {
                    PruneSends(entry, now, opt.OtpRateLimitWindowMinutes);
                    if (entry.SendTimestamps.Count >= opt.OtpMaxSendsPerWindow)
                        return (false, $"Too many OTP requests. Wait up to {opt.OtpRateLimitWindowMinutes} minutes and try again.", null);
                }

                var otp = Random.Shared.Next(100000, 1000000).ToString();
                entry.Otp = otp;
                entry.ExpiresAt = now.AddMinutes(opt.OtpExpiryMinutes);
                entry.FailedVerifications = 0;
                if (opt.OtpMaxSendsPerWindow > 0)
                    entry.SendTimestamps.Add(now);
                return (true, "OTP generated.", otp);
            }
        }
    }

    private static void PruneSends(OtpEntry entry, DateTimeOffset now, int windowMinutes)
    {
        var cutoff = now.AddMinutes(-windowMinutes);
        entry.SendTimestamps.RemoveAll(t => t < cutoff);
    }

    public (bool Ok, string Message) TryVerify(string loginId, string otp)
    {
        var opt = _options.Value;
        var key = Normalize(loginId);
        if (string.IsNullOrEmpty(key))
            return (false, "Login id is required.");

        lock (_global)
        {
            if (!_entries.TryGetValue(key, out var entry))
                return (false, "No OTP found. Request a new code.");

            lock (entry.LockObj)
            {
                var now = DateTimeOffset.UtcNow;
                if (entry.LockedUntil.HasValue && entry.LockedUntil.Value > now)
                {
                    var mins = Math.Ceiling((entry.LockedUntil.Value - now).TotalMinutes);
                    return (false, $"Account temporarily locked. Try again in {(int)mins} minute(s).");
                }

                if (string.IsNullOrEmpty(entry.Otp) || !entry.ExpiresAt.HasValue)
                    return (false, "No active OTP. Request a new code.");

                if (now > entry.ExpiresAt.Value)
                {
                    entry.Otp = null;
                    entry.ExpiresAt = null;
                    return (false, "OTP expired. Request a new code.");
                }

                if (entry.Otp != (otp ?? "").Trim())
                {
                    entry.FailedVerifications++;
                    if (entry.FailedVerifications >= opt.OtpMaxVerifyFailures)
                    {
                        entry.LockedUntil = now.AddMinutes(opt.OtpLockoutMinutes);
                        entry.Otp = null;
                        entry.ExpiresAt = null;
                        return (false, "Too many wrong codes. You are temporarily locked out.");
                    }

                    return (false, "Invalid OTP.");
                }

                entry.Otp = null;
                entry.ExpiresAt = null;
                entry.FailedVerifications = 0;
                entry.LockedUntil = null;
                return (true, "Verified.");
            }
        }
    }
}
