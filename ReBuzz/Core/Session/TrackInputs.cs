#nullable disable
// Fork (docs/specs/neo-audio.md "Inputs and arm"): what a track listens to, and the input meter. The pure parts: no dependency on
// the rest of ReBuzz, so ReBuzz.SessionTests links this file (and ReBuzz/Midi/MidiDevice.cs).

using ReBuzz.Midi;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;

namespace ReBuzz.Core.Session
{
    internal enum AudioFromKind
    {
        /// <summary>What is wired into the machine (an old song's way, and what ReBuzz Audio In feeds).</summary>
        Wired,
        /// <summary>Nothing: the track records and monitors silence.</summary>
        None,
        /// <summary>One input channel, centred to both sides.</summary>
        Mono,
        /// <summary>Two neighbouring input channels, left and right.</summary>
        Stereo,
        /// <summary>Another machine's output after its mixer (resampling it).</summary>
        Track
    }

    /// <summary>
    /// Where an audio track takes its audio: "wired", "none", "in:3" (input 3), "in:1-2" (inputs 1 and 2), "track:Name" (the
    /// machine of that name). Input numbers count from 1 in the text and from 0 in <see cref="Channel"/>.
    /// </summary>
    internal readonly struct AudioFrom : IEquatable<AudioFrom>
    {
        public AudioFrom(AudioFromKind kind, int channel = 0, string machine = "")
        {
            Kind = kind;
            Channel = Math.Max(0, channel);
            Machine = machine ?? "";
        }

        public AudioFromKind Kind { get; }
        /// <summary>The first input channel (from 0) of a mono or stereo source.</summary>
        public int Channel { get; }
        /// <summary>The machine of a track source.</summary>
        public string Machine { get; }

        public static AudioFrom Wired => new AudioFrom(AudioFromKind.Wired);
        public static AudioFrom None => new AudioFrom(AudioFromKind.None);
        public static AudioFrom Mono(int channel) => new AudioFrom(AudioFromKind.Mono, channel);
        public static AudioFrom Stereo(int channel) => new AudioFrom(AudioFromKind.Stereo, channel);
        public static AudioFrom Track(string machine) => new AudioFrom(AudioFromKind.Track, 0, machine);

        /// <summary>Whether it takes input channels of the audio device.</summary>
        public bool IsDevice => Kind == AudioFromKind.Mono || Kind == AudioFromKind.Stereo;

        /// <summary>Input channels it needs the device to have (0 for the others).</summary>
        public int ChannelsNeeded => Kind == AudioFromKind.Mono ? Channel + 1 : Kind == AudioFromKind.Stereo ? Channel + 2 : 0;

        public string ToText() => Kind switch
        {
            AudioFromKind.None => "none",
            AudioFromKind.Mono => "in:" + (Channel + 1).ToString(CultureInfo.InvariantCulture),
            AudioFromKind.Stereo => "in:" + (Channel + 1).ToString(CultureInfo.InvariantCulture) + "-" + (Channel + 2).ToString(CultureInfo.InvariantCulture),
            AudioFromKind.Track => "track:" + Machine,
            _ => "wired"
        };

        public override string ToString() => ToText();

        /// <summary>Parses the text of <see cref="ToText"/> (case-insensitive; "in:1/2" and "in 1-2" too).</summary>
        public static bool TryParse(string text, out AudioFrom from)
        {
            from = Wired;
            text = text?.Trim();
            if (string.IsNullOrEmpty(text)) return false;
            string lower = text.ToLowerInvariant();
            if (lower == "wired" || lower == "wired inputs") return true;
            if (lower == "none") { from = None; return true; }
            if (lower.StartsWith("track:", StringComparison.Ordinal))
            {
                string name = text.Substring(6).Trim();
                if (name.Length == 0) return false;
                from = Track(name);
                return true;
            }
            if (lower.StartsWith("in:", StringComparison.Ordinal) || lower.StartsWith("in ", StringComparison.Ordinal))
            {
                var parts = lower.Substring(3).Trim().Split('-', '/', '+');
                if (parts.Length == 1 && int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int one) && one >= 1 && one <= 256)
                {
                    from = Mono(one - 1);
                    return true;
                }
                if (parts.Length == 2 && int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int a) &&
                    int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int b) && a >= 1 && b == a + 1 && b <= 256)
                {
                    from = Stereo(a - 1);
                    return true;
                }
            }
            return false;
        }

        /// <summary>What the choosers show: "None", "Wired inputs", "In 3", "In 1/2" (or the driver's names), "Track: Name".</summary>
        public string Label(IReadOnlyList<string> channelNames = null)
        {
            string Name(int c) => channelNames != null && c < channelNames.Count && !string.IsNullOrWhiteSpace(channelNames[c]) ? channelNames[c] : "In " + (c + 1);
            return Kind switch
            {
                AudioFromKind.None => "None",
                AudioFromKind.Mono => Name(Channel),
                AudioFromKind.Stereo => channelNames != null && Channel + 1 < channelNames.Count && !string.IsNullOrWhiteSpace(channelNames[Channel]) && !string.IsNullOrWhiteSpace(channelNames[Channel + 1])
                    && !(Name(Channel) == "In " + (Channel + 1) && Name(Channel + 1) == "In " + (Channel + 2))
                    ? Name(Channel) + " / " + Name(Channel + 1)
                    : "In " + (Channel + 1) + "/" + (Channel + 2),
                AudioFromKind.Track => "Track: " + Machine,
                _ => "Wired inputs"
            };
        }

        public bool Equals(AudioFrom other) => Kind == other.Kind && Channel == other.Channel && string.Equals(Machine, other.Machine, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is AudioFrom a && Equals(a);
        public override int GetHashCode() => HashCode.Combine(Kind, Channel, Machine);
        public static bool operator ==(AudioFrom a, AudioFrom b) => a.Equals(b);
        public static bool operator !=(AudioFrom a, AudioFrom b) => !a.Equals(b);
    }

    internal enum MidiFromKind
    {
        /// <summary>Every MIDI input, except the devices claimed as control surfaces (and the computer keyboard).</summary>
        All,
        /// <summary>One device, by name.</summary>
        Device,
        /// <summary>The computer keyboard (the machine keyboard's keys).</summary>
        Keyboard
    }

    /// <summary>
    /// What a track (a machine's tracks) listens to: its audio source, its MIDI source and channel. Saved with the song's session
    /// layout (<c>Inputs</c>), per machine. A machine with no entry has the defaults: audio from what is wired, MIDI from all inputs.
    /// </summary>
    internal sealed class TrackInput
    {
        public AudioFrom Audio { get; set; } = AudioFrom.Wired;
        public MidiFromKind Midi { get; set; } = MidiFromKind.All;
        /// <summary>The device of <see cref="MidiFromKind.Device"/> (its name).</summary>
        public string Device { get; set; } = "";
        /// <summary>The MIDI channel it listens to: 0 for all, else 1 to 16.</summary>
        public int Channel { get; set; }

        public bool IsDefault => Audio == AudioFrom.Wired && Midi == MidiFromKind.All && Channel == 0;

        public TrackInput Clone() => new TrackInput { Audio = Audio, Midi = Midi, Device = Device, Channel = Channel };

        public bool SameAs(TrackInput other) =>
            other != null && Audio == other.Audio && Midi == other.Midi && Channel == other.Channel &&
            (Midi != MidiFromKind.Device || string.Equals(Device, other.Device, StringComparison.OrdinalIgnoreCase));

        /// <summary>What the MIDI chooser shows: "All inputs", the device's name, "Computer keyboard".</summary>
        public string MidiLabel => Midi switch
        {
            MidiFromKind.Device => string.IsNullOrEmpty(Device) ? "All inputs" : Device,
            MidiFromKind.Keyboard => MidiDevice.KeyboardName,
            _ => "All inputs"
        };

        public string ChannelLabel => Channel <= 0 ? "All" : Channel.ToString(CultureInfo.InvariantCulture);

        internal XElement ToXml(string machine)
        {
            var e = new XElement("Input", new XAttribute("machine", machine));
            if (Audio != AudioFrom.Wired) e.SetAttributeValue("audio", Audio.ToText());
            if (Midi != MidiFromKind.All) e.SetAttributeValue("midi", Midi == MidiFromKind.Keyboard ? "keyboard" : "device");
            if (Midi == MidiFromKind.Device) e.SetAttributeValue("device", Device ?? "");
            if (Channel != 0) e.SetAttributeValue("channel", Channel);
            return e;
        }

        internal static TrackInput FromXml(XElement e)
        {
            var input = new TrackInput();
            if (AudioFrom.TryParse((string)e.Attribute("audio"), out var audio)) input.Audio = audio;
            string midi = (string)e.Attribute("midi");
            string device = (string)e.Attribute("device") ?? "";
            if (midi == "keyboard") input.Midi = MidiFromKind.Keyboard;
            else if (midi == "device" && device.Length > 0) { input.Midi = MidiFromKind.Device; input.Device = device; }
            if (int.TryParse((string)e.Attribute("channel"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int channel))
                input.Channel = Math.Clamp(channel, 0, 16);
            return input;
        }
    }

    /// <summary>Which MIDI messages a track takes.</summary>
    internal static class MidiRouting
    {
        /// <summary>
        /// Whether a track listening as <paramref name="input"/> takes a message from <paramref name="device"/> on MIDI channel
        /// <paramref name="channel"/> (0 to 15). <paramref name="claimed"/>: the device is a control surface (All inputs leave it out).
        /// </summary>
        public static bool Accepts(TrackInput input, MidiDevice device, int channel, bool claimed)
        {
            if (input == null) return !claimed;
            if (input.Channel > 0 && channel != input.Channel - 1) return false;
            switch (input.Midi)
            {
                case MidiFromKind.Device:
                    return device.Named(input.Device);
                case MidiFromKind.Keyboard:
                    return device.IsKeyboard;
                default:
                    return !claimed;
            }
        }

        /// <summary>
        /// Whether an armed track listens for MIDI at all: a note or MIDI machine does; an audio track (a mic on Neo Audio) never does (it has no use for notes),
        /// so arming a mic leaves the keyboard to the selected machine.
        /// </summary>
        public static bool TakesMidi(bool audioTrack, TrackInput input) => !audioTrack;

        /// <summary>Whether the computer keyboard under a machine's window plays its track (the keyboard's, or all inputs').</summary>
        public static bool AcceptsKeyboard(TrackInput input) => input == null || input.Midi != MidiFromKind.Device;
    }

    /// <summary>
    /// The input meter of a track: a level that falls, a peak that holds, and a clip light that stays lit until it is cleared.
    /// Fed with the peak (in sample units, 32768 is full scale) taken since the last poll.
    /// </summary>
    internal sealed class InputMeterModel
    {
        public const double HoldSeconds = 1.5;
        public const double FallPerSecond = 0.9;       // of the meter's range, as the mixer's
        public const float ClipLevel = 32767f;

        double holdAge;

        /// <summary>The level as the meter shows it (0 to 1 over the mixer meter's -60 to +6 dB).</summary>
        public double Level { get; private set; }
        /// <summary>The held peak, same scale.</summary>
        public double Hold { get; private set; }
        /// <summary>A sample reached full scale since <see cref="ClearClip"/>.</summary>
        public bool Clipped { get; private set; }

        /// <summary>Takes a peak, in sample units; <paramref name="seconds"/> since the last call.</summary>
        public void Update(float peak, double seconds, Func<float, double> scale)
        {
            double level = peak > 0 ? scale(peak) : 0;
            Level = Math.Max(level, Level - FallPerSecond * Math.Max(0, seconds));
            holdAge += Math.Max(0, seconds);
            if (level >= Hold || holdAge > HoldSeconds)
            {
                Hold = level >= Hold ? level : Math.Max(level, Level);
                holdAge = 0;
            }
            if (peak >= ClipLevel) Clipped = true;
        }

        public void ClearClip() => Clipped = false;

        public void Reset()
        {
            Level = Hold = 0;
            holdAge = 0;
            Clipped = false;
        }
    }
}
