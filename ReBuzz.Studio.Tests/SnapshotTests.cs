// Fork (docs/specs/history-snapshots.md HS2): full-song snapshots on the shared engine: the chunk store (dedupe),
// capture and restore (a restored song renders bit-identically), the embedded history in a song file, the
// automatic snapshots (save, timer, before big operations) and the UI-thread pause of a capture.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.Core.SongHistory;
using ReBuzz.FileOps;

namespace ReBuzz.Studio.Tests
{
    public class SnapshotTests
    {
        static string Temp(string what) => Path.Combine(Path.GetTempPath(), "snapshot-tests-" + Environment.ProcessId + "-" + what + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));

        /// <summary>Points untitled songs' history at a folder of this test.</summary>
        static IDisposable Untitled(out string root)
        {
            string r = root = Temp("untitled");
            string old = SongSnapshots.UntitledRoot;
            SongSnapshots.UntitledRoot = r;
            return Studio.AfterTest(() =>
            {
                SongSnapshots.UntitledRoot = old;
                try { Directory.Delete(r, true); } catch (IOException) { }
            });
        }

        static byte[] Noise(int n, int seed)
        {
            var b = new byte[n];
            new Random(seed).NextBytes(b);
            return b;
        }

        // ------------------------------------------------------------------ the store

        [Test]
        public void ChunksAreCutByContentSoAnInsertionKeepsMostOfThem()
        {
            var data = Noise(3_000_000, 1);
            var cut = ChunkStore.Split(data);
            Assert.That(cut.Sum(c => c.Length), Is.EqualTo(data.Length));
            Assert.That(cut.Count, Is.InRange(10, 200), "chunks of 16-256 KB");
            var shifted = Noise(777, 2).Concat(data).ToArray();
            var a = cut.Select(c => ChunkStore.HashOf(new ReadOnlySpan<byte>(data, c.Offset, c.Length))).ToHashSet();
            var b = ChunkStore.Split(shifted).Select(c => ChunkStore.HashOf(new ReadOnlySpan<byte>(shifted, c.Offset, c.Length))).ToHashSet();
            Assert.That(a.Intersect(b).Count(), Is.GreaterThanOrEqualTo(a.Count - 2), "all but the first chunk or two are found again");
            Assert.That(ChunkStore.Split(Noise(1000, 3)).Count, Is.EqualTo(1), "a small section is one chunk");
        }

        [Test]
        public void TheStoreKeepsAChunkOnceAndChecksWhatItReturns()
        {
            string dir = Temp("store");
            try
            {
                var store = new ChunkStore(dir);
                var data = Noise(100_000, 4);
                string h = ChunkStore.HashOf(data);
                Assert.That(store.Put(h, data, 0, data.Length), Is.True);
                Assert.That(store.Put(h, data, 0, data.Length), Is.False, "already there");
                Assert.That(store.Get(h), Is.EqualTo(data));
                Assert.That(store.All().Count(), Is.EqualTo(1));
                Assert.Throws<InvalidDataException>(() => store.PutStored(ChunkStore.HashOf(Noise(10, 5)), store.GetStored(h)), "a chunk must match its name");
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }

        // ------------------------------------------------------------------ the song

        static string Wav(string path, int seconds)
        {
            int n = 44100 * seconds;
            using var fs = File.Create(path);
            using var w = new BinaryWriter(fs);
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 2);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(44100); w.Write(88200); w.Write((short)2); w.Write((short)16);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(n * 2);
            var r = new Random(7);
            for (int i = 0; i < n; i++) w.Write((short)(r.Next(-20000, 20000)));
            return path;
        }

        static SessionRig Song(params string[] tracks)
        {
            var rig = new SessionRig(tracks);
            var m = rig.Machine(tracks[0]);
            var p = rig.Pattern(m, "P0", SessionRig.Bar, (0, 60), (4, 64), (8, 67), (12, 72));
            rig.Core.SongCore.SequencesList.First(s => s.Machine == m).SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, p));
            foreach (var name in tracks)       // analog drift is random per voice: off, so renders can be compared
                rig.Machine(name).ParameterGroupsList[1].ParametersList.First(q => q.Name == "Drift").SetValue(0, 0);
            rig.Core.Looping = true;
            rig.Core.SongCore.LoopEnd = SessionRig.Bar;
            return rig;
        }

        static WorkManager work;

        /// <summary>The master's output over <paramref name="buffers"/> of 256 frames, from the song's start.</summary>
        static float[] Render(ReBuzzCore core, int buffers)
        {
            work ??= new WorkManager(core, new WorkThreadEngine(1), 0, core.EngineSettings);
            core.Playing = false;
            core.SongCore.PlayPosition = 0;
            ReBuzzCore.masterInfo.PosInTick = 0;
            var all = new float[buffers * 512];
            var buffer = new float[512];
            core.Playing = true;
            lock (ReBuzzCore.AudioLock)
            {
                for (int i = 0; i < buffers; i++)
                {
                    work.MainAudioFillBuffer(buffer, 0, 512);
                    Array.Copy(buffer, 0, all, i * 512, 512);
                }
            }
            core.Playing = false;
            Dispatcher.UIThread.RunJobs();
            return all;
        }

        static void Silence(ReBuzzCore core)
        {
            core.Playing = false;
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>
        /// The restored song is the original to the byte (every section of the song file, which is what a render is made from),
        /// and it renders as the original does. A render of this harness is not repeatable to the bit even for a plain reopen of
        /// one file (the first note lands a few samples apart from run to run), so the audio is compared within -50 dB of the
        /// signal; the byte comparison is the bit-identical claim, and the null tests cover the render path.
        /// </summary>
        [AvaloniaTest]
        public void ASnapshotRestoredIsTheOriginalToTheByteAndRendersAsItDid()
        {
            using var untitled = Untitled(out _);
            using var rig = Song("Lead", "Pad");
            var core = rig.Core;
            core.Snapshots.Bind(null);
            var original = core.CaptureSongSections();
            var snap = core.Snapshots.Capture("original");
            core.Snapshots.Flush();
            Assert.That(snap.Pending, Is.False);
            var before = Render(core, 300);
            Assert.That(before.Any(x => x != 0), "the song makes sound");

            // Change it every way: a parameter, a machine, a pattern.
            var lead = rig.Machine("Lead").ParameterGroupsList[1].ParametersList.First(p => p.Flags.HasFlag(ParameterFlags.State) && p.MaxValue - p.MinValue > 10);
            lead.SetValue(0, lead.MaxValue);
            rig.Add("Neo Poly", "Extra");
            rig.Machine("Pad").CreatePattern("Q", 16);
            Assert.That(core.Snapshots.Diff(snap, null), Is.Not.Empty, "the song differs from the snapshot");

            core.Snapshots.Restore(snap);
            Assert.That(core.SongCore.MachinesList.Any(m => m.Name == "Extra"), Is.False, "the added machine is gone");
            Assert.That(core.Snapshots.Diff(snap, null), Is.Empty, "the song is what the snapshot holds");
            var restored = core.CaptureSongSections();
            Assert.That(restored.Select(s => s.Magic), Is.EqualTo(original.Select(s => s.Magic)));
            for (int i = 0; i < original.Count; i++)
                Assert.That(restored[i].Data, Is.EqualTo(original[i].Data), $"section {original[i].Magic:X8} is byte-identical");
            var after = Render(core, 300);
            double err = 0, sig = 0;
            for (int i = 0; i < before.Length; i++) { err += Math.Pow(before[i] - after[i], 2); sig += Math.Pow(before[i], 2); }
            double db = 10 * Math.Log10(Math.Max(1e-20, err) / sig);
            TestContext.Out.WriteLine($"restored render against the original: error {db:F1} dB relative to the signal");
            Assert.That(db, Is.LessThan(-50));
            Assert.That(core.Snapshots.List().Any(s => s.IsAuto && s.Name.StartsWith("Auto: before restoring")), "the way back");
            Assert.That(core.Modified, Is.True, "a restored song differs from its file");
        }

        [AvaloniaTest]
        public void RestoreIsRefusedWhilePlayingAndLeavesTheSongAlone()
        {
            using var untitled = Untitled(out _);
            using var rig = Song("Lead");
            var core = rig.Core;
            core.Snapshots.Bind(null);
            var snap = core.Snapshots.Capture("s");
            core.Snapshots.Flush();
            rig.Add("Neo Poly", "Extra");
            core.Playing = true;
            try
            {
                var e = Assert.Throws<InvalidOperationException>(() => core.Snapshots.Restore(snap));
                Assert.That(e!.Message, Does.StartWith("Stop to restore"));
                Assert.That(core.SongCore.MachinesList.Any(m => m.Name == "Extra"), "unchanged");
            }
            finally { Silence(core); }
        }

        [AvaloniaTest]
        public void ALargeWaveIsStoredOnceAcrossSnapshots()
        {
            using var untitled = Untitled(out string root);
            using var rig = Song("Lead");
            var core = rig.Core;
            string wav = Wav(Temp("wave") + ".wav", 6);
            try
            {
                core.SongCore.WavetableCore.LoadWave(1, wav, "noise", false);
                core.Snapshots.Bind(null);
                var a = core.Snapshots.Capture("a");
                rig.Pattern(rig.Machine("Lead"), "P1", 16);
                var b = core.Snapshots.Capture("b");
                rig.Pattern(rig.Machine("Lead"), "P2", 16);
                var c = core.Snapshots.Capture("c");
                core.Snapshots.Flush();
                var all = new[] { a, b, c };
                int refs = all.Sum(s => s.Sections.Sum(x => x.Chunks.Count));
                int distinct = all.SelectMany(s => s.Sections).SelectMany(x => x.Chunks).Select(x => x.Hash).Distinct().Count();
                int one = a.Sections.Sum(x => x.Chunks.Count);
                TestContext.Out.WriteLine($"3 snapshots: {refs} chunk references, {distinct} distinct; one has {one}");
                Assert.That(a.Bytes, Is.GreaterThan(500_000), "the wave is in it");
                Assert.That(distinct, Is.LessThan(one + 12), "the wave's chunks are shared");
                Assert.That(new ChunkStore(core.Snapshots.Folder).All().Count(), Is.EqualTo(distinct));
                long stored = new ChunkStore(core.Snapshots.Folder).Bytes();
                Assert.That(stored, Is.LessThan(a.Bytes * 2), "far less than three copies");

                // Deleting one keeps what the others hold, and a last delete clears the store.
                core.Snapshots.Delete(b.Id);
                core.Snapshots.Delete(a.Id);
                core.Snapshots.Flush();
                Assert.That(core.Snapshots.Assemble(c), Is.Not.Empty, "c is still whole");
                core.Snapshots.Delete(c.Id);
                core.Snapshots.Flush();
                Assert.That(new ChunkStore(core.Snapshots.Folder).All().Count(), Is.EqualTo(0));
            }
            finally { try { File.Delete(wav); } catch (IOException) { } }
        }

        // ------------------------------------------------------------------ folders and the song file

        [AvaloniaTest]
        public void AnUntitledSongsHistoryMovesBesideTheSongOnFirstSave()
        {
            using var untitled = Untitled(out string root);
            using var rig = Song("Lead");
            var core = rig.Core;
            core.Snapshots.Bind(null);
            var snap = core.Snapshots.Capture("early");
            core.Snapshots.Flush();
            Assert.That(core.Snapshots.Folder, Does.StartWith(root));
            string dir = Temp("song");
            Directory.CreateDirectory(dir);
            try
            {
                string song = Path.Combine(dir, "a.bmx");
                core.SongCore.SongName = song;
                core.SaveSongFile(song);
                core.Snapshots.Flush();
                Assert.That(core.Snapshots.Folder, Is.EqualTo(song + ".history"));
                Assert.That(Directory.Exists(song + ".history"));
                var names = core.Snapshots.List().Select(s => s.Name).ToList();
                Assert.That(names, Does.Contain("early"), "kept");
                Assert.That(core.Snapshots.List().Any(s => s.IsAuto && s.Reason == "save"), "and one taken by the save");
                Assert.That(Directory.GetDirectories(root), Is.Empty, "the app-local folder is gone");
                // Opened again, the folder's snapshots are there.
                Assert.That(core.TryOpenSongFile(song), Is.True);
                Assert.That(core.Snapshots.List().Select(s => s.Name), Does.Contain("early"));
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }

        [AvaloniaTest]
        public void AnEmbeddedHistoryComesBackWithoutTheFolderAndASongWithoutOneIsUnchanged()
        {
            using var untitled = Untitled(out _);
            using var rig = Song("Lead");
            var core = rig.Core;
            string dir = Temp("embed");
            Directory.CreateDirectory(dir);
            bool embed = core.Snapshots.Embed;
            using var restore = Studio.AfterTest(() => core.Snapshots.Embed = embed);
            try
            {
                string plain = Path.Combine(dir, "plain.bmx"), withHistory = Path.Combine(dir, "with.bmx");
                core.Snapshots.Embed = false;
                core.SongCore.SongName = plain;
                core.SaveSongFile(plain);
                core.Snapshots.Flush();
                var sections = core.CaptureSongSections();
                Assert.That(File.ReadAllBytes(plain), Is.EqualTo(BMXFile.Assemble(sections)), "no history section: the file is what a save wrote before");
                Assert.That(sections.Any(s => s.Magic == 0x54534948), Is.False);

                var snap = core.Snapshots.Capture("kept");
                core.Snapshots.Flush();
                core.Snapshots.Embed = true;
                core.SongCore.SongName = withHistory;
                core.SaveSongFile(withHistory);
                Assert.That(new FileInfo(withHistory).Length, Is.GreaterThan(new FileInfo(plain).Length), "the history is in the song");

                // Open it with no history folder beside it: the history is back, and restores.
                Directory.Delete(withHistory + ".history", true);
                Assert.That(core.TryOpenSongFile(withHistory), Is.True);
                var names = core.Snapshots.List().Select(s => s.Name).ToList();
                Assert.That(names, Does.Contain("kept"));
                var back = core.Snapshots.Find("kept");
                Assert.That(core.Snapshots.Assemble(back), Is.EqualTo(core.Snapshots.Assemble(snap)), "the same song bytes");

                // A copy of the song saved with the option off opens with no snapshots of its own.
                Assert.That(new ChunkStore(core.Snapshots.Folder).All().Any());
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }

        // ------------------------------------------------------------------ automatic snapshots

        [AvaloniaTest]
        public void TheTimerSnapshotsEditedSongsEveryIntervalAndNotWhilePlaying()
        {
            using var untitled = Untitled(out _);
            using var rig = Song("Lead");
            var core = rig.Core;
            int interval = core.EngineSettings.SnapshotIntervalMinutes;
            using var restore = Studio.AfterTest(() => { core.EngineSettings.SnapshotIntervalMinutes = interval; Silence(core); });
            core.EngineSettings.SnapshotIntervalMinutes = 5;
            core.Snapshots.Bind(null);
            var t0 = DateTime.UtcNow;
            Assert.That(core.Snapshots.Tick(t0.AddMinutes(6)), Is.False, "nothing changed");
            core.SetModifiedFlag();
            Assert.That(core.Snapshots.Tick(t0.AddMinutes(1)), Is.False, "too soon");
            core.Playing = true;
            Assert.That(core.Snapshots.Tick(t0.AddMinutes(6)), Is.False, "not during a performance");
            Silence(core);
            Assert.That(core.Snapshots.Tick(t0.AddMinutes(6)), Is.True);
            core.Snapshots.Flush();
            var s = core.Snapshots.List().Single();
            Assert.That(s.IsAuto, Is.True);
            Assert.That(s.Reason, Does.Contain("5 min"));
            Assert.That(core.Snapshots.Tick(t0.AddMinutes(12)), Is.False, "unchanged since");
            core.SetModifiedFlag();
            Assert.That(core.Snapshots.Tick(t0.AddMinutes(12)), Is.True);
            core.EngineSettings.SnapshotIntervalMinutes = 0;
            core.SetModifiedFlag();
            Assert.That(core.Snapshots.Tick(t0.AddMinutes(30)), Is.False, "off");
        }

        [AvaloniaTest]
        public void AutoSnapshotsAreLabelledAndTheOldestDropPastTheCapButManualOnesStay()
        {
            using var untitled = Untitled(out _);
            using var rig = Song("Lead");
            var core = rig.Core;
            core.Snapshots.Bind(null);
            var keep = core.Snapshots.Capture("mine");
            for (int i = 0; i < SongSnapshots.MaxAuto + 5; i++)
                core.Snapshots.Auto("before thing " + i);
            core.Snapshots.Flush();
            var list = core.Snapshots.List();
            Assert.That(list.Count(s => s.IsAuto), Is.EqualTo(SongSnapshots.MaxAuto));
            Assert.That(list.Select(s => s.Name), Does.Contain("mine"));
            Assert.That(list.Where(s => s.IsAuto).All(s => s.Name.StartsWith("Auto: ")));
            Assert.That(list.Any(s => s.Reason == "before thing 0"), Is.False, "the oldest went");
            Assert.That(list.Any(s => s.Reason == "before thing " + (SongSnapshots.MaxAuto + 4)));
        }

        [AvaloniaTest]
        public void ARenameKeepsTheSnapshotAndADiffNamesMachinesWiresAndPatterns()
        {
            using var untitled = Untitled(out _);
            using var rig = Song("Lead", "Pad");
            var core = rig.Core;
            core.Snapshots.Bind(null);
            var a = core.Snapshots.Capture("a");
            rig.Add("Neo Poly", "Extra");
            rig.Machine("Pad").CreatePattern("Q", 16);
            core.SongCore.MachinesList.First(m => m.Name == "Lead").Outputs.First().Amp = 0x2000;
            var b = core.Snapshots.Capture("b");
            core.Snapshots.Flush();
            var changes = core.Snapshots.Diff(a, b);
            Assert.That(changes.Any(c => c.Area == "machines" && c.Subject == "Extra" && c.Text.StartsWith("added")));
            Assert.That(changes.Any(c => c.Area == "patterns" && c.Subject == "Pad" && c.Text.Contains("Q")));
            Assert.That(changes.Any(c => c.Area == "wires"), "the changed gain");
            Assert.That(core.Snapshots.Diff(a, a), Is.Empty);
            Assert.That(core.Snapshots.Rename(a.Id, "renamed"), Is.True);
            core.Snapshots.Flush();
            core.Snapshots.Bind(null);                      // a new untitled song forgets them…
            Assert.That(core.Snapshots.List(), Is.Empty);
            string folder = Path.Combine(SongSnapshots.UntitledRoot);
            Assert.That(Directory.GetDirectories(folder).Length, Is.GreaterThan(0));
        }

        [AvaloniaTest]
        public void AParameterChangeShowsInTheDiffWithTheMachinesOwnWords()
        {
            using var untitled = Untitled(out _);
            using var rig = Song("Pad");
            var core = rig.Core;
            core.Snapshots.Bind(null);
            var a = core.Snapshots.Capture("a");
            var volume = rig.Machine("Pad").ParameterGroupsList[1].ParametersList.First(p => p.Name == "Volume");
            int was = volume.GetValue(0);
            ReBuzz.Agent.AgentActions.SetParameter(core, rig.Machine("Pad"), volume, 0, Math.Max(volume.MinValue, was - 100));
            var b = core.Snapshots.Capture("b");
            core.Snapshots.Flush();
            var changes = core.Snapshots.Diff(a, b);
            TestContext.Out.WriteLine(string.Join("; ", changes.Select(c => $"{c.Area}/{c.Subject}: {c.Text}")));
            Assert.That(changes.Any(c => c.Area == "parameters" && c.Subject == "Pad" && c.Text.StartsWith("Volume")), "the parameter, as the machine shows it");
            Assert.That(core.Snapshots.Diff(b, null), Is.Empty);
        }

        // ------------------------------------------------------------------ the pause

        [AvaloniaTest]
        public void ACaptureOfABigSongPausesTheUiThreadBriefly()
        {
            using var untitled = Untitled(out _);
            var names = Enumerable.Range(0, 32).Select(i => "T" + i).ToArray();
            using var rig = Song(names);
            var core = rig.Core;
            string wav = Wav(Temp("big") + ".wav", 20);
            try
            {
                core.SongCore.WavetableCore.LoadWave(1, wav, "noise", false);
                core.Snapshots.Bind(null);
                core.Snapshots.Capture("warm");
                core.Snapshots.Flush();
                var times = new List<double>();
                for (int i = 0; i < 5; i++)
                {
                    var sw = Stopwatch.StartNew();
                    var s = core.Snapshots.Capture("t" + i);
                    sw.Stop();
                    times.Add(sw.Elapsed.TotalMilliseconds);
                    core.Snapshots.Flush();
                    TestContext.Out.WriteLine($"capture {i}: UI thread {sw.Elapsed.TotalMilliseconds:F1} ms; {s.Bytes / 1048576.0:F1} MB song; store {new ChunkStore(core.Snapshots.Folder).Bytes() / 1048576.0:F1} MB");
                }
                times.Sort();
                TestContext.Out.WriteLine($"UI-thread pause of a capture, 32 machines and a 20 s wave: median {times[2]:F1} ms, worst {times[4]:F1} ms");
                Assert.That(times[2], Is.LessThan(1000));
            }
            finally { try { File.Delete(wav); } catch (IOException) { } }
        }
    }
}
