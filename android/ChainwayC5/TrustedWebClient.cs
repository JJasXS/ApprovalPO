using System;
using Android.Webkit;

namespace ChainwayC5
{
    public sealed class TrustedWebClient : WebViewClient
    {
        readonly Uri _origin;
        readonly Action<string> _onBlocked;
        readonly Action _onPrint;

        public TrustedWebClient(Uri origin, Action<string> onBlocked, Action onPrint)
        {
            _origin = origin;
            _onBlocked = onBlocked;
            _onPrint = onPrint;
        }

        public override bool ShouldOverrideUrlLoading(WebView view, IWebResourceRequest request)
        {
            return BlockUnlessTrusted(request?.Url?.ToString());
        }

        public override bool ShouldOverrideUrlLoading(WebView view, string url)
        {
            return BlockUnlessTrusted(url);
        }

        public override void OnPageStarted(WebView view, string url, Android.Graphics.Bitmap favicon)
        {
            base.OnPageStarted(view, url, favicon);
            InjectPrintHook(view, url);
        }

        public override void OnPageFinished(WebView view, string url)
        {
            base.OnPageFinished(view, url);
            InjectPrintHook(view, url);
        }

        void InjectPrintHook(WebView view, string url)
        {
            if (view == null || !SiteOrigin.Matches(_origin, url))
                return;
            view.EvaluateJavascript(PrintBridge.HookScript, null);
        }

        bool BlockUnlessTrusted(string url)
        {
            if (IsPrinterAddress(url))
            {
                _onPrint?.Invoke();
                return true;
            }
            if (SiteOrigin.Matches(_origin, url))
                return false;
            _onBlocked?.Invoke(url ?? "");
            return true;
        }

        static bool IsPrinterAddress(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return true;
            var trimmed = url.Trim();
            return trimmed.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase);
        }
    }
}
