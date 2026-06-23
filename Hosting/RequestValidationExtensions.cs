namespace ApprovalPO.Hosting;

/// <summary>
/// Middleware for validating incoming requests.
/// Protects against oversized payloads, malformed requests, and common attack patterns.
/// </summary>
public static class RequestValidationExtensions
{
    /// <summary>
    /// Configure strict request validation including size limits and content type validation.
    /// </summary>
    public static IApplicationBuilder UseApprovalRequestValidation(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            // Block requests with suspiciously large Content-Length headers (prevent DoS)
            var contentLength = context.Request.ContentLength;
            const long maxRequestSize = 50 * 1024 * 1024; // 50 MB
            if (contentLength > maxRequestSize)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await context.Response.WriteAsync("Request entity too large").ConfigureAwait(false);
                return;
            }

            // Validate HTTP method (prevent request smuggling)
            var validMethods = new[] { "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS" };
            if (!validMethods.Contains(context.Request.Method, StringComparer.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                await context.Response.WriteAsync("Method not allowed").ConfigureAwait(false);
                return;
            }

            // Block requests with suspicious User-Agent patterns (common attack bots)
            var userAgent = context.Request.Headers.UserAgent.ToString();
            if (IsBlockedUserAgent(userAgent))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Access denied").ConfigureAwait(false);
                return;
            }

            await next().ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Check for known malicious user agents and common attack patterns.
    /// </summary>
    private static bool IsBlockedUserAgent(string userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return false;

        var blockedPatterns = new[]
        {
            "sqlmap", "nikto", "nessus", "nmap", "masscan",
            "metasploit", "burp", "zap", "acunetix",
            "havij", "joomla", "wordpress",
            "<?php", "<script", "eval(", "base64",
            "union select", "drop table", "insert into",
            "../", "..\\", "%2e%2e"
        };

        var lowerAgent = userAgent.ToLowerInvariant();
        return blockedPatterns.Any(pattern => lowerAgent.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Configure request size limits in Kestrel to prevent large upload attacks.
    /// </summary>
    public static WebApplicationBuilder ConfigureRequestSizeLimits(this WebApplicationBuilder builder)
    {
        builder.WebHost.ConfigureKestrel(options =>
        {
            // Maximum request body size (default 30 MB)
            options.Limits.MaxRequestBodySize = 50 * 1024 * 1024;
            
            // Maximum request header size (default 32 KB)
            options.Limits.MaxRequestHeadersTotalSize = 64 * 1024;
            
            // HTTP/2 stream size
            options.Limits.MaxStreamSize = 5 * 1024 * 1024;
        });

        return builder;
    }
}
