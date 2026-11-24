#nullable disable
// Fork (docs/specs/neo-audio.md "Inputs and arm"): which MIDI device a message came from. The name is the identity (a device's index
// changes when others are plugged in); the index is where it was when it opened. No dependency on the rest of ReBuzz, so
// ReBuzz.SessionTests links this file.

using System;

namespace ReBuzz.Midi
{
    /// <summary>The device a MIDI message came from (<see cref="Unknown"/> for callers that don't say).</summary>
    public readonly record struct MidiDevice(int Index, string Name)
    {
        /// <summary>The name of the computer keyboard (the app's KeyboardPlayer and piano keys).</summary>
        public const string KeyboardName = "Computer keyboard";

        /// <summary>A message with no device: from an older caller, a virtual port, or a test.</summary>
        public static MidiDevice Unknown => new MidiDevice(-1, "");

        /// <summary>The computer keyboard playing notes.</summary>
        public static MidiDevice Keyboard => new MidiDevice(-2, KeyboardName);

        /// <summary>
        /// Fork (docs/specs/midi-mapping.md, MM1): what a mapping calls this controller: its name, and " #2", " #3" for a second and third one of
        /// the same name (set when the port opens, so a message never builds the text). Null: the name.
        /// </summary>
        public string Key { get; init; }

        /// <summary>The controller's identity for mappings and roles: <see cref="Key"/>, else the name ("" for no device).</summary>
        public string Id => Key ?? Name ?? "";

        public bool IsUnknown => Index == -1 && string.IsNullOrEmpty(Name);

        public bool IsKeyboard => Index == -2;

        /// <summary>Fork (docs/specs/voice-to-midi.md §7): a machine that makes MIDI (Neo Voice to MIDI), named by the machine's name.</summary>
        public static MidiDevice Machine(string machineName) => new MidiDevice(-3, machineName ?? "");

        /// <summary>A machine's own MIDI (see <see cref="Machine"/>).</summary>
        public bool IsMachine => Index == -3;

        /// <summary>A hardware (or virtual port) input, named.</summary>
        public bool IsDevice => !IsUnknown && !IsKeyboard && !IsMachine;

        /// <summary>Whether a saved device name means this device (the name is the identity, compared without case).</summary>
        public bool Named(string name) => IsDevice && string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether a saved machine name means this machine source.</summary>
        public bool NamedMachine(string name) => IsMachine && string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);
    }
}
