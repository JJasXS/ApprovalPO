namespace ApprovalPO.Hosting;

/// <summary>Standard browser security headers for all HTML/API responses.</summary>
public static class SecurityHeadersExtensions
{
    public static IApplicationBuilder UseApprovalSecurityHeaders(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "SAMEORIGIN";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = "camera=(self), microphone=(), geolocation=()";
            headers["X-Permitted-Cross-Domain-Policies"] = "none";
            await next().ConfigureAwait(false);
        });
    }
}
