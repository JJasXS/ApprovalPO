using ApprovalPO.Options;
using Microsoft.Extensions.Options;

namespace ApprovalPO.Services.Auth;

/// <summary>In-memory OTP with expiry, resend cooldown, send rate limit, and verify lockout.</summary>
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
        public DateTimeOffset? LastSentAt;
        public int FailedVerifications;
        public DateTimeOffset? LockedUntil;
        public readonly List<DateTimeOffset> SendTimestamps = new();
    }

    public readonly record struct BeginSendResult(
        bool Ok,
        string Message,
        string? OtpPlainForDev,
        long ExpiresAtUnix,
        int CooldownRemainingSeconds);

    private static int EffectiveExpirySeconds(ApprovalOptions opt)
    {
        if (opt.OtpExpirySeconds > 0)
            return opt.OtpExpirySeconds;
        if (opt.OtpExpiryMinutes > 0)
            return opt.OtpExpiryMinutes * 60;
        return 120;
    }

    public BeginSendResult TryBeginSend(string loginId)
    {
        var opt = _options.Value;
        var key = Normalize(loginId);
        if (string.IsNullOrEmpty(key))
            return new BeginSendResult(false, "Login id is required.", null, 0, 0);

        var lifetimeSeconds = EffectiveExpirySeconds(opt);
        var cooldownSeconds = opt.OtpResendCooldownSeconds > 0 ? opt.OtpResendCooldownSeconds : 30;

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
                if (opt.OtpMaxSendsPerWindow <= 0 || opt.OtpMaxVerifyFailures <= 0)
                {
                    entry.LockedUntil = null;
                    entry.SendTimestamps.Clear();
                }
                else if (entry.LockedUntil.HasValue && entry.LockedUntil.Value > now)
                {
                    var mins = Math.Ceiling((entry.LockedUntil.Value - now).TotalMinutes);
                    return new BeginSendResult(false, $"Too many attempts. Try again in {(int)mins} minute(s).", null, 0, 0);
                }

                if (entry.LastSentAt.HasValue)
                {
                    var since = (int)(now - entry.LastSentAt.Value).TotalSeconds;
                    if (since < cooldownSeconds)
                    {
                        var wait = cooldownSeconds - since;
                        if (wait < 1) wait = 1;
                        var existingExpiresUnix = entry.ExpiresAt.HasValue
                            ? entry.ExpiresAt.Value.ToUnixTimeSeconds()
                            : 0L;
                        return new BeginSendResult(
                            false,
                            $"Please wait {wait}s before requesting another OTP.",
                            null,
                            existingExpiresUnix,
                            wait);
                    }
                }

                if (opt.OtpMaxSendsPerWindow > 0)
                {
                    PruneSends(entry, now, opt.OtpRateLimitWindowMinutes);
                    if (entry.SendTimestamps.Count >= opt.OtpMaxSendsPerWindow)
                        return new BeginSendResult(
                            false,
                            $"Too many OTP requests. Wait up to {opt.OtpRateLimitWindowMinutes} minutes and try again.",
                            null,
                            0,
                            0);
                }

                var otp = Random.Shared.Next(100000, 1000000).ToString();
                entry.Otp = otp;
                entry.ExpiresAt = now.AddSeconds(lifetimeSeconds);
                entry.LastSentAt = now;
                entry.FailedVerifications = 0;
                if (opt.OtpMaxSendsPerWindow > 0)
                    entry.SendTimestamps.Add(now);

                return new BeginSendResult(
                    true,
                    "OTP generated.",
                    otp,
                    entry.ExpiresAt.Value.ToUnixTimeSeconds(),
                    0);
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
                if (opt.OtpMaxVerifyFailures > 0 &&
                    entry.LockedUntil.HasValue && entry.LockedUntil.Value > now)
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
                    if (opt.OtpMaxVerifyFailures > 0 && entry.FailedVerifications >= opt.OtpMaxVerifyFailures)
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
