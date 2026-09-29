using System;

namespace ChainwayC5
{
    public static class SiteOrigin
    {
        public static bool TryParse(string raw, out Uri origin)
        {
            origin = null;
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
                return false;
            if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.IsNullOrEmpty(uri.Host))
                return false;
            origin = uri;
            return true;
        }

        public static bool Matches(Uri origin, string url)
        {
            if (origin == null || string.IsNullOrWhiteSpace(url))
                return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var page))
                return false;
            if (!string.Equals(page.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.Equals(page.Host, origin.Host, StringComparison.OrdinalIgnoreCase))
                return false;
            return page.Port == origin.Port;
        }
    }
}
