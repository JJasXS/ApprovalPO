using System;
using Android.OS;
using Android.Webkit;
using Java.Interop;

namespace ChainwayC5
{
    /// <summary>Lets the website call Android print without leaving the trusted site.</summary>
    public sealed class PrintBridge : Java.Lang.Object
    {
        public const string HookScript =
            "(function(){if(window.__approvalPrint)return;window.__approvalPrint=true;window.print=function(){ApprovalPrint.printPage();};})();";

        readonly MainActivity _activity;

        public PrintBridge(MainActivity activity)
        {
            _activity = activity;
        }

        [JavascriptInterface]
        [Export("printPage")]
        public void PrintPage()
        {
            _activity.RunOnUiThread(_activity.PrintCurrentPage);
        }
    }

    /// <summary>Print popups stay inside the app instead of opening a blocked address.</summary>
    public sealed class PrintChromeClient : WebChromeClient
    {
        readonly Action _onPrint;

        public PrintChromeClient(Action onPrint)
        {
            _onPrint = onPrint;
        }

        public override bool OnCreateWindow(WebView view, bool isDialog, bool isUserGesture, Message resultMsg)
        {
            _onPrint?.Invoke();
            return false;
        }
    }
}
