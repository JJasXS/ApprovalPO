using System;
using Android.Webkit;

namespace ChainwayC5
{
    public sealed class TrustedWebClient : WebViewClient
    {
        readonly Uri _origin;
        readonly Action<string> _onBlocked;

        public TrustedWebClient(Uri origin, Action<string> onBlocked)
        {
            _origin = origin;
            _onBlocked = onBlocked;
        }

        public override bool ShouldOverrideUrlLoading(WebView view, IWebResourceRequest request)
        {
            return BlockUnlessTrusted(request?.Url?.ToString());
        }

        public override bool ShouldOverrideUrlLoading(WebView view, string url)
        {
            return BlockUnlessTrusted(url);
        }

        bool BlockUnlessTrusted(string url)
        {
            if (SiteOrigin.Matches(_origin, url) || IsPrintDocumentAddress(url))
                return false;
            _onBlocked?.Invoke(url ?? "");
            return true;
        }

        static bool IsPrintDocumentAddress(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;
            var trimmed = url.Trim();
            return trimmed.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("approvalprint:", StringComparison.OrdinalIgnoreCase);
        }
    }
}
