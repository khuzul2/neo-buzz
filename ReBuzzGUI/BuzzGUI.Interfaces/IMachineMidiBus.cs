// Fork (docs/specs/voice-to-midi.md §7): a machine that makes MIDI of its own (Neo Voice to MIDI) offers it to armed tracks as a "MIDI From"
// source, "Machine: <name>". The machine implements IMachineMidiSource (a marker the track input chooser looks for) and sends through the
// host's IMachineMidiBus, which routes the message like a device's: to the armed tracks whose MIDI From names that machine (and its
// channel), and nowhere else (it never reaches "All inputs", the selected machine or the MIDI focus).
using System;

namespace BuzzGUI.Interfaces
{
    /// <summary>Marker: this machine's managed class offers its MIDI as a MIDI From source for armed tracks.</summary>
    public interface IMachineMidiSource
    {
    }

    /// <summary>The host's bus for a machine's own MIDI (implemented by the core; get it with <c>machine.Graph.Buzz as IMachineMidiBus</c>).</summary>
    public interface IMachineMidiBus
    {
        /// <summary>A MIDI message (status | data1 &lt;&lt; 8 | data2 &lt;&lt; 16) from <paramref name="source"/> for the armed tracks that listen to it. Any thread.</summary>
        void SendMachineMIDI(IMachine source, int data);
    }
}
