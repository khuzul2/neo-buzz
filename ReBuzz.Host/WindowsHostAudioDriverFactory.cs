// Fork (docs/specs/ux-overhaul.md P6 and its decision 3 of 2026-11-13): the audio drivers of a host without WPF on Windows, the
// user's choice (option C):
//   1. ASIO drivers, through the WPF app's own AsioAudioDriver (ReBuzz/Audio/Driver, linked into this assembly; NAudio.Asio talks
//      to the installed drivers' COM interface and holds no Steinberg SDK code). The default when a driver is installed.
//   2. WASAPI, through the WPF app's WasapiAudioDriver (NAudio's WasapiPlayer, linked the same way) in three modes, named as
//      devices so one setting (audio.Device) holds the choice: "WASAPI: Shared, low latency" (IAudioClient3; the default when
//      there is no ASIO), "WASAPI: Shared" (classic), and "WASAPI: Exclusive (other apps go silent)", event-driven, offered
//      only while the switch audio.WasapiExclusive is on (Preferences ▸ Audio).
//   3. WDM-KS through PortAudio, only with audio.ShowAdvancedDevices on. No DirectSound, no MME.
// The first device of the list is what the engine opens when no device is saved or the saved one is gone
// (AudioDeviceService.ResolveDevice), so the order is the default rule. The drivers read the "WASAPI" section of the settings
// (Mode, LowLatencyMode, Poll); Create writes them from the chosen name. The WPF app's settings windows stay there: here an ASIO
// driver's own control panel opens, and the rest is Preferences' Audio page's.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Common.Settings;
using NAudio.Wave;
using ReBuzz.Audio;
using ReBuzz.Audio.Engine;
using ReBuzz.Core;
using ReBuzz.Platform;

namespace ReBuzz.Host
{
    /// <summary>The names of the WASAPI modes as devices.</summary>
    internal static class WasapiModes
    {
        public const string SharedLowLatency = "WASAPI: Shared, low latency";
        public const string Shared = "WASAPI: Shared";
        public const string Exclusive = "WASAPI: Exclusive (other apps go silent)";

        public static bool IsWasapi(string name) => name == SharedLowLatency || name == Shared || name == Exclusive;
    }

    internal sealed class WindowsHostAudioDriverFactory : IAudioDriverFactory
    {
        /// <summary>The Settings section's keys (the audio section of the settings model: AppBackend's audio.WasapiExclusive and audio.ShowAdvancedDevices).</summary>
        public const string ExclusiveKey = "WasapiExclusive", AdvancedKey = "ShowAdvancedDevices";

        readonly PortAudioDriverFactory portAudio = new PortAudioDriverFactory();
        readonly IRegistryEx registry;
        readonly Func<IEnumerable<string>> asioNames;

        /// <param name="registry">The settings (the two switches).</param>
        /// <param name="asioNames">Tests: the installed ASIO drivers, instead of the system's.</param>
        public WindowsHostAudioDriverFactory(IRegistryEx registry, Func<IEnumerable<string>> asioNames = null)
        {
            this.registry = registry;
            this.asioNames = asioNames;
        }

        bool Switch(string key)
        {
            try { return registry != null && registry.Read(key, 0, "Settings") == 1; }
            catch (Exception) { return false; }
        }

        public bool AsioSupported
        {
            get
            {
                if (asioNames != null) return true;
                try { return AsioOut.isSupported(); }
                catch (Exception) { return false; }
            }
        }

        public IEnumerable<string> AsioDriverNames()
        {
            if (asioNames != null) return asioNames().ToList();
            try { return AsioOut.GetDriverNames().ToList(); }
            catch (Exception) { return Array.Empty<string>(); }
        }

        public IEnumerable<AudioEngine.AudioOutDevice> OutputDevices()
        {
            var devices = new List<AudioEngine.AudioOutDevice>();
            if (AsioSupported)
                foreach (var name in AsioDriverNames())
                    devices.Add(new AudioEngine.AudioOutDevice { Name = name, Type = AudioEngine.AudioOutType.ASIO });
            if (Switch(ExclusiveKey))
                devices.Add(new AudioEngine.AudioOutDevice { Name = WasapiModes.Exclusive, Type = AudioEngine.AudioOutType.Wasapi });
            else
            {
                devices.Add(new AudioEngine.AudioOutDevice { Name = WasapiModes.SharedLowLatency, Type = AudioEngine.AudioOutType.Wasapi });
                devices.Add(new AudioEngine.AudioOutDevice { Name = WasapiModes.Shared, Type = AudioEngine.AudioOutType.Wasapi });
            }
            if (Switch(AdvancedKey) && PortAudio.Available)
            {
                PortAudio.Acquire();
                try
                {
                    foreach (var d in PortAudio.Devices().Where(d => d.Outputs >= 2 && d.HostApi.Contains("WDM", StringComparison.OrdinalIgnoreCase)))
                        devices.Add(new AudioEngine.AudioOutDevice { Name = PortAudioDriverFactory.Label(d), Type = AudioEngine.AudioOutType.PortAudio });
                }
                finally
                {
                    PortAudio.Release();
                }
            }
            return devices;
        }

        public IAudioDriver Create(AudioEngine.AudioOutType type, string deviceName, ReBuzzCore buzzCore, EngineSettings settings, IRegistryEx registryEx)
        {
            switch (type)
            {
                case AudioEngine.AudioOutType.ASIO:
                    return new AsioAudioDriver(deviceName, buzzCore, settings, registryEx);
                case AudioEngine.AudioOutType.Wasapi:
                    // The driver's own settings (WasapiConfigWindow's keys), from the mode the name says: the system's default output.
                    registryEx.Write("Mode", deviceName == WasapiModes.Exclusive ? 1 : 0, "WASAPI");
                    registryEx.Write("LowLatencyMode", deviceName == WasapiModes.SharedLowLatency ? 1 : 0, "WASAPI");
                    registryEx.Write("Poll", 0, "WASAPI");                 // event-driven
                    registryEx.Write("RawMode", 0, "WASAPI");
                    registryEx.Write("DeviceID", "", "WASAPI");
                    return new WasapiAudioDriver(deviceName, buzzCore, settings, registryEx);
                default:
                    return portAudio.Create(type, deviceName, buzzCore, settings, registryEx);
            }
        }

        /// <summary>An ASIO driver's own control panel (it applies its settings itself); false: nothing for the engine to reopen.</summary>
        public bool ShowConfig(AudioEngine.AudioOutDevice device, ReBuzzCore buzzCore, IRegistryEx registryEx, string buzzPath)
        {
            if (device.Type == AudioEngine.AudioOutType.ASIO && device.WavePlayer is AsioDevice asio)
            {
                try { asio.ShowControlPanel(); }
                catch (Exception e) { buzzCore.DCWriteLine("ASIO control panel: " + e.Message, BuzzGUI.Interfaces.DCLogLevel.Error); }
            }
            return false;
        }

        public int AsioPreferredBufferSize(string deviceName)
        {
            var device = AsioDevice.Open(deviceName);
            int size = device.Capabilities.BufferPreferredSize;
            device.Dispose();
            return size;
        }
    }
}
