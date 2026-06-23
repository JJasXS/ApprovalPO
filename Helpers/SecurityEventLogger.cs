namespace ApprovalPO.Helpers;

/// <summary>
/// Security event logging helper.
/// Logs authentication failures, authorization denials, suspicious patterns, and security events.
/// </summary>
public static class SecurityEventLogger
{
    private static readonly ILogger<object>? _fallbackLogger;

    /// <summary>
    /// Log a failed authentication attempt (e.g., wrong OTP, invalid credentials).
    /// </summary>
    public static void LogAuthenticationFailure(ILogger logger, string userId, string reason, string? ipAddress = null)
    {
        logger.LogWarning(
            "Security: Authentication failed for user {UserId} - Reason: {Reason} - IP: {IpAddress} - Time: {UtcNow}",
            userId,
            reason,
            ipAddress ?? "unknown",
            DateTime.UtcNow);
    }

    /// <summary>
    /// Log an authorization denial (e.g., user tried to access a restricted resource).
    /// </summary>
    public static void LogAuthorizationDenial(ILogger logger, string userId, string resource, string reason, string? ipAddress = null)
    {
        logger.LogWarning(
            "Security: Authorization denied for user {UserId} accessing {Resource} - Reason: {Reason} - IP: {IpAddress} - Time: {UtcNow}",
            userId,
            resource,
            reason,
            ipAddress ?? "unknown",
            DateTime.UtcNow);
    }

    /// <summary>
    /// Log a suspicious request pattern (e.g., SQL injection attempt, script tag detection).
    /// </summary>
    public static void LogSuspiciousRequest(ILogger logger, string pattern, string? userInput = null, string? ipAddress = null)
    {
        logger.LogCritical(
            "Security: Suspicious request pattern detected - Pattern: {Pattern} - Input: {UserInput} - IP: {IpAddress} - Time: {UtcNow}",
            pattern,
            userInput ?? "[redacted]",
            ipAddress ?? "unknown",
            DateTime.UtcNow);
    }

    /// <summary>
    /// Log rate limiting enforcement (e.g., too many login attempts from one IP).
    /// </summary>
    public static void LogRateLimitEnforced(ILogger logger, string limitType, string identifier, int attemptCount, string? ipAddress = null)
    {
        logger.LogWarning(
            "Security: Rate limit enforced - Type: {LimitType} - Identifier: {Identifier} - Attempts: {AttemptCount} - IP: {IpAddress} - Time: {UtcNow}",
            limitType,
            identifier,
            attemptCount,
            ipAddress ?? "unknown",
            DateTime.UtcNow);
    }

    /// <summary>
    /// Log a successful authentication/login event for audit trail.
    /// </summary>
    public static void LogSuccessfulAuthentication(ILogger logger, string userId, string? method = null, string? ipAddress = null)
    {
        logger.LogInformation(
            "Security: Successful authentication for user {UserId} - Method: {Method} - IP: {IpAddress} - Time: {UtcNow}",
            userId,
            method ?? "standard",
            ipAddress ?? "unknown",
            DateTime.UtcNow);
    }

    /// <summary>
    /// Log a privilege escalation attempt (user trying to elevate their role/permissions).
    /// </summary>
    public static void LogPrivilegeEscalationAttempt(ILogger logger, string userId, string attemptedRole, string? ipAddress = null)
    {
        logger.LogCritical(
            "Security: Privilege escalation attempt - User: {UserId} - Attempted Role: {AttemptedRole} - IP: {IpAddress} - Time: {UtcNow}",
            userId,
            attemptedRole,
            ipAddress ?? "unknown",
            DateTime.UtcNow);
    }

    /// <summary>
    /// Log a data access audit event for compliance tracking.
    /// </summary>
    public static void LogDataAccess(ILogger logger, string userId, string dataType, string action, string? identifier = null, string? ipAddress = null)
    {
        logger.LogInformation(
            "Audit: Data access - User: {UserId} - DataType: {DataType} - Action: {Action} - Identifier: {Identifier} - IP: {IpAddress} - Time: {UtcNow}",
            userId,
            dataType,
            action,
            identifier ?? "N/A",
            ipAddress ?? "unknown",
            DateTime.UtcNow);
    }

    /// <summary>
    /// Log a configuration or sensitive change for audit purposes.
    /// </summary>
    public static void LogConfigurationChange(ILogger logger, string userId, string changeDescription, string? previousValue = null, string? newValue = null)
    {
        logger.LogWarning(
            "Audit: Configuration changed - User: {UserId} - Change: {ChangeDescription} - Previous: {PreviousValue} - New: {NewValue} - Time: {UtcNow}",
            userId,
            changeDescription,
            previousValue ?? "[hidden]",
            newValue ?? "[hidden]",
            DateTime.UtcNow);
    }

    /// <summary>
    /// Extract client IP address from HttpContext (handles X-Forwarded-For headers for proxies).
    /// </summary>
    public static string? GetClientIpAddress(HttpContext httpContext)
    {
        if (httpContext == null)
            return null;

        // Check for X-Forwarded-For header (when behind proxy)
        var xForwardedFor = httpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(xForwardedFor))
        {
            var ips = xForwardedFor.Split(',');
            if (ips.Length > 0)
                return ips[0].Trim();
        }

        // Check for X-Real-IP header (Nginx proxy)
        var xRealIp = httpContext.Request.Headers["X-Real-IP"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(xRealIp))
            return xRealIp;

        // Fall back to remote IP
        return httpContext.Connection.RemoteIpAddress?.ToString();
    }
}
