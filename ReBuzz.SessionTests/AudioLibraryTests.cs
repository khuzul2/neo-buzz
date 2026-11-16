// Fork (docs/specs/neo-audio.md, NA1): the song's audio store, the resampler and the importer (a file of each format is made here).

using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using ReBuzz.Core.AudioLibrary;
using ReBuzz.Core.Clips;
using libsndfile;

namespace ReBuzz.SessionTests
{
    public class AudioLibraryTests
    {
        [OneTimeSetUp]
        public void FindLibsndfile()
        {
            ReBuzz.Platform.NativeLibraries.Register(typeof(AudioLibraryTests).Assembly);
            try { SoundFile.FormatsMajor.ToList(); }
            catch (DllNotFoundException) { Assert.Ignore("libsndfile isn't installed here"); }
        }

        static float[] Tone(double hz, int rate, double seconds, double level = 0.5)
        {
            var x = new float[(int)(rate * seconds)];
            for (int i = 0; i < x.Length; i++) x[i] = (float)(level * Math.Sin(2 * Math.PI * hz * i / rate));
            return x;
        }

        static double Rms(float[] x, int from, int count)
        {
            double s = 0;
            for (int i = from; i < from + count; i++) s += (double)x[i] * x[i];
            return Math.Sqrt(s / count);
        }

        /// <summary>The frequency of a tone by its zero crossings (positive-going), over the middle of the signal.</summary>
        static double Hz(float[] x, int rate)
        {
            int from = x.Length / 4, to = x.Length * 3 / 4, n = 0;
            int first = -1, last = -1;
            for (int i = from + 1; i < to; i++)
                if (x[i - 1] < 0 && x[i] >= 0) { if (first < 0) first = i; last = i; n++; }
            return (n - 1) * (double)rate / (last - first);
        }

        // ------------------------------------------------------------------ the store

        [Test]
        public void TheSameAudioIsTheSameAssetAndAnAliasFindsTheRealOne()
        {
            var store = new AudioAssetStore();
            var a = new AudioAsset("a", "/x/a.wav", null, new AudioSource(Tone(440, 44100, 0.2), null, 44100), 1);
            var b = new AudioAsset("copy", "/y/b.wav", null, new AudioSource(Tone(440, 44100, 0.2), null, 44100), 1);
            var c = new AudioAsset("other", "", null, new AudioSource(Tone(441, 44100, 0.2), null, 44100), 1);
            Assert.That(b.Id, Is.EqualTo(a.Id), "content-addressed");
            Assert.That(c.Id, Is.Not.EqualTo(a.Id));
            int changes = 0;
            store.Changed += () => changes++;
            Assert.That(store.Add(a), Is.SameAs(a));
            Assert.That(store.Add(b), Is.SameAs(a), "a second copy shares the first");
            store.Add(c);
            Assert.That(store.Count, Is.EqualTo(2));
            store.Alias("take-1", c.Id);
            Assert.That(store.Get("take-1"), Is.SameAs(c));
            Assert.That(store.Resolve("take-1"), Is.EqualTo(c.Id));
            Assert.That(store.Get("nothing"), Is.Null);
            Assert.That(changes, Is.GreaterThan(0));
            store.Clear();
            Assert.That(store.Count, Is.EqualTo(0));
        }

        [Test]
        public void AnAssetReaderGivesSamplesAndSilenceOutsideThem()
        {
            var tone = Tone(1000, 48000, 0.1);
            var asset = new AudioAsset("t", "", null, new AudioSource(tone, null, 48000), 1);
            var r = asset.Reader;
            Assert.That(r.Frames, Is.EqualTo(tone.Length));
            Assert.That(r.SampleRate, Is.EqualTo(48000));
            Assert.That(r.Channels, Is.EqualTo(1));
            var buffer = new float[16];
            r.Read(100, 0, buffer);
            Assert.That(buffer, Is.EqualTo(tone.Skip(100).Take(16).ToArray()));
            r.Read(tone.Length - 4, 0, buffer);
            Assert.That(buffer.Skip(4), Is.All.EqualTo(0f), "past the end is silence");
            r.Read(-8, 0, buffer);
            Assert.That(buffer.Take(8), Is.All.EqualTo(0f), "before the start is silence");
        }

        // ------------------------------------------------------------------ the resampler

        [TestCase(44100, 48000)]
        [TestCase(48000, 44100)]
        [TestCase(22050, 48000)]
        [TestCase(96000, 44100)]
        public void AToneKeepsItsPitchAndLevelWhenResampled(int from, int to)
        {
            var y = Resampler.Resample(Tone(1000, from, 1.0), from, to);
            Assert.That(y.Length, Is.EqualTo(to).Within(2), "the length follows the rate");
            Assert.That(Hz(y, to), Is.EqualTo(1000).Within(2));
            double rms = Rms(y, to / 4, to / 2);
            Assert.That(rms, Is.EqualTo(0.5 / Math.Sqrt(2)).Within(0.01), "the level is kept");
        }

        [Test]
        public void WhatIsAboveTheNewNyquistIsRemovedNotFolded()
        {
            // 20 kHz sampled at 48 kHz down to 32 kHz (Nyquist 16 kHz): it must not come back as 12 kHz.
            var y = Resampler.Resample(Tone(20000, 48000, 1.0), 48000, 32000);
            Assert.That(Rms(y, 4000, 16000), Is.LessThan(0.5 / Math.Sqrt(2) * 0.01), "at least 40 dB down");
        }

        [Test]
        public void TheSameRateIsTheSameSamplesAndNothingBreaks()
        {
            var x = Tone(300, 44100, 0.1);
            Assert.That(Resampler.Resample(x, 44100, 44100), Is.EqualTo(x));
            Assert.That(Resampler.Resample(Array.Empty<float>(), 44100, 48000), Is.Empty);
            Assert.That(Resampler.Resample(new[] { 0.5f }, 44100, 48000).All(float.IsFinite), Is.True);
        }

        // ------------------------------------------------------------------ importing

        static string Dir() { var d = Path.Combine(Path.GetTempPath(), "na1-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

        /// <summary>Writes a stereo file with libsndfile, or null when this libsndfile can't encode the format.</summary>
        static string Make(string dir, string name, Format format, float[] left, float[] right, int rate)
        {
            string path = Path.Combine(dir, name);
            try
            {
                using var f = SoundFile.Create(path, rate, 2, format);
                var inter = new float[left.Length * 2];
                for (int i = 0; i < left.Length; i++) { inter[2 * i] = left[i]; inter[2 * i + 1] = right[i]; }
                f.WriteFloat(inter, 0, left.Length);
            }
            catch (Exception) { return null; }
            return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
        }

        public static readonly object[] Formats =
        {
            new object[] { "a.wav", Format.SF_FORMAT_WAV | Format.SF_FORMAT_PCM_16, 1e-3, true },
            new object[] { "a24.wav", Format.SF_FORMAT_WAV | Format.SF_FORMAT_PCM_24, 1e-5, true },
            new object[] { "a.aiff", Format.SF_FORMAT_AIFF | Format.SF_FORMAT_PCM_16, 1e-3, true },
            new object[] { "a.flac", Format.SF_FORMAT_FLAC | Format.SF_FORMAT_PCM_16, 1e-3, true },
            new object[] { "a.ogg", Format.SF_FORMAT_OGG | Format.SF_FORMAT_VORBIS, 0.05, false },
            new object[] { "a.mp3", Format.SF_FORMAT_MPEG | Format.SF_FORMAT_MPEG_LAYER_III, 0.08, false },
        };

        [TestCaseSource(nameof(Formats))]
        public void AFileOfEachFormatImportsAtTheSongRateWithItsPitchAndLevel(string file, Format format, double tolerance, bool exact)
        {
            string dir = Dir();
            try
            {
                var left = Tone(440, 44100, 1.5, 0.5);
                var right = Tone(660, 44100, 1.5, 0.25);
                string path = Make(dir, file, format, left, right, 44100);
                if (path == null) Assert.Inconclusive($"this libsndfile can't write {file}");
                Assert.That(AudioImporter.IsAudioFile(path), Is.True);
                var asset = AudioImporter.Import(path, 48000);
                Assert.That(asset.SampleRate, Is.EqualTo(48000), "resampled once to the song rate");
                Assert.That(asset.Channels, Is.EqualTo(2));
                Assert.That(asset.Frames, Is.EqualTo(48000 * 1.5).Within(2400), "mp3 and ogg pad their ends");
                Assert.That(asset.SourcePath, Is.EqualTo(path));
                Assert.That(asset.SourceSha256, Has.Length.EqualTo(64));
                Assert.That(Hz(asset.Source.Left, 48000), Is.EqualTo(440).Within(3));
                Assert.That(Hz(asset.Source.Right, 48000), Is.EqualTo(660).Within(4));
                double l = Rms(asset.Source.Left, 12000, 24000), r = Rms(asset.Source.Right, 12000, 24000);
                Assert.That(l, Is.EqualTo(0.5 / Math.Sqrt(2)).Within(tolerance * 4 + 0.01));
                Assert.That(r, Is.EqualTo(0.25 / Math.Sqrt(2)).Within(tolerance * 4 + 0.01));
                if (exact)
                {
                    var again = AudioImporter.Import(path, 48000);
                    Assert.That(again.Id, Is.EqualTo(asset.Id), "the same file imports to the same asset");
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Test]
        public void AFileAtTheSongRateIsNotResampledAndAMonoFileStaysMono()
        {
            string dir = Dir();
            try
            {
                string path = Path.Combine(dir, "m.wav");
                var tone = Tone(500, 48000, 0.5, 0.5);
                using (var f = SoundFile.Create(path, 48000, 1, Format.SF_FORMAT_WAV | Format.SF_FORMAT_FLOAT)) f.WriteFloat(tone, 0, tone.Length);
                var asset = AudioImporter.Import(path, 48000);
                Assert.That(asset.Channels, Is.EqualTo(1));
                Assert.That(asset.Source.Left, Is.EqualTo(tone), "float WAV at the song's rate: bit exact");
                Assert.That(asset.Source.Right, Is.SameAs(asset.Source.Left), "mono shares one array");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Test]
        public void ThingsThatAreNotAudioAreRefusedWithAReason()
        {
            string dir = Dir();
            try
            {
                string path = Path.Combine(dir, "x.wav");
                File.WriteAllText(path, "this is not audio");
                Assert.That(AudioImporter.IsAudioFile(path), Is.True, "by its name");
                Assert.Throws<InvalidDataException>(() => AudioImporter.Import(path, 48000), "by its content");
                Assert.That(AudioImporter.IsAudioFile(Path.Combine(dir, "x.txt")), Is.False);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
