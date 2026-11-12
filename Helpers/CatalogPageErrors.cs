namespace ApprovalPO.Helpers;

/// <summary>User-facing messages when Acc catalog loads fail (no stack traces).</summary>
public static class CatalogPageErrors
{
    public static string Format(Exception ex, string documentLabel)
    {
        var root = Unwrap(ex);
        var detail = FirstLine(root.Message);
        if (detail.Contains("Column unknown", StringComparison.OrdinalIgnoreCase))
        {
            return $"Could not load {documentLabel}: this company Acc database is missing a required column. "
                + "Try another company under Connect databases, or ask your admin to update the schema. "
                + $"({detail})";
        }

        if (detail.Contains("user name and password are not defined", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("Your user name and password", StringComparison.OrdinalIgnoreCase))
        {
            return $"Could not load {documentLabel}: Firebird login failed for the active company database. "
                + "Check Connect databases.";
        }

        if (detail.Contains("Unable to complete network request", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("I/O error", StringComparison.OrdinalIgnoreCase))
        {
            return $"Could not load {documentLabel}: cannot reach Firebird on this server. "
                + "Confirm the .FDB path and that Firebird is running.";
        }

        return $"Could not load {documentLabel}. {detail}";
    }

    private static Exception Unwrap(Exception ex)
    {
        var current = ex;
        while (current.InnerException is not null)
            current = current.InnerException;
        return current;
    }

    private static string FirstLine(string? message)
    {
        var text = (message ?? "").Trim();
        if (text.Length == 0)
            return "Unknown error.";
        var nl = text.IndexOfAny(['\r', '\n']);
        return nl < 0 ? text : text[..nl].Trim();
    }
}
