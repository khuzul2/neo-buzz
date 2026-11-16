using System.Xml.Linq;
using ReBuzz.Core.AudioLibrary;
using ReBuzz.Core.Clips;
using ReBuzz.Core.Session;

namespace ReBuzz.SessionTests
{
    /// <summary>
    /// Audio clips in the arrangement (docs/specs/neo-audio.md, NA2), pure: fades, crossfades, gain, reverse and stretch in the clip voice and mixer
    /// (each render compared with the envelope it should make), the peak cache, and the settings the song keeps.
    /// </summary>
    [TestFixture]
    public class AudioClipEditingTests
    {
        const int Rate = 44100;
        const double Bpm = 120;
        /// <summary>Beats per output sample at 120 BPM.</summary>
        const double Bps = Bpm / 60.0 / Rate;

        static AudioSource Dc(double value, double seconds) { var x = new float[(int)(seconds * Rate)]; Array.Fill(x, (float)value); return new AudioSource(x, x, Rate); }

        static AudioSource Sine(double freq, double seconds, double amplitude = 0.5)
        {
            var x = new float[(int)(seconds * Rate)];
            for (int i = 0; i < x.Length; i++) x[i] = (float)(amplitude * Math.Sin(2 * Math.PI * freq * i / Rate));
            return new AudioSource(x, x, Rate);
        }

        static AudioSource Clicks(int beats, double bpm = Bpm)
        {
            int perBeat = (int)Math.Round(Rate * 60 / bpm);
            var x = new float[perBeat * beats];
            for (int b = 0; b < beats; b++)
                for (int i = 0; i < Rate / 100 && b * perBeat + i < x.Length; i++)
                    x[b * perBeat + i] = (float)(0.8 * Math.Sin(2 * Math.PI * 1000 * i / Rate) * Math.Exp(-i / (0.002 * Rate)));
            return new AudioSource(x, x, Rate);
        }

        /// <summary>A clip of the audio at 120 BPM, warped by Repitch (the beat is the audio, sample for sample).</summary>
        static AudioClipParams Clip(AudioSource s, ClipFades fades = default, float gain = 1f, double bpm = Bpm)
        {
            var map = WarpMap.Straight(s.Length, s.Seconds * bpm / 60);
            return new AudioClipParams(s, map, WarpMode.Repitch, null, gain, 0) { Fades = fades };
        }

        /// <summary>Plays a clip the way the engine does for an arrangement clip that starts at clip beat 0 and is <paramref name="lengthBeats"/> long; returns what the mixer made.</summary>
        static float[] Play(ClipPlayback clip, double lengthBeats, int samples, ClipMixer? mixer = null, int block = 256)
        {
            mixer ??= new ClipMixer();
            var output = new float[samples];
            for (int at = 0; at < samples; at += block)
            {
                int n = Math.Min(block, samples - at);
                mixer.Begin(n);
                double beat = at * Bps;
                mixer.Play(clip, beat, Bps, Rate, n, beat, lengthBeats - beat);
                mixer.End(n, Bps, Rate);
                Array.Copy(mixer.Left, 0, output, at, n);
            }
            return output;
        }

        // ---- Fades ---------------------------------------------------------------------------------

        [Test]
        public void AFadeInRisesAsALineOverItsBeatsSampleForSample()
        {
            var clip = new ClipPlayback { Params = Clip(Dc(0.5, 10), new ClipFades(1, 0, 0, false)) };
            var o = Play(clip, 20, Rate);                                   // one second: two beats
            for (int i = ClipVoice.Fade; i < o.Length; i++)
                Assert.That(o[i], Is.EqualTo(0.5 * Math.Min(1, i * Bps / 1)).Within(2e-3), $"sample {i}");
            Assert.That(o[Rate - 1], Is.EqualTo(0.5).Within(2e-3), "full after the fade");
        }

        [Test]
        public void AFadeOutFallsToSilenceAtTheClipsEnd()
        {
            var clip = new ClipPlayback { Params = Clip(Dc(0.5, 10), new ClipFades(0, 1, 0, false)) };
            int samples = (int)(3 / Bps);                                   // the clip is three beats long
            var o = Play(clip, 3, samples);
            for (int i = ClipVoice.Fade; i < samples; i++)
                Assert.That(o[i], Is.EqualTo(0.5 * Math.Clamp((3 - i * Bps) / 1, 0, 1)).Within(2e-3), $"sample {i}");
            Assert.That(Math.Abs(o[samples - 1]), Is.LessThan(1e-3), "silent at the end");
        }

        [Test]
        public void TheFadesAndTheGainMultiply()
        {
            var clip = new ClipPlayback { Params = Clip(Dc(0.5, 10), new ClipFades(1, 1, 0, false), gain: 0.5f) };
            int samples = (int)(4 / Bps);
            var o = Play(clip, 4, samples);
            for (int i = ClipVoice.Fade; i < samples; i += 7)
            {
                double b = i * Bps;
                Assert.That(o[i], Is.EqualTo(0.25 * Math.Min(1, b) * Math.Clamp(4 - b, 0, 1)).Within(2e-3), $"sample {i}");
            }
        }

        [Test]
        public void AFadeMatchesWhateverTheBlockSize()
        {
            var fades = new ClipFades(2, 2, 0, false);
            var a = Play(new ClipPlayback { Params = Clip(Dc(0.5, 10), fades) }, 4, (int)(4 / Bps), block: 64);
            var b = Play(new ClipPlayback { Params = Clip(Dc(0.5, 10), fades) }, 4, (int)(4 / Bps), block: 1000);
            for (int i = 200; i < a.Length; i++) Assert.That(a[i], Is.EqualTo(b[i]).Within(1e-4), $"sample {i}");
        }

        // ---- Crossfades ----------------------------------------------------------------------------

        [Test]
        public void ACrossfadeIsTheFirstClipsTailFallingUnderTheSecondsFadeIn()
        {
            const double Beats = 0.5;                                       // the crossfade's length
            int boundary = 100 * 256, crossLen = (int)(Beats / Bps);
            var first = new ClipPlayback { Params = Clip(Dc(0.5, 10), new ClipFades(0, 0, Beats, false)) };
            var second = new ClipPlayback { Params = Clip(Dc(0.25, 10), new ClipFades(Beats, 0, 0, true)) };
            var mixer = new ClipMixer();
            int total = (boundary + crossLen + 4 * 256 + 255) / 256 * 256;
            var o = new float[total];
            for (int at = 0; at < total; at += 256)
            {
                mixer.Begin(256);
                if (at < boundary) mixer.Play(first, at * Bps, Bps, Rate, 256, at * Bps, boundary * Bps - at * Bps);
                else mixer.Play(second, (at - boundary) * Bps, Bps, Rate, 256, (at - boundary) * Bps, 100);
                mixer.End(256, Bps, Rate);
                Array.Copy(mixer.Left, 0, o, at, 256);
            }
            for (int j = ClipVoice.Fade + 1; j < crossLen; j++)
            {
                double x = j * Bps / Beats;
                double expected = 0.5 * Math.Cos(x * Math.PI / 2) + 0.25 * Math.Sin(x * Math.PI / 2);
                Assert.That(o[boundary + j], Is.EqualTo(expected).Within(3e-3), $"sample {j} of the crossfade");
            }
            Assert.That(o[boundary + crossLen + 300], Is.EqualTo(0.25).Within(2e-3), "the second clip alone afterwards");
            Assert.That(first.Voice.IsSounding, Is.False, "the first clip's voice is done");
            Assert.That(o[boundary - 10], Is.EqualTo(0.5).Within(2e-3), "the first clip at full level up to the cut");
        }

        [Test]
        public void ATailIsNotHeardWhenTheTransportStops()
        {
            var clip = new ClipPlayback { Params = Clip(Dc(0.5, 10), new ClipFades(0, 0, 1, false)) };
            var mixer = new ClipMixer();
            for (int at = 0; at < 2048; at += 256)
            {
                mixer.Begin(256);
                mixer.Play(clip, at * Bps, Bps, Rate, 256, at * Bps, 10);
                mixer.End(256, Bps, Rate);
            }
            mixer.Begin(256);
            mixer.End(256);                                                  // stopped: no beats per sample, no tail
            for (int block = 0; block < 3; block++) { mixer.Begin(256); mixer.End(256); }
            Assert.That(mixer.Left.Take(256).All(v => v == 0), Is.True);
            Assert.That(clip.Voice.IsSounding, Is.False);
        }

        // ---- The audio thread ----------------------------------------------------------------------

        [Test]
        public void FadesAndTailsAllocateNothingOnTheAudioThread()
        {
            var first = new ClipPlayback { Params = Clip(Sine(220, 5), new ClipFades(0.25, 0.25, 0.25, false)) };
            var second = new ClipPlayback { Params = Clip(Sine(330, 5), new ClipFades(0.25, 0.25, 0, true)) };
            var mixer = new ClipMixer();
            void Run(int blocks)
            {
                for (int k = 0; k < blocks; k++)
                {
                    mixer.Begin(256);
                    double beat = k * 256 * Bps;
                    if (k < blocks / 2) mixer.Play(first, beat, Bps, Rate, 256, beat, blocks / 2 * 256 * Bps - beat);
                    else mixer.Play(second, beat, Bps, Rate, 256, beat - blocks / 2 * 256 * Bps, 100);
                    mixer.End(256, Bps, Rate);
                }
            }
            Run(400);                                                        // warm up (JIT)
            long before = GC.GetAllocatedBytesForCurrentThread();
            Run(400);
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.EqualTo(0L));
        }

        // ---- Reverse -------------------------------------------------------------------------------

        [Test]
        public void ReverseIsTheSameAudioBackwardsSampleForSample()
        {
            var source = Sine(220, 2);
            var forward = Clip(source);
            var map = WarpMap.Straight(source.Length, source.Seconds * Bpm / 60);
            var reversed = new AudioClipParams(source.Reversed(), map.Mirrored(source.Length, map.BeatAt(source.Length)), WarpMode.Repitch, null, 1f, 0);
            int n = source.Length;
            var f = Play(new ClipPlayback { Params = forward }, 4, n + 4);
            var r = Play(new ClipPlayback { Params = reversed }, 4, n);
            for (int i = 200; i < n - 200; i += 3)
                Assert.That(r[i], Is.EqualTo(f[n - i]).Within(2e-3), $"sample {i}");
        }

        [Test]
        public void AMirroredMapPutsEachMarkerWhereTheReversedAudioHasIt()
        {
            // 500 samples a beat for two beats, then 1500: backwards the slow stretch comes first.
            var map = new WarpMap(new[] { new WarpMarker(0, 0), new WarpMarker(1000, 2), new WarpMarker(4000, 4) });
            var mirrored = map.Mirrored(4001, 4);
            Assert.That(mirrored.SampleAt(0), Is.EqualTo(0).Within(1e-9), "the reversed audio starts at its own start, which is the end of the original");
            Assert.That(mirrored.SampleAt(2), Is.EqualTo(3000).Within(1e-9));
            Assert.That(mirrored.SampleAt(4), Is.EqualTo(4000).Within(1e-9));
            Assert.That(mirrored.SamplesPerBeat(1), Is.EqualTo(1500).Within(1e-9), "the original's slow part now comes last");
            Assert.That(mirrored.SamplesPerBeat(3), Is.EqualTo(500).Within(1e-9));
        }

        // ---- Stretch -------------------------------------------------------------------------------

        static List<int> Onsets(float[] x, float threshold = 0.3f)
        {
            var list = new List<int>();
            int quiet = 3000;
            for (int i = 0; i < x.Length; i++)
            {
                if (Math.Abs(x[i]) > threshold && quiet > 2000) { list.Add(i); quiet = 0; }
                else if (Math.Abs(x[i]) <= threshold) quiet++;
            }
            return list;
        }

        [Test]
        public void StretchingAClipToTwiceItsLengthPutsEveryClickTwiceAsLate()
        {
            var clicks = Clicks(8);                                          // a click a beat of the audio: every half second
            int samples = (int)(16 / Bps);
            var normal = Play(new ClipPlayback { Params = new AudioClipParams(clicks, WarpMap.Straight(clicks.Length, 8), WarpMode.Repitch, null, 1f, 0) }, 16, samples);
            // What the stretch drag does: the audio's tempo doubles, so its 8 beats are 16 (its markers' beats, the clip's length and its regions scale the same).
            var stretched = Play(new ClipPlayback { Params = new AudioClipParams(clicks, WarpMap.Straight(clicks.Length, 16), WarpMode.Repitch, null, 1f, 0) }, 16, samples);
            var a = Onsets(normal);
            var b = Onsets(stretched);
            Assert.That(a.Count, Is.EqualTo(8), "a click a beat");
            Assert.That(b.Count, Is.EqualTo(8), "the same clicks, further apart");
            for (int k = 0; k < 8; k++)
            {
                Assert.That(a[k], Is.EqualTo((int)(k / Bps)).Within(80), $"click {k} where its beat is");
                Assert.That(b[k], Is.EqualTo((int)(2 * k / Bps)).Within(80), $"click {k} stretched to twice the time");
            }
        }

        // ---- The peak cache ------------------------------------------------------------------------

        [Test]
        public void PeaksAreTheExtremesOfAnyRangeAtAnyZoom()
        {
            var rnd = new Random(7);
            int n = 300_000;
            var l = new float[n];
            var r = new float[n];
            for (int i = 0; i < n; i++) { l[i] = (float)(Math.Sin(i * 0.01) * rnd.NextDouble()); r[i] = (float)(rnd.NextDouble() - 0.5); }
            var source = new AudioSource(l, r, Rate);
            var peaks = PeakCache.Build(source);
            Assert.That(peaks.Levels, Is.GreaterThan(4));
            for (int k = 0; k < 300; k++)
            {
                double a = rnd.NextDouble() * n * 0.9, len = Math.Pow(10, rnd.NextDouble() * 5);
                double b = Math.Min(n - 1, a + len);
                peaks.Range(a, b, out float lo, out float hi);
                float eLo = float.MaxValue, eHi = float.MinValue;
                for (long i = (long)Math.Floor(a); i <= (long)Math.Ceiling(b) && i < n; i++)
                {
                    eLo = Math.Min(eLo, Math.Min(l[i], r[i]));
                    eHi = Math.Max(eHi, Math.Max(l[i], r[i]));
                }
                // The cache works in blocks, so it may show a little more at the ends of a long range, never less.
                Assert.That(lo, Is.LessThanOrEqualTo(eLo + 1e-6f), $"low of {a:0}..{b:0}");
                Assert.That(hi, Is.GreaterThanOrEqualTo(eHi - 1e-6f), $"high of {a:0}..{b:0}");
                if (len < PeakCache.BaseBlock * 2)
                {
                    Assert.That(lo, Is.EqualTo(eLo).Within(1e-6f));
                    Assert.That(hi, Is.EqualTo(eHi).Within(1e-6f));
                }
            }
            peaks.Range(-500, -100, out float zl, out float zh);
            Assert.That((zl, zh), Is.EqualTo((0f, 0f)), "silence outside the audio");
        }

        [Test]
        public void PeaksAreMadeInTheBackgroundOnceAndKeptWithTheAsset()
        {
            var asset = new AudioAsset("x", "", null, Sine(100, 3), 2);
            Assert.That(asset.Peaks, Is.Null);
            using var ready = new ManualResetEventSlim();
            Assert.That(asset.PeaksOrBuild(ready.Set), Is.Null, "not there yet: the build runs off the caller's thread");
            Assert.That(ready.Wait(10000), Is.True);
            var peaks = asset.Peaks;
            Assert.That(peaks, Is.Not.Null);
            Assert.That(asset.PeaksOrBuild(), Is.SameAs(peaks), "kept, not made again");
        }

        // ---- The settings the song keeps -----------------------------------------------------------

        [Test]
        public void FadesCrossfadesAndReverseSurviveSavingAndOldSongsLoadWithNone()
        {
            var doc = new SessionDocument();
            doc.SetClip("Audio", "one", new SessionDocument.ClipSettings
            {
                Audio = new SessionDocument.AudioSettings { Asset = "a1", FadeIn = 1.5, FadeOut = 0.25, Tail = 2, CrossIn = true, Reverse = true, GainDb = -3 },
            });
            doc.SetClip("Audio", "plain", new SessionDocument.ClipSettings { Audio = new SessionDocument.AudioSettings { Asset = "a1" } });
            var stream = new MemoryStream();
            doc.Save(stream);
            string xml = System.Text.Encoding.UTF8.GetString(stream.ToArray());
            Assert.That(xml, Does.Contain("version=\"2\""));
            var back = SessionDocument.Load(new MemoryStream(stream.ToArray()));
            var a = back.GetClip("Audio", "one").Audio;
            Assert.That((a.FadeIn, a.FadeOut, a.Tail, a.CrossIn, a.Reverse, a.GainDb), Is.EqualTo((1.5, 0.25, 2.0, true, true, -3.0)));
            var plain = back.GetClip("Audio", "plain").Audio;
            Assert.That((plain.FadeIn, plain.FadeOut, plain.Tail, plain.CrossIn, plain.Reverse), Is.EqualTo((0.0, 0.0, 0.0, false, false)));
            Assert.That(xml.Split("fadeIn").Length, Is.EqualTo(2), "written only where set");
            var clone = a.Clone();
            Assert.That((clone.FadeIn, clone.Tail, clone.CrossIn, clone.Reverse), Is.EqualTo((1.5, 2.0, true, true)));
            // A song written before NA2 (version 1, no such attributes) loads as it did.
            var old = XDocument.Parse(xml);
            old.Root!.SetAttributeValue("version", 1);
            foreach (var e in old.Descendants("Audio"))
                foreach (var name in new[] { "fadeIn", "fadeOut", "tail", "crossIn", "reverse" }) e.Attribute(name)?.Remove();
            var loaded = SessionDocument.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(old.ToString())));
            Assert.That(loaded.GetClip("Audio", "one").Audio.FadeIn, Is.EqualTo(0));
            Assert.That(loaded.GetClip("Audio", "one").Audio.GainDb, Is.EqualTo(-3));
        }

        [Test]
        public void NonFiniteFadesAreIgnoredByTheEngine()
        {
            var p = Clip(Dc(0.5, 4), new ClipFades(double.NaN, double.PositiveInfinity, -3, false));
            Assert.That(p.Fades.Active, Is.False.Or.True);
            var o = Play(new ClipPlayback { Params = p }, 2, 8192);
            Assert.That(o.All(float.IsFinite), Is.True);
        }
    }
}
