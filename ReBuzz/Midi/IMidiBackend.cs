using ReBuzz.Core.Session;
using System;

namespace ReBuzz.Midi
{
    /// <summary>
    /// The platform's MIDI devices (engine-split.md S8, cross-platform.md §3.2): device lists,
    /// opening inputs and outputs, hot-plug, and the MIDI 2 endpoint. On Windows: NAudio (WinMM),
    /// Windows MIDI Services and a WinRT device watcher (WindowsMidiBackend, in ReBuzz.exe).
    /// Devices are addressed by index, as WinMM numbers them.
    /// </summary>
    internal interface IMidiBackend
    {
        int InputCount { get; }
        string InputName(int index);
        int OutputCount { get; }
        string OutputName(int index);

        /// <summary>Opens and starts an input; its short messages go to the core's MIDI input.</summary>
        IMidiInput OpenInput(int index);

        IMidiOutput OpenOutput(int index);

        /// <summary>A control surface's own input and output (SessionControllers).</summary>
        IControllerPort OpenControllerPort(int inputIndex, int outputIndex);

        /// <summary>Reports devices that connect or disconnect until <see cref="StopWatching"/>.</summary>
        void StartWatching(Action<MidiDeviceChange> changed);
        void StopWatching();

        bool CreateMidi2Endpoint();
        void ReleaseMidi2();
    }

    /// <summary>
    /// A device connected or disconnected. The device counts are taken when the change was
    /// noticed, before the device's details were looked up.
    /// </summary>
    internal readonly record struct MidiDeviceChange(string Name, bool Connected, int InputCount, int OutputCount);

    internal interface IMidiInput
    {
        string ProductName { get; }

        /// <summary>Stops and closes the device.</summary>
        void Close();
    }

    internal interface IMidiOutput : IDisposable
    {
        string ProductName { get; }
        void Send(int message);
        void Send(byte[] message);
    }

    /// <summary>No MIDI devices (no platform backend).</summary>
    internal sealed class NoMidiBackend : IMidiBackend
    {
        public int InputCount => 0;
        public string InputName(int index) => throw new ArgumentOutOfRangeException(nameof(index));
        public int OutputCount => 0;
        public string OutputName(int index) => throw new ArgumentOutOfRangeException(nameof(index));
        public IMidiInput OpenInput(int index) => throw new NotSupportedException("No MIDI devices here.");
        public IMidiOutput OpenOutput(int index) => throw new NotSupportedException("No MIDI devices here.");
        public IControllerPort OpenControllerPort(int inputIndex, int outputIndex) => throw new NotSupportedException("No MIDI devices here.");
        public void StartWatching(Action<MidiDeviceChange> changed) { }
        public void StopWatching() { }
        public bool CreateMidi2Endpoint() => false;
        public void ReleaseMidi2() { }
    }
}
