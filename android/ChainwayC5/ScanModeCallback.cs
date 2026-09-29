using Android.Webkit;

namespace ChainwayC5
{
    public sealed class ScanModeCallback : Java.Lang.Object, IValueCallback
    {
        readonly MainActivity _activity;

        public ScanModeCallback(MainActivity activity)
        {
            _activity = activity;
        }

        public void OnReceiveValue(Java.Lang.Object value)
        {
            var mode = (value?.ToString() ?? "single").Trim().Trim('"');
            _activity.StartForMode(mode);
        }
    }
}
