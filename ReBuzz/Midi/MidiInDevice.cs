using BuzzGUI.Common;
using NAudio.Midi;
using ReBuzz.Core;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using BuzzGUI.Interfaces;
using System.Security.RightsManagement;
using System.Xml.Serialization;

namespace ReBuzz.Midi
{
    internal class MidiInDevice : IMidiInput
    {
        internal MidiIn midiIn;
        private readonly IBuzz buzz;
        ConcurrentQueue<object> midiMessages;
        private bool stopped;

        public string ProductName { get; private set; }

        // Fork (docs/specs/neo-audio.md "Inputs and arm"): where messages go instead of the core's plain MIDI input (MidiEngine's, which names the device).
        private readonly Action<int> received;

        public MidiInDevice(IBuzz buzz, Action<int> received = null)
        {
            this.buzz = buzz;
            this.received = received;
        }

        public void CreateMidiIn(int selectedDeviceIndex)
        {
            DisposeMidiIn();

            try
            {
                midiMessages = new ConcurrentQueue<object>();

                // Create midi message handler task
                stopped = false;

                ProductName = MidiIn.DeviceInfo(selectedDeviceIndex).ProductName;
                // Start listening Midi messages
                midiIn = new MidiIn(selectedDeviceIndex);
                midiIn.MessageReceived += MidiIn_MessageReceived;
                midiIn.ErrorReceived += MidiIn_ErrorReceived;
                midiIn.SysexMessageReceived += MidiIn_SysexMessageReceived;
                midiIn.Start();
            }
            catch (Exception e)
            {
                buzz.DCWriteLine(e.Message);
            }
        }

        public void UnsubscribeEvents()
        {
            try
            {
                midiIn.MessageReceived -= MidiIn_MessageReceived;
                midiIn.ErrorReceived -= MidiIn_ErrorReceived;
                midiIn.SysexMessageReceived -= MidiIn_SysexMessageReceived;
            }
            catch { }
        }
        /// <summary>Fork (engine-split.md S8): IMidiInput.</summary>
        public void Close() => DisposeMidiIn();

        public void DisposeMidiIn()
        {
            stopped = true;
            if (midiIn != null)
            {
                try
                {
                    midiIn.Stop();
                    UnsubscribeEvents();
                    midiIn.Dispose();
                }
                catch { }
                midiIn = null;
            }
        }

        private void MidiIn_ErrorReceived(object sender, MidiInMessageEventArgs e)
        {
            Global.Buzz.DCWriteLine(String.Format("Time {0} Message 0x{1:X8} Event {2}",
                e.Timestamp, e.RawMessage, e.MidiEvent));
        }

        private void MidiIn_MessageReceived(object sender, MidiInMessageEventArgs e)
        {
            if (received != null)
                received(e.RawMessage);
            else
                buzz.SendMIDIInput(e.RawMessage);

            //midiMessages.Enqueue(e.RawMessage);
            //midiMessageReceivedEvent.Set();
        }

        private void MidiIn_SysexMessageReceived(object sender, MidiInSysexMessageEventArgs e)
        {
            //buzz.SendMIDIInput(e.SysexBytes);
            //midiMessages.Enqueue(e.SysexBytes);
            //midiMessageReceivedEvent.Set();
        }
    }
}
