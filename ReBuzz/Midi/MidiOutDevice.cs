using NAudio.Midi;
using System;
using System.Collections.Generic;
using System.Text;

namespace ReBuzz.Midi
{
    internal class MidiOutDevice : IDisposable
    {
        public string ProductName { get; private set; }

        public MidiOut MidiOut { get; private set; }

        public MidiOutDevice(int deviceid)
        {
            this.ProductName = MidiOut.DeviceInfo(deviceid).ProductName;
            this.MidiOut = new MidiOut(deviceid);
        }

        /// <summary>Fork: closes the device's handle (so it can be opened again).</summary>
        public void Dispose()
        {
            try { MidiOut?.Reset(); } catch { }
            MidiOut?.Dispose();
            MidiOut = null;
        }
    }
}
