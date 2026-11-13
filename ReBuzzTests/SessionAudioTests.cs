using AtmaFileSystem;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzzTests.Automation;
using ReBuzzTests.Automation.TestMachines;
using ReBuzzTests.Automation.TestMachinesControllers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition: audio tracks and clips (docs/specs/session-view.md 1.0) through the real
    /// audio loop. The Session Clip Player machine is compiled from its source in the repository
    /// (Machines/SessionClipPlayer); the clip's audio is made in the wavetable directly, and a
    /// WAV file is imported (Inconclusive where libsndfile can't load). The warping itself is
    /// tested in detail by ReBuzz.SessionTests (AudioClipTests).
    /// </summary>
    [Apartment(ApartmentState.STA)]
    public class SessionAudioTests
    {
        /// <summary>The real Session Clip Player, compiled from its source.</summary>
        internal sealed class ClipPlayerInfo : IDynamicTestMachineInfo
        {
            public static IDynamicTestMachineInfo Instance { get; } = new ClipPlayerInfo();

            static string SourcePath([CallerFilePath] string here = "") =>
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "Machines", "SessionClipPlayer", "SessionClipPlayer.cs"));

            MachineDLL ITestMachineInfo.GetMachineDll(ReBuzzCore buzz, AbsoluteFilePath location) => new MachineDLL
            {
                Name = SessionClipHost.MachineName,
                Buzz = buzz,
                Path = location.ToString(),
                Is64Bit = true,
                IsCrashed = false,
                IsManaged = true,
                IsLoaded = false,
                IsMissing = false,
                IsOutOfProcess = false,
                ManagedDLL = null,
                MachineInfo = new MachineInfo
                {
                    Flags = MachineInfoFlags.LOAD_DATA_RUNTIME,
                    Author = "neo-buzz",
                    InternalVersion = 0,
                    MaxTracks = 0,
                    MinTracks = 0,
                    Name = SessionClipHost.MachineName,
                    ShortName = "Clips",
                    Type = MachineType.Effect,
                    Version = 66
                },
                Presets = null,
                SHA1Hash = "5E55C0A1D0000000000000000000000000000000",
                GUIFactoryDecl = new MachineGUIFactoryDecl { IsGUIResizable = true, PreferWindowedGUI = true, UseThemeStyles = false },
                ModuleHandle = 0
            };

            public string DllName => SessionClipHost.MachineName + ".dll";
            public string RealDllName => DllName;
            public string SourceCode => File.ReadAllText(SourcePath());
        }

        internal sealed class Rig : IDisposable
        {
            public Driver Driver { get; } = new Driver();
            public ReBuzzCore Core => Driver.Core;
            public SessionLayout Layout => Core.SessionLayout;
            public SessionCore Session => (SessionCore)Core.Session;
            public HumController? Hum { get; }
            readonly Driver.AudioReader reader;

            public Rig(bool hum = false)
            {
                Driver.Gear.AddDynamicEffect(ClipPlayerInfo.Instance);
                if (hum)
                {
                    Hum = HumController.NewInstance("Hum");
                    Driver.Gear.AddDynamicGenerator(HumController.Info);
                }
                Driver.Start();
                if (Hum != null) Driver.MachineGraph.InsertMachineInstanceFor(Hum);
                Core.Looping = true;
                reader = Driver.CreateAudioReader(256);
            }

            public int Rate => ReBuzzCore.masterInfo.SamplesPerSec;

            /// <summary>A wave of the song holding this audio (mono floats).</summary>
            public int Wave(float[] audio, int rate)
            {
                var wavetable = Core.SongCore.Wavetable;
                int index = Enumerable.Range(0, wavetable.Waves.Count).First(i => wavetable.Waves[i] == null);
                wavetable.AllocateWave(index, "", "test " + index, audio.Length, WaveFormat.Float32, false, 65, false, true);
                var layer = wavetable.Waves[index].Layers[0];
                layer.SampleRate = rate;
                layer.SetDataAsFloat(audio, 0, 1, 0, 0, audio.Length);
                return index;
            }

            /// <summary>An audio track with a clip of a wave in scene 0.</summary>
            public (SequenceCore Track, PatternCore Clip) AudioClip(int wave, double bpm, string warp = "Beats")
            {
                Layout.Ensure();
                var machine = Layout.NewAudioTrack();
                Assert.That(machine, Is.Not.Null, "the Session Clip Player is there");
                int track = Layout.FirstTrackOf(machine!);
                var clip = (PatternCore)Layout.AddAudioClip(track, 0, machine!, "clip",
                    new SessionDocument.AudioSettings { Wave = wave, Bpm = bpm, Warp = warp })!;
                Assert.That(clip, Is.Not.Null);
                Layout.RebuildLaunchMap();
                return ((SequenceCore)Layout.SequenceOf(track)!, clip);
            }

            /// <summary>Renders the output (left channel) for a while.</summary>
            public float[] Play(double seconds)
            {
                int frames = (int)(seconds * Rate);
                var output = new float[frames];
                for (int at = 0; at < frames; at += 256)
                {
                    int n = Math.Min(256, frames - at);
                    reader.Read(n);
                    for (int i = 0; i < n; i++) output[at + i] = reader.Buffer[2 * i];
                }
                return output;
            }

            /// <summary>Plays for a while, polling takes as the view's timer would (headless tests poll themselves).</summary>
            public float[] PlayPolling(double seconds)
            {
                var all = new List<float>();
                for (double t = 0; t < seconds; t += 0.1)
                {
                    all.AddRange(Play(Math.Min(0.1, seconds - t)));
                    Layout.PollTakes();
                }
                return all.ToArray();
            }

            /// <summary>Bytes the audio loop allocated while playing for a while (nothing else allocating meanwhile).</summary>
            public long AllocatedWhilePlaying(double seconds)
            {
                int blocks = (int)(seconds * Rate / 256);
                return GuardPolicy.Allocated(() => { for (int i = 0; i < blocks; i++) reader.Read(256); }, warm: false);   // GuardPolicy: 0 unless every attempt allocates
            }

            /// <summary>Plays in short blocks until the session clock reaches a tick.</summary>
            public void PlayToClock(long clock)
            {
                for (int guard = 0; Session.Clock < clock && guard < 100_000; guard++) reader.Read(64);
            }

            /// <summary>Plays in short blocks until a pattern plays (or for a while); its tick then.</summary>
            public int PlayUntilPlaying(PatternCore p)
            {
                for (int guard = 0; p.PlayPosition == int.MinValue && guard < 10_000; guard++) reader.Read(64);
                return p.PlayPosition == int.MinValue ? -1 : p.PlayPosition / PatternEvent.TimeBase;
            }

            public void Dispose()
            {
                SessionCore.Timestamp = System.Diagnostics.Stopwatch.GetTimestamp;
                Core.Playing = false;
                Driver.Dispose();
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>A short 1 kHz burst on each beat at <paramref name="bpm"/>.</summary>
        static float[] Clicks(double bpm, int beats, int rate)
        {
            int perBeat = (int)Math.Round(rate * 60 / bpm);
            var x = new float[perBeat * beats];
            for (int b = 0; b < beats; b++)
                for (int i = 0; i < rate / 50; i++)
                    x[b * perBeat + i] = (float)(0.8 * Math.Sin(2 * Math.PI * 1000 * i / rate) * Math.Exp(-i / (0.005 * rate)));
            return x;
        }

        /// <summary>Where sound starts (above a fifth of the loudest) with nothing as loud in the 20 ms before, in seconds.</summary>
        static List<double> SoundStarts(float[] x, int rate)
        {
            float peak = x.Length == 0 ? 0 : x.Max(v => Math.Abs(v));
            float threshold = peak / 5;
            var starts = new List<double>();
            int loud = -rate;
            for (int i = 0; i < x.Length; i++)
            {
                if (Math.Abs(x[i]) <= threshold) continue;
                if (i - loud > rate / 50) starts.Add(i / (double)rate);
                loud = i;
            }
            return starts;
        }

        // ------------------------------------------------------------------ tests

        [TestCase(80)]
        [TestCase(120)]
        [TestCase(160)]
        public void ALoopAt100BpmPlaysInTimeAtTheSongsTempo(int bpm)
        {
            using var rig = new Rig();
            var (track, clip) = rig.AudioClip(rig.Wave(Clicks(100, 8, rig.Rate), rig.Rate), 100);
            rig.Core.BPM = bpm;
            Assert.That(clip.Length, Is.EqualTo(8 * rig.Core.TPB), "8 beats at 100 BPM");
            rig.Layout.LaunchClip(track, clip);
            var x = rig.Play(8 * 60.0 / bpm + 0.3);
            var starts = SoundStarts(x, rig.Rate);
            TestContext.Out.WriteLine($"[notice] {bpm} BPM: sound starts at " + string.Join(", ", starts.Select(t => t.ToString("0.0000"))));
            Assert.That(starts.Count, Is.GreaterThanOrEqualTo(8));
            for (int b = 1; b < 8; b++)
                Assert.That(starts[b] - starts[0], Is.EqualTo(b * 60.0 / bpm).Within(0.006), $"beat {b}");
        }

        [Test]
        public void StoppingFadesOutAndTheTrackFallsSilent()
        {
            using var rig = new Rig();
            rig.Core.BPM = 120;
            var tone = new float[rig.Rate * 4];
            for (int i = 0; i < tone.Length; i++) tone[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 220 * i / rig.Rate));
            var (track, clip) = rig.AudioClip(rig.Wave(tone, rig.Rate), 120, "Complex");
            rig.Layout.LaunchClip(track, clip, 127);
            var playing = rig.Play(1.0);
            Assert.That(playing.Skip(rig.Rate / 2).Max(v => Math.Abs(v)), Is.GreaterThan(0.1f), "it plays");
            rig.Session.Stop(track);
            var after = rig.Play(2.5);                               // the stop waits for the next bar (2 s at 120)
            Assert.That(after.Skip(after.Length - rig.Rate / 4).Max(v => Math.Abs(v)), Is.EqualTo(0f), "silent after the stop");
            double maxStep = 0;
            for (int i = 1; i < after.Length; i++) maxStep = Math.Max(maxStep, Math.Abs(after[i] - after[i - 1]));
            Assert.That(maxStep, Is.LessThan(0.1), "no click at the stop (a 220 Hz sine at 0.5 moves 0.016 a sample)");
        }

        [Test]
        public void ClipSettingsChangeHowItPlays()
        {
            using var rig = new Rig();
            var tone = new float[rig.Rate * 4];
            for (int i = 0; i < tone.Length; i++) tone[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 220 * i / rig.Rate));
            var (track, clip) = rig.AudioClip(rig.Wave(tone, rig.Rate), 120, "Repitch");
            var audio = rig.Layout.AudioOf(clip)!;
            audio.GainDb = -6;
            rig.Layout.SetAudio(clip, audio);
            rig.Layout.RebuildLaunchMap();
            Assert.That(clip.SessionAudio?.Params?.Gain, Is.EqualTo(0.501f).Within(0.001f));
            audio.Bpm = 60;                                            // twice as many beats: the clip is twice as long
            rig.Layout.SetAudio(clip, audio);
            Assert.That(clip.Length, Is.EqualTo(4 * rig.Core.TPB));
            rig.Layout.Undo();
            Assert.That(clip.Length, Is.EqualTo(8 * rig.Core.TPB), "undo puts the length back");
            Assert.That(rig.Layout.AudioOf(clip)!.Bpm, Is.EqualTo(120));
        }

        [Test]
        public void AClipPlayerHearsItsInputWhenArmedOrMonitoring()
        {
            using var rig = new Rig(hum: true);
            rig.Layout.Ensure();
            var player = (MachineCore)rig.Layout.NewAudioTrack()!;
            var hum = rig.Driver.MachineFor(rig.Hum!);
            rig.Core.SongCore.ConnectMachines(hum, player, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            int track = rig.Layout.FirstTrackOf(player);

            Assert.That(rig.Play(0.1).Skip(1000).Max(v => Math.Abs(v)), Is.EqualTo(0f), "auto, not armed: not heard");
            rig.Layout.SetArmed(track, true);
            Assert.That(rig.Play(0.1).Skip(1000).Max(v => Math.Abs(v)), Is.GreaterThan(0f), "auto, armed: heard");
            rig.Layout.SetArmed(track, false);
            Monitor(player, 2);                                                // off
            Assert.That(rig.Play(0.1).Skip(1000).Max(v => Math.Abs(v)), Is.EqualTo(0f), "off: not heard");
            Monitor(player, 1);                                                // in
            Assert.That(rig.Play(0.1).Skip(1000).Max(v => Math.Abs(v)), Is.GreaterThan(0f), "in: heard");
        }

        [TestCase(".bmx")]
        [TestCase(".bmxml")]
        public void ASongKeepsItsAudioClips(string extension)
        {
            using var rig = new Rig();
            var (_, clip) = rig.AudioClip(rig.Wave(Clicks(100, 4, rig.Rate), rig.Rate), 100, "Complex");
            var audio = rig.Layout.AudioOf(clip)!;
            audio.Transpose = 3;
            audio.Markers = new List<(double, double)> { (0, 0), (rig.Rate * 1.2, 2), (rig.Rate * 2.4, 4.5) };
            rig.Layout.SetAudio(clip, audio);

            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "audio-" + Guid.NewGuid().ToString("N") + extension);
            rig.Core.SaveSongCopy(path);
            rig.Core.Modified = false;
            rig.Core.OpenSongFile(path);
            rig.Driver.AssertNoErrorsReportedToUser();

            var machine = rig.Core.SongCore.MachinesList.Single(SessionLayout.IsClipPlayer);
            var loaded = machine.PatternsList.Single(p => p.Name == "clip");
            var again = rig.Layout.AudioOf(loaded)!;
            Assert.That(again.Warp, Is.EqualTo("Complex"));
            Assert.That(again.Transpose, Is.EqualTo(3));
            Assert.That(again.Markers.Count, Is.EqualTo(3));
            Assert.That(again.Markers[2].Beat, Is.EqualTo(4.5));
            Assert.That(loaded.Length, Is.EqualTo((int)Math.Ceiling(4.5 * rig.Core.TPB)), "the length the markers give");
            rig.Layout.RebuildLaunchMap();
            Assert.That(loaded.SessionAudio?.Params, Is.Not.Null, "it plays (its wave came back with the song)");
            Assert.That(loaded.SessionAudio!.Params!.Source.Length, Is.EqualTo(rig.Rate * 60 * 4 / 100));
        }

        [Test]
        public void DroppingAFileMakesAnAudioTrackAtItsTempo()
        {
            using var rig = new Rig();
            rig.Layout.Ensure();
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "loop-" + Guid.NewGuid().ToString("N") + ".wav");
            WriteWav(path, Clicks(125, 8, 44100), 44100);
            var clip = rig.Layout.ImportAudio(-1, 2, path, out var error);
            if (clip == null && error != null && (error.Contains("sndfile") || error.Contains("DLL") || error.Contains("dll")))
                Assert.Inconclusive("libsndfile isn't loadable here: " + error);
            Assert.That(clip, Is.Not.Null, error);
            Assert.That(SessionLayout.IsClipPlayer(clip!.Machine), Is.True, "a new audio track");
            var audio = rig.Layout.AudioOf(clip)!;
            Assert.That(audio.Bpm, Is.EqualTo(125).Within(0.01), "two bars at 125 BPM");
            Assert.That(clip.Length, Is.EqualTo(8 * rig.Core.TPB));
            int track = rig.Layout.FirstTrackOf(clip.Machine);
            Assert.That(rig.Layout.PatternAt(track, 2), Is.SameAs(clip), "in the slot it was dropped on");
            rig.Layout.Undo();
            Assert.That(rig.Layout.PatternAt(track, 2), Is.Null, "one undo step takes the clip away");
        }

        [Test]
        public void PlayingAudioClipsAllocatesNothing()
        {
            using var rig = new Rig();
            int wave = rig.Wave(Clicks(100, 8, rig.Rate), rig.Rate);
            var clips = new List<(SequenceCore, PatternCore)>();
            foreach (var warp in new[] { "Beats", "Complex", "Repitch", "Off" })
                clips.Add(rig.AudioClip(wave, 100, warp));
            foreach (var (track, clip) in clips)
                rig.Layout.LaunchClip(track, clip);
            rig.Core.BPM = 133;
            rig.AllocatedWhilePlaying(3);                                    // warm up: JIT, launches
            long bytes = 0;
            for (int stretch = 0; stretch < 10; stretch++)                   // steady state after one-time costs
                if ((bytes = rig.AllocatedWhilePlaying(1)) == 0) break;
            bytes = rig.AllocatedWhilePlaying(4);
            TestContext.Out.WriteLine($"[notice] four audio clips allocated {bytes} bytes in 4 s");
            Assert.That(bytes, Is.EqualTo(0), "the audio thread must not allocate for audio clips (GC pauses are dropouts)");
        }

        static void Monitor(IMachine player, int value) =>
            player.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters.Single(p => p.Name == "Monitor").SetValue(-1, value);

        // ------------------------------------------------------------------ recording

        /// <summary>An audio track with the Hum (a constant 1000 of 32768) coming in, armed.</summary>
        static (MachineCore Player, int Track) HumTrack(Rig rig)
        {
            rig.Layout.Ensure();
            var player = (MachineCore)rig.Layout.NewAudioTrack()!;
            var hum = rig.Driver.MachineFor(rig.Hum!);
            rig.Core.SongCore.ConnectMachines(hum, player, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            int track = rig.Layout.FirstTrackOf(player);
            rig.Layout.SetArmed(track, true);
            return (player, track);
        }

        const float HumLevel = 1000f / 32768;

        [Test]
        public void AnArmedAudioTrackRecordsItsInputIntoATakeThatPlaysFromItsNextPass()
        {
            using var rig = new Rig(hum: true);
            rig.Core.BPM = 120;
            var (_, track) = HumTrack(rig);
            rig.Layout.SetRecordBars(1);
            var take = rig.Layout.RecordTake(track, 0)!;
            Assert.That(take, Is.Not.Null);
            Assert.That(rig.Layout.IsAudioClip(take), Is.True);
            Assert.That(rig.Layout.IsRecordingClip(take), Is.True);
            rig.PlayPolling(2.6);                                   // the bar (2 s at 120), and a little
            Assert.That(rig.Layout.IsRecordingClip(take), Is.False, "the take is done");
            var source = rig.Layout.AudioSourceOf(take)!;
            Assert.That(source.Length, Is.EqualTo(2 * rig.Rate).Within(1), "one bar of audio");
            Assert.That(source.Left.Skip(1000).Take(source.Length - 2000).Min(), Is.EqualTo(HumLevel).Within(0.0005f), "what came in");
            Assert.That(rig.Layout.AudioOf(take)!.Bpm, Is.EqualTo(120));

            rig.Layout.SetArmed(track, false);                      // not heard now: only the clip plays
            var played = rig.Play(1.0);
            Assert.That(played.Skip(rig.Rate / 4).Take(rig.Rate / 2).Average(v => Math.Abs(v)), Is.EqualTo(HumLevel).Within(0.003f), "the take plays it");
        }

        [Test]
        public void ATakeRecordedUntilStoppedKeepsWholeBars()
        {
            using var rig = new Rig(hum: true);
            rig.Core.BPM = 120;
            var (_, track) = HumTrack(rig);
            rig.Layout.SetRecordBars(0);
            var take = rig.Layout.RecordTake(track, 1)!;
            rig.PlayPolling(2.7);                                   // into the second bar
            rig.Layout.StopRecording(rig.Layout.SequenceOf(track)!);
            Assert.That(take.Length, Is.EqualTo(2 * 4 * rig.Core.TPB), "two bars");
            Assert.That(rig.Layout.IsRecordingClip(take), Is.True, "records to the end of the bar");
            rig.PlayPolling(2.0);
            Assert.That(rig.Layout.IsRecordingClip(take), Is.False);
            Assert.That(rig.Layout.AudioSourceOf(take)!.Length, Is.EqualTo(4 * rig.Rate).Within(1));
        }

        [Test]
        public void AResamplingTrackRecordsTheMaster()
        {
            using var rig = new Rig(hum: true);
            rig.Core.BPM = 120;
            var hum = rig.Driver.MachineFor(rig.Hum!);
            var master = rig.Core.SongCore.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);
            rig.Core.SongCore.ConnectMachines(hum, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            rig.Layout.Ensure();
            var resampler = rig.Layout.NewResamplingTrack()!;
            Assert.That(SessionLayout.IsResampling(resampler), Is.True);
            int track = rig.Layout.FirstTrackOf(resampler);
            Assert.That(rig.Layout.IsArmed(track), Is.True, "made armed");
            rig.Layout.SetRecordBars(1);
            var take = rig.Layout.RecordTake(track, 0)!;
            rig.PlayPolling(2.6);
            var source = rig.Layout.AudioSourceOf(take)!;
            Assert.That(source.Length, Is.EqualTo(2 * rig.Rate).Within(1));
            float level = source.Left.Skip(1000).Take(source.Length - 2000).Average(v => Math.Abs(v));
            TestContext.Out.WriteLine($"[notice] resampled level {level} (the hum is {HumLevel} before the master)");
            Assert.That(level, Is.GreaterThan(HumLevel / 4), "the master's sound");
        }

        // ------------------------------------------------------------------ hardening (1.0, review)

        [Test]
        public void ALoopASampleLongerThanItsBarsIsItsBars()
        {
            using var rig = new Rig();
            rig.Layout.Ensure();
            // Two bars at 93 BPM are 227,612.9 samples; exporters write 227,613.
            var x = new float[227_613];
            int perBeat = (int)(44100 * 60 / 93.0);
            for (int b = 0; b < 8; b++)
                for (int i = 0; i < 800; i++)
                    x[b * perBeat + i] = (float)(0.8 * Math.Sin(2 * Math.PI * 1000 * i / 44100) * Math.Exp(-i / 220.0));
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "loop93-" + Guid.NewGuid().ToString("N") + ".wav");
            WriteWav(path, x, 44100);
            var clip = rig.Layout.ImportAudio(-1, 0, path, out var error);
            if (clip == null && error != null && (error.Contains("sndfile") || error.Contains("DLL") || error.Contains("dll")))
                Assert.Inconclusive("libsndfile isn't loadable here: " + error);
            Assert.That(clip, Is.Not.Null, error);
            Assert.That(rig.Layout.AudioOf(clip!)!.Bpm, Is.EqualTo(93).Within(0.001));
            Assert.That(clip!.Length, Is.EqualTo(8 * rig.Core.TPB), "8 beats, not a tick more");
            Assert.That(SessionLayout.ClipTicks(8.0000034, 4), Is.EqualTo(32));
            Assert.That(SessionLayout.ClipTicks(8.1, 4), Is.EqualTo(33), "a real part of a tick more is kept");
        }

        [Test]
        public void UndoAfterTheAudioTracksRenameChangesTheLengthBack()
        {
            using var rig = new Rig();
            var (_, clip) = rig.AudioClip(rig.Wave(Clicks(100, 8, rig.Rate), rig.Rate), 100);
            var audio = rig.Layout.AudioOf(clip)!;
            audio.Bpm = 50;
            rig.Layout.SetAudio(clip, audio);
            Assert.That(clip.Length, Is.EqualTo(4 * rig.Core.TPB));
            clip.Machine.Name = "Drums";                                // in the Machine View
            Assert.DoesNotThrow(() => rig.Layout.Undo());
            Assert.That(clip.Length, Is.EqualTo(8 * rig.Core.TPB));
            Assert.That(rig.Layout.AudioOf(clip)!.Bpm, Is.EqualTo(100));
            Assert.DoesNotThrow(() => rig.Layout.Redo());
            Assert.That(clip.Length, Is.EqualTo(4 * rig.Core.TPB));
        }

        [Test]
        public void AGainChangeKeepsALengthSetByHand()
        {
            using var rig = new Rig();
            var (_, clip) = rig.AudioClip(rig.Wave(Clicks(100, 8, rig.Rate), rig.Rate), 100);
            clip.Length = 4 * rig.Core.TPB;                             // the first half only
            var audio = rig.Layout.AudioOf(clip)!;
            audio.GainDb = -3;
            rig.Layout.SetAudio(clip, audio);
            Assert.That(clip.Length, Is.EqualTo(4 * rig.Core.TPB));
        }

        [Test]
        public void ATakeLaunchedLateKeepsItsPatternsTime()
        {
            using var rig = new Rig(hum: true);
            rig.Core.BPM = 120;
            var (_, track) = HumTrack(rig);
            rig.Layout.SetRecordBars(1);
            SessionCore.Timestamp = () => 1_000_000;                   // heard = rendered − latency, exactly
            rig.Core.Playing = true;
            int samplesPerTick = ReBuzzCore.masterInfo.SamplesPerTick;
            rig.Core.OutputLatencySamples = 3 * samplesPerTick;
            rig.PlayToClock(17);                                        // heard: tick 14, before the bar line at 16
            var take = (PatternCore)rig.Layout.RecordTake(track, 0)!;
            int k = rig.PlayUntilPlaying(take);
            TestContext.Out.WriteLine($"[notice] the take started at its tick {k}");
            Assert.That(k, Is.GreaterThan(0), "late: it starts into its pattern");
            rig.PlayPolling(2.6);
            var source = rig.Layout.AudioSourceOf(take)!;
            Assert.That(source.Length, Is.EqualTo(2 * rig.Rate).Within(1), "a bar");
            int silent = 0;
            while (silent < source.Length && source.Left[silent] == 0) silent++;
            double expected = k * rig.Rate * 60.0 / (120 * rig.Core.TPB);
            Assert.That(silent, Is.EqualTo(expected).Within(2), "silence for the ticks it missed, then what came in, in its place");
        }

        [Test]
        public void ANewTakeOnATrackFinishingOneKeepsTheFirst()
        {
            using var rig = new Rig(hum: true);
            rig.Core.BPM = 120;
            var (_, track) = HumTrack(rig);
            rig.Layout.SetRecordBars(0);
            var first = rig.Layout.RecordTake(track, 0)!;
            rig.PlayPolling(2.7);
            rig.Layout.StopRecording(rig.Layout.SequenceOf(track)!);  // finishing its second bar
            var second = rig.Layout.RecordTake(track, 1)!;
            Assert.That(second, Is.Not.Null);
            Assert.That(rig.Layout.IsRecordingClip(first), Is.False, "the first is done");
            var source = rig.Layout.AudioSourceOf(first)!;
            Assert.That(source.Length, Is.GreaterThan(rig.Rate * 2), "what it had");
            Assert.That(source.Left.Skip(rig.Rate).Take(rig.Rate / 2).Average(v => Math.Abs(v)), Is.EqualTo(HumLevel).Within(0.001f));
            Assert.That(rig.Session.TakeCaptures.Length, Is.EqualTo(1), "only the new take records");
        }

        [Test]
        public void AWaveEditedInPlacePlaysAsEdited()
        {
            using var rig = new Rig();
            var tone = Enumerable.Repeat(0.25f, rig.Rate * 2).ToArray();
            int wave = rig.Wave(tone, rig.Rate);
            var (_, clip) = rig.AudioClip(wave, 120, "Repitch");
            Assert.That(clip.SessionAudio!.Params!.Source.Left[1000], Is.EqualTo(0.25f));
            var layer = rig.Core.SongCore.Wavetable.Waves[wave].Layers[0];
            var half = Enumerable.Repeat(0.125f, tone.Length).ToArray();
            layer.SetDataAsFloat(half, 0, 1, 0, 0, half.Length);          // as Normalize or Fade would
            layer.InvalidateData();
            Assert.That(clip.SessionAudio!.Params!.Source.Left[1000], Is.EqualTo(0.125f), "read again");
        }

        /// <summary>A 16-bit mono WAV file.</summary>
        static void WriteWav(string path, float[] samples, int rate)
        {
            using var w = new BinaryWriter(File.Create(path));
            int bytes = samples.Length * 2;
            w.Write("RIFF"u8.ToArray());
            w.Write(36 + bytes);
            w.Write("WAVEfmt "u8.ToArray());
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(rate);
            w.Write(rate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write("data"u8.ToArray());
            w.Write(bytes);
            foreach (var s in samples) w.Write((short)Math.Clamp(Math.Round(s * 32767), -32768, 32767));
        }
    }
}
