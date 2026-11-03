using BuzzGUI.Common.Settings;
using Helios.Concurrency;
using NAudio.Wave;
using ReBuzz.Common;
using ReBuzz.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ReBuzz.Audio.Engine
{
    public class AudioEngine
    {
        public enum AudioOutType
        {
            ASIO,
            Wasapi,
            DirectSound,
            PortAudio       // Fork (docs/specs/mac-engine.md M5): macOS and Linux
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

        /// <summary>The open driver (the agent's studio_status reports on it), or null.</summary>
        internal IAudioDriver CurrentDriver => currentDriver;
        private readonly AudioBufferManager bufferManager = new();
        private readonly AudioDeviceService deviceService;
        private readonly IAudioDriverFactory drivers;   // engine-split.md S8

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
            drivers = buzzCore.AudioDriverFactory;
            deviceService = new AudioDeviceService(buzzCore.UserMessages, drivers);

            CreateScheduler();

            // Fork (audio hygiene): the audio path compiled in the background before it first runs.
            AudioWarmUp.Begin();
        }

        internal void CreateScheduler()
        {
            ThreadCount = registryEx.Read("AudioThreads", 4, "Settings");

            var dedicatedPool = new DedicatedThreadPool(
                new DedicatedThreadPoolSettings(ThreadCount));

            TaskSchedulerAudio = new DedicatedThreadPoolTaskScheduler(dedicatedPool);
            TaskFactoryAudio = new TaskFactory(TaskSchedulerAudio);

            // Fork (audio hygiene): the work algorithms dispatch to render threads of their own
            // (RenderJobs), with nothing allocated per dispatch.
            RenderThreads.Ensure(ThreadCount);
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
                    case AudioOutType.PortAudio:
                        currentDriver = (drivers ?? throw new NotSupportedException($"Unsupported audio driver type: {device.Type}")).Create(
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
                        // PortAudio reports the device's own latency (mac-engine.md M5).
                        int frames = currentDriver is PortAudioDriver portAudio
                            ? portAudio.LatencyFrames
                            : currentDriver.BufferSize * (device.Type == AudioOutType.Wasapi ? 2 : 1);
                        if (engineSettings.AudioBufferFillThread && OperatingSystem.IsWindows())
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

            // Fork (engine-split.md S8): the ASIO and WASAPI settings windows are the driver
            // factory's; when the user accepts, the device reopens with the new settings.
            switch (SelectedOutDevice.Type)
            {
                case AudioOutType.ASIO:
                case AudioOutType.Wasapi:
                    if (drivers != null && drivers.ShowConfig(SelectedOutDevice, buzzCore, registryEx, buzzPath))
                    {
                        CreateAudioOut(SelectedOutDevice.Name);
                        Play();
                    }
                    break;

                case AudioOutType.DirectSound:
                    break;
            }
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
            return drivers.AsioPreferredBufferSize(deviceName);
        }
    }
}
