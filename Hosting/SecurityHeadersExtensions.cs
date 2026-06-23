namespace ApprovalPO.Hosting;

/// <summary>
/// Standard browser security headers for all HTML/API responses.
/// Implements defense-in-depth with CSP, HSTS, frame options, and XSS protection.
/// </summary>
public static class SecurityHeadersExtensions
{
    public static IApplicationBuilder UseApprovalSecurityHeaders(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            
            // Prevent MIME type sniffing
            headers["X-Content-Type-Options"] = "nosniff";
            
            // Clickjacking protection - prevent embedding in frames
            headers["X-Frame-Options"] = "DENY";
            
            // XSS protection for older browsers
            headers["X-XSS-Protection"] = "1; mode=block";
            
            // Referrer policy - limit referrer information leakage
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            
            // Permissions policy - restrict camera/microphone/geolocation
            headers["Permissions-Policy"] = "camera=(self), microphone=(), geolocation=(), payment=()";
            
            // Block cross-domain policies
            headers["X-Permitted-Cross-Domain-Policies"] = "none";
            
            // Content Security Policy - prevent XSS, injection attacks
            // Adjust 'self' and specific domains based on your requirements
            headers["Content-Security-Policy"] = "default-src 'self'; " +
                "script-src 'self' 'unsafe-inline' 'unsafe-eval'; " +
                "style-src 'self' 'unsafe-inline'; " +
                "img-src 'self' data: https:; " +
                "font-src 'self' data:; " +
                "connect-src 'self'; " +
                "frame-ancestors 'none'; " +
                "base-uri 'self'; " +
                "form-action 'self'";
            
            // Expect-CT: Enforce certificate transparency (helps detect mis-issued certificates)
            headers["Expect-CT"] = "max-age=86400, enforce";
            
            await next().ConfigureAwait(false);
        });
    }
}
