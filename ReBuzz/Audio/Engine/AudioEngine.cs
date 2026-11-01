using BuzzDotNet.Audio;
using BuzzGUI.Common.Settings;
using Helios.Concurrency;
using NAudio.Wave;
using ReBuzz.Common;
using ReBuzz.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace ReBuzz.Audio.Engine
{
    public class AudioEngine
    {
        public enum AudioOutType
        {
            ASIO,
            Wasapi,
            DirectSound
        }

        public class AudioOutDevice
        {
            public string Name;
            public AudioOutType Type;
            public object WavePlayer;
        }

        public class AudioInDevice
        {
            public string Name;
            public AudioOutType Type;
            public NAudio.Wave.WaveFormat WaveFormat { get; internal set; }
        }

        public AudioOutDevice SelectedOutDevice { get; private set; }
        public AudioInDevice SelectedInDevice { get; private set; }
        public int SampleRateIn { get; private set; }

        private readonly ReBuzzCore buzzCore;
        private readonly EngineSettings engineSettings;
        private readonly string buzzPath;
        private readonly IUiDispatcher dispatcher;
        private readonly IRegistryEx registryEx;

        private IAudioDriver currentDriver;
        private readonly AudioBufferManager bufferManager = new();
        private readonly AudioDeviceService deviceService;

        private WasapiConfigWindow wasapiConfigWindow;
        private AsioConfigWindow asioConfigWindow;

        internal static DedicatedThreadPoolTaskScheduler TaskSchedulerAudio { get; private set; }
        internal static TaskFactory TaskFactoryAudio { get; private set; }
        public static int ThreadCount { get; private set; }

        private readonly object audioEngineLock = new();

        public AudioEngine(
            ReBuzzCore buzzCore,
            EngineSettings settings,
            string buzzPath,
            IUiDispatcher dispatcher,
            IRegistryEx registryEx)
        {
            this.registryEx = registryEx;
            this.buzzPath = buzzPath;
            this.buzzCore = buzzCore;
            this.engineSettings = settings;
            this.dispatcher = dispatcher;
            deviceService = new AudioDeviceService(buzzCore.UserMessages);

            CreateScheduler();
        }

        internal void CreateScheduler()
        {
            ThreadCount = registryEx.Read("AudioThreads", 4, "Settings");

            var dedicatedPool = new DedicatedThreadPool(
                new DedicatedThreadPoolSettings(ThreadCount));

            TaskSchedulerAudio = new DedicatedThreadPoolTaskScheduler(dedicatedPool);
            TaskFactoryAudio = new TaskFactory(TaskSchedulerAudio);
        }

        public List<AudioOutDevice> AudioDevices()
        {
            return deviceService.GetOutputDevices();
        }

        internal void CreateAudioOut(string audioDriver)
        {
            dispatcher.Invoke(FinalStop);
            ReleaseAudioDriver();

            var device = deviceService.ResolveDevice(audioDriver);
            if (device == null)
                return;

            try
            {
                switch (device.Type)
                {
                    case AudioOutType.ASIO:
                    case AudioOutType.Wasapi:
                        currentDriver = AudioDriverFactory.Create(
                            device.Type,
                            device.Name,
                            buzzCore,
                            engineSettings,
                            registryEx);

                        currentDriver.Initialize();
                        SelectedOutDevice = currentDriver.DeviceInfo;
                        SelectedInDevice = currentDriver.InputDeviceInfo;
                        SampleRateIn = currentDriver.SampleRate;

                        // Fork (0.4.1): output latency for the Session View's launch timing. WASAPI
                        // runs with a 2 x BufferSize buffer, ASIO with BufferSize; the fill thread
                        // keeps up to two more buffers ready.
                        int frames = currentDriver.BufferSize * (device.Type == AudioOutType.Wasapi ? 2 : 1);
                        if (engineSettings.AudioBufferFillThread)
                            frames += currentDriver.BufferSize * 2;
                        buzzCore.OutputLatencySamples = Math.Max(0, frames);
                        break;

                    case AudioOutType.DirectSound:
                        // Intentionally not implemented (same as original comment).
                        break;
                }
            }
            catch (Exception e)
            {
                buzzCore.DCWriteLine("Audio Driver Error: " + e.Message);
            }
        }

        public void Play()
        {
            if (SelectedOutDevice == null || currentDriver == null)
                return;

            try
            {
                currentDriver.Start();
            }
            catch (Exception e)
            {
                buzzCore.DCWriteLine("WavePlayer error: " + e.Message);
            }
        }

        public void Stop()
        {
            try
            {
                StopPlayback();
                ClearAudioBuffer();
            }
            catch (Exception e)
            {
                buzzCore.DCWriteLine(e.Message);
            }
        }

        private void StopPlayback()
        {
            try
            {
                currentDriver?.Stop();
            }
            catch (Exception e)
            {
                buzzCore.DCWriteLine(e.Message);
            }
        }

        public void FinalStop()
        {
            lock (audioEngineLock)
            {
                currentDriver?.Provider?.AudioSampleProvider?.Stop();

                if (SelectedOutDevice == null)
                    return;

                StopPlayback();
            }
        }

        public void ReleaseAudioDriver()
        {
            currentDriver?.Dispose();
            currentDriver = null;

            SelectedOutDevice = null;
            SelectedInDevice = null;
        }

        internal void ShowControlPanel()
        {
            if (SelectedOutDevice == null)
                return;

            switch (SelectedOutDevice.Type)
            {
                case AudioOutType.ASIO:
                    ShowAsioControlPanel();
                    break;

                case AudioOutType.Wasapi:
                    ShowWasapiControlPanel();
                    break;

                case AudioOutType.DirectSound:
                    break;
            }
        }

        private void ShowAsioControlPanel()
        {
            if (asioConfigWindow != null)
                return;

            var asio = SelectedOutDevice.WavePlayer as AsioDevice;
            if (asio == null)
                return;

            asioConfigWindow = new AsioConfigWindow(buzzCore, asio.DriverName, registryEx);

            asioConfigWindow.OpenAsioControlPanel += () =>
            {
                asio.ShowControlPanel();
            };

            var rd = UiUtils.GetUserControlXAML<ResourceDictionary>(
                "MachineView\\MVResources.xaml",
                buzzPath);

            asioConfigWindow.Resources.MergedDictionaries.Add(rd);

            if (asioConfigWindow.ShowDialog() == true)
            {
                asioConfigWindow.SaveSelection();
                CreateAudioOut(SelectedOutDevice.Name);
                Play();
            }

            asioConfigWindow = null;
        }

        private void ShowWasapiControlPanel()
        {
            if (wasapiConfigWindow != null)
                return;

            wasapiConfigWindow = new WasapiConfigWindow(registryEx);

            var rd = UiUtils.GetUserControlXAML<ResourceDictionary>(
                "MachineView\\MVResources.xaml",
                buzzPath);

            wasapiConfigWindow.Resources.MergedDictionaries.Add(rd);

            if (wasapiConfigWindow.ShowDialog() == true)
            {
                wasapiConfigWindow.SaveSelection();
                CreateAudioOut(SelectedOutDevice.Name);
                Play();
            }

            wasapiConfigWindow = null;
        }

        internal void ClearAudioBuffer()
        {
            var provider = GetAudioProvider();
            bufferManager.Clear(provider);
        }

        internal void ClearChannels()
        {
            var provider = GetAudioProvider();
            bufferManager.ClearChannels(provider);
        }

        internal void Reset()
        {
            if (SelectedOutDevice == null)
                return;

            CreateAudioOut(SelectedOutDevice.Name);
            Play();
        }

        internal IReBuzzAudioProvider GetAudioProvider()
        {
            return currentDriver?.Provider;
        }

        internal int GetASIOCurrentBufferSize(string deviceName)
        {
            var device = AsioDevice.Open(deviceName);
            int size = device.Capabilities.BufferPreferredSize;
            device.Dispose();
            return size;
        }
    }
}
