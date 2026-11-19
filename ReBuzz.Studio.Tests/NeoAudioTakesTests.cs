// Fork (docs/specs/neo-audio.md "Takes and comping"): audio takes on the real engine. A fake input device (the engine's input ring, fed in
// step with the rendering) stands in for the drivers: a Session take of bars at a quantized launch, Arrangement recording of several tracks at
// once, a loop recording of three passes (three takes of exact length, the newest playing), comping ranges of takes (the expected samples with
// the crossfades), undo and redo, save and reopen, deleting the unused takes, flattening, and takes a crash left behind.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Headless.NUnit;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Core.Clips;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using static ReBuzz.Studio.Tests.TrackInputEngineTests;

namespace ReBuzz.Studio.Tests
{
    public class NeoAudioTakesTests
    {
        const int Rate = 44100;
        const int Bar = 16;
        /// <summary>At 120 bpm, 4 ticks a beat: a tick is 5512.5 frames, a bar of 16 ticks is two seconds.</summary>
        const int BarFrames = 88200;

        /// <summary>What the input hears at device frame f, channel c: noise-like but exact, and different in every frame.</summary>
        static float V(long f, int c = 0)
        {
            ulong x = (ulong)(f * 4 + c) + 0x9E3779B97F4A7C15UL;       // splitmix64: no period to confuse an alignment
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            x ^= x >> 31;
            return (float)((x >> 40) & 0xFFFF) / 65536f - 0.5f;
        }

        static float Signal(long f, int c) => V(f, c);

        /// <summary>Frames of the device and the rendering together, in blocks the input ring holds.</summary>
        static void Play(FakeInput device, SessionRig rig, int frames)
        {
            for (int done = 0; done < frames; done += 2048)
                device.Run(rig, Math.Min(2048, frames - done), Signal);
        }

        static float[] Take(SessionRig rig, IPattern pattern, bool right = false)
        {
            var source = rig.Layout.AudioSourceOf(pattern)!;
            return (right ? source.Right : source.Left).ToArray();
        }

        static ReBuzzCore Core => Studio.Host.Core;

        sealed class Setup2 : IDisposable
        {
            readonly IDisposable inner;
            readonly string was;
            public readonly string Folder;
            public Setup2(out SessionRig rig)
            {
                was = TakeFile.Directory;
                Folder = Path.Combine(Path.GetTempPath(), "neobuzz-takes-studio-" + Guid.NewGuid().ToString("N"));
                TakeFile.Directory = Folder;
                AudioCapture.SpillAheadSeconds = 120;               // the test renders far faster than real time: the writer can't keep up with that
                inner = TrackInputEngineTests.Setup(out rig);
            }

            public void Dispose()
            {
                inner.Dispose();
                AudioCapture.SpillAheadSeconds = 3;
                TakeFile.Directory = was;
                try { Directory.Delete(Folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>A two-input device and an audio track listening to In 1/2, a loop of <paramref name="bars"/> bars at 120 bpm.</summary>
        static (FakeInput Device, MachineCore Track) Rig(SessionRig rig, int bars = 1, string name = "Mic")
        {
            var core = rig.Core;
            core.OutputLatencySamples = 0;                  // no round trip to drop: the first input frame is the first take frame
            core.BPM = 120;
            core.SongCore.SongEnd = 3 * bars * Bar;
            core.SongCore.LoopStart = 0;
            core.SongCore.LoopEnd = bars * Bar;
            core.SongCore.PlayPosition = 0;
            var device = new FakeInput(core, 2);
            var track = NewTrack(rig, name, AudioFrom.Stereo(0));
            return (device, track);
        }

        static ISequence MainLane(SessionRig rig, IMachine machine) => rig.Core.Song.Sequences.First(s => s.Machine == machine && rig.Core.Arrangement.RoleOf(s) == LaneRole.Main);

        static List<ISequence> TakeLanes(SessionRig rig, IMachine machine) =>
            rig.Core.Song.Sequences.Where(s => s.Machine == machine && rig.Core.Arrangement.RoleOf(s) == LaneRole.Take).ToList();

        /// <summary>
        /// The device frame the recording's first frame is: the transport starts at the next tick, so the take begins some frames after Record was
        /// pressed. Found in the audio itself (a run of frames that matches the device's signal there).
        /// </summary>
        static long BeganAt(float[] first, int channel = 0)
        {
            for (int k = 0; k < 8000; k++)
            {
                bool all = true;
                for (int i = 0; i < 12 && all; i++) all = first[i] == V(k + i, channel);
                if (all) return k;
            }
            Assert.Fail("the take's first frames aren't the device's signal within the first tick");
            return -1;
        }

        /// <summary>Records <paramref name="passes"/> passes of the loop with the fake device and stops; returns the device frame the recording began at.</summary>
        static long RecordLoop(SessionRig rig, FakeInput device, MachineCore track, int passes, int bars = 1)
        {
            var rec = rig.Core.Arrangement.Takes;
            rec.SetArmed(track, true);
            Assert.That(rec.Start(true), Is.Null);
            Assert.That(rec.IsRecording && rec.IsLoopRecording, Is.True);
            // The loop wraps once after each pass: stop right after the last wrap (a few frames into the next pass, less than a tick: not a take).
            var entry = rig.Core.SessionCore.ArrangementAudio.Single();
            for (int guard = 0; entry.MarkCount < passes && guard < 100000; guard++) Play(device, rig, 256);
            Assert.That(entry.MarkCount, Is.EqualTo(passes));
            Assert.That(rec.Stop(), Is.Null);
            Assert.That(rec.Status, Is.Null.Or.Empty);
            return BeganAt(Take(rig, TakeLanes(rig, track)[0].Events.Single().Value.Pattern));
        }

        // ------------------------------------------------------------------ Session

        [AvaloniaTest]
        public void ASessionTakeOfABarAtAQuantizedLaunchIsSampleAccurateAfterTheRoundTrip()
        {
            using var setup = new Setup2(out var rig);
            var core = rig.Core;
            const int outLatency = 300, inLatency = 100, round = outLatency + inLatency, spacing = 4410;
            core.OutputLatencySamples = outLatency;
            var device = new FakeInput(core, 2, inLatency);
            var a = NewTrack(rig, "Mic", AudioFrom.Mono(0));
            core.BPM = 120;
            rig.Layout.SetRecordBars(1);
            rig.Layout.Document.CountInBars = 0;
            rig.Layout.SetMachineArmed(a, true);
            var take = rig.Layout.RecordTake(rig.Layout.FirstTrackOf(a), 0)!;
            core.Playing = true;
            // What is played at render frame t comes back on In 1 at t + round trip: a click every spacing frames.
            float Click(long f, int c) => c == 0 && f >= round && (f - round) % spacing == 0 ? 0.9f : 0f;
            long start = 0;
            var cap = ((PatternCore)take).SessionAudio.Capture;
            var started = typeof(AudioCapture).GetField("started", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            while (!(bool)started.GetValue(cap)! && start < 20000) { device.Run(rig, 1, Click); start++; }
            for (int i = 0; i < 40 && (i < 4 || rig.Layout.IsRecordingClip(take)); i++) { device.Run(rig, 4410, Click); rig.Layout.PollTakes(); }
            Assert.That(rig.Layout.IsRecordingClip(take), Is.False);

            var left = Take(rig, take);
            Assert.That(left.Length, Is.EqualTo(BarFrames), "a bar of audio exactly");
            var clicks = Enumerable.Range(0, left.Length).Where(i => Math.Abs(left[i]) > 0.4f).ToList();
            Assert.That(clicks.Count, Is.GreaterThanOrEqualTo(15));
            foreach (int at in clicks)
                Assert.That(Math.Abs((at + start + spacing / 2) % spacing - spacing / 2), Is.LessThanOrEqualTo(1), "click at take frame " + at + ": on the grid to the sample (a tick is 5512.5 frames: one frame of rounding at most; the take began at render frame " + start + ")");
            Assert.That(rig.Layout.AudioOf(take)!.Warp, Is.EqualTo("Off"), "a recording is unwarped");
            Assert.That(rig.Layout.AudioOf(take)!.Bpm, Is.EqualTo(120), "with the tempo it was recorded at, so it can be warped later");
            Assert.That(Directory.Exists(setup.Folder) ? Directory.GetFiles(setup.Folder) : Array.Empty<string>(), Is.Empty, "the temp file went with the commit");
        }

        // ------------------------------------------------------------------ Arrangement

        [AvaloniaTest]
        public void ALoopRecordingOfThreePassesMakesThreeTakesOfExactLengthAndTheNewestPlays()
        {
            using var setup = new Setup2(out var rig);
            var (device, track) = Rig(rig);
            long begin = RecordLoop(rig, device, track, 3);

            var lanes = TakeLanes(rig, track);
            Assert.That(lanes.Count, Is.EqualTo(3), "each pass is a take on a lane of its own");
            for (int pass = 0; pass < 3; pass++)
            {
                var clip = lanes[pass].Events.Single();
                Assert.That(clip.Key, Is.EqualTo(0), "a take is placed where the loop starts");
                var left = Take(rig, clip.Value.Pattern);
                var right = Take(rig, clip.Value.Pattern, right: true);
                Assert.That(left.Length, Is.EqualTo(BarFrames), "take " + (pass + 1) + " is the exact length of the loop");
                for (int j = 0; j < left.Length; j += 7)
                {
                    long f = begin + (long)pass * BarFrames + j;
                    Assert.That(left[j], Is.EqualTo(V(f, 0)), $"take {pass + 1} left {j}");
                    Assert.That(right[j], Is.EqualTo(V(f, 1)), $"take {pass + 1} right {j}");
                }
            }
            var main = MainLane(rig, track).Events.Single();
            Assert.That(rig.Layout.AudioOf(main.Value.Pattern)!.Asset, Is.EqualTo(rig.Layout.AudioOf(lanes[2].Events.Single().Value.Pattern)!.Asset), "the newest take plays on the main lane");
            Assert.That(Directory.Exists(setup.Folder) ? Directory.GetFiles(setup.Folder) : Array.Empty<string>(), Is.Empty, "committed takes leave no temp file");

            // One undo step brought them in.
            core(rig).SongCore.ActionStack.Undo();
            Assert.That(TakeLanes(rig, track), Is.Empty);
            Assert.That(MainLane(rig, track).Events, Is.Empty);
            core(rig).SongCore.ActionStack.Redo();
            Assert.That(TakeLanes(rig, track).Count, Is.EqualTo(3));
        }

        static ReBuzzCore core(SessionRig rig) => rig.Core;

        [AvaloniaTest]
        public void SeveralArmedTracksRecordTogetherFromThePlayPosition()
        {
            using var setup = new Setup2(out var rig);
            var (device, a) = Rig(rig, 2, "A");
            var b = NewTrack(rig, "B", AudioFrom.Mono(1));
            var rec = rig.Core.Arrangement.Takes;
            rig.Core.SongCore.PlayPosition = 0;
            rec.SetArmed(a, true);
            rec.SetArmed(b, true);
            Assert.That(rec.Start(false), Is.Null);
            for (int i = 0; i < 3; i++) Play(device, rig, BarFrames);
            Assert.That(rec.Stop(), Is.Null);
            long begin = BeganAt(Take(rig, MainLane(rig, a).Events.Single().Value.Pattern));

            foreach (var (machine, ch, stereo) in new[] { (a, 0, true), (b, 1, false) })
            {
                var clip = MainLane(rig, machine).Events.Single();
                Assert.That(clip.Key, Is.EqualTo(0));
                var left = Take(rig, clip.Value.Pattern);
                Assert.That(left.Length, Is.InRange(2 * BarFrames, 4 * BarFrames), "about what was played");
                Assert.That(BeganAt(left, ch), Is.EqualTo(begin), machine.Name + " began with the other track");
                for (int j = 0; j < 2 * BarFrames; j += 11)
                {
                    Assert.That(left[j], Is.EqualTo(V(begin + j, ch)), machine.Name + " left " + j);
                    Assert.That(Take(rig, clip.Value.Pattern, true)[j], Is.EqualTo(V(begin + j, stereo ? 1 : ch)), machine.Name + " right " + j);
                }
            }
        }

        // ------------------------------------------------------------------ comping

        /// <summary>Three passes of a two-bar loop; the comp takes bar 1 of take 1 and bar 2 of take 3.</summary>
        static (IMachine Track, long Begin) RecordAndComp(SessionRig rig, FakeInput device, MachineCore track)
        {
            long begin = RecordLoop(rig, device, track, 3, 2);
            var editor = new ClipEditor(rig.Core);
            var main = MainLane(rig, track);
            // The newest take plays; comp bar 1 from take 1 and bar 2 from take 3 as one step.
            Assert.That(editor.CompRanges(track, new[] { (1, 0, Bar), (3, Bar, 2 * Bar) }), Is.Null);
            return (track, begin);
        }

        [AvaloniaTest]
        public void ACompOfRangesFromTakesOneAndThreeRendersTheExpectedSamplesWithTheCrossfade()
        {
            using var setup = new Setup2(out var rig);
            var (device, track) = Rig(rig, 2);
            var (_, begin) = RecordAndComp(rig, device, track);
            var editor = new ClipEditor(rig.Core);
            var main = MainLane(rig, track);
            var items = editor.Read(main);
            Assert.That(items.Select(i => (i.Time, i.End)), Is.EqualTo(new[] { (0, Bar), (Bar, 2 * Bar) }), "two pieces on the main lane");

            // Flatten renders the comp (what the engine's mixer plays) into one asset: compare it with the expected samples.
            Assert.That(editor.FlattenComp(track, null, null, out var flat), Is.Null);
            var got = Take(rig, flat);
            Assert.That(got.Length, Is.InRange(2 * BarFrames - 2, 2 * BarFrames + 2));
            int fade = (int)Math.Round(ClipEditor.CompCrossfadeMs / 1000 * Rate);
            float T1(int j) => V(begin + j);
            float T3(int j) => V(begin + 2L * 2 * BarFrames + j);
            double worst = 0;
            for (int j = 512; j < BarFrames - 4; j++) worst = Math.Max(worst, Math.Abs(got[j] - T1(j)));
            Assert.That(worst, Is.LessThan(1e-4), "bar 1 is take 1, sample for sample (after the voice's own few-millisecond declick at a clip start)");
            worst = 0;
            for (int j = BarFrames + fade + 8; j < 2 * BarFrames - 4; j++) worst = Math.Max(worst, Math.Abs(got[j] - T3(j)));
            Assert.That(worst, Is.LessThan(1e-4), "bar 2 is take 3, sample for sample");
            // Across the cut take 1's tail goes out under take 3's start coming in, equal power, a few milliseconds long: in each window of 40 frames the
            // output is a * take 1 + b * take 3 (least squares), a falls from 1 to 0, b rises from 0 to 1 and a^2 + b^2 stays 1.
            double lastA = 2, lastB = -1;
            for (int b0 = 0; b0 < fade + 60; b0 += 40)
            {
                double s11 = 0, s33 = 0, s13 = 0, sg1 = 0, sg3 = 0;
                for (int k = b0; k < b0 + 40; k++)
                {
                    int j = BarFrames - 10 + k;
                    double x = T1(j), y = T3(j), g = got[j];
                    s11 += x * x; s33 += y * y; s13 += x * y; sg1 += g * x; sg3 += g * y;
                }
                double det = s11 * s33 - s13 * s13, ga = (sg1 * s33 - sg3 * s13) / det, gb = (sg3 * s11 - sg1 * s13) / det;
                Assert.That(ga, Is.LessThanOrEqualTo(lastA + 0.15), "take 1 fades out, window " + b0);
                Assert.That(gb, Is.GreaterThanOrEqualTo(lastB - 0.15), "take 3 fades in, window " + b0);
                if (b0 >= 40 && b0 + 40 <= fade) Assert.That(ga * ga + gb * gb, Is.EqualTo(1).Within(0.3), "equal power, window " + b0);
                (lastA, lastB) = (ga, gb);
            }
            Assert.That(lastA, Is.EqualTo(0).Within(0.1), "the tail is over after the few milliseconds");
            Assert.That(lastB, Is.EqualTo(1).Within(0.1));
            Assert.That(Math.Abs(got[BarFrames - 1] - T1(BarFrames - 1)), Is.LessThan(1e-3), "no click before the cut");
        }

        [AvaloniaTest]
        public void TheEngineRendersTheCompTheSameAsTheFlattenedOne()
        {
            using var setup = new Setup2(out var rig);
            var (device, track) = Rig(rig, 2);
            var (_, begin) = RecordAndComp(rig, device, track);
            var editor = new ClipEditor(rig.Core);
            rig.Core.Arrangement.Takes.SetArmed(track, false);
            // The engine plays the comp (the take lanes are never played).
            rig.Core.Looping = false;
            rig.Core.SongCore.PlayPosition = 0;
            rig.Core.Playing = true;
            var heard = new List<float>();
            for (int i = 0; i < 100 && heard.Count < 2 * 2 * BarFrames + 20000; i++) heard.AddRange(rig.RunFrames(4096).Where((_, k) => k % 2 == 0));
            rig.Core.Playing = false;
            var want = new float[BarFrames * 2];
            for (int j = 0; j < want.Length; j++) want[j] = j < BarFrames ? V(begin + j) : V(begin + 2L * 2 * BarFrames + j);
            // The transport starts at the next tick: find where the music begins in what was heard, then compare sample for sample.
            int lag = Enumerable.Range(0, 12000).FirstOrDefault(k => Enumerable.Range(2000, 16).All(i => Math.Abs(heard[k + i] - want[i]) < 2e-3), -1);
            Assert.That(lag, Is.GreaterThanOrEqualTo(0), "the heard comp lines up with the expected samples");
            double worstOne = Enumerable.Range(2000, BarFrames - 3000).Max(i => Math.Abs(heard[lag + i] - want[i]));
            double worstTwo = Enumerable.Range(BarFrames + 600, BarFrames - 2000).Max(i => Math.Abs(heard[lag + i] - want[i]));
            Assert.That(worstOne, Is.LessThan(2e-3), "bar 1 as played: take 1 (lag " + lag + ")");
            Assert.That(worstTwo, Is.LessThan(2e-3), "bar 2 as played: take 3");
        }

        [AvaloniaTest]
        public void ACompIsOneUndoStepAndComesBackWithRedoAndWithTheSavedSong()
        {
            using var setup = new Setup2(out var rig);
            var (device, track) = Rig(rig, 2);
            RecordLoop(rig, device, track, 3, 2);
            var editor = new ClipEditor(rig.Core);
            string Snap() => string.Join(";", editor.Read(MainLane(rig, track)).Select(i => $"{i.Time}-{i.End}:{rig.Layout.AudioOf(i.Pattern)?.Asset}")) + "|" +
                             string.Join(";", TakeLanes(rig, track).Select(l => string.Join(",", editor.Read(l).Select(i => $"{i.Time}-{i.End}"))));
            string before = Snap();
            Assert.That(editor.CompRanges(track, new[] { (1, 0, Bar), (3, Bar, 2 * Bar) }), Is.Null);
            string comped = Snap();
            Assert.That(comped, Is.Not.EqualTo(before));
            rig.Core.SongCore.ActionStack.Undo();
            Assert.That(Snap(), Is.EqualTo(before), "one undo takes the whole comp away");
            rig.Core.SongCore.ActionStack.Redo();
            Assert.That(Snap(), Is.EqualTo(comped), "and redo brings it back");

        }

        [AvaloniaTest]
        public void SavingAndOpeningAgainKeepsTheTakesTheCompAndTheLaneState()
        {
            using var setup = new Setup2(out var rig);
            var (device, track) = Rig(rig, 2);
            RecordLoop(rig, device, track, 3, 2);
            var editor = new ClipEditor(rig.Core);
            Assert.That(editor.CompRanges(track, new[] { (1, 0, Bar), (3, Bar, 2 * Bar) }), Is.Null);
            rig.Core.Arrangement.SetTakesHidden(track.Name, true);
            // Save and open again: the takes and the comp are as they were.
            string file = Path.Combine(setup.Folder, "takes.nbl");
            Directory.CreateDirectory(setup.Folder);
            rig.Core.SaveSongCopy(file);
            rig.Core.Modified = false;
            Assert.That(rig.Core.TryOpenSongFile(file), Is.True);
            var machine = rig.Core.SongCore.MachinesList.First(m => m.Name == "Mic");
            Assert.That(rig.Core.Arrangement.AreTakesHidden(machine.Name), Is.True, "the collapsed take lanes stay collapsed");
            var reopened = new ClipEditor(rig.Core);
            Assert.That(TakeLanes(rig, machine).Count, Is.EqualTo(3), "the take lanes come back as take lanes");
            Assert.That(reopened.Read(MainLane(rig, machine)).Select(i => (i.Time, i.End)), Is.EqualTo(new[] { (0, Bar), (Bar, 2 * Bar) }));
            var takes = reopened.TakesOf(machine);
            Assert.That(takes.Count, Is.EqualTo(3));
            Assert.That(takes.Select(t => t.Used).ToArray(), Is.EqualTo(new[] { true, false, true }), "take 2 is the one the comp doesn't use");
            var tail = reopened.Read(MainLane(rig, machine)).Select(i => rig.Layout.AudioOf(i.Pattern)!).ToList();
            Assert.That(tail[0].Tail, Is.GreaterThan(0), "the crossfade is saved");
            Assert.That(tail[1].CrossIn, Is.True);
        }

        [AvaloniaTest]
        public void DeleteUnusedTakesKeepsWhatTheCompPlaysAndFlattenCommitsItToOneClip()
        {
            using var setup = new Setup2(out var rig);
            var (device, track) = Rig(rig, 2);
            var (_, begin) = RecordAndComp(rig, device, track);
            var editor = new ClipEditor(rig.Core);
            Assert.That(editor.TakesOf(track).Select(t => t.Used).ToArray(), Is.EqualTo(new[] { true, false, true }));
            Assert.That(editor.DeleteUnusedTakes(track), Is.EqualTo(1));
            Assert.That(editor.TakesOf(track).Select(t => t.Number).ToArray(), Is.EqualTo(new[] { 1, 2 }), "take 2 is gone");
            Assert.That(TakeLanes(rig, track).Count, Is.EqualTo(2), "its empty lane with it");
            rig.Core.SongCore.ActionStack.Undo();
            Assert.That(TakeLanes(rig, track).Count, Is.EqualTo(3), "undo puts take 2 back, on a take lane");
            rig.Core.SongCore.ActionStack.Redo();

            Assert.That(editor.FlattenComp(track, null, null, out var flat), Is.Null);
            Assert.That(editor.Read(MainLane(rig, track)).Count, Is.EqualTo(1), "one clip of the rendered comp");
            Assert.That(Take(rig, flat)[100], Is.EqualTo(V(begin + 100)).Within(1e-4));
        }

        // ------------------------------------------------------------------ crash

        [AvaloniaTest]
        public void ATakeACrashLeftBehindRecoversAsClipsOnARecoveryTrack()
        {
            using var setup = new Setup2(out var rig);
            var (device, track) = Rig(rig, 1);
            var rec = rig.Core.Arrangement.Takes;
            rec.SetArmed(track, true);
            Assert.That(rec.Start(false), Is.Null);
            for (int i = 0; i < 4; i++) Play(device, rig, BarFrames / 2);
            // The writer thread has put what it has in the file. The process dies here: nothing is committed.
            var files = Enumerable.Range(0, 100).Select(_ => { System.Threading.Thread.Sleep(20); return Directory.GetFiles(setup.Folder, "take-*.nbtake.wav"); }).First(f => f.Length > 0 && new FileInfo(f[0]).Length > 44 + 8 * AudioCapture.ChunkFrames);
            string crashed = Path.Combine(setup.Folder, "take-crashed.nbtake.wav");
            File.Copy(files[0], crashed);
            File.Copy(files[0] + ".json", crashed + ".json");
            rec.Stop();                                           // (the dead run's recording ends)
            long begin = BeganAt(Take(rig, MainLane(rig, track).Events.Single().Value.Pattern));
            // The next launch finds it, and a take of the run that is still alive isn't offered.
            foreach (var f in Directory.GetFiles(setup.Folder, "take-*.nbtake.wav").Where(f => f != crashed)) { }
            var found = rec.LeftoverTakes().Where(t => t.Path == crashed).ToList();
            Assert.That(found.Count, Is.EqualTo(1), "Recover takes lists it");
            Assert.That(found[0].Frames, Is.GreaterThanOrEqualTo(AudioCapture.ChunkFrames));

            Assert.That(rec.Recover(found), Is.EqualTo(1));
            var recovered = rig.Core.SongCore.MachinesList.First(m => m.Name == "Recovered");
            var clip = MainLane(rig, recovered).Events.Single();
            Assert.That(clip.Key, Is.EqualTo(0));
            var left = Take(rig, clip.Value.Pattern);
            Assert.That(left.Length, Is.EqualTo(found[0].Frames));
            for (int j = 0; j < left.Length; j += 13)
                Assert.That(left[j], Is.EqualTo(V(begin + j)), "recovered frame " + j);
            Assert.That(File.Exists(crashed), Is.False, "recovered takes leave the folder");
        }
    }
}
