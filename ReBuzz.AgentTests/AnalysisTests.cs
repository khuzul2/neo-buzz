using NeoBuzz.Analysis;
using static ReBuzz.AgentTests.Signals;

namespace ReBuzz.AgentTests
{
    /// <summary>
    /// The analysis library (docs/specs/mcp-server.md §7) against signals with known answers:
    /// spectrum, dynamics, stereo, masking, pitch, key, tempo. The tolerances asserted here are
    /// the ones the spec's table quotes.
    /// </summary>
    [TestFixture]
    public class AnalysisTests
    {
        // ---- Spectrum ----------------------------------------------------------------------

        [Test]
        public void PinkNoiseReadsFlatInThirdOctaves()
        {
            var pink = PeriodicNoise(-1, 0.1, 4.5);
            var s = Spectrum.Measure(pink, pink, Rate);
            Assert.That(s.Psd.Size, Is.EqualTo(32768), "the default Welch size at 48 kHz");
            double mean = s.ThirdOctaveDb.Average();
            double worst = s.ThirdOctaveDb.Max(d => Math.Abs(d - mean));
            TestContext.Out.WriteLine($"pink: third octaves within ±{worst:F3} dB of {mean:F2}; tilt {s.TiltDbPerOctave:F3} dB/oct");
            Assert.That(s.ThirdOctaveDb.Length, Is.EqualTo(31), "20 Hz – 20 kHz");
            Assert.That(worst, Is.LessThan(0.5), "every third octave, 20 Hz to 20 kHz, within ±0.5 dB");
            Assert.That(s.TiltDbPerOctave, Is.EqualTo(0).Within(0.05));
            // One scale: the named bands (20 Hz – 20 kHz end to end) add up to the total.
            double sum = 10 * Math.Log10(s.NamedDb.Sum(d => Math.Pow(10, d / 10)));
            Assert.That(sum, Is.EqualTo(s.TotalDb).Within(0.01));

            var white = PeriodicNoise(0, 0.1, 4.5);
            var w = Spectrum.Measure(white, null, Rate);
            TestContext.Out.WriteLine($"white: tilt {w.TiltDbPerOctave:F3} dB/oct, flatness {w.Flatness:F3}; pink flatness {s.Flatness:F3}");
            Assert.That(w.TiltDbPerOctave, Is.EqualTo(10 * Math.Log10(2)).Within(0.05), "white noise rises 3 dB per octave in proportional bands");
            Assert.That(w.TotalDb, Is.EqualTo(-20 + 10 * Math.Log10((20000 - 20) / 24000.0)).Within(0.02), "RMS 0.1 is -20 dB, spread evenly to 24 kHz");
            Assert.That(w.Flatness, Is.GreaterThan(0.95));
            Assert.That(s.Flatness, Is.LessThan(w.Flatness));
        }

        [Test]
        public void RandomPinkNoiseReadsNearlyFlat()
        {
            // Ordinary (random) pink noise: estimation noise grows in the narrow low bands, so above 100 Hz only.
            var white = Noise(0.1, 4, seed: 7);
            // Paul Kellet's pink filter.
            double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
            var pink = new float[white.Length];
            for (int i = 0; i < white.Length; i++)
            {
                double x = white[i];
                b0 = 0.99886 * b0 + x * 0.0555179; b1 = 0.99332 * b1 + x * 0.0750759; b2 = 0.96900 * b2 + x * 0.1538520;
                b3 = 0.86650 * b3 + x * 0.3104856; b4 = 0.55000 * b4 + x * 0.5329522; b5 = -0.7616 * b5 - x * 0.0168980;
                pink[i] = (float)(b0 + b1 + b2 + b3 + b4 + b5 + b6 + x * 0.5362);
                b6 = x * 0.115926;
            }
            var s = Spectrum.Measure(pink, null, Rate);
            var bands = Enumerable.Range(0, 31).Where(i => Bands.ThirdOctaveNominal[i] >= 100 && Bands.ThirdOctaveNominal[i] <= 10000).Select(i => s.ThirdOctaveDb[i]).ToArray();
            double mean = bands.Average(), worst = bands.Max(d => Math.Abs(d - mean));
            TestContext.Out.WriteLine($"random pink: 100 Hz – 10 kHz within ±{worst:F2} dB; tilt {s.TiltDbPerOctave:F2}");
            Assert.That(worst, Is.LessThan(1.0));
            Assert.That(s.TiltDbPerOctave, Is.EqualTo(0).Within(0.3));
        }

        [TestCase(1000.0, 0.5)]
        [TestCase(97.0, 0.1)]
        [TestCase(12345.0, 0.05)]
        public void ASineLandsInItsBin(double hz, double amplitude)
        {
            var x = Sine(hz, amplitude, 2);
            var s = Spectrum.Measure(x, x, Rate);
            var p = s.Psd.Mean;
            int peak = Array.IndexOf(p, p.Max());
            Assert.That(peak, Is.EqualTo((int)Math.Round(hz / s.Psd.BinHz)), "the peak bin");
            double rmsDb = 20 * Math.Log10(amplitude) - 10 * Math.Log10(2);
            int band = Enumerable.Range(0, 31).First(i => Bands.ThirdOctaveEdges(i).Lo <= hz && hz < Bands.ThirdOctaveEdges(i).Hi);
            int named = Array.FindIndex(Bands.Named, b => b.Lo <= hz && hz < b.Hi);
            TestContext.Out.WriteLine($"{hz} Hz: band {Bands.ThirdOctaveNominal[band]} reads {s.ThirdOctaveDb[band]:F3} (expected {rmsDb:F3}); " +
                                      $"centroid {s.CentroidHz:F1}, roll-off {s.RolloffHz:F1}, flatness {s.Flatness:G3}");
            Assert.That(s.ThirdOctaveDb[band], Is.EqualTo(rmsDb).Within(0.05), "the sine's own third octave holds its RMS level");
            Assert.That(s.NamedDb[named], Is.EqualTo(rmsDb).Within(0.05), Bands.Named[named].Name);
            Assert.That(s.TotalDb, Is.EqualTo(rmsDb).Within(0.05));
            for (int i = 0; i < 31; i++)
                if (Math.Abs(i - band) >= 2) Assert.That(s.ThirdOctaveDb[i], Is.LessThan(rmsDb - 40), $"band {Bands.ThirdOctaveNominal[i]} stays empty");
            Assert.That(s.CentroidHz, Is.EqualTo(hz).Within(hz * 0.01 + 2));
            Assert.That(s.RolloffHz, Is.EqualTo(hz).Within(2 * s.Psd.BinHz));
            Assert.That(s.Flatness, Is.LessThan(0.01));
        }

        // ---- Dynamics ----------------------------------------------------------------------

        [Test]
        public void CrestFactorAndPlrOfKnownSignals()
        {
            var sine = Sine(1000, 0.1, 5);
            var d = Dynamics.Measure(sine, sine, Rate);
            TestContext.Out.WriteLine($"sine: crest {d.CrestDb:F3}, PLR {d.PlrDb:F3}, RMS {d.RmsDb:F3}");
            Assert.That(d.CrestDb, Is.EqualTo(3.01).Within(0.02), "a sine's peak is √2 times its RMS");
            Assert.That(d.RmsDb, Is.EqualTo(-23.01).Within(0.02));
            Assert.That(d.PlrDb, Is.EqualTo(0).Within(0.2), "a 1 kHz stereo sine reads its peak level in LUFS");
            Assert.That(d.EnvelopeP95 - d.EnvelopeP10, Is.LessThan(0.1), "a steady sine has a flat RMS envelope");

            var square = sine.Select(v => v >= 0 ? 0.5f : -0.5f).ToArray();
            Assert.That(Dynamics.Measure(square, null, Rate).CrestDb, Is.EqualTo(0).Within(0.01));

            var noise = Noise(0.05, 5, seed: 3);
            var n = Dynamics.Measure(noise, null, Rate);
            TestContext.Out.WriteLine($"noise: crest {n.CrestDb:F2}");
            Assert.That(n.CrestDb, Is.InRange(11.0, 15.0), "Gaussian noise peaks 4–5 sigma over a few seconds");

            // Loud and quiet halves: the envelope's spread shows it.
            var steps = Mix(Place(Sine(500, 0.5, 2), 0, 4), Place(Sine(500, 0.05, 2), 2, 4));
            var e = Dynamics.Measure(steps, steps, Rate);
            Assert.That(e.EnvelopeP95 - e.EnvelopeP10, Is.EqualTo(20).Within(0.5));
            Assert.That(e.Envelope.Length, Is.EqualTo(80), "50 ms windows");
        }

        [Test]
        public void TransientDensityCountsHits()
        {
            var clicks = Clicks(120, 16, offset: 0.1);
            var d = Dynamics.Measure(clicks, clicks, Rate);
            double seconds = clicks.Length / (double)Rate;
            TestContext.Out.WriteLine($"clicks: {d.Onsets.Count} onsets, {d.TransientsPerSecond:F2}/s, crest {d.CrestDb:F1}");
            Assert.That(d.Onsets.Count, Is.EqualTo(16));
            Assert.That(d.TransientsPerSecond, Is.EqualTo(16 / seconds).Within(0.01));
            Assert.That(d.CrestDb, Is.GreaterThan(15), "short clicks in silence");
            Assert.That(Dynamics.Measure(Sine(300, 0.3, 3), null, Rate).TransientsPerSecond, Is.EqualTo(0));
        }

        // ---- Stereo ------------------------------------------------------------------------

        [Test]
        public void StereoOfMonoAntiPhaseAndUnrelatedNoise()
        {
            var a = Noise(0.1, 4, seed: 1);
            var mono = Stereo.Measure(a, (float[])a.Clone(), Rate);
            Assert.That(mono.Mono, Is.True);
            Assert.That(mono.Correlation, Is.EqualTo(1));
            Assert.That(mono.Width, Is.EqualTo(0));
            Assert.That(mono.MonoLossDb, Is.EqualTo(0));

            var half = Stereo.Measure(a, Scale(a, 0.5), Rate);
            Assert.That(half.Mono, Is.False);
            Assert.That(half.Correlation, Is.EqualTo(1).Within(1e-6), "a level difference is still in phase");
            Assert.That(half.Width, Is.EqualTo(1 / 9.0).Within(1e-4), "side/mid = (1 - 0.5)² / (1 + 0.5)²");
            Assert.That(half.BalanceDb, Is.EqualTo(6.02).Within(0.01));

            var anti = Stereo.Measure(a, Scale(a, -1), Rate);
            Assert.That(anti.Correlation, Is.EqualTo(-1).Within(1e-6));
            Assert.That(double.IsPositiveInfinity(anti.Width), Is.True, "no mid at all");
            Assert.That(double.IsNegativeInfinity(anti.MonoLossDb), Is.True, "cancels in mono");
            Assert.That(anti.NegativeShare, Is.EqualTo(1));
            Assert.That(anti.Bands.All(b => b.Correlation < -0.999), Is.True, "every band");

            var b2 = Noise(0.1, 4, seed: 2);
            var wide = Stereo.Measure(a, b2, Rate);
            TestContext.Out.WriteLine($"unrelated: corr {wide.Correlation:F4}, width {wide.Width:F4}, mono loss {wide.MonoLossDb:F3}; bands " +
                                      string.Join(" ", wide.Bands.Select(b => $"{b.Name} {b.Width:F2}/{b.Correlation:F2}")));
            Assert.That(wide.Correlation, Is.EqualTo(0).Within(0.02));
            Assert.That(wide.Width, Is.EqualTo(1).Within(0.05));
            Assert.That(wide.MonoLossDb, Is.EqualTo(-3.01).Within(0.1));
            foreach (var band in wide.Bands)
            {
                Assert.That(band.Width, Is.EqualTo(1).Within(0.3), band.Name);
                Assert.That(band.MonoLossDb, Is.EqualTo(-3.01).Within(0.6), band.Name);
            }
        }

        [Test]
        public void WidthPerBandFindsAnOutOfPhaseLowEnd()
        {
            // Centred mids (the same 1 kHz in both) over a 45 Hz sub that is inverted in the right channel.
            var mid = Sine(1000, 0.2, 3);
            var sub = Sine(45, 0.3, 3);
            var l = Mix(mid, sub);
            var r = Mix(mid, Scale(sub, -1));
            var s = Stereo.Measure(l, r, Rate);
            var subBand = s.Bands.Single(b => b.Name == "sub");
            var midBand = s.Bands.Single(b => b.Name == "mid");
            TestContext.Out.WriteLine($"sub width {subBand.Width:F1}, loss {subBand.MonoLossDb:F1}; mid width {midBand.Width:F4}");
            Assert.That(subBand.Correlation, Is.LessThan(-0.99));
            Assert.That(subBand.MonoLossDb, Is.LessThan(-30));
            Assert.That(midBand.Width, Is.LessThan(0.001));
            Assert.That(midBand.Correlation, Is.GreaterThan(0.999));
        }

        // ---- Masking -----------------------------------------------------------------------

        [Test]
        public void ConstructedOverlapsMask()
        {
            var bass = Sine(55, 0.25, 4);
            var kick = Kick(4, 0.5, 0.5);
            var hat = Mix(Sine(8000, 0.05, 4), Sine(11000, 0.05, 4));
            var tracks = new[] { BandFrames.Analyze("Bass", bass, bass, Rate), BandFrames.Analyze("Kick", kick, kick, Rate), BandFrames.Analyze("Hat", hat, hat, Rate) };
            var table = Masking.Table(tracks);
            foreach (var p in table)
                TestContext.Out.WriteLine($"{p.A}/{p.B}: {p.Severity:F2} " + string.Join(", ", p.Bands.Select(b => $"{b.LoHz}-{b.HiHz} Hz {b.Severity:F2} ({b.Overlap:P0}, {b.LevelA:F1}/{b.LevelB:F1} dB)")));
            Assert.That(table.Count, Is.EqualTo(1), "only the kick and the bass fight");
            var pair = table[0];
            Assert.That(new[] { pair.A, pair.B }, Is.EquivalentTo(new[] { "Bass", "Kick" }));
            Assert.That(pair.Bands[0].LoHz, Is.EqualTo(20), "in the lowest critical band");
            Assert.That(pair.Severity, Is.GreaterThan(0.35), "a strong fight: every kick decays through the bass's level");

            // Twenty dB apart: the kick is under the bass, not fighting it.
            var quiet = BandFrames.Analyze("Kick", Scale(kick, 0.1), null, Rate);
            Assert.That(Masking.Pair(tracks[0], quiet).Bands, Is.Empty);
            // One after the other: no time overlap.
            var first = BandFrames.Analyze("Bass", Place(Sine(55, 0.25, 2), 0, 4), null, Rate);
            var second = BandFrames.Analyze("Kick", Place(Kick(2, 0.5, 0.5), 2, 4), null, Rate);
            Assert.That(Masking.Pair(first, second).Bands, Is.Empty);
            // Half the kicks play over the bass: severity follows the overlap.
            var halfBass = BandFrames.Analyze("Bass", Place(Sine(55, 0.25, 2), 0, 4), null, Rate);
            var halfPair = Masking.Pair(halfBass, tracks[1]);
            TestContext.Out.WriteLine($"half: {halfPair.Severity:F2}, overlap {halfPair.Bands[0].Overlap:F2}");
            Assert.That(halfPair.Severity, Is.EqualTo(pair.Severity / 2).Within(0.15));
        }

        // ---- Pitch -------------------------------------------------------------------------

        [TestCase(33, 0.0)]     // A1, 55 Hz
        [TestCase(40, 13.0)]
        [TestCase(57, -7.0)]
        [TestCase(69, 0.0)]     // A4, 440 Hz
        [TestCase(76, 21.0)]
        [TestCase(88, -3.0)]
        [TestCase(93, 9.0)]     // A6, 1760 Hz
        public void YinFindsSynthNotesWithinCents(int midi, double detune)
        {
            var x = new float[(int)(0.6 * Rate)];
            AddNote(x, midi, 0.05, 0.5, detuneCents: detune);
            var track = Yin.Track(x, Rate);
            var voiced = track.Frames.Where(f => f.Voiced).ToList();
            double worst = voiced.Max(f => Math.Abs(f.Midi - (midi + detune / 100))) * 100;
            var note = track.Notes.Single();
            TestContext.Out.WriteLine($"midi {midi} {detune:+0;-0} c: note {note.Midi} {note.Cents:F2} c, worst frame {worst:F2} c, confidence {note.Confidence:F3}, {voiced.Count} frames");
            Assert.That(note.Midi, Is.EqualTo(midi));
            Assert.That(note.Cents, Is.EqualTo(detune).Within(1), "the note's cents from equal temperament");
            Assert.That(worst, Is.LessThan(5), "every voiced frame within 5 cents");
            // Frames are stamped at their window's centre; the window is 50 ms (two periods of 40 Hz).
            Assert.That(note.Start, Is.EqualTo(0.05).Within(0.035));
            Assert.That(note.End, Is.EqualTo(0.55).Within(0.035));
            Assert.That(note.Confidence, Is.GreaterThan(0.9));
        }

        [Test]
        public void YinSplitsAMelodyIntoNotes()
        {
            int[] melody = { 60, 62, 64, 65, 67, 67, 72 };
            var x = new float[(int)(0.3 * melody.Length * Rate)];
            for (int i = 0; i < melody.Length; i++) AddNote(x, melody[i], 0.3 * i, 0.27);
            var mono = x;
            var track = Yin.Track(mono, Rate, onsets: Tempo.Onsets(mono, Rate));
            TestContext.Out.WriteLine(string.Join(" ", track.Notes.Select(n => $"{n.Midi}@{n.Start:F2}")));
            Assert.That(track.Notes.Select(n => n.Midi), Is.EqualTo(melody), "repeated G4s split at their onset");
            Assert.That(Yin.Track(new float[Rate], Rate).Notes, Is.Empty, "silence");
            Assert.That(Yin.Track(Noise(0.1, 1, seed: 9), Rate).VoicedShare, Is.LessThan(0.1), "noise has no pitch");
        }

        // ---- Key ---------------------------------------------------------------------------

        static float[] Play(params (int[] Pitches, double Beats)[] chords)
        {
            const double Beat = 0.5;
            var x = new float[(int)(chords.Sum(c => c.Beats) * Beat * Rate) + Rate / 10];
            double t = 0;
            foreach (var (pitches, beats) in chords)
            {
                foreach (int p in pitches) AddNote(x, p, t, beats * Beat * 0.95, amplitude: 0.15, maxHarmonics: 12);
                t += beats * Beat;
            }
            return x;
        }

        static (int[], double) N(int p, double beats = 1) => (new[] { p }, beats);

        [Test]
        public void KeyOfScalesAndProgressions()
        {
            // C major scale up and back, C4 … C5.
            int[] cMajor = { 60, 62, 64, 65, 67, 69, 71, 72, 71, 69, 67, 65, 64, 62, 60 };
            var c = KeyFinder.Measure(Play(cMajor.Select(p => N(p)).ToArray()), null, Rate);
            // A natural minor scale, then an A minor triad held.
            int[] aMinor = { 57, 59, 60, 62, 64, 65, 67, 69, 67, 65, 64, 62, 60, 59, 57 };
            var a = KeyFinder.Measure(Play(aMinor.Select(p => N(p)).Append((new[] { 57, 60, 64 }, 4.0)).ToArray()), null, Rate);
            // F# major: I – IV – V – I triads.
            var fs = KeyFinder.Measure(Play((new[] { 54, 58, 61 }, 2), (new[] { 59, 63, 66 }, 2), (new[] { 61, 65, 68 }, 2), (new[] { 54, 58, 61 }, 4)), null, Rate);
            // E minor: i – iv – V – i (harmonic minor's D#).
            var em = KeyFinder.Measure(Play((new[] { 52, 55, 59 }, 2), (new[] { 57, 60, 64 }, 2), (new[] { 59, 63, 66 }, 2), (new[] { 52, 55, 59 }, 4)), null, Rate);
            // Bb major: I – vi – ii – V – I.
            var bb = KeyFinder.Measure(Play((new[] { 58, 62, 65 }, 2), (new[] { 55, 58, 62 }, 2), (new[] { 60, 63, 67 }, 2), (new[] { 53, 57, 60 }, 2), (new[] { 58, 62, 65 }, 4)), null, Rate);
            // A lone C major triad of pure tones. (A lone triad of bright tones can read as its
            // mediant's minor key: the third's overtones add the major seventh.)
            var triad = new float[2 * Rate];
            foreach (int p in new[] { 60, 64, 67 }) AddNote(triad, p, 0, 2, amplitude: 0.15, maxHarmonics: 1);
            var ct = KeyFinder.Measure(triad, null, Rate);
            foreach (var k in new[] { c, a, fs, em, bb, ct })
                TestContext.Out.WriteLine($"{k.Name} {k.Correlation:F3}, runner-up {k.RunnerUp} {k.RunnerUpCorrelation:F3}");
            Assert.That(c.Name, Is.EqualTo("C major"));
            Assert.That(a.Name, Is.EqualTo("A minor"));
            Assert.That(fs.Name, Is.EqualTo("F# major"));
            Assert.That(em.Name, Is.EqualTo("E minor"));
            Assert.That(bb.Name, Is.EqualTo("Bb major"));
            Assert.That(ct.Name, Is.EqualTo("C major"));
            Assert.That(c.Correlation, Is.GreaterThan(0.7));
            Assert.That(c.RunnerUpCorrelation, Is.LessThan(c.Correlation));
            Assert.That(c.Ranking.Length, Is.EqualTo(24));
        }

        // ---- Tempo and onsets --------------------------------------------------------------

        [TestCase(90.0)]
        [TestCase(120.0)]
        [TestCase(128.0)]
        public void TempoOfClickTracks(double bpm)
        {
            var x = Clicks(bpm, 16, offset: 0.1);
            var t = Tempo.Measure(x, Rate);
            TestContext.Out.WriteLine($"{bpm}: {t.Bpm:F4} BPM, confidence {t.Confidence:F2}, grid {t.Grid} ({t.OnGrid} onsets), first beat {t.FirstBeat:F4}");
            Assert.That(t.Bpm, Is.EqualTo(bpm).Within(0.05));
            Assert.That(t.Onsets.Count, Is.EqualTo(16));
            double period = 60 / bpm;
            for (int b = 0; b < 16; b++)
                Assert.That(t.Onsets[b], Is.EqualTo(0.1 + b * period).Within(0.005), $"onset {b}");
            Assert.That(t.Grid, Is.True);
            Assert.That(t.FirstBeat, Is.EqualTo(0.1).Within(0.005));
        }

        [Test]
        public void TempoOfABeatWithOffbeatHats()
        {
            // Kicks on the beat at 124 BPM, quieter hats on the eighths between: still 124, not 248 or 62.
            var kicks = Clicks(124, 16, offset: 0.05);
            int perBeat = (int)Math.Round(Rate * 60 / 124.0);
            var hats = Place(Scale(Clicks(124, 16), 0.3), 0.05 + perBeat / 2.0 / Rate, kicks.Length / (double)Rate);
            var t = Tempo.Measure(Mix(kicks, hats), Rate);
            TestContext.Out.WriteLine($"beat: {t.Bpm:F3}");
            Assert.That(t.Bpm, Is.EqualTo(124).Within(0.1));
            Assert.That(Tempo.Measure(Sine(220, 0.3, 4), Rate).Onsets, Is.Empty);
        }
    }
}
