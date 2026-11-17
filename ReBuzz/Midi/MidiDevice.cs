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

        public bool IsUnknown => Index == -1 && string.IsNullOrEmpty(Name);

        public bool IsKeyboard => Index == -2;

        /// <summary>A hardware (or virtual port) input, named.</summary>
        public bool IsDevice => !IsUnknown && !IsKeyboard;

        /// <summary>Whether a saved device name means this device (the name is the identity, compared without case).</summary>
        public bool Named(string name) => IsDevice && string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);
    }
}
