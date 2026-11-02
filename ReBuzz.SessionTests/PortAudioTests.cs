using System.Diagnostics;
using ReBuzz.Platform;

namespace ReBuzz.SessionTests
{
    /// <summary>
    /// The PortAudio binding for the Mac engine (docs/specs/mac-engine.md M5). Linux CI runs it on
    /// ALSA, which has the same LP64 ABI as macOS: the library loads through the engine's resolver,
    /// devices enumerate, and a stream runs its callback where the runner has a device.
    /// </summary>
    [TestFixture]
    public class PortAudioTests
    {
        [Test]
        public void TheResolverKnowsThePlatformsNamesAndPlaces()
        {
            var names = NativeLibraries.FileNames("portaudio").ToList();
            var folders = NativeLibraries.Folders().ToList();
            Assert.That(folders[0], Is.EqualTo(AppContext.BaseDirectory), "the app's own copy first");
            if (OperatingSystem.IsMacOS())
            {
                Assert.That(names[0], Is.EqualTo("libportaudio.dylib"));
                Assert.That(folders, Does.Contain("/opt/homebrew/lib"));
            }
            else if (OperatingSystem.IsLinux())
                Assert.That(names, Does.Contain("libportaudio.so.2"), "the runtime package's versioned name");
        }

        [Test]
        public void ItLoadsAndListsDevices()
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) Assert.Ignore("PortAudio is the macOS and Linux driver");
            if (!PortAudio.Available)
            {
                if (Environment.GetEnvironmentVariable("CI") == "true" && OperatingSystem.IsLinux())
                    Assert.Fail("libportaudio2 is installed in CI, so it should load");
                Assert.Ignore("PortAudio isn't installed here");
            }
            Assert.That(PortAudio.VersionText, Does.StartWith("PortAudio"));
            PortAudio.Acquire();
            try
            {
                var devices = PortAudio.Devices();
                TestContext.Out.WriteLine(devices.Count == 0 ? "no devices" : string.Join("\n", devices));
                Assert.That(devices.All(d => d.Inputs >= 0 && d.Outputs >= 0 && d.Name.Length > 0), Is.True);
            }
            finally
            {
                PortAudio.Release();
            }
        }

        [Test]
        public void AStreamRunsItsCallbackWithBuffersAllocatedUpFront()
        {
            if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) || !PortAudio.Available) Assert.Ignore("PortAudio isn't here");
            PortAudio.Acquire();
            try
            {
                int device = PortAudio.DefaultOutput;
                if (device == PortAudio.NoDevice)
                {
                    if (Environment.GetEnvironmentVariable("CI") == "true" && OperatingSystem.IsLinux())
                        Assert.Fail("CI runs a PulseAudio null sink, so there should be an output");
                    Assert.Ignore("no output device on this machine");
                }
                long frames = 0, allocated = -1;
                double phase = 0;
                using var stream = new PortAudio.Stream(device, PortAudio.NoDevice, 0, 48000, 256, 0.05, (output, input, n) =>
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < n; i++)
                    {
                        float v = (float)(0.01 * Math.Sin(phase));
                        phase += 2 * Math.PI * 440 / 48000;
                        output[2 * i] = output[2 * i + 1] = v;
                    }
                    frames += n;
                    allocated = Math.Max(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
                });
                stream.Start();
                var clock = Stopwatch.StartNew();
                while (Interlocked.Read(ref frames) < 4800 && clock.ElapsedMilliseconds < 3000) Thread.Sleep(10);
                stream.Stop();
                Assert.That(frames, Is.GreaterThanOrEqualTo(4800), "a tenth of a second played");
                Assert.That(stream.Underruns, Is.EqualTo(0));
                Assert.That(allocated, Is.EqualTo(0), "the render allocates nothing on the device's thread");
                Assert.That(stream.SampleRate, Is.EqualTo(48000).Within(1));
            }
            finally
            {
                PortAudio.Release();
            }
        }

        [Test]
        public void WorkersJoinTheDevicesAudioWorkgroupAndLeaveIt()
        {
            // mac-engine.md M7: render workers join the playing device's I/O workgroup (macOS).
            if (!AudioWorkgroup.Supported)
            {
                Assert.That(AudioWorkgroup.Attach(1, 0.005), Is.False, "nothing to attach off macOS");
                AudioWorkgroup.Enter();
                Assert.That(AudioWorkgroup.JoinedThreads, Is.Zero);
                Assert.Ignore("audio workgroups are macOS's");
            }
            if (!PortAudio.Available) Assert.Ignore("PortAudio isn't here");
            PortAudio.Acquire();
            try
            {
                int device = PortAudio.DefaultOutput;
                if (device == PortAudio.NoDevice) Assert.Ignore("no output device on this machine");
                using var stream = new PortAudio.Stream(device, PortAudio.NoDevice, 0, 48000, 256, 0.05, (output, input, n) => Array.Clear(output, 0, 2 * n));
                stream.Start();
                Assert.That(stream.MacOutputDevice, Is.Not.EqualTo(0u), "the CoreAudio device behind the stream");
                Assert.That(AudioWorkgroup.Attach(stream.MacOutputDevice, 256 / 48000.0), Is.True, AudioWorkgroup.LastError);
                TestContext.Out.WriteLine($"max parallel threads: {AudioWorkgroup.MaxParallelThreads}");

                int joinedInside = -1, failures = AudioWorkgroup.Failures;
                var worker = new Thread(() =>
                {
                    AudioWorkgroup.Enter();
                    AudioWorkgroup.Enter();             // nothing changed: nothing to do
                    joinedInside = AudioWorkgroup.JoinedThreads;
                    AudioWorkgroup.Leave();
                });
                worker.Start();
                worker.Join();
                Assert.That(AudioWorkgroup.Failures, Is.EqualTo(failures), AudioWorkgroup.LastError);
                Assert.That(joinedInside, Is.EqualTo(1));
                Assert.That(AudioWorkgroup.JoinedThreads, Is.Zero, "it left when it ended");

                // A worker in the workgroup when the device stops leaves at its next Enter.
                using var go = new ManualResetEventSlim();
                using var detached = new ManualResetEventSlim();
                int afterDetach = -1;
                worker = new Thread(() =>
                {
                    AudioWorkgroup.Enter();
                    go.Set();
                    detached.Wait();
                    AudioWorkgroup.Enter();
                    afterDetach = AudioWorkgroup.JoinedThreads;
                    AudioWorkgroup.Leave();
                });
                worker.Start();
                go.Wait();
                Assert.That(AudioWorkgroup.JoinedThreads, Is.EqualTo(1));
                AudioWorkgroup.Detach();
                detached.Set();
                worker.Join();
                Assert.That(afterDetach, Is.Zero);
                Assert.That(AudioWorkgroup.Attached, Is.False);
                stream.Stop();
            }
            finally
            {
                AudioWorkgroup.Detach();
                PortAudio.Release();
            }
        }
    }
}
