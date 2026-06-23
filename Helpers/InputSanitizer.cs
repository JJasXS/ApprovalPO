using System.Text.RegularExpressions;

namespace ApprovalPO.Helpers;

/// <summary>
/// Input sanitization and validation helpers.
/// Provides defense-in-depth against XSS, injection, and data validation attacks.
/// </summary>
public static class InputSanitizer
{
    /// <summary>
    /// Removes potentially dangerous characters and HTML tags from user input.
    /// Use for sanitizing free-form text fields.
    /// </summary>
    public static string SanitizeText(string? input, int maxLength = 2000)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        input = input.Trim();
        
        // Limit length to prevent buffer-based attacks
        if (input.Length > maxLength)
            input = input.Substring(0, maxLength);

        // Remove null bytes
        input = input.Replace("\0", "", StringComparison.Ordinal);

        // Remove control characters except tab, newline, carriage return
        input = Regex.Replace(input, @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", "", RegexOptions.Compiled);

        return input;
    }

    /// <summary>
    /// Sanitizes HTML content. Removes script tags, event handlers, and dangerous attributes.
    /// Use for user-submitted HTML (e.g., description fields).
    /// </summary>
    public static string SanitizeHtml(string? input, int maxLength = 2000)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        input = SanitizeText(input, maxLength);

        // Remove script tags and content
        input = Regex.Replace(input, @"<script\b[^<]*(?:(?!<\/script>)<[^<]*)*<\/script>", "", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Remove event handlers (onclick, onerror, etc.)
        input = Regex.Replace(input, @"\s*on\w+\s*=\s*[""']?[^""'>\s]+[""']?", "", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Remove iframe, object, embed tags
        input = Regex.Replace(input, @"<(iframe|object|embed|frame|frameset)\b[^>]*>(?:.*?)</\1>", "", RegexOptions.IgnoreCase | RegexOptions.Compiled);
                                        
        return input;
    }

    /// <summary>
    /// Validates email addresses using a conservative regex pattern.
    /// Returns normalized email (lowercase trim).
    /// </summary>
    public static (bool IsValid, string NormalizedEmail)ValidateEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return (false, string.Empty);

        email = email.Trim().ToLowerInvariant();

        // Basic email validation - RFC 5322 simplified
        const string emailPattern = @"^[^@\s]+@[^@\s]+\.[^@\s]+$";
        var isValid = Regex.IsMatch(email, emailPattern, RegexOptions.Compiled);

        // Additional checks
        if (isValid)
        {
            // Prevent excessively long emails
            if (email.Length > 254)
                return (false, email);

            // Prevent multiple @ symbols or spaces
            if (email.Count(c => c == '@') != 1 || email.Contains(' '))
                return (false, email);
        }

        return (isValid, email);
    }

    /// <summary>
    /// Validates phone numbers - accepts digits, spaces, dashes, plus, parentheses.
    /// Returns normalized phone (digits and essential separators only).
    /// </summary>
    public static (bool IsValid, string NormalizedPhone) ValidatePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return (false, string.Empty);

        phone = phone.Trim();

        // Only allow digits, space, dash, plus, parentheses
        if (!Regex.IsMatch(phone, @"^[\d\s\-\+\(\)]+$", RegexOptions.Compiled))
            return (false, phone);

        // Extract digits only for validation
        var digitsOnly = Regex.Replace(phone, @"\D", "", RegexOptions.Compiled);

        // Phone should have between 7 and 15 digits
        var isValid = digitsOnly.Length >= 7 && digitsOnly.Length <= 15;

        return (isValid, phone);
    }

    /// <summary>
    /// Validates numeric input ranges.
    /// Prevents negative values and excessive magnitudes.
    /// </summary>
    public static (bool IsValid, decimal? NormalizedValue) ValidateNumeric(string? input, decimal minValue = 0, decimal maxValue = 999999999)
    {
        if (string.IsNullOrWhiteSpace(input))
            return (false, null);

        input = input.Trim();

        if (!decimal.TryParse(input, out var value))
            return (false, null);

        var isValid = value >= minValue && value <= maxValue;
        return (isValid, isValid ? value : (decimal?)null);
    }

    /// <summary>
    /// Validates alphanumeric identifiers (no special characters, prevents injection).
    /// Common use: order codes, product IDs, etc.
    /// </summary>
    public static (bool IsValid, string NormalizedId) ValidateIdentifier(string? id, int maxLength = 50)
    {
        if (string.IsNullOrWhiteSpace(id))
            return (false, string.Empty);

        id = id.Trim();

        if (id.Length > maxLength)
            return (false, id);

        // Allow alphanumeric, dash, underscore only
        const string pattern = @"^[a-zA-Z0-9_\-]+$";
        var isValid = Regex.IsMatch(id, pattern, RegexOptions.Compiled);

        return (isValid, id);
    }

    /// <summary>
    /// Validates SQL-like input to prevent injection patterns.
    /// Returns true if input appears safe; false if it contains SQL keywords or injection patterns.
    /// </summary>
    public static bool IsSafeSqlInput(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return true;

        input = input.ToUpperInvariant();

        // Block common SQL injection patterns
        var dangerousPatterns = new[]
        {
            "SELECT ", "INSERT ", "UPDATE ", "DELETE ", "DROP ", "CREATE ",
            "ALTER ", "EXEC ", "EXECUTE ", "UNION ", "DECLARE ", "CAST(",
            "CONVERT(", "SUBSTRING(", "';--", "1=1", "1=0", "--", "/*", "*/"
        };

        return !dangerousPatterns.Any(pattern => input.Contains(pattern, StringComparison.Ordinal));
    }
}
