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
        readonly IWasapiEndpointSource endpoints;

        /// <param name="registry">The settings (the two switches).</param>
        /// <param name="asioNames">Tests: the installed ASIO drivers, instead of the system's.</param>
        /// <param name="endpoints">The render endpoints WASAPI can play through (MmWasapiEndpointSource on Windows; tests: a fake); null: the system default only.</param>
        internal WindowsHostAudioDriverFactory(IRegistryEx registry, Func<IEnumerable<string>> asioNames = null, IWasapiEndpointSource endpoints = null)
        {
            this.registry = registry;
            this.asioNames = asioNames;
            this.endpoints = endpoints;
        }

        /// <summary>The factory with the system's endpoints (Windows) behind the WASAPI modes.</summary>
        public static WindowsHostAudioDriverFactory ForSystem(IRegistryEx registry) =>
            new WindowsHostAudioDriverFactory(registry, null, OperatingSystem.IsWindows() ? new MmWasapiEndpointSource() : null);

        internal IWasapiEndpointSource Endpoints => endpoints;

        IReadOnlyList<WasapiEndpoint> ActiveEndpoints()
        {
            if (endpoints == null) return Array.Empty<WasapiEndpoint>();
            try { return endpoints.Active(); }
            catch (Exception) { return Array.Empty<WasapiEndpoint>(); }
        }

        IEnumerable<AudioEngine.AudioOutDevice> WasapiDevices(string mode, IReadOnlyList<(WasapiEndpoint Endpoint, string Display)> named)
        {
            // The system default first (the bare mode name: what was saved before endpoints, and it follows Windows), then every active endpoint.
            yield return new AudioEngine.AudioOutDevice { Name = mode, Type = AudioEngine.AudioOutType.Wasapi };
            foreach (var n in named)
                yield return new AudioEngine.AudioOutDevice { Name = WasapiChoice.Name(mode, n.Display), Type = AudioEngine.AudioOutType.Wasapi };
        }

        /// <summary>
        /// The device that plays when the saved WASAPI endpoint is not in the list: the endpoint under its new name (the ID says it is the same one),
        /// else the same mode on the system default. Null: not a WASAPI endpoint choice, or its mode isn't offered.
        /// </summary>
        public string FallbackDevice(string saved)
        {
            if (!WasapiChoice.TryParse(saved, out var mode, out var endpoint) || endpoint == null) return null;
            bool exclusive = Switch(ExclusiveKey);
            if ((mode == WasapiModes.Exclusive) != exclusive) return null;
            var r = WasapiChoice.Resolve(saved, registry?.Read("DeviceID", "", "WASAPI"), registry?.Read("DeviceName", "", "WASAPI"), ActiveEndpoints());
            return WasapiChoice.Name(mode, r.Display);
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
            var named = WasapiChoice.Named(ActiveEndpoints());
            if (Switch(ExclusiveKey))
                devices.AddRange(WasapiDevices(WasapiModes.Exclusive, named));
            else
            {
                devices.AddRange(WasapiDevices(WasapiModes.SharedLowLatency, named));
                devices.AddRange(WasapiDevices(WasapiModes.Shared, named));
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
                    // The driver's own settings (WasapiConfigWindow's keys), from the mode and endpoint the name says. The endpoint is
                    // saved by its ID with its name beside it (the name finds it again if the ID is not there); an endpoint that is
                    // gone (unplugged since the list was made) plays through the system default, which the empty ID asks for.
                    var r = WasapiChoice.Resolve(deviceName, registryEx.Read("DeviceID", "", "WASAPI"), registryEx.Read("DeviceName", "", "WASAPI"), ActiveEndpoints());
                    registryEx.Write("Mode", r.Mode == WasapiModes.Exclusive ? 1 : 0, "WASAPI");
                    registryEx.Write("LowLatencyMode", r.Mode == WasapiModes.SharedLowLatency ? 1 : 0, "WASAPI");
                    registryEx.Write("Poll", 0, "WASAPI");                 // event-driven
                    registryEx.Write("RawMode", 0, "WASAPI");
                    registryEx.Write("DeviceID", r.Endpoint?.Id ?? "", "WASAPI");
                    registryEx.Write("DeviceName", r.Display ?? "", "WASAPI");
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
