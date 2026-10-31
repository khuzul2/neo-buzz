using ReBuzz.Core.Clips;

namespace ReBuzz.SessionTests
{
    /// <summary>Audio clips (docs/specs/session-view.md 1.0), pure: warp maps, analysis, and the voice in each warp mode.</summary>
    [TestFixture]
    public class AudioClipTests
    {
        const int Rate = 44100;

        // ---- Sources -------------------------------------------------------------------------

        /// <summary>A click (a short 1 kHz burst) on each of <paramref name="beats"/> beats at <paramref name="bpm"/>, except where skipped.</summary>
        static AudioSource Clicks(double bpm, int beats, double freq = 1000, Func<int, bool>? on = null)
        {
            int perBeat = (int)Math.Round(Rate * 60 / bpm);
            var x = new float[perBeat * beats];
            for (int b = 0; b < beats; b++)
            {
                if (on != null && !on(b)) continue;
                for (int i = 0; i < Rate / 50 && b * perBeat + i < x.Length; i++)
                    x[b * perBeat + i] = (float)(0.8 * Math.Sin(2 * Math.PI * freq * i / Rate) * Math.Exp(-i / (0.005 * Rate)));
            }
            return new AudioSource(x, x, Rate);
        }

        static AudioSource Sine(double freq, double seconds, double amplitude = 0.5, int rate = Rate)
        {
            var x = new float[(int)(seconds * rate)];
            for (int i = 0; i < x.Length; i++) x[i] = (float)(amplitude * Math.Sin(2 * Math.PI * freq * i / rate));
            return new AudioSource(x, x, rate);
        }

        static AudioClipParams Clip(AudioSource s, double bpm, WarpMode mode, double semitones = 0)
        {
            var map = WarpMap.Straight(s.Length, s.Seconds * bpm / 60);
            return new AudioClipParams(s, map, mode, AudioAnalysis.Slices(s, map, AudioAnalysis.Onsets(s)), 1f, semitones);
        }

        /// <summary>Plays <paramref name="beats"/> beats from <paramref name="from"/> at a tempo, in blocks as the engine would.</summary>
        static float[] Play(ClipVoice voice, AudioClipParams p, double bpm, double from, double beats, int block = 256, int rate = Rate)
        {
            double bps = bpm / 60.0 / rate;
            int n = (int)Math.Round(beats / bps);
            var l = new float[n];
            var r = new float[n];
            for (int at = 0; at < n; at += block)
                voice.Render(p, from + at * bps, bps, rate, l, r, at, Math.Min(block, n - at));
            return l;
        }

        // ---- Measures ------------------------------------------------------------------------

        /// <summary>Where sound starts (goes over 0.05) with nothing as loud in the 20 ms before, in seconds.</summary>
        static List<double> SoundStarts(float[] x, int rate = Rate)
        {
            var starts = new List<double>();
            int loud = -rate;
            for (int i = 0; i < x.Length; i++)
            {
                if (Math.Abs(x[i]) <= 0.05f) continue;
                if (i - loud > rate / 50) starts.Add(i / (double)rate);
                loud = i;
            }
            return starts;
        }

        /// <summary>The frequency of what plays from <paramref name="from"/> for <paramref name="count"/> samples (rising zero crossings).</summary>
        static double Frequency(float[] x, int from, int count, int rate = Rate)
        {
            double first = double.NaN, last = double.NaN;
            int crossings = 0;
            for (int i = Math.Max(1, from); i < Math.Min(x.Length, from + count); i++)
            {
                if (x[i - 1] < 0 && x[i] >= 0)
                {
                    double t = i - 1 + x[i - 1] / (x[i - 1] - x[i]);
                    if (crossings == 0) first = t;
                    last = t;
                    crossings++;
                }
            }
            return crossings < 2 ? 0 : (crossings - 1) * rate / (last - first);
        }

        static double Rms(float[] x, int from, int count)
        {
            double sum = 0;
            for (int i = from; i < from + count; i++) sum += x[i] * (double)x[i];
            return Math.Sqrt(sum / count);
        }

        static double MaxStep(float[] x)
        {
            double max = 0;
            for (int i = 1; i < x.Length; i++) max = Math.Max(max, Math.Abs(x[i] - x[i - 1]));
            return max;
        }

        // ---- Warp maps and reading -----------------------------------------------------------

        [Test]
        public void AWarpMapMapsBeatsAndSamplesBothWays()
        {
            var m = new WarpMap(new[]
            {
                new WarpMarker(88200, 3), new WarpMarker(0, 0), new WarpMarker(44100, 2),
                new WarpMarker(30000, 2.5),                             // not after the one before it: dropped
            });
            Assert.That(m.Markers.Count, Is.EqualTo(3));
            Assert.That(m.SampleAt(1), Is.EqualTo(22050));
            Assert.That(m.SampleAt(2.5), Is.EqualTo(66150));
            Assert.That(m.BeatAt(66150), Is.EqualTo(2.5));
            Assert.That(m.SamplesPerBeat(0.5), Is.EqualTo(22050));
            Assert.That(m.SamplesPerBeat(2.5), Is.EqualTo(44100));
            Assert.That(m.SampleAt(4), Is.EqualTo(132300), "on past the last marker");
            Assert.That(m.SampleAt(-1), Is.EqualTo(-22050), "and before the first");
            Assert.That(m.Beats, Is.EqualTo(3));
            Assert.That(new WarpMap(null!).Markers.Count, Is.EqualTo(2), "always two");
        }

        [Test]
        public void ReadingBetweenSamplesIsSmoothAndSilentOutside()
        {
            var x = new float[] { 0, 1, 2, 3, 4, 5 };
            var s = new AudioSource(x, null!, Rate);
            s.Read(2, out float a, out _);
            s.Read(2.5, out float b, out float br);
            s.Read(-5, out float c, out _);
            s.Read(99, out float d, out _);
            Assert.That(a, Is.EqualTo(2f));
            Assert.That(b, Is.EqualTo(2.5f));
            Assert.That(br, Is.EqualTo(2.5f), "mono: the right is the left");
            Assert.That(c, Is.EqualTo(0f));
            Assert.That(d, Is.EqualTo(0f));
        }

        [Test]
        public void TheFftFindsASineAndGoesBack()
        {
            var fft = new Fft(256);
            var re = new double[256];
            var im = new double[256];
            for (int i = 0; i < 256; i++) re[i] = Math.Sin(2 * Math.PI * 8 * i / 256);
            var original = (double[])re.Clone();
            fft.Transform(re, im);
            int best = 0;
            for (int k = 1; k < 128; k++) if (re[k] * re[k] + im[k] * im[k] > re[best] * re[best] + im[best] * im[best]) best = k;
            Assert.That(best, Is.EqualTo(8));
            fft.Transform(re, im, inverse: true);
            double error = 0;
            for (int i = 0; i < 256; i++) error = Math.Max(error, Math.Abs(re[i] - original[i]));
            Assert.That(error, Is.LessThan(1e-9));
        }

        // ---- Analysis ------------------------------------------------------------------------

        [Test]
        public void OnsetsAreFoundAtEachClick()
        {
            var s = Clicks(100, 8);
            var onsets = AudioAnalysis.Onsets(s);
            int perBeat = Rate * 60 / 100;
            for (int b = 1; b < 8; b++)
                Assert.That(onsets.Any(o => Math.Abs(o - b * perBeat) <= 0.005 * Rate), Is.True, $"a click on beat {b}");
            Assert.That(onsets.All(o => Enumerable.Range(0, 8).Any(b => Math.Abs(o - b * perBeat) <= 0.005 * Rate)), Is.True, "nothing else");
        }

        [TestCase(100, 4)]          // a bar
        [TestCase(125, 8)]          // two
        [TestCase(90, 16)]          // four
        [TestCase(150, 8)]
        public void ALoopsTempoComesFromItsBars(double bpm, int beats)
        {
            var (tempo, length) = AudioAnalysis.Tempo(Clicks(bpm, beats));
            Assert.That(Math.Abs(tempo - bpm), Is.LessThan(0.05), $"{tempo} BPM");
            Assert.That(length, Is.EqualTo((double)beats));
        }

        [Test]
        public void AudioThatIsntBarsLongGetsItsTransientsTempo()
        {
            var s = Clicks(120, 15);                                    // 7.5 s: 3.75 bars
            var (tempo, _) = AudioAnalysis.Tempo(s);
            Assert.That(Math.Abs(tempo - 120), Is.LessThan(1.0), $"{tempo} BPM");
        }

        [Test]
        public void SlicesFillTheBeatGridBetweenFarTransients()
        {
            var s = Clicks(100, 4, on: b => b == 0 || b == 2);
            var map = WarpMap.Straight(s.Length, 4);
            var slices = AudioAnalysis.Slices(s, map, AudioAnalysis.Onsets(s));
            Assert.That(slices.Length, Is.EqualTo(4), string.Join(", ", slices));
            int perBeat = Rate * 60 / 100;
            for (int b = 0; b < 4; b++)
                Assert.That(slices.Any(x => Math.Abs(x - b * perBeat) <= 0.005 * Rate), Is.True, $"beat {b}");
        }

        // ---- Warp modes ----------------------------------------------------------------------

        [Test]
        public void RepitchPlaysFasterAndHigherWithTheTempo()
        {
            var p = Clip(Sine(440, 2.4), 100, WarpMode.Repitch);       // 4 beats at 100
            var x = Play(new ClipVoice(), p, 120, 0, 4);
            Assert.That(x.Length, Is.EqualTo(88200), "4 beats at 120: 2 s");
            Assert.That(Math.Abs(Frequency(x, 4410, 44100) - 528), Is.LessThan(2.0));
        }

        [Test]
        public void OffPlaysAtItsOwnSpeedWhateverTheTempo()
        {
            var p = Clip(Sine(440, 2.4), 100, WarpMode.Off);
            var x = Play(new ClipVoice(), p, 150, 0, 4);
            Assert.That(Math.Abs(Frequency(x, 4410, 44100) - 440), Is.LessThan(1.0));
            var up = Play(new ClipVoice(), Clip(Sine(440, 2.4), 100, WarpMode.Off, 12), 150, 0, 4);
            Assert.That(Math.Abs(Frequency(up, 4410, 22050) - 880), Is.LessThan(2.0), "transposed: faster");
        }

        /// <summary>The acceptance test: a loop at 100 BPM plays in time at 80-160 BPM in Beats mode, its pitch kept.</summary>
        [TestCase(80)]
        [TestCase(100)]
        [TestCase(120)]
        [TestCase(160)]
        public void BeatsKeepsALoopInTimeAndPitchFrom80To160(double bpm)
        {
            var p = Clip(Clicks(100, 8), 100, WarpMode.Beats);
            var x = Play(new ClipVoice(), p, bpm, 0, 8);
            var starts = SoundStarts(x);
            Assert.That(starts.Count, Is.EqualTo(8), string.Join(", ", starts.Select(t => t.ToString("0.0000"))));
            for (int b = 0; b < 8; b++)
                Assert.That(Math.Abs(starts[b] - b * 60 / bpm), Is.LessThan(0.005), $"beat {b} at {starts[b]:0.0000} s");
            int at = (int)(starts[3] * Rate);
            Assert.That(Math.Abs(Frequency(x, at, Rate / 100) - 1000), Is.LessThan(20.0), "pitch kept");
        }

        [Test]
        public void BeatsIsSilentPastTheAudiosEnd()
        {
            var p = Clip(Sine(440, 2.4), 100, WarpMode.Beats);
            var x = Play(new ClipVoice(), p, 100, 4.5, 1);
            Assert.That(x.Skip(ClipVoice.Fade).All(v => v == 0), Is.True);
        }

        [TestCase(80, 0)]
        [TestCase(160, 0)]
        [TestCase(100, 12)]
        [TestCase(100, -12)]
        public void ComplexKeepsPitchAtAnyTempoAndTransposes(double bpm, double semitones)
        {
            var p = Clip(Sine(440, 4.8), 100, WarpMode.Complex, semitones);   // 8 beats at 100
            var x = Play(new ClipVoice(), p, bpm, 0, 6);
            double expected = 440 * Math.Pow(2, semitones / 12);
            Assert.That(Math.Abs(Frequency(x, 8192, Rate / 2) - expected) / expected, Is.LessThan(0.01), "pitch");
            double rms = Rms(x, 8192, Rate / 2) / (0.5 / Math.Sqrt(2));
            Assert.That(rms, Is.InRange(0.8, 1.2), "level");
        }

        [Test]
        public void ComplexResamplesAudioAtAnotherRate()
        {
            var s = Sine(440, 4, rate: 48000);
            var map = WarpMap.Straight(s.Length, 8);                    // 120 BPM
            var p = new AudioClipParams(s, map, WarpMode.Complex, null!, 1f, 0);
            var x = Play(new ClipVoice(), p, 120, 0, 6);
            Assert.That(Math.Abs(Frequency(x, 8192, Rate / 2) - 440), Is.LessThan(4.4));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void BlocksOfAnySizePlayTheSame(int which)
        {
            var mode = (WarpMode)which;
            var p = Clip(Clicks(100, 8, freq: 600), 100, mode, mode == WarpMode.Complex ? 3 : 0);
            var whole = Play(new ClipVoice(), p, 133, 0.25, 6, block: 1 << 30);
            var blocks = Play(new ClipVoice(), p, 133, 0.25, 6, block: 97);
            double error = 0;
            for (int i = 0; i < whole.Length; i++) error = Math.Max(error, Math.Abs(whole[i] - blocks[i]));
            Assert.That(error, Is.LessThan(1e-4));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void ALoopGoingRoundDoesntClick(int which)
        {
            var mode = (WarpMode)which;
            var p = Clip(Sine(300, 2), 120, mode);   // 4 beats at 120
            var voice = new ClipVoice();
            double bps = 120 / 60.0 / Rate;
            var l = new float[Rate * 2];
            var r = new float[Rate * 2];
            // Beats 0 to 1.37, round to 0 (mid block), and on: each block's beat as the engine gives it.
            double beat = 0;
            for (int at = 0; at < l.Length; at += 128)
            {
                int n = Math.Min(128, l.Length - at);
                if (beat >= 1.37) beat -= 1.37;
                voice.Render(p, beat, bps, Rate, l, r, at, n);
                beat += n * bps;
            }
            // A sine of 300 Hz at 0.5 moves at most 0.0214 a sample; a cut would jump up to 1.
            Assert.That(MaxStep(l.Skip(8192).ToArray()), Is.LessThan(0.05));
        }

        [Test]
        public void StoppingFadesOut()
        {
            var p = Clip(Sine(300, 2), 120, WarpMode.Repitch);
            var voice = new ClipVoice();
            Play(voice, p, 120, 0, 1);
            Assert.That(voice.IsSounding, Is.True);
            var l = new float[256];
            var r = new float[256];
            voice.Stop(l, r, 0, 256);
            Assert.That(voice.IsSounding, Is.False);
            Assert.That(MaxStep(l), Is.LessThan(0.05));
            Assert.That(l.Skip(ClipVoice.Fade).All(v => v == 0), Is.True);
            Assert.That(Math.Abs(l[0]), Is.LessThan(0.5), "from where it was");
        }

        [Test]
        public void AMixerFadesOutWhatStopsPlaying()
        {
            var a = new ClipPlayback { Params = Clip(Sine(300, 2), 120, WarpMode.Repitch) };
            var b = new ClipPlayback { Params = Clip(Sine(500, 2), 120, WarpMode.Repitch) };
            var mixer = new ClipMixer();
            double bps = 2.0 / Rate;
            mixer.Begin(256);
            mixer.Play(a, 0, bps, Rate, 256);
            mixer.Play(b, 0, bps, Rate, 256);
            mixer.Play(a, 0, bps, Rate, 256);                          // once a block
            Assert.That(mixer.End(256), Is.True);
            Assert.That(mixer.Sounding, Is.EqualTo(2));
            mixer.Begin(256);
            mixer.Play(b, 256 * bps, bps, Rate, 256);                   // a stopped
            Assert.That(mixer.End(256), Is.True);
            Assert.That(mixer.Sounding, Is.EqualTo(1), "a has faded out within the block");
            Assert.That(a.Voice.IsSounding, Is.False);
            mixer.Begin(256);
            Assert.That(mixer.End(256), Is.True, "b fades out");
            mixer.Begin(256);
            Assert.That(mixer.End(256), Is.False, "then nothing");
            Assert.That(mixer.Left.Take(256).All(v => v == 0), Is.True);
            b.Params = null;
            mixer.Begin(256);
            mixer.Play(b, 0, bps, Rate, 256);
            Assert.That(mixer.End(256), Is.False, "no audio: nothing plays");
        }

        [Test]
        public void AudioSettingsGoThroughXml()
        {
            var a = new ReBuzz.Core.Session.SessionDocument.AudioSettings
            {
                Wave = 7, Bpm = 123.45, Warp = "Complex", Preserve = "1/8", GainDb = -3.5, Transpose = -5, Detune = 12,
                Markers = new List<(double, double)> { (0, 0), (22050.5, 1.25) },
            };
            var c = new ReBuzz.Core.Session.SessionDocument.ClipSettings { Audio = a };
            Assert.That(c.IsDefault, Is.False, "an audio clip's settings are kept");
            var again = ReBuzz.Core.Session.SessionDocument.AudioSettings.FromXml(a.ToXml())!;
            Assert.That(again.ToXml().ToString(), Is.EqualTo(a.ToXml().ToString()));
            Assert.That(again.Markers[1], Is.EqualTo((22050.5, 1.25)));
            Assert.That(again.Semitones, Is.EqualTo(-4.88));
            Assert.That(again.Grid, Is.EqualTo(0.5));
            Assert.That(again.PreservesTransients, Is.False);
            var clone = c.Clone();
            clone.Audio!.Markers.Add((44100, 2));
            Assert.That(a.Markers.Count, Is.EqualTo(2), "a clone's markers are its own");
            var odd = ReBuzz.Core.Session.SessionDocument.AudioSettings.FromXml(System.Xml.Linq.XElement.Parse(
                "<Audio wave='-3' bpm='9999' warp='Stretchy' preserve='?' gain='x' transpose='99' detune='-77'><Marker sample='1' beat='NaN'/></Audio>"))!;
            Assert.That((odd.Wave, odd.Bpm, odd.Warp, odd.Preserve, odd.GainDb, odd.Transpose, odd.Detune, odd.Markers.Count),
                Is.EqualTo((0, 999.0, "Beats", "Transients", 0.0, 48, -50, 0)));
        }

        [Test]
        public void PlayingAllocatesNothing()
        {
            var modes = new[] { WarpMode.Off, WarpMode.Repitch, WarpMode.Beats, WarpMode.Complex };
            var clips = modes.Select(m => Clip(Clicks(100, 8), 100, m, 2)).ToArray();
            var voice = new ClipVoice();
            var l = new float[256];
            var r = new float[256];
            double bps = 130 / 60.0 / Rate;
            void Run(int blocks)
            {
                foreach (var p in clips)
                    for (int b = 0; b < blocks; b++) voice.Render(p, b * 256 * bps, bps, Rate, l, r, 0, 256);
                voice.Stop(l, r, 0, 256);
            }
            Run(20);
            long before = GC.GetAllocatedBytesForCurrentThread();
            Run(200);
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.EqualTo(0L));
        }
    }
}
