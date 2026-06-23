# ApprovalPO Security Hardening Guide

## Overview
This document outlines the security improvements made to ApprovalPO and best practices for maintaining and extending the security posture of the application.

## Security Enhancements Implemented

### 1. Enhanced Security Headers (`SecurityHeadersExtensions.cs`)
**What changed:**
- Upgraded X-Frame-Options from `SAMEORIGIN` to `DENY` (stronger clickjacking protection)
- Added `X-XSS-Protection` header for legacy browser support
- Added **Content-Security-Policy (CSP)** - prevents XSS attacks by controlling resource loading
- Added `Expect-CT` header - detects mis-issued SSL certificates in real-time

**Impact:** Reduces XSS, clickjacking, and MIME-type sniffing attack surface.

---

### 2. Request Validation Middleware (`RequestValidationExtensions.cs`)
**New middleware features:**
- **Payload size limits**: Blocks requests >50MB (prevents DoS attacks)
- **HTTP method validation**: Only allows standard HTTP methods
- **Malicious User-Agent detection**: Blocks requests from known scanning tools (sqlmap, nikto, burp, etc.)
- **Kestrel request limits**: Configures server-level size constraints

**Impact:** Prevents several classes of attacks:
- Denial of Service (large uploads)
- SQL injection scanner attempts
- Vulnerability scanners
- Buffer overflow attacks

---

### 3. Input Sanitization Helpers (`InputSanitizer.cs`)
**New utility functions:**
- `SanitizeText()` - Removes dangerous characters, control chars, null bytes
- `SanitizeHtml()` - Strips script tags, event handlers, dangerous HTML attributes
- `ValidateEmail()` - RFC-compliant email validation with length checks
- `ValidatePhone()` - Phone number format and length validation
- `ValidateNumeric()` - Numeric range validation (prevents overflow)
- `ValidateIdentifier()` - Alphanumeric-only validation for IDs/codes
- `IsSafeSqlInput()` - Detects SQL injection patterns in input

**Usage example:**
```csharp
var (isValid, sanitized) = InputSanitizer.SanitizeText(userInput, maxLength: 1000);
if (!isValid) return BadRequest("Invalid input");

var (emailValid, normalizedEmail) = InputSanitizer.ValidateEmail(email);
```

**Impact:** Prevents:
- XSS (Cross-Site Scripting)
- Script injection
- HTML injection
- SQL injection patterns
- Buffer overflows

---

### 4. Security Event Logging (`SecurityEventLogger.cs`)
**New logging methods:**
- `LogAuthenticationFailure()` - Failed login attempts
- `LogAuthorizationDenial()` - Access control violations
- `LogSuspiciousRequest()` - Detected attack patterns
- `LogRateLimitEnforced()` - Rate limit hits
- `LogSuccessfulAuthentication()` - Audit trail
- `LogPrivilegeEscalationAttempt()` - Attempted privilege elevation
- `LogDataAccess()` - Compliance audit logging
- `LogConfigurationChange()` - Audit of config changes

**Usage example:**
```csharp
var ipAddress = SecurityEventLogger.GetClientIpAddress(HttpContext);
SecurityEventLogger.LogAuthenticationFailure(
    _logger, 
    userId: "user@example.com", 
    reason: "Invalid OTP", 
    ipAddress: ipAddress
);
```

**Impact:** 
- Better security event visibility
- Compliance audit trails
- Anomaly detection
- Forensics for incident response

---

### 5. Improved HTTPS Redirect
**Change:** Redirect HTTP → HTTPS now uses HTTP 301 (Permanent) instead of 302 (Temporary)

**Why:** 
- Permanent redirects allow browsers to cache the redirect, reducing latency
- Better for security headers propagation
- Improved SEO and performance

---

### 6. Request Size Limits (Kestrel)
**Configured limits:**
- Maximum request body: 50 MB
- Maximum request headers: 64 KB
- Maximum HTTP/2 stream: 5 MB

**Impact:** Prevents memory exhaustion and DoS attacks via large uploads.

---

## Recommended Implementation Patterns

### When Accepting User Input
Always sanitize and validate:

```csharp
// In your Razor Pages or API controllers:
[BindProperty]
public string UserDescription { get; set; } = "";

public IActionResult OnPost()
{
    // Sanitize before storing
    var (isValid, sanitized) = InputSanitizer.SanitizeText(UserDescription, maxLength: 2000);
    if (!isValid)
    {
        ModelState.AddModelError("UserDescription", "Invalid characters in description");
        return Page();
    }
    
    // Log data access
    var ipAddress = SecurityEventLogger.GetClientIpAddress(HttpContext);
    SecurityEventLogger.LogDataAccess(
        _logger, 
        userId: User.FindFirst(ClaimTypes.Email)?.Value ?? "unknown",
        dataType: "PurchaseOrder",
        action: "Create",
        identifier: sanitized,
        ipAddress: ipAddress
    );
    
    // Proceed with sanitized data
    return HandleSanitizedInput(sanitized);
}
```

### Authentication Event Logging
```csharp
// In Login.cshtml.cs
var ipAddress = SecurityEventLogger.GetClientIpAddress(HttpContext);

if (loginFailed)
{
    SecurityEventLogger.LogAuthenticationFailure(
        _logger,
        userId: LoginId,
        reason: "Invalid OTP",
        ipAddress: ipAddress
    );
}
else
{
    SecurityEventLogger.LogSuccessfulAuthentication(
        _logger,
        userId: email,
        method: "OTP",
        ipAddress: ipAddress
    );
}
```

### Preventing Privilege Escalation
```csharp
// Always re-validate user roles from database, never trust client claims
var actualRoles = await _roleResolver.ResolveRolesForEmailAsync(email);
if (!actualRoles.Contains(requestedRole))
{
    var ipAddress = SecurityEventLogger.GetClientIpAddress(HttpContext);
    SecurityEventLogger.LogPrivilegeEscalationAttempt(
        _logger,
        userId: email,
        attemptedRole: requestedRole,
        ipAddress: ipAddress
    );
    return Unauthorized();
}
```

---

## Configuration Best Practices

### Production appsettings.Production.json
```json
{
  "Approval": {
    "BindLoopbackOnly": false,
    "UseHsts": true,
    "UseHttpsRedirection": true,
    "CookieSecureAlways": true,
    "DebugOtpToConsole": false,
    "ExposeOtpWhenEmailFails": false,
    "SessionHours": 2,
    "OtpExpiryMinutes": 5,
    "OtpMaxSendsPerWindow": 3,
    "OtpMaxVerifyFailures": 5,
    "OtpLockoutMinutes": 15,
    "LoginMaxSendOtpPerIpPerMinute": 3,
    "LoginMaxVerifyOtpPerIpPerMinute": 5
  },
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.AspNetCore.Authentication": "Information"
    }
  }
}
```

### Environment Variables (Never commit these!)
```bash
# Production - Use AWS Secrets Manager
TenantBootstrap__FirebirdPassword=<strong-password>
TenantBootstrap__AwsApiKey=<api-key>
SMTP__Password=<app-password>
OpenAi__ApiKey=<api-key>
```

### AllowedHosts Configuration
```json
"AllowedHosts": "approvalpo.example.com,www.approvalpo.example.com"
```
**Note:** Change from `*` to specific domains in production to prevent Host Header Injection.

---

## Security Audit Checklist

- [ ] **SSL/TLS**: HTTPS enabled, valid certificate installed
- [ ] **Headers**: Verify security headers in browser Dev Tools (Network tab)
- [ ] **Authentication**: OTP working, session timeout enforced
- [ ] **Authorization**: Test role-based access controls, verify denials are logged
- [ ] **Input Validation**: Test with special chars, SQL keywords, script tags
- [ ] **Logging**: Monitor logs for security events (authentication failures, suspicious requests)
- [ ] **Secrets**: Verify no passwords/API keys in config files or source control
- [ ] **Rate Limiting**: Test login throttling (should block after N attempts)
- [ ] **CORS**: If serving API, verify CORS is properly restricted
- [ ] **Dependencies**: Run `dotnet list package --vulnerable` for security updates

---

## Ongoing Security Maintenance

### Monthly Tasks
1. Review security logs for anomalies
2. Check for new security updates in NuGet packages
3. Verify HTTPS certificate validity and expiration

### Quarterly Tasks
1. Perform security vulnerability scan
2. Review and update rate limit thresholds based on attack patterns
3. Audit user access and role assignments
4. Test account lockout and recovery procedures

### Annually
1. Penetration testing
2. Security training for development team
3. Review and update security policies
4. Audit third-party integrations (AWS, email provider, etc.)

---

## Related Files
- `Program.cs` - Main security configuration
- `SecurityHeadersExtensions.cs` - Security headers middleware
- `RequestValidationExtensions.cs` - Request validation middleware
- `InputSanitizer.cs` - Input validation and sanitization
- `SecurityEventLogger.cs` - Security event logging
- `appsettings.*.json` - Configuration by environment

---

## Questions or Issues?
If you discover a security vulnerability or have questions about the security implementation, please report it confidentially to the security team.
