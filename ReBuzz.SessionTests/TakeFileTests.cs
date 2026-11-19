using ReBuzz.Core.Clips;

namespace ReBuzz.SessionTests
{
    /// <summary>
    /// Temp-file takes (docs/specs/neo-audio.md "Takes and comping"): the audio thread writes chunks, a writer thread writes them to a
    /// WAV and gives them back. RAM stays bounded for a long take, nothing is allocated on the audio thread, a crash leaves a file that
    /// recovers, and what comes back is what was written.
    /// </summary>
    [TestFixture, NonParallelizable]
    public class TakeFileTests
    {
        const int Rate = 44100;
        string folder;
        string was;

        [SetUp]
        public void SetUp()
        {
            was = TakeFile.Directory;
            folder = Path.Combine(Path.GetTempPath(), "neobuzz-takes-test-" + Guid.NewGuid().ToString("N"));
            TakeFile.Directory = folder;
        }

        [TearDown]
        public void TearDown()
        {
            TakeFile.Directory = was;
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        static float Left(long f) => 0.5f * MathF.Sin(2 * MathF.PI * 440 * (f % Rate) / Rate);
        static float Right(long f) => (f % 1000) / 2000f;

        /// <summary>The audio thread's side: blocks of 512 frames into the capture.</summary>
        static void Feed(AudioCapture capture, long from, long frames)
        {
            var l = new float[512];
            var r = new float[512];
            for (long at = 0; at < frames; at += 512)
            {
                int n = (int)Math.Min(512, frames - at);
                for (int i = 0; i < n; i++)
                {
                    l[i] = Left(from + at + i);
                    r[i] = Right(from + at + i);
                }
                capture.Write(l, r, n, 1f);
            }
        }

        [Test]
        public void ATakeComesBackFromItsFileSampleForSample()
        {
            var file = TakeFile.Create("take", Rate, 120)!;
            var capture = new AudioCapture(Rate, 0, long.MaxValue, 60L * Rate, 0, file);
            long total = 3L * AudioCapture.ChunkFrames + 12345;                      // some whole chunks and a partial one
            for (long done = 0; done < total; done += Rate)
            {
                Feed(capture, done, Math.Min(Rate, total - done));
                Thread.Sleep(30);
            }
            capture.SetLength(total);
            var (left, right) = capture.Collect(total);
            Assert.That(left.Length, Is.EqualTo(total));
            for (long f = 0; f < total; f += 97)
            {
                Assert.That(left[f], Is.EqualTo(Left(f)), "left " + f);
                Assert.That(right[f], Is.EqualTo(Right(f)), "right " + f);
            }
            Assert.That(capture.Dropped, Is.EqualTo(0));
            string path = file.Path;
            capture.Close();
            Assert.That(File.Exists(path), Is.False, "committed: the temp file goes");
            Assert.That(File.Exists(path + ".json"), Is.False);
        }

        [Test]
        public void ARoundTripDropIsKeptInTheFile()
        {
            var file = TakeFile.Create("take", Rate, 120)!;
            var capture = new AudioCapture(Rate, 1000, 2L * Rate, 60L * Rate, 0, file);
            for (long done = 0; done < 2L * Rate + 1000; done += Rate / 2)
            {
                Feed(capture, done, Rate / 2);
                Thread.Sleep(20);
            }
            Assert.That(capture.IsDone, Is.True);
            var (left, _) = capture.Collect(2L * Rate);
            Assert.That(left[0], Is.EqualTo(Left(1000)), "the first 1000 frames are the round trip, dropped");
            Assert.That(left[2 * Rate - 1], Is.EqualTo(Left(1000 + 2 * Rate - 1)));
            capture.Close();
        }

        [Test]
        public void ATenMinuteTakeKeepsRamBoundedAndTheFileWhole()
        {
            var file = TakeFile.Create("long", Rate, 120)!;
            long total = 600L * Rate;
            var capture = new AudioCapture(Rate, 0, long.MaxValue, 30L * 60 * Rate, 0, file);
            GC.Collect();
            long heap = GC.GetTotalMemory(true);
            for (long done = 0; done < total; done += Rate)
            {
                Feed(capture, done, Rate);
                // The test runs far faster than real time (a second of audio a second is all the writer has to keep up with): wait for it when it falls behind.
                Thread.Sleep(5);
                int guard = 0;
                while (capture.Written - file.Frames > 2L * AudioCapture.ChunkFrames && guard++ < 2000) Thread.Sleep(1);
            }
            long grown = GC.GetTotalMemory(false) - heap;
            Assert.That(capture.PeakResidentChunks, Is.LessThanOrEqualTo(10), "a few chunks, whatever the length");
            Assert.That(grown, Is.LessThan(24L * 1024 * 1024), "a ten-minute take is 212 MB of audio: the heap grew " + grown);
            Assert.That(capture.Dropped, Is.EqualTo(0));
            capture.SetLength(total);
            capture.Complete(total);
            Assert.That(file.Frames, Is.EqualTo(total));
            Assert.That(new FileInfo(file.Path).Length, Is.EqualTo(44 + total * 8));
            capture.Close();
        }

        [Test]
        public void TheAudioThreadAllocatesNothingWhileItRecords()
        {
            var file = TakeFile.Create("alloc", Rate, 120)!;
            var capture = new AudioCapture(Rate, 0, long.MaxValue, 60L * Rate, 0, file);
            var l = new float[512];
            var r = new float[512];
            Array.Fill(l, 0.25f);
            Array.Fill(r, -0.25f);
            Thread.Sleep(100);
            capture.Write(l, r, 512, 1f);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 3000; i++)               // about 35 seconds of audio: many chunks are crossed
            {
                capture.Write(l, r, 512, 1f);
                if (i % 80 == 0) Thread.Sleep(2);
            }
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.EqualTo(0L), "Write() on the audio thread allocates nothing");
            capture.SetLength(capture.Written);
            capture.Close();
        }

        [Test]
        public void ACrashLeavesATakeThatRecovers()
        {
            var file = TakeFile.Create("Lead take 2", Rate, 133)!;
            var capture = new AudioCapture(Rate, 0, long.MaxValue, 60L * Rate, 0, file);
            Feed(capture, 0, 3L * AudioCapture.ChunkFrames + 777);
            long flushed = 0;
            for (int i = 0; i < 200 && flushed < 3L * AudioCapture.ChunkFrames; i++)
            {
                Thread.Sleep(10);
                flushed = file.Frames;
            }
            Assert.That(flushed, Is.GreaterThanOrEqualTo(3L * AudioCapture.ChunkFrames), "the writer keeps up");
            // The process dies here: no commit, no close. Another instance finds the file (a copy stands for it).
            string path = file.Path;
            string crashed = Path.Combine(folder, "take-crashed.nbtake.wav");
            File.Copy(path, crashed);
            File.Copy(path + ".json", crashed + ".json");
            capture.Close();                              // (the dead run's writer thread)

            var found = TakeFile.Leftovers();
            Assert.That(found.Count, Is.EqualTo(1), "one take to recover");
            var take = found[0];
            Assert.That(take.Name, Is.EqualTo("Lead take 2"));
            Assert.That(take.Bpm, Is.EqualTo(133));
            Assert.That(take.SampleRate, Is.EqualTo(Rate));
            Assert.That(take.Frames, Is.GreaterThanOrEqualTo(3L * AudioCapture.ChunkFrames));
            var (left, right) = TakeFile.Read(take);
            for (long f = 0; f < take.Frames; f += 101)
            {
                Assert.That(left[f], Is.EqualTo(Left(f)), "left " + f);
                Assert.That(right[f], Is.EqualTo(Right(f)), "right " + f);
            }
            TakeFile.Discard(take);
            Assert.That(TakeFile.Leftovers(), Is.Empty, "recovered takes are taken out of the folder");
        }

        [Test]
        public void ARunningTakeIsNotOfferedForRecovery()
        {
            var file = TakeFile.Create("live", Rate, 120)!;
            var capture = new AudioCapture(Rate, 0, long.MaxValue, 60L * Rate, 0, file);
            Feed(capture, 0, 2L * AudioCapture.ChunkFrames);
            Thread.Sleep(100);
            Assert.That(TakeFile.Leftovers(), Is.Empty);
            capture.Close();
        }

        [Test]
        public void ARecordingKeepsAPeakPerBlockForDrawingItGrow()
        {
            var capture = new AudioCapture(Rate, 0, long.MaxValue, 60L * Rate, 0);
            var l = new float[AudioCapture.PeakFrames * 2];
            var r = new float[l.Length];
            for (int i = AudioCapture.PeakFrames; i < l.Length; i++) l[i] = 0.5f;
            capture.Write(l, r, l.Length, 1f);
            Assert.That(capture.PeakAt(0), Is.EqualTo(0f));
            Assert.That(capture.PeakAt(1), Is.EqualTo(0.5f));
            Assert.That(capture.PeakCount, Is.GreaterThanOrEqualTo(2));
        }
    }
}
