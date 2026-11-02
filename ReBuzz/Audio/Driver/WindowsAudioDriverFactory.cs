using BuzzDotNet.Audio;
using BuzzGUI.Common.Settings;
using NAudio.Wave;
using ReBuzz.Audio.Engine;
using ReBuzz.Common;
using ReBuzz.Core;
using System;
using System.Collections.Generic;
using System.Windows;

namespace ReBuzz.Audio
{
    /// <summary>
    /// Fork (docs/specs/engine-split.md S8): ASIO and WASAPI, and their settings windows. Moved
    /// unchanged from AudioDriverFactory, AudioDeviceService and AudioEngine. Windows only, so it
    /// stays in ReBuzz.exe (not in ReBuzz.Engine).
    /// </summary>
    internal sealed class WindowsAudioDriverFactory : IAudioDriverFactory
    {
        private WasapiConfigWindow wasapiConfigWindow;
        private AsioConfigWindow asioConfigWindow;

        public bool AsioSupported => AsioOut.isSupported();

        public IEnumerable<string> AsioDriverNames() => AsioOut.GetDriverNames();

        public IAudioDriver Create(
            AudioEngine.AudioOutType type,
            string deviceName,
            ReBuzzCore buzzCore,
            EngineSettings settings,
            IRegistryEx registryEx)
        {
            return type switch
            {
                AudioEngine.AudioOutType.ASIO =>
                    new AsioAudioDriver(deviceName, buzzCore, settings, registryEx),

                AudioEngine.AudioOutType.Wasapi =>
                    new WasapiAudioDriver(deviceName, buzzCore, settings, registryEx),

                _ => throw new NotSupportedException($"Unsupported audio driver type: {type}")
            };
        }

        public bool ShowConfig(AudioEngine.AudioOutDevice device, ReBuzzCore buzzCore, IRegistryEx registryEx, string buzzPath)
        {
            switch (device.Type)
            {
                case AudioEngine.AudioOutType.ASIO:
                    return ShowAsioControlPanel(device, buzzCore, registryEx, buzzPath);

                case AudioEngine.AudioOutType.Wasapi:
                    return ShowWasapiControlPanel(registryEx, buzzPath);

                default:
                    return false;
            }
        }

        private bool ShowAsioControlPanel(AudioEngine.AudioOutDevice device, ReBuzzCore buzzCore, IRegistryEx registryEx, string buzzPath)
        {
            if (asioConfigWindow != null)
                return false;

            var asio = device.WavePlayer as AsioDevice;
            if (asio == null)
                return false;

            asioConfigWindow = new AsioConfigWindow(buzzCore, asio.DriverName, registryEx);

            asioConfigWindow.OpenAsioControlPanel += () =>
            {
                asio.ShowControlPanel();
            };

            var rd = UiUtils.GetUserControlXAML<ResourceDictionary>(
                "MachineView\\MVResources.xaml",
                buzzPath);

            asioConfigWindow.Resources.MergedDictionaries.Add(rd);

            bool accepted = false;
            if (asioConfigWindow.ShowDialog() == true)
            {
                asioConfigWindow.SaveSelection();
                accepted = true;
            }

            asioConfigWindow = null;
            return accepted;
        }

        private bool ShowWasapiControlPanel(IRegistryEx registryEx, string buzzPath)
        {
            if (wasapiConfigWindow != null)
                return false;

            wasapiConfigWindow = new WasapiConfigWindow(registryEx);

            var rd = UiUtils.GetUserControlXAML<ResourceDictionary>(
                "MachineView\\MVResources.xaml",
                buzzPath);

            wasapiConfigWindow.Resources.MergedDictionaries.Add(rd);

            bool accepted = false;
            if (wasapiConfigWindow.ShowDialog() == true)
            {
                wasapiConfigWindow.SaveSelection();
                accepted = true;
            }

            wasapiConfigWindow = null;
            return accepted;
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
