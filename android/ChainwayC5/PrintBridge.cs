using Android.Webkit;
using Java.Interop;

namespace ChainwayC5
{
    /// <summary>Lets the website call Android print without leaving the trusted site.</summary>
    public sealed class PrintBridge : Java.Lang.Object
    {
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
}
