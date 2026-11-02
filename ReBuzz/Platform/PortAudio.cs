#nullable disable
// Fork (docs/specs/mac-engine.md M5): PortAudio, for audio on macOS (CoreAudio) and Linux (ALSA),
// through our own P/Invoke: just what the engine needs (devices, one duplex or output stream of
// interleaved 32-bit float, its latency). Decision 3 there: no runtime package, so the dylib we
// sign is the one we build.
//
// The binding is for LP64 platforms (macOS, Linux), where C's `unsigned long` is 64 bits: the
// sample format, frame counts and callback flags are nuint. Windows keeps ASIO and WASAPI.
//
// Real time: the callback runs on the device's thread (CoreAudio's I/O thread on macOS, already
// real-time and in the device's workgroup). It calls the render delegate with buffers allocated
// up front, copies, and returns: no allocation, no locks of its own.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ReBuzz.Platform
{
    public static unsafe class PortAudio
    {
        const string Lib = "portaudio";

        public const int NoDevice = -1;
        public const int Continue = 0;
        const ulong Float32 = 0x00000001;
        const ulong ClipOff = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        public struct DeviceInfo
        {
            public int StructVersion;
            public IntPtr Name;
            public int HostApi;
            public int MaxInputChannels;
            public int MaxOutputChannels;
            public double DefaultLowInputLatency;
            public double DefaultLowOutputLatency;
            public double DefaultHighInputLatency;
            public double DefaultHighOutputLatency;
            public double DefaultSampleRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HostApiInfo
        {
            public int StructVersion;
            public int Type;
            public IntPtr Name;
            public int DeviceCount;
            public int DefaultInputDevice;
            public int DefaultOutputDevice;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct StreamParameters
        {
            public int Device;
            public int ChannelCount;
            public nuint SampleFormat;
            public double SuggestedLatency;
            public IntPtr HostApiSpecificStreamInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct StreamInfo
        {
            public int StructVersion;
            public double InputLatency;
            public double OutputLatency;
            public double SampleRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct CallbackTimeInfo
        {
            public double InputBufferAdcTime;
            public double CurrentTime;
            public double OutputBufferDacTime;
        }

        [DllImport(Lib)] static extern int Pa_Initialize();
        [DllImport(Lib)] static extern int Pa_Terminate();
        [DllImport(Lib)] static extern int Pa_GetVersion();
        [DllImport(Lib)] static extern IntPtr Pa_GetVersionText();
        [DllImport(Lib)] static extern IntPtr Pa_GetErrorText(int error);
        [DllImport(Lib)] static extern int Pa_GetDeviceCount();
        [DllImport(Lib)] static extern int Pa_GetDefaultOutputDevice();
        [DllImport(Lib)] static extern int Pa_GetDefaultInputDevice();
        [DllImport(Lib)] static extern DeviceInfo* Pa_GetDeviceInfo(int device);
        [DllImport(Lib)] static extern HostApiInfo* Pa_GetHostApiInfo(int hostApi);
        [DllImport(Lib)] static extern int Pa_OpenStream(out IntPtr stream, StreamParameters* input, StreamParameters* output, double sampleRate,
                                                         nuint framesPerBuffer, nuint flags, delegate* unmanaged[Cdecl]<void*, void*, nuint, CallbackTimeInfo*, nuint, void*, int> callback, void* userData);
        [DllImport(Lib)] static extern int Pa_StartStream(IntPtr stream);
        [DllImport(Lib)] static extern int Pa_StopStream(IntPtr stream);
        [DllImport(Lib)] static extern int Pa_CloseStream(IntPtr stream);
        [DllImport(Lib)] static extern StreamInfo* Pa_GetStreamInfo(IntPtr stream);

        static PortAudio() => NativeLibraries.Register();

        static readonly object gate = new object();
        static int users;

        /// <summary>Whether the library loads here (it is installed or bundled).</summary>
        public static bool Available
        {
            get
            {
                try { return Pa_GetVersion() > 0; }
                catch (DllNotFoundException) { return false; }
                catch (EntryPointNotFoundException) { return false; }
            }
        }

        public static string VersionText => Marshal.PtrToStringAnsi(Pa_GetVersionText());

        /// <summary>PortAudio is initialised once per user; each <see cref="Acquire"/> needs a <see cref="Release"/>.</summary>
        public static void Acquire()
        {
            lock (gate)
            {
                if (users == 0) Check(Pa_Initialize(), "Pa_Initialize");
                users++;
            }
        }

        public static void Release()
        {
            lock (gate)
            {
                if (users == 0) return;
                if (--users == 0) Pa_Terminate();
            }
        }

        public sealed record Device(int Index, string Name, string HostApi, int Inputs, int Outputs, double DefaultSampleRate, double LowOutputLatency);

        /// <summary>The devices (between Acquire and Release).</summary>
        public static IReadOnlyList<Device> Devices()
        {
            var list = new List<Device>();
            int n = Pa_GetDeviceCount();
            for (int i = 0; i < n; i++)
            {
                var d = Pa_GetDeviceInfo(i);
                if (d == null) continue;
                var api = Pa_GetHostApiInfo(d->HostApi);
                list.Add(new Device(i, Marshal.PtrToStringUTF8(d->Name) ?? $"Device {i}", api == null ? "" : Marshal.PtrToStringUTF8(api->Name) ?? "",
                                    d->MaxInputChannels, d->MaxOutputChannels, d->DefaultSampleRate, d->DefaultLowOutputLatency));
            }
            return list;
        }

        public static int DefaultOutput => Pa_GetDefaultOutputDevice();
        public static int DefaultInput => Pa_GetDefaultInputDevice();

        internal static void Check(int error, string what)
        {
            if (error < 0) throw new InvalidOperationException($"{what}: {Marshal.PtrToStringAnsi(Pa_GetErrorText(error))} ({error})");
        }

        /// <summary>
        /// Fills interleaved stereo output for <c>frames</c> frames; <c>input</c> holds the
        /// interleaved input (its channel count as opened), or is empty. Runs on the device's thread.
        /// </summary>
        public delegate void Render(float[] output, float[] input, int frames);

        /// <summary>A stereo float stream on one device (with input from another, or none).</summary>
        public sealed class Stream : IDisposable
        {
            readonly Render render;
            readonly float[] output;
            readonly float[] input;
            readonly int inputChannels;
            GCHandle self;
            IntPtr stream;
            long callbacks;
            long underruns;

            /// <param name="outputDevice">The device to play on (<see cref="DefaultOutput"/>).</param>
            /// <param name="inputDevice">A device to record from, or <see cref="NoDevice"/>.</param>
            /// <param name="bufferFrames">Frames per callback (0: PortAudio chooses, varying).</param>
            public Stream(int outputDevice, int inputDevice, int inputChannels, double sampleRate, int bufferFrames, double suggestedLatency, Render render)
            {
                this.render = render;
                this.inputChannels = inputDevice == NoDevice ? 0 : Math.Max(1, inputChannels);
                // Room for the largest callback PortAudio may make when it chooses (CoreAudio caps at 4096).
                int room = bufferFrames > 0 ? bufferFrames : 8192;
                output = new float[room * 2];
                input = new float[room * Math.Max(1, this.inputChannels)];
                Acquire();
                try
                {
                    var outParams = new StreamParameters { Device = outputDevice, ChannelCount = 2, SampleFormat = (nuint)Float32, SuggestedLatency = suggestedLatency };
                    var inParams = new StreamParameters { Device = inputDevice, ChannelCount = this.inputChannels, SampleFormat = (nuint)Float32, SuggestedLatency = suggestedLatency };
                    self = GCHandle.Alloc(this);
                    Check(Pa_OpenStream(out stream, inputDevice == NoDevice ? null : &inParams, &outParams, sampleRate, (nuint)Math.Max(0, bufferFrames),
                                        (nuint)ClipOff, &Callback, (void*)GCHandle.ToIntPtr(self)), "Pa_OpenStream");
                    var info = Pa_GetStreamInfo(stream);
                    if (info != null)
                    {
                        SampleRate = info->SampleRate;
                        OutputLatency = info->OutputLatency;
                        InputLatency = info->InputLatency;
                    }
                }
                catch
                {
                    if (self.IsAllocated) self.Free();
                    Release();
                    throw;
                }
            }

            public double SampleRate { get; }
            /// <summary>Seconds from a rendered sample to the speaker, as the device reports it.</summary>
            public double OutputLatency { get; }
            public double InputLatency { get; }
            public long Callbacks => System.Threading.Interlocked.Read(ref callbacks);
            /// <summary>Callbacks bigger than the buffers allocated up front (they got silence).</summary>
            public long Underruns => System.Threading.Interlocked.Read(ref underruns);

            public void Start() => Check(Pa_StartStream(stream), "Pa_StartStream");
            public void Stop() => Check(Pa_StopStream(stream), "Pa_StopStream");

            public void Dispose()
            {
                if (stream == IntPtr.Zero) return;
                Pa_CloseStream(stream);           // stops it first if it runs
                stream = IntPtr.Zero;
                if (self.IsAllocated) self.Free();
                Release();
            }

            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
            static int Callback(void* inputBuffer, void* outputBuffer, nuint frameCount, CallbackTimeInfo* time, nuint flags, void* user)
            {
                var s = (Stream)GCHandle.FromIntPtr((IntPtr)user).Target;
                int frames = (int)frameCount;
                float* outPtr = (float*)outputBuffer;
                System.Threading.Interlocked.Increment(ref s.callbacks);
                if (frames * 2 > s.output.Length)
                {
                    System.Threading.Interlocked.Increment(ref s.underruns);
                    new Span<float>(outPtr, frames * 2).Clear();
                    return Continue;
                }
                try
                {
                    if (inputBuffer != null && s.inputChannels > 0)
                        new ReadOnlySpan<float>(inputBuffer, frames * s.inputChannels).CopyTo(s.input);
                    s.render(s.output, s.input, frames);
                    new ReadOnlySpan<float>(s.output, 0, frames * 2).CopyTo(new Span<float>(outPtr, frames * 2));
                }
                catch (Exception)
                {
                    // An exception must never cross into the device's thread: play silence.
                    new Span<float>(outPtr, frames * 2).Clear();
                }
                return Continue;
            }
        }
    }
}
