#nullable disable
// Fork (docs/specs/ux-overhaul.md P6, the studio's Windows preview): the audio drivers of a host without WPF on Windows. ASIO
// drivers first, through the WPF app's own AsioAudioDriver (ReBuzz/Audio/Driver, linked into this assembly; NAudio.Asio talks to
// the installed drivers' COM interface and holds no Steinberg SDK code), then PortAudio's devices (WASAPI, WDM-KS, DirectSound,
// MME: PortAudioDriverFactory). With an ASIO driver installed and no device saved, the first device the engine finds is the
// ASIO one (AudioDeviceService.ResolveDevice), so ASIO is the default; a saved device stays. The WPF app's settings windows
// (WasapiConfigWindow, AsioConfigWindow) stay there: here an ASIO driver's own control panel opens, and PortAudio's choices
// are Preferences' Audio page's.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Common.Settings;
using NAudio.Wave;
using ReBuzz.Audio;
using ReBuzz.Audio.Engine;
using ReBuzz.Core;

namespace ReBuzz.Host
{
    internal sealed class WindowsHostAudioDriverFactory : IAudioDriverFactory
    {
        readonly PortAudioDriverFactory portAudio = new PortAudioDriverFactory();

        public bool AsioSupported
        {
            get
            {
                try { return AsioOut.isSupported(); }
                catch (Exception) { return false; }
            }
        }

        public IEnumerable<string> AsioDriverNames()
        {
            try { return AsioOut.GetDriverNames().ToList(); }
            catch (Exception) { return Array.Empty<string>(); }
        }

        public IEnumerable<AudioEngine.AudioOutDevice> OutputDevices()
        {
            var devices = new List<AudioEngine.AudioOutDevice>();
            if (AsioSupported)
                foreach (var name in AsioDriverNames())
                    devices.Add(new AudioEngine.AudioOutDevice { Name = name, Type = AudioEngine.AudioOutType.ASIO });
            devices.AddRange(portAudio.OutputDevices());
            return devices;
        }

        public IAudioDriver Create(AudioEngine.AudioOutType type, string deviceName, ReBuzzCore buzzCore, EngineSettings settings, IRegistryEx registryEx) =>
            type == AudioEngine.AudioOutType.ASIO
                ? new AsioAudioDriver(deviceName, buzzCore, settings, registryEx)
                : portAudio.Create(type, deviceName, buzzCore, settings, registryEx);

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
