// Fork (docs/specs/mac-engine.md M5): the audio drivers off Windows. PortAudio's output devices
// (CoreAudio on macOS, ALSA or PulseAudio on Linux), named "PortAudio: <device> (<host API>)",
// after one that follows the system's default output. No settings dialog: the settings live in
// the registry's "PortAudio" section (PortAudioDriver), which rebuzz-headless's options write.

using BuzzGUI.Common.Settings;
using ReBuzz.Audio.Engine;
using ReBuzz.Core;
using ReBuzz.Platform;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Audio
{
    internal sealed class PortAudioDriverFactory : IAudioDriverFactory
    {
        internal const string Prefix = "PortAudio: ";

        /// <summary>The device that follows the system's default output (or input).</summary>
        internal const string DefaultName = Prefix + "default";

        public bool AsioSupported => false;

        public IEnumerable<string> AsioDriverNames() => Array.Empty<string>();

        public IEnumerable<AudioEngine.AudioOutDevice> OutputDevices()
        {
            var devices = new List<AudioEngine.AudioOutDevice>();
            if (!PortAudio.Available) return devices;
            PortAudio.Acquire();
            try
            {
                devices.Add(new AudioEngine.AudioOutDevice { Name = DefaultName, Type = AudioEngine.AudioOutType.PortAudio });
                foreach (var d in PortAudio.Devices().Where(d => d.Outputs >= 2))
                    devices.Add(new AudioEngine.AudioOutDevice { Name = Label(d), Type = AudioEngine.AudioOutType.PortAudio });
            }
            finally
            {
                PortAudio.Release();
            }
            return devices;
        }

        /// <summary>A device's name in the settings: "PortAudio: MacBook Pro Speakers (Core Audio)".</summary>
        internal static string Label(PortAudio.Device d) => $"{Prefix}{d.Name} ({d.HostApi})";

        /// <summary>
        /// The device a name means: the default one, or one by its label, its bare name or (as a
        /// last resort) the start of its name. Null when none fits.
        /// </summary>
        internal static PortAudio.Device Find(IReadOnlyList<PortAudio.Device> devices, string name, int defaultIndex, Func<PortAudio.Device, bool> fits)
        {
            var usable = devices.Where(fits).ToList();
            if (string.IsNullOrWhiteSpace(name) || name == DefaultName || string.Equals(name, "default", StringComparison.OrdinalIgnoreCase))
                return usable.FirstOrDefault(d => d.Index == defaultIndex) ?? usable.FirstOrDefault();
            string bare = name.StartsWith(Prefix, StringComparison.Ordinal) ? name.Substring(Prefix.Length) : name;
            return usable.FirstOrDefault(d => Label(d) == name)
                   ?? usable.FirstOrDefault(d => string.Equals(d.Name, bare, StringComparison.OrdinalIgnoreCase))
                   ?? usable.FirstOrDefault(d => d.Name.StartsWith(bare, StringComparison.OrdinalIgnoreCase));
        }

        public IAudioDriver Create(AudioEngine.AudioOutType type, string deviceName, ReBuzzCore buzzCore, EngineSettings settings, IRegistryEx registryEx)
        {
            if (type != AudioEngine.AudioOutType.PortAudio)
                throw new NotSupportedException($"Unsupported audio driver type here: {type}");
            return new PortAudioDriver(deviceName, buzzCore, settings, registryEx);
        }

        public bool ShowConfig(AudioEngine.AudioOutDevice device, ReBuzzCore buzzCore, IRegistryEx registryEx, string buzzPath) => false;

        public int AsioPreferredBufferSize(string deviceName) => 0;
    }
}
