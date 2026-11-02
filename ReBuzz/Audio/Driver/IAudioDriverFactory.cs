using BuzzGUI.Common.Settings;
using ReBuzz.Audio.Engine;
using ReBuzz.Core;
using System.Collections.Generic;

namespace ReBuzz.Audio
{
    /// <summary>
    /// The platform's audio drivers (engine-split.md S8): device names, opening a driver, its
    /// settings dialog. On Windows: ASIO and WASAPI (WindowsAudioDriverFactory, in ReBuzz.exe).
    /// On macOS and Linux: PortAudio (PortAudioDriverFactory, mac-engine.md M5).
    /// </summary>
    internal interface IAudioDriverFactory
    {
        /// <summary>
        /// The output devices, in the order the settings list them. By default the ASIO drivers,
        /// then WASAPI (Windows); PortAudioDriverFactory lists PortAudio's devices.
        /// </summary>
        IEnumerable<AudioEngine.AudioOutDevice> OutputDevices()
        {
            var devices = new List<AudioEngine.AudioOutDevice>();
            if (AsioSupported)
                foreach (var name in AsioDriverNames())
                    devices.Add(new AudioEngine.AudioOutDevice { Name = name, Type = AudioEngine.AudioOutType.ASIO });
            devices.Add(new AudioEngine.AudioOutDevice { Name = "WASAPI", Type = AudioEngine.AudioOutType.Wasapi });
            return devices;
        }

        /// <summary>Whether ASIO drivers can be used here.</summary>
        bool AsioSupported { get; }

        /// <summary>The installed ASIO drivers.</summary>
        IEnumerable<string> AsioDriverNames();

        IAudioDriver Create(AudioEngine.AudioOutType type, string deviceName, ReBuzzCore buzzCore, EngineSettings settings, IRegistryEx registryEx);

        /// <summary>
        /// Shows the selected device's settings dialog. True when the user accepted new settings
        /// (the engine then reopens the device).
        /// </summary>
        bool ShowConfig(AudioEngine.AudioOutDevice device, ReBuzzCore buzzCore, IRegistryEx registryEx, string buzzPath);

        /// <summary>An ASIO driver's preferred buffer size.</summary>
        int AsioPreferredBufferSize(string deviceName);
    }
}
