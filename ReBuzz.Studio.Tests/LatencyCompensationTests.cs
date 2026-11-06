// Fork (docs/specs/mastering-tools.md §4.1): machine delay compensation in the engine, with real Neo machines, on
// Linux and macOS (ReBuzzTests has the Windows build's tests). Machine delay compensation is on by default; a path
// around a latent machine cancels against an inverted parallel path; a latency that changes while playing moves the
// other path's delay by a crossfade, applied on the audio thread before the block's work (no UI thread needed); a
// bypassed machine keeps its latency (its dry input delayed by it), and switching the bypass keeps the null.

using System;
using System.Linq;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Audio;
using ReBuzz.Core;

namespace ReBuzz.Studio.Tests
{
    public class LatencyCompensationTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static void Pump()
        {
            for (int i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
        }

        static void NewSong()
        {
            lock (ReBuzzCore.AudioLock) Core.Playing = false;
            Core.ExecuteCommand(BuzzCommand.Stop);
            Core.Modified = false;
            Core.ExecuteCommand(BuzzCommand.NewFile);
            Pump();
        }

        static IParameter Param(MachineCore m, string name) => m.ParameterGroupsList[1].ParametersList.First(p => p.Name == name);

        static MachineCore Add(string gear, string name)
        {
            var song = Core.SongCore;
            song.CreateMachine(gear, null, name, null, null, null, -1, 0.3f, 0.3f);
            return song.MachinesList.Last(x => x.Name == name);
        }

        static void Connect(MachineCore from, MachineCore to) =>
            Core.SongCore.ConnectMachines(from, to, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);

        /// <summary>A Neo Poly holding a sine (C4), its own level well under full scale.</summary>
        static MachineCore Sine(string name)
        {
            var poly = Add("Neo Poly", name);
            Param(poly, "O1 shape").SetValue(0, 0);
            return poly;
        }

        /// <summary>source → fx → Master and source → Neo Utility → Master: the graph whose two paths meet again.</summary>
        static (MachineCore Source, MachineCore Fx, MachineCore Parallel) Diamond(string gear, string tag)
        {
            var master = Core.SongCore.MachinesList[0];
            var source = Sine("Lat source " + tag);
            var fx = Add(gear, "Lat fx " + tag);
            var parallel = Add("Neo Utility", "Lat parallel " + tag);
            Connect(source, fx);
            Connect(fx, master);
            Connect(source, parallel);
            Connect(parallel, master);
            Pump();
            return (source, fx, parallel);
        }

        /// <summary>The engine's output, left channel, as the device's thread reads it (in 256-frame buffers).</summary>
        sealed class Engine : IDisposable
        {
            readonly WorkManager work = new WorkManager(Core, new WorkThreadEngine(1), 0, Core.EngineSettings);
            readonly float[] buffer = new float[512];

            public float[] Read(double seconds)
            {
                int frames = (int)Math.Round(seconds * ReBuzzCore.masterInfo.SamplesPerSec / 256) * 256;
                var y = new float[frames];
                for (int f = 0; f < frames; f += 256)
                {
                    work.MainAudioFillBuffer(buffer, 0, 512);
                    for (int i = 0; i < 256; i++) y[f + i] = buffer[2 * i];
                }
                return y;
            }

            public void Dispose() => work.Stop();
        }

        static double Rms(float[] x, int from = 0)
        {
            double s = 0;
            for (int i = from; i < x.Length; i++) s += (double)x[i] * x[i];
            return Math.Sqrt(s / Math.Max(1, x.Length - from));
        }

        static double Db(double ratio) => 20 * Math.Log10(Math.Max(ratio, 1e-30));

        static double LargestStep(float[] x, int from = 1)
        {
            double most = 0;
            for (int i = Math.Max(1, from); i < x.Length; i++) most = Math.Max(most, Math.Abs(x[i] - x[i - 1]));
            return most;
        }

        static int DelayOf(MachineCore from, MachineCore to) =>
            Core.CompensationDelays().Single(d => d.Connection.Source == from && d.Connection.Destination == to).Delay;

        [AvaloniaTest]
        public void CompensationIsOnByDefaultAndAParallelPathCancels()
        {
            using var restore = Studio.AfterTest(() =>
            {
                Core.EngineSettings.Set("MachineDelayCompensation", true);
                NewSong();
            });
            Assert.That(Core.EngineSettings.MachineDelayCompensation, Is.True, "machine delay compensation is on by default");
            NewSong();
            var (source, mb, parallel) = Diamond("Neo Multiband", "mb");
            for (int b = 1; b <= 4; b++) Param(mb, $"B{b} ratio").SetValue(0, 200);       // 1:1: the input delayed, bit for bit
            Pump();
            int latency = mb.Latency;
            Assert.That(latency, Is.EqualTo(4607), "linear phase");
            Assert.That(DelayOf(parallel, Core.SongCore.MachinesList[0]), Is.EqualTo(latency), "the parallel path waits for the multiband");
            source.SendMIDINote(0, 60, 100);
            using var engine = new Engine();

            // The multiband's path alone (the parallel one at −∞), then both with the parallel one inverted.
            Param(parallel, "Gain").SetValue(0, 0);
            engine.Read(0.5);
            double alone = Rms(engine.Read(0.5));
            Param(parallel, "Gain").SetValue(0, Param(parallel, "Gain").DefValue);
            Param(parallel, "Invert L").SetValue(0, 1);
            Param(parallel, "Invert R").SetValue(0, 1);
            engine.Read(0.3);
            var both = engine.Read(0.5);
            double residual = Rms(both);
            TestContext.Out.WriteLine($"[notice] Delay compensation (default on): Neo Multiband's {latency} samples against an inverted parallel path: residual {Db(residual / alone):F1} dB (alone {Db(alone):F1} dBFS)");
            Assert.That(alone, Is.GreaterThan(1e-3), "the sine plays");
            Assert.That(residual, Is.EqualTo(0), "the two paths cancel exactly");

            // Off: the paths comb.
            Core.EngineSettings.Set("MachineDelayCompensation", false);
            Pump();
            Assert.That(DelayOf(parallel, Core.SongCore.MachinesList[0]), Is.EqualTo(0));
            engine.Read(0.3);
            double combed = Rms(engine.Read(0.5));
            TestContext.Out.WriteLine($"[notice] Delay compensation off: the same paths {Db(combed / alone):F1} dB against the multiband alone");
            Assert.That(Db(combed / alone), Is.GreaterThan(-20), "without compensation the inverted path doesn't cancel");
        }

        [AvaloniaTest]
        public void ALatencyChangeMovesTheOtherPathWithoutAGapOrAClick()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var master = Core.SongCore.MachinesList[0];
            var (source, limiter, parallel) = Diamond("Neo Limiter", "lim");
            Param(limiter, "Ceiling").SetValue(0, 120);           // 0 dBTP: the sine stays under it, so the limiter only delays
            Pump();
            int rate = ReBuzzCore.masterInfo.SamplesPerSec;
            int Latency(double ms) => (int)Math.Round(ms * 0.001 * (double)(float)rate) + 11;
            Assert.That(DelayOf(parallel, master), Is.EqualTo(Latency(2)));
            source.SendMIDINote(0, 60, 100);
            using var engine = new Engine();

            // The parallel path alone (the limiter's connection at 0: its latency still counts; muted, it wouldn't be worked
            // where ProcessMutedMachines is off, and its lookahead wouldn't change), its delay changing under a sine.
            var limiterOut = limiter.Outputs.Single();
            limiterOut.Amp = 0;
            engine.Read(0.5);
            var steady = engine.Read(0.3);
            double ownStep = LargestStep(steady);
            Param(limiter, "Lookahead").SetValue(0, 450);         // 5 ms
            // No Pump: the new latency reaches the compensation on the audio thread, at the limiter's tick.
            var moving = engine.Read(0.5);
            Assert.That(DelayOf(parallel, master), Is.EqualTo(Latency(5)), "the other path's delay followed, without the UI thread");
            double step = LargestStep(moving);
            int quietest = Enumerable.Range(0, moving.Length / 64).Min(w => (int)(Rms(moving[(w * 64)..(w * 64 + 64)]) * 1e6));
            TestContext.Out.WriteLine($"[notice] A latency change ({Latency(2)} → {Latency(5)} samples) while a sine plays: largest step {step / ownStep:F3}× the sine's own; quietest 64 samples {quietest / 1e6:F5} RMS (steady {Rms(steady):F5})");
            Assert.That(step, Is.LessThan(1.2 * ownStep), "no click");
            // The crossfade between the old and the new tap briefly sums two copies of the sine a few samples apart, which
            // comb-filter each other: a dip, not a gap (0.79 of the level measured here, 0.51 on Windows). A gap is silence.
            Assert.That(quietest / 1e6, Is.GreaterThan(0.25 * Rms(steady)), "no gap");

            // Both paths, the other inverted: the null comes back once the change is through.
            limiterOut.Amp = 0x4000;
            Param(parallel, "Invert L").SetValue(0, 1);
            Param(parallel, "Invert R").SetValue(0, 1);
            engine.Read(0.3);
            double alone = Rms(steady);
            double residual = Rms(engine.Read(0.3));
            TestContext.Out.WriteLine($"[notice] After the change, against the inverted parallel path: {Db(residual / alone):F1} dB");
            Assert.That(Db(residual / alone), Is.LessThan(-100));
        }

        [AvaloniaTest]
        public void ABypassedLatentMachineKeepsItsLatencyAndTheNull()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var master = Core.SongCore.MachinesList[0];
            var (source, mb, parallel) = Diamond("Neo Multiband", "byp");
            for (int b = 1; b <= 4; b++) Param(mb, $"B{b} ratio").SetValue(0, 200);
            Param(parallel, "Invert L").SetValue(0, 1);
            Param(parallel, "Invert R").SetValue(0, 1);
            Pump();
            source.SendMIDINote(0, 60, 100);
            using var engine = new Engine();
            engine.Read(0.5);
            Assert.That(Rms(engine.Read(0.3)), Is.EqualTo(0), "cancelling before the bypass");

            // Bypassed: the dry input delayed by the latency, so the null holds through the switch and after.
            mb.IsBypassed = true;
            Pump();
            Assert.That(DelayOf(parallel, master), Is.EqualTo(4607), "the bypass keeps the latency");
            var bypassed = engine.Read(0.5);
            Assert.That(Rms(bypassed), Is.EqualTo(0), "the bypassed path still lines up");
            mb.IsBypassed = false;
            Pump();
            Assert.That(Rms(engine.Read(0.5)), Is.EqualTo(0), "and back");

            // Compressing hard, the multiband's own path alone: bypass and back crossfade (no click).
            Param(parallel, "Gain").SetValue(0, 0);
            for (int b = 1; b <= 4; b++)
            {
                Param(mb, $"B{b} ratio").SetValue(0, 1000);
                Param(mb, $"B{b} threshold").SetValue(0, 100);
            }
            engine.Read(0.8);
            double ownStep = LargestStep(engine.Read(0.3));
            mb.IsBypassed = true;
            var on = engine.Read(0.3);
            mb.IsBypassed = false;
            var off = engine.Read(0.3);
            double dry = Rms(on, on.Length / 2), wet = Rms(off, off.Length / 2);
            double step = Math.Max(LargestStep(on), LargestStep(off));
            // The louder of the two steady signals sets the scale: the dry sine (bypassed) against the compressed one.
            double own = Math.Max(ownStep, LargestStep(on, on.Length / 2));
            TestContext.Out.WriteLine($"[notice] Bypassing Neo Multiband (compressing {Db(wet / dry):F1} dB) and back while playing: largest step {step / own:F3}× the louder steady signal's own");
            Assert.That(Db(wet / dry), Is.LessThan(-3), "it was compressing");
            Assert.That(step, Is.LessThan(1.5 * own), "no click either way");
        }

        [AvaloniaTest]
        public void AnInsertedLatentMachineIsCompensatedWithoutAnyoneAsking()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var master = Core.SongCore.MachinesList[0];
            var source = Sine("Ins source");
            var parallel = Add("Neo Utility", "Ins parallel");
            Connect(source, master);
            Connect(source, parallel);
            Connect(parallel, master);
            Pump();
            // Neo Drive (62 samples at every quality) inserted into source → Master: the insert action changes the graph
            // without asking for compensation; the engine follows the graph by itself.
            var link = source.Outputs.First(c => c.Destination == master);
            int before = Core.SongCore.MachinesList.Count;
            Core.SongCore.InsertMachine(link, "Neo Drive", null, 0.5f, 0.5f);
            Pump();
            var drive = Core.SongCore.MachinesList.Skip(before).Single();
            Assert.That(drive.Latency, Is.EqualTo(62));
            Assert.That(DelayOf(parallel, master), Is.EqualTo(62));
        }
    }
}
