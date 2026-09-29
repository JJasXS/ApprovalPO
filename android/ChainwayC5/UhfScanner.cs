using System;
using System.Collections.Generic;
using System.Threading;
using Android.Util;
using Com.Rscja.Deviceapi;
using Com.Rscja.Deviceapi.Entity;

namespace ChainwayC5
{
    /// <summary>
    /// Inventory loop copied from the Chainway demo Scan_Fragment:
    /// StartInventoryTag, ReadTagFromBuffer, InventorySingleTag, StopInventory, Free.
    /// </summary>
    public sealed class UhfScanner
    {
        const string Tag = "ChainwayC5";

        readonly RFIDWithUHFUART _reader = RFIDWithUHFUART.Instance;
        readonly HashSet<string> _holdEpcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly object _gate = new object();

        bool _loopFlag;
        bool _inited;
        Action<string> _onEpc;

        public bool Init()
        {
            try
            {
                _inited = _reader.Init();
                return _inited;
            }
            catch (Exception ex)
            {
                Log.Error(Tag, "Init failed: " + ex.Message);
                _inited = false;
                return false;
            }
        }

        public void Free()
        {
            StopInventory();
            if (!_inited)
                return;
            try
            {
                _reader.Free();
            }
            catch (Exception ex)
            {
                Log.Error(Tag, "Free failed: " + ex.Message);
            }
            _inited = false;
        }

        public bool StartContinuous(Action<string> onEpc)
        {
            if (!_inited || _loopFlag)
                return _loopFlag;
            _onEpc = onEpc;
            if (!_reader.StartInventoryTag())
            {
                Log.Error(Tag, "StartInventoryTag failed");
                return false;
            }
            _loopFlag = true;
            var thread = new Thread(ContinuousRead) { IsBackground = true };
            thread.Start();
            return true;
        }

        public bool ReadSingle(Action<string> onEpc)
        {
            if (!_inited || _loopFlag)
                return false;
            _onEpc = onEpc;
            UHFTAGInfo info;
            try
            {
                info = _reader.InventorySingleTag();
            }
            catch (Exception ex)
            {
                Log.Error(Tag, "InventorySingleTag failed: " + ex.Message);
                return false;
            }
            if (info == null)
            {
                Log.Warn(Tag, "InventorySingleTag returned no tag");
                return false;
            }
            Deliver(info.EPC);
            return true;
        }

        public void StopInventory()
        {
            if (!_loopFlag)
            {
                lock (_gate)
                    _holdEpcs.Clear();
                return;
            }
            _loopFlag = false;
            try
            {
                _reader.StopInventory();
            }
            catch (Exception ex)
            {
                Log.Error(Tag, "StopInventory failed: " + ex.Message);
            }
            lock (_gate)
                _holdEpcs.Clear();
        }

        void ContinuousRead()
        {
            while (_loopFlag)
            {
                UHFTAGInfo info = null;
                try
                {
                    info = _reader.ReadTagFromBuffer();
                }
                catch (Exception ex)
                {
                    Log.Error(Tag, "ReadTagFromBuffer failed: " + ex.Message);
                    _loopFlag = false;
                    try { _reader.StopInventory(); }
                    catch (Exception stopEx) { Log.Error(Tag, "StopInventory failed: " + stopEx.Message); }
                    return;
                }
                if (info != null)
                    Deliver(info.EPC);
                else
                    Thread.Sleep(2);
            }
        }

        void Deliver(string epc)
        {
            if (string.IsNullOrEmpty(epc))
                return;
            lock (_gate)
            {
                if (!_holdEpcs.Add(epc))
                    return;
            }
            _onEpc?.Invoke(epc);
        }
    }
}
