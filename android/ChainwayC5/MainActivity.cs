using System;
using Android.App;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Android.Widget;

namespace ChainwayC5
{
    [Activity(
        Label = "@string/app_name",
        MainLauncher = true,
        Exported = true,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden)]
    public class MainActivity : Activity
    {
        WebView _web;
        TextView _status;
        Uri _origin;
        bool _triggerDown;

        public UhfScanner Scanner { get; private set; }

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            SetContentView(Resource.Layout.Main);
            _status = FindViewById<TextView>(Resource.Id.status);
            _web = FindViewById<WebView>(Resource.Id.web);
            Scanner = new UhfScanner();
            if (!SiteOrigin.TryParse(GetString(Resource.String.site_origin), out _origin))
            {
                SetStatus("Set site_origin to the HTTPS site address before installing.");
                return;
            }
            ConfigureWebView();
            _web.LoadUrl(_origin.AbsoluteUri);
            new ReaderInitTask(this).Execute();
        }

        protected override void OnPause()
        {
            _triggerDown = false;
            Scanner?.StopInventory();
            base.OnPause();
        }

        protected override void OnDestroy()
        {
            Scanner?.Free();
            base.OnDestroy();
        }

        public override bool DispatchKeyEvent(KeyEvent e)
        {
            if (e != null && IsTrigger(e))
            {
                if (e.Action == KeyEventActions.Down)
                    return OnKeyDown(e.KeyCode, e);
                if (e.Action == KeyEventActions.Up)
                    return OnKeyUp(e.KeyCode, e);
            }
            return base.DispatchKeyEvent(e);
        }

        public override bool OnKeyDown(Keycode keyCode, KeyEvent e)
        {
            if (!IsTrigger(e) || e.RepeatCount != 0)
                return base.OnKeyDown(keyCode, e);
            _triggerDown = true;
            BeginScan();
            return true;
        }

        public override bool OnKeyUp(Keycode keyCode, KeyEvent e)
        {
            if (!IsTrigger(e))
                return base.OnKeyUp(keyCode, e);
            _triggerDown = false;
            Scanner?.StopInventory();
            return true;
        }

        public void SetStatus(string text)
        {
            if (_status == null)
                return;
            RunOnUiThread(() => _status.Text = text ?? "");
        }

        public void StartForMode(string mode)
        {
            if (Scanner == null || !_triggerDown)
                return;
            var continuous = string.Equals(mode, "continuous", StringComparison.Ordinal);
            if (continuous)
            {
                if (!Scanner.StartContinuous(DeliverEpc))
                    SetStatus("Continuous scan failed to start.");
                return;
            }
            if (!Scanner.ReadSingle(DeliverEpc))
                SetStatus("No tag read.");
        }

        void BeginScan()
        {
            if (_origin == null || _web == null || Scanner == null)
                return;
            if (!SiteOrigin.Matches(_origin, _web.Url))
            {
                SetStatus("Scan ignored: page is not the trusted site.");
                return;
            }
            _web.EvaluateJavascript(EpcScript.ModeQuery, new ScanModeCallback(this));
        }

        void DeliverEpc(string epc)
        {
            RunOnUiThread(() => PublishEpc(epc));
        }

        void PublishEpc(string epc)
        {
            if (!EpcScript.IsHexEpc(epc) || _web == null || !SiteOrigin.Matches(_origin, _web.Url))
                return;
            _web.EvaluateJavascript(EpcScript.EventScript(epc), null);
        }

        void ConfigureWebView()
        {
            var settings = _web.Settings;
            settings.JavaScriptEnabled = true;
            settings.DomStorageEnabled = true;
            settings.MixedContentMode = MixedContentHandling.NeverAllow;
            CookieManager.Instance.SetAcceptCookie(true);
            CookieManager.Instance.SetAcceptThirdPartyCookies(_web, true);
            _web.SetWebViewClient(new TrustedWebClient(_origin, _ => SetStatus("Blocked an untrusted address.")));
        }

        static bool IsTrigger(KeyEvent e)
        {
            var code = e.KeyCode.GetHashCode();
            return code == 139 || code == 280 || code == 293;
        }
    }
}
