// Fork (docs/specs/mac-engine.md M5): ReBuzz's audio output (and input, for audio takes) through
// PortAudio: CoreAudio on macOS, ALSA or PulseAudio on Linux. One stereo float stream; the
// device's thread renders, as ASIO's does on Windows (single-threaded rendering on macOS first:
// mac-engine.md decision 2).
//
// Settings, in the registry's "PortAudio" section:
//   BufferSize   frames per callback (default 256)
//   SampleRate   0: the device's own rate (default)
//   Input        "" (default): no input; "default", or a device's name: record from it
//
// Real time: the callback reads the engine's provider into buffers allocated when the stream
// opens (PortAudio.Stream), and hands input to the engine the same way. No allocation.

using BuzzGUI.Common.Settings;
using NAudio.Wave;
using ReBuzz.Audio.Engine;
using ReBuzz.Core;
using ReBuzz.Platform;
using System;
using System.Linq;

namespace ReBuzz.Audio
{
    internal sealed class PortAudioDriver : IAudioDriver
    {
        internal const string Section = "PortAudio";

        private readonly string deviceName;
        private readonly ReBuzzCore buzzCore;
        private readonly EngineSettings engineSettings;
        private readonly IRegistryEx registryEx;

        private PortAudio.Stream stream;
        private AudioWaveProvider provider;
        private int inputChannels;
        private bool running;

        public int SampleRate { get; private set; }
        public int BufferSize { get; private set; }

        /// <summary>Frames from a rendered sample to the speaker, as the device reports it.</summary>
        public int LatencyFrames { get; private set; }

        public IReBuzzAudioProvider Provider => provider;
        public AudioEngine.AudioOutDevice DeviceInfo { get; private set; }
        public AudioEngine.AudioInDevice InputDeviceInfo { get; private set; }

        /// <summary>The open stream's counters (callbacks, oversize callbacks), for tests and status.</summary>
        internal PortAudio.Stream Stream => stream;

        public PortAudioDriver(string deviceName, ReBuzzCore buzzCore, EngineSettings settings, IRegistryEx registryEx)
        {
            this.deviceName = deviceName;
            this.buzzCore = buzzCore;
            this.engineSettings = settings;
            this.registryEx = registryEx;
        }

        public void Initialize()
        {
            BufferSize = Math.Clamp(registryEx.Read("BufferSize", 256, Section), 16, 4096);
            int rate = registryEx.Read("SampleRate", 0, Section);
            string input = registryEx.Read("Input", "", Section);

            PortAudio.Acquire();        // to list the devices; the stream holds its own
            try
            {
                var devices = PortAudio.Devices();
                var output = PortAudioDriverFactory.Find(devices, deviceName, PortAudio.DefaultOutput, d => d.Outputs >= 2)
                             ?? throw new InvalidOperationException($"No PortAudio output device \"{deviceName}\" with two channels.");
                var inputDevice = string.IsNullOrWhiteSpace(input) ? null
                    : PortAudioDriverFactory.Find(devices, input, PortAudio.DefaultInput, d => d.Inputs >= 1);
                if (!string.IsNullOrWhiteSpace(input) && inputDevice == null)
                    buzzCore.DCWriteLine($"PortAudio: no input device \"{input}\"; playing without input.");

                SampleRate = rate > 0 ? rate : (int)Math.Round(output.DefaultSampleRate);
                provider = new AudioWaveProvider(buzzCore, SampleRate, 2, BufferSize, true, registryEx, engineSettings);

                stream = Open(output, inputDevice);
                if (stream == null && inputDevice != null)
                {
                    // Input and output on different devices isn't possible everywhere: play without input.
                    buzzCore.DCWriteLine($"PortAudio: can't record from {inputDevice.Name} while playing on {output.Name}; playing without input.");
                    inputDevice = null;
                    stream = Open(output, null);
                }
                if (stream == null)
                    throw new InvalidOperationException($"PortAudio couldn't open {output.Name} at {SampleRate} Hz, {BufferSize} frames.");

                LatencyFrames = Math.Max(BufferSize, (int)Math.Round(stream.OutputLatency * SampleRate));
                DeviceInfo = new AudioEngine.AudioOutDevice
                {
                    Name = deviceName,
                    Type = AudioEngine.AudioOutType.PortAudio,
                    WavePlayer = stream
                };
                if (inputDevice != null)
                {
                    InputDeviceInfo = new AudioEngine.AudioInDevice
                    {
                        Name = PortAudioDriverFactory.Label(inputDevice),
                        Type = AudioEngine.AudioOutType.PortAudio,
                        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, inputChannels)
                    };
                }
                buzzCore.DCWriteLine($"PortAudio {PortAudio.VersionText}: {PortAudioDriverFactory.Label(output)}, {SampleRate} Hz, " +
                                     $"{BufferSize} frames, {stream.OutputLatency * 1000:F1} ms out" +
                                     (inputDevice != null ? $", input from {inputDevice.Name} ({inputChannels} ch)" : ""));
            }
            finally
            {
                PortAudio.Release();
            }
        }

        private PortAudio.Stream Open(PortAudio.Device output, PortAudio.Device input)
        {
            inputChannels = input == null ? 0 : Math.Min(2, input.Inputs);
            try
            {
                return new PortAudio.Stream(output.Index, input?.Index ?? PortAudio.NoDevice, inputChannels, SampleRate, BufferSize,
                                            output.LowOutputLatency, Render);
            }
            catch (InvalidOperationException e)
            {
                buzzCore.DCWriteLine("PortAudio: " + e.Message);
                inputChannels = 0;
                return null;
            }
        }

        /// <summary>The device's thread: input to the engine, then the engine's output.</summary>
        private void Render(float[] output, float[] input, int frames)
        {
            if (inputChannels > 0)
                buzzCore.AudioInputAvalable(input, frames, inputChannels);
            int samples = frames * 2;
            int read = provider.Read(output.AsSpan(0, samples));
            if (read < samples)
                output.AsSpan(Math.Max(0, read), samples - Math.Max(0, read)).Clear();
        }

        public void Start()
        {
            if (stream == null || running) return;
            stream.Start();
            running = true;
        }

        public void Stop()
        {
            if (stream == null || !running) return;
            running = false;
            stream.Stop();
        }

        public void Reset()
        {
            Stop();
            Dispose();
            Initialize();
            Start();
        }

        public void ShowControlPanel()
        {
            // No control panel: the settings are in the registry (rebuzz-headless's options).
        }

        public void Dispose()
        {
            running = false;
            stream?.Dispose();          // stops it first if it runs
            stream = null;
        }
    }
}
