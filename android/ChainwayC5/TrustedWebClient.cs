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

        public override void OnPageFinished(WebView view, string url)
        {
            base.OnPageFinished(view, url);
            if (view == null || !SiteOrigin.Matches(_origin, url))
                return;
            view.EvaluateJavascript(PrintBridge.HookScript, null);
        }

        bool BlockUnlessTrusted(string url)
        {
            if (SiteOrigin.Matches(_origin, url))
                return false;
            _onBlocked?.Invoke(url ?? "");
            return true;
        }
    }
}
