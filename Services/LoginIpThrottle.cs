using ApprovalPO.Options;
using Microsoft.Extensions.Options;

namespace ApprovalPO.Services;

/// <summary>
/// Per-client-IP sliding minute windows for login OTP send/verify (complements per-email limits in <see cref="OtpSessionStore"/>).
/// </summary>
public sealed class LoginIpThrottle
{
    private readonly IOptions<ApprovalOptions> _opt;
    private readonly ILogger<LoginIpThrottle> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    private sealed class Bucket
    {
        public long MinuteKey;
        public int Sends;
        public int Verifies;
    }

    public LoginIpThrottle(IOptions<ApprovalOptions> opt, ILogger<LoginIpThrottle> logger)
    {
        _opt = opt;
        _logger = logger;
    }

    public bool TrySend(HttpContext http, out string message)
    {
        message = "";
        var max = _opt.Value.LoginMaxSendOtpPerIpPerMinute;
        if (max <= 0)
            return true;

        var ip = ClientIp(http);
        var minuteKey = UtcMinuteKey();
        lock (_sync)
        {
            MaybePrune(minuteKey);
            var b = GetBucket(ip, minuteKey);
            if (b.Sends >= max)
            {
                message = "Too many sign-in attempts from this network. Please wait a minute and try again.";
                _logger.LogWarning("Login send OTP IP throttle hit for {Ip}", ip);
                return false;
            }
            b.Sends++;
        }
        return true;
    }

    public bool TryVerify(HttpContext http, out string message)
    {
        message = "";
        var max = _opt.Value.LoginMaxVerifyOtpPerIpPerMinute;
        if (max <= 0)
            return true;

        var ip = ClientIp(http);
        var minuteKey = UtcMinuteKey();
        lock (_sync)
        {
            MaybePrune(minuteKey);
            var b = GetBucket(ip, minuteKey);
            if (b.Verifies >= max)
            {
                message = "Too many verification attempts from this network. Please wait a minute.";
                _logger.LogWarning("Login verify OTP IP throttle hit for {Ip}", ip);
                return false;
            }
            b.Verifies++;
        }
        return true;
    }

    private static string ClientIp(HttpContext http) =>
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static long UtcMinuteKey() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;

    private Bucket GetBucket(string ip, long minuteKey)
    {
        if (!_buckets.TryGetValue(ip, out var b))
        {
            b = new Bucket { MinuteKey = minuteKey };
            _buckets[ip] = b;
            return b;
        }
        if (b.MinuteKey != minuteKey)
        {
            b = new Bucket { MinuteKey = minuteKey };
            _buckets[ip] = b;
        }
        return b;
    }

    private void MaybePrune(long currentMinuteKey)
    {
        if (_buckets.Count <= 800)
            return;
        var cutoff = currentMinuteKey - 45L;
        foreach (var key in _buckets.Where(kv => kv.Value.MinuteKey < cutoff).Select(kv => kv.Key).ToList())
            _buckets.Remove(key);
    }
}
