using Android.OS;

namespace ChainwayC5
{
    /// <summary>
    /// Same background Init() call as the demo MainActivity.InitTask.
    /// </summary>
    public sealed class ReaderInitTask : AsyncTask
    {
        readonly MainActivity _activity;

        public ReaderInitTask(MainActivity activity)
        {
            _activity = activity;
        }

        protected override Java.Lang.Object DoInBackground(params Java.Lang.Object[] @params)
        {
            return _activity.Scanner.Init() ? "OK" : "No";
        }

        protected override void OnPostExecute(Java.Lang.Object result)
        {
            if ("OK".Equals(result?.ToString()))
            {
                _activity.SetStatus("Reader ready.");
                return;
            }
            _activity.SetStatus("Reader init failed.");
        }
    }
}
