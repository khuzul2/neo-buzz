using ReBuzz.Core.Clips;

namespace ReBuzz.SessionTests
{
    /// <summary>
    /// Warp quality against an objective reference (docs/specs/neo-audio.md section 11, WP1). The generators of NeoStretchTests take the
    /// tempo and the pitch, so a drum loop, a bass line or a pad made at the target tempo (or pitch) is the ideal render of a change.
    /// <see cref="WriteWp1Renders"/> writes every warp mode and the ideal for the nine blind-test groups (and for real excerpts, from outside
    /// the repository) as float WAVs; tools/warp_quality.py measures them (log-spectral distance, transient smearing, clicks).
    /// </summary>
    [TestFixture, NonParallelizable]
    public class WarpQualityTests
    {
        const int Rate = 44100;
        const double Bpm = 120;

        /// <summary>The nine blind-test groups: excerpt, change, the tempo factor, the semitones.</summary>
        internal static readonly (string Name, string Excerpt, double Tempo, double Semis)[] Groups =
        {
            ("g01-drums-20-slower", "drums", 0.8, 0), ("g02-drums-25-faster", "drums", 1.25, 0), ("g03-drums-7-up", "drums", 1.0, 7),
            ("g04-bass-20-slower", "bass", 0.8, 0), ("g05-bass-25-faster", "bass", 1.25, 0), ("g06-bass-7-up", "bass", 1.0, 7),
            ("g07-pad-20-slower", "pad", 0.8, 0), ("g08-pad-25-faster", "pad", 1.25, 0), ("g09-pad-7-up", "pad", 1.0, 7),
        };

        internal static AudioSource Excerpt(string name, double bpm, double pitch) => name switch
        {
            "drums" => NeoStretchTests.Drums(bpm, 8, Rate, pitch),
            "bass" => NeoStretchTests.Bass(bpm, 8, pitch),
            _ => NeoStretchTests.Pad(bpm, 8, pitch),
        };

        /// <summary>
        /// Writes, per group, <c>group--mode.wav</c> (32-bit float, the left channel, at the engine's own level) for the modes original (the
        /// source), ideal (generated at the target tempo and pitch), repitch, beats, complex and pro; and the same for the real excerpts
        /// <c>realbass.wav</c> and <c>realdrums.wav</c> in WP1_REAL_DIR (8 s, 44.1 kHz, 32-bit float, any channels), at 20 % slower and 25 %
        /// faster, which have no ideal. Set WP1_RENDER_DIR. Run by hand; the real audio is never in the repository.
        /// </summary>
        [Test, Explicit("writes the WP1 renders; set WP1_RENDER_DIR (and WP1_REAL_DIR)")]
        public void WriteWp1Renders()
        {
            string dir = Environment.GetEnvironmentVariable("WP1_RENDER_DIR") ?? throw new InvalidOperationException("WP1_RENDER_DIR");
            Directory.CreateDirectory(dir);
            var modes = new[] { WarpMode.Repitch, WarpMode.Beats, WarpMode.Complex, WarpMode.Pro };
            foreach (var (name, excerpt, tempo, semis) in Groups)
            {
                var source = Excerpt(excerpt, Bpm, 1);
                Write(dir, name, "original", source.Left, source.SampleRate);
                var ideal = Excerpt(excerpt, Bpm * tempo, Math.Pow(2, semis / 12));
                Write(dir, name, "ideal", ideal.Left, ideal.SampleRate);
                foreach (var m in modes)
                    Write(dir, name, m.ToString().ToLowerInvariant(), NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, m, semis), Bpm * tempo, 0, 8), Rate);                Write(dir, name, "pro-old", RenderOld(source, Bpm, Bpm * tempo, semis, 8, WarpMode.Pro), Rate);
                Write(dir, name, "complex-old", RenderOld(source, Bpm, Bpm * tempo, semis, 8, WarpMode.Complex), Rate);
            }
            string? real = Environment.GetEnvironmentVariable("WP1_REAL_DIR");
            if (real == null) return;
            int n = 10;
            foreach (var (file, tag) in new[] { ("realbass", "realbass"), ("realdrums", "realdrums") })
            {
                var path = Path.Combine(real, file + ".wav");
                if (!File.Exists(path)) continue;
                var (l, r, rate) = ReadWav(path);
                var source = new AudioSource(l, r, rate);
                double beats = source.Seconds * Bpm / 60;
                foreach (var (label, tempo) in new[] { ("20-slower", 0.8), ("25-faster", 1.25) })
                {
                    string name = $"g{n++}-{tag}-{label}";
                    Write(dir, name, "original", l, rate);
                    foreach (var m in modes)
                        Write(dir, name, m.ToString().ToLowerInvariant(), NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, m, 0), Bpm * tempo, 0, beats, 256, rate), rate);
                    PeriodicVoice.Enabled = false;
                    try
                    {
                        Write(dir, name, "pro-old", NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, WarpMode.Pro, 0), Bpm * tempo, 0, beats, 256, rate), rate);
                        Write(dir, name, "complex-old", NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, WarpMode.Complex, 0), Bpm * tempo, 0, beats, 256, rate), rate);
                    }
                    finally { PeriodicVoice.Enabled = true; }
                }
            }
        }

        /// <summary>Pro as it was before the time-domain path (the stretcher alone).</summary>
        internal static float[] RenderOld(AudioSource source, double sourceBpm, double bpm, double semis, double beats, WarpMode mode = WarpMode.Pro)
        {
            PeriodicVoice.Enabled = false;
            try { return NeoStretchTests.Play(NeoStretchTests.Clip(source, sourceBpm, mode, semis), bpm, 0, beats); }
            finally { PeriodicVoice.Enabled = true; }
        }

        static void Write(string dir, string group, string mode, float[] x, int rate)
        {
            using var w = new BinaryWriter(File.Create(Path.Combine(dir, $"{group}--{mode}.wav")));
            int bytes = x.Length * 4;
            w.Write("RIFF"u8.ToArray()); w.Write(36 + bytes); w.Write("WAVEfmt "u8.ToArray());
            w.Write(16); w.Write((short)3); w.Write((short)1); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)32);
            w.Write("data"u8.ToArray()); w.Write(bytes);
            foreach (float v in x) w.Write(v);
        }

        /// <summary>A 16-bit or 32-bit float PCM WAV, any channel count (the first two are kept).</summary>
        static (float[] L, float[] R, int Rate) ReadWav(string path)
        {
            var b = File.ReadAllBytes(path);
            int channels = 1, rate = Rate, bits = 16, format = 1, at = 12;
            int dataAt = -1, dataLen = 0;
            while (at + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, at, 4);
                int len = BitConverter.ToInt32(b, at + 4);
                if (id == "fmt ")
                {
                    format = BitConverter.ToInt16(b, at + 8);
                    channels = BitConverter.ToInt16(b, at + 10);
                    rate = BitConverter.ToInt32(b, at + 12);
                    bits = BitConverter.ToInt16(b, at + 22);
                }
                else if (id == "data") { dataAt = at + 8; dataLen = Math.Min(len, b.Length - dataAt); break; }
                at += 8 + len + (len & 1);
            }
            if (dataAt < 0) throw new InvalidDataException(path);
            int bytesPer = bits / 8, frames = dataLen / (bytesPer * channels);
            var l = new float[frames];
            var r = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                float Sample(int c)
                {
                    int o = dataAt + (i * channels + c) * bytesPer;
                    return format == 3 ? BitConverter.ToSingle(b, o) : BitConverter.ToInt16(b, o) / 32768f;
                }
                l[i] = Sample(0);
                r[i] = channels > 1 ? Sample(1) : l[i];
            }
            return (l, channels > 1 ? r : l, rate);
        }

        // ---- The selector on more material (WP1b) -------------------------------------------------------

        /// <summary>
        /// Synthetic basses besides <see cref="NeoStretchTests.Bass"/>, on another line (a note a beat, from A1, E1, G1...): "square" (a square and a
        /// sub sine, the phase restarting on each note), "pulse" (a quarter-width pulse and a sub), "filtered-saw" (a free-running sawtooth through
        /// the repository's own state-variable low-pass, resonant, its cutoff falling from 1.2 kHz to 220 Hz over each note), "detuned" (two
        /// sawtooths 6 cents apart, which beat: not strictly periodic) and "saw-eighths" (the first one's sawtooth and sub on a note every half
        /// beat: a faster line); "saw-noisy" (the saw and sub with white noise 30 dB under the note, as a played instrument's bow, pick or key noise) and
        /// "saw-vibrato" (the same line with the pitch swinging 12 cents at 5 Hz). Mono, at <see cref="Rate"/>.
        /// </summary>
        internal static AudioSource SynthBass(string kind, double bpm, int beats, double pitch = 1)
        {
            int perBeat = (int)Math.Round(Rate * 60 / bpm), perNote = kind == "saw-eighths" ? perBeat / 2 : perBeat;
            int notesTotal = (int)Math.Round((double)perBeat * beats / perNote);
            var x = new float[perBeat * beats];
            double[] notes = { 55.00, 41.20, 49.00, 55.00, 61.74, 73.42, 65.41, 49.00 };
            double free = 0, free2 = 0, clock = 0;
            var noise = new Random(3);
            var svf = new NeoBuzz.Dsp.Svf { Mode = NeoBuzz.Dsp.SvfMode.LowPass };
            for (int n = 0; n < notesTotal; n++)
            {
                double f = pitch * notes[n % notes.Length], phase = 0;
                for (int i = 0; i < perNote && n * perNote + i < x.Length; i++)
                {
                    double env = Math.Min(1, i / (0.004 * Rate)) * Math.Exp(-i / (0.35 * perBeat)), v;
                    clock += 1.0 / Rate;
                    phase += (kind == "saw-vibrato" ? f * Math.Pow(2, 12 / 1200.0 * Math.Sin(2 * Math.PI * 5 * clock)) : f) / Rate;
                    switch (kind)
                    {
                        case "square":
                            v = 0.6 * (phase - Math.Floor(phase) < 0.5 ? 1 : -1) + 0.4 * Math.Sin(2 * Math.PI * phase);
                            break;
                        case "pulse":
                            v = 0.6 * (phase - Math.Floor(phase) < 0.25 ? 1 : -1) + 0.4 * Math.Sin(2 * Math.PI * phase);
                            break;
                        case "filtered-saw":
                        case "filtered-saw-2k":                   // the same, the cutoff opening to 2 kHz (WP1c: what the energy above 2 kHz does to the choice)
                        case "filtered-saw-4k":
                        case "filtered-saw-slow":                 // and the sweep slow (0.6 s): a filtered line whose shape hardly changes from one period to the next
                        case "filtered-saw-static":               // and no sweep at all: the cutoff stays at 800 Hz
                            {
                                free += f / Rate;
                                double open = kind == "filtered-saw" || kind == "filtered-saw-slow" ? 980 : kind == "filtered-saw-2k" ? 1780 : 3780;
                                double cutoff = kind == "filtered-saw-static" ? 800 : 220 + open * Math.Exp(-i / ((kind == "filtered-saw-slow" ? 0.6 : 0.15) * Rate));
                                svf.Set(cutoff, Rate, 2.0);
                                v = 1.4 * svf.Process((float)(2 * (free - Math.Floor(free)) - 1));
                                break;
                            }
                        case "detuned":
                            free += f / Rate;
                            free2 += f * 1.0035 / Rate;
                            v = 0.5 * (2 * (free - Math.Floor(free)) - 1) + 0.5 * (2 * (free2 - Math.Floor(free2)) - 1);
                            break;
                        case "saw-noisy":
                            v = 0.6 * Math.Sin(2 * Math.PI * phase) + 0.4 * (2 * (phase - Math.Floor(phase)) - 1) + 0.03 * (noise.NextDouble() * 2 - 1);
                            break;
                        default:                                  // saw-eighths, saw-vibrato
                            v = 0.6 * Math.Sin(2 * Math.PI * phase) + 0.4 * (2 * (phase - Math.Floor(phase)) - 1);
                            break;
                    }
                    x[n * perNote + i] = (float)(0.45 * env * v);
                }
            }
            return new AudioSource(x, x, Rate);
        }

        /// <summary>The excerpts of WP1B_REAL_DIR (mono 16-bit or float WAV files, never in the repository), by file name.</summary>
        internal static (string Name, AudioSource Source)[] RealExcerpts(string dir)
        {
            return Directory.GetFiles(dir, "*.wav").OrderBy(f => f, StringComparer.Ordinal).Select(f =>
            {
                var (l, r, rate) = ReadWav(f);
                return (Path.GetFileNameWithoutExtension(f), new AudioSource(l, r, rate));
            }).ToArray();
        }

        /// <summary>
        /// WP1b renders, by hand: for the generated synthetic basses (every <see cref="SynthBass"/> kind) and every excerpt in WP1B_REAL_DIR, at 20 %
        /// slower and 25 % faster, <c>name-label--mode.wav</c> (32-bit float, left channel) for original, pro-old (the stretcher alone), pro-new (Pro as
        /// it plays now: the path where the selector takes it), pro-forced (the path whatever the selector says), beats, complex-old, complex and, for the generated ones, ideal. Set WP1B_RENDER_DIR.
        /// </summary>
        [Test, Explicit("writes the WP1b renders; set WP1B_RENDER_DIR (and WP1B_REAL_DIR)")]
        public void WriteWp1bRenders()
        {
            string dir = Environment.GetEnvironmentVariable("WP1B_RENDER_DIR") ?? throw new InvalidOperationException("WP1B_RENDER_DIR");
            Directory.CreateDirectory(dir);
            var items = new List<(string Name, AudioSource Source, Func<double, AudioSource>? Ideal)>();
            foreach (var kind in new[] { "square", "pulse", "filtered-saw", "detuned", "saw-eighths", "saw-noisy", "saw-vibrato" })
            {
                string k = kind;
                items.Add(("synth-" + k, SynthBass(k, Bpm, 8), t => SynthBass(k, Bpm * t, 8)));
            }
            string? real = Environment.GetEnvironmentVariable("WP1B_REAL_DIR");
            if (real != null) items.AddRange(RealExcerpts(real).Select(e => (e.Name, e.Source, (Func<double, AudioSource>?)null)));
            foreach (var (name, source, ideal) in items)
            {
                double beats = source.Seconds * Bpm / 60;
                foreach (var (label, tempo) in new[] { ("20-slower", 0.8), ("25-faster", 1.25) })
                {
                    string group = $"{name}-{label}";
                    int rate = source.SampleRate;
                    Write(dir, group, "original", source.Left, rate);
                    if (ideal != null) Write(dir, group, "ideal", ideal(tempo).Left, rate);
                    Write(dir, group, "beats", NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, WarpMode.Beats, 0), Bpm * tempo, 0, beats, 256, rate), rate);
                    Write(dir, group, "complex", NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, WarpMode.Complex, 0), Bpm * tempo, 0, beats, 256, rate), rate);
                    Write(dir, group, "pro-new", NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, WarpMode.Pro, 0), Bpm * tempo, 0, beats, 256, rate), rate);
                    PeriodicVoice.Enabled = false;
                    try
                    {
                        Write(dir, group, "pro-old", NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, WarpMode.Pro, 0), Bpm * tempo, 0, beats, 256, rate), rate);
                        Write(dir, group, "complex-old", NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, WarpMode.Complex, 0), Bpm * tempo, 0, beats, 256, rate), rate);
                    }
                    finally { PeriodicVoice.Enabled = true; }
                    // The path forced on (a fresh source, so the analysis is made again): to hear it where the selector leaves it out.
                    PeriodicAnalysis.Force = true;
                    try
                    {
                        var again = new AudioSource(source.Left, source.Right, rate);
                        Write(dir, group, "pro-forced", NeoStretchTests.Play(NeoStretchTests.Clip(again, Bpm, WarpMode.Pro, 0), Bpm * tempo, 0, beats, 256, rate), rate);
                    }
                    finally { PeriodicAnalysis.Force = false; }
                }
            }
        }

        /// <summary>
        /// WP1c, by hand: the five synthetic lines (saw+sub, filtered saw, square, pulse, 12-cent vibrato) at the given tempo factors (WP1C_TEMPOS, comma
        /// list, default 0.8,1.1,1.25,1.5), <c>name-pct--mode.wav</c> (32-bit float, left channel): original, ideal (the generator at the target tempo),
        /// pro-old (the stretcher alone), pro-path (the time-domain path forced on, whatever the selector says) and pro-now (Pro as it plays: the selector's
        /// choice). Set WP1C_RENDER_DIR.
        /// </summary>
        [Test, Explicit("writes the WP1c renders; set WP1C_RENDER_DIR")]
        public void WriteWp1cRenders()
        {
            string dir = Environment.GetEnvironmentVariable("WP1C_RENDER_DIR") ?? throw new InvalidOperationException("WP1C_RENDER_DIR");
            Directory.CreateDirectory(dir);
            var tempos = (Environment.GetEnvironmentVariable("WP1C_TEMPOS") ?? "0.8,1.1,1.25,1.5").Split(',').Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var lines = new (string Name, Func<double, AudioSource> Make)[]
            {
                ("saw-sub", t => NeoStretchTests.Bass(Bpm * t, 8)),
                ("filtered-saw", t => SynthBass("filtered-saw", Bpm * t, 8)),
                ("square", t => SynthBass("square", Bpm * t, 8)),
                ("pulse", t => SynthBass("pulse", Bpm * t, 8)),
                ("saw-vibrato", t => SynthBass("saw-vibrato", Bpm * t, 8)),
                ("filtered-saw-2k", t => SynthBass("filtered-saw-2k", Bpm * t, 8)),
                ("filtered-saw-4k", t => SynthBass("filtered-saw-4k", Bpm * t, 8)),
                ("filtered-saw-slow", t => SynthBass("filtered-saw-slow", Bpm * t, 8)),
                ("saw-noisy", t => SynthBass("saw-noisy", Bpm * t, 8)),
                ("filtered-saw-static", t => SynthBass("filtered-saw-static", Bpm * t, 8)),
            };
            foreach (var (name, make) in lines)
                foreach (double tempo in tempos)
                {
                    string group = $"{name}-{(int)Math.Round(tempo * 100)}";
                    var source = make(1);
                    int rate = source.SampleRate;
                    Write(dir, group, "original", source.Left, rate);
                    Write(dir, group, "ideal", make(tempo).Left, rate);
                    Write(dir, group, "pro-now", NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, WarpMode.Pro, 0), Bpm * tempo, 0, 8, 256, rate), rate);
                    PeriodicVoice.Enabled = false;
                    try { Write(dir, group, "pro-old", NeoStretchTests.Play(NeoStretchTests.Clip(new AudioSource(source.Left, source.Right, rate), Bpm, WarpMode.Pro, 0), Bpm * tempo, 0, 8, 256, rate), rate); }
                    finally { PeriodicVoice.Enabled = true; }
                    PeriodicAnalysis.Force = true;
                    try { Write(dir, group, "pro-path", NeoStretchTests.Play(NeoStretchTests.Clip(new AudioSource(source.Left, source.Right, rate), Bpm, WarpMode.Pro, 0), Bpm * tempo, 0, 8, 256, rate), rate); }
                    finally { PeriodicAnalysis.Force = false; }
                }
        }

        /// <summary>Prints what the selector measures on each kind of material: the generated ones and, from WP1B_REAL_DIR, the real excerpts.</summary>
        [Test, Explicit("prints the selector's features; set WP1B_REAL_DIR for the real excerpts")]
        public void PrintTheSelectorFeatures()
        {
            var rows = new List<(string Name, AudioSource Source)>
            {
                ("synth bass (saw+sub, NA5 blind test)", Excerpt("bass", Bpm, 1)),
                ("synth bass 2: square+sub", SynthBass("square", Bpm, 8)),
                ("synth bass 3: pulse 25 %+sub", SynthBass("pulse", Bpm, 8)),
                ("synth bass 4: filtered saw", SynthBass("filtered-saw", Bpm, 8)),
                ("synth bass 5: detuned saws", SynthBass("detuned", Bpm, 8)),
                ("synth bass 6: saw+sub, eighth notes", SynthBass("saw-eighths", Bpm, 8)),
                ("synth bass 7: saw+sub, noise -30 dB", SynthBass("saw-noisy", Bpm, 8)),
                ("synth bass 8: saw+sub, vibrato 12 cents", SynthBass("saw-vibrato", Bpm, 8)),
                ("synth bass 9: filtered saw, cutoff to 2 kHz", SynthBass("filtered-saw-2k", Bpm, 8)),
                ("synth bass 10: filtered saw, cutoff to 4 kHz", SynthBass("filtered-saw-4k", Bpm, 8)),
                ("synth bass 11: filtered saw, slow sweep", SynthBass("filtered-saw-slow", Bpm, 8)),
                ("synth bass 12: filtered saw, no sweep", SynthBass("filtered-saw-static", Bpm, 8)),
                ("drums", Excerpt("drums", Bpm, 1)),
                ("pad", Excerpt("pad", Bpm, 1)),
            };
            var long180 = new float[Rate * 180];
            var one = NeoStretchTests.Bass(Bpm, 8).Left;
            for (int i = 0; i < long180.Length; i++) long180[i] = one[i % one.Length];
            rows.Add(("synth bass, 180 s (a loop 45 times)", new AudioSource(long180, long180, Rate)));
            string? real = Environment.GetEnvironmentVariable("WP1B_REAL_DIR");
            if (real != null) rows.AddRange(RealExcerpts(real));
            string? csv = Environment.GetEnvironmentVariable("WP1B_FRAMES_DIR");         // the measured frames and the audio, for the prototype that measures the other candidates
            if (csv != null) { Directory.CreateDirectory(csv); PeriodicAnalysis.Diagnostics = true; }
            try
            {
                foreach (var (name, source) in rows)
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var info = PeriodicAnalysis.Of(source);
                    clock.Stop();
                    TestContext.Out.WriteLine(info == null
                        ? $"{name,-46} no analysis"
                        : $"{name,-46} voiced {info.VoicedShare:F3}  low {info.LowShare:F3}  fidelity {info.Fidelity:F3}  p10 {info.FidelityLow:F3}  frames {info.FidelityFrames,2}  use {info.Use}  ({clock.ElapsedMilliseconds} ms)");
                    if (csv == null) continue;
                    string file = new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());
                    Write(csv, file, "audio", source.Left, source.SampleRate);
                    File.WriteAllLines(Path.Combine(csv, file + ".csv"),
                        (info?.Measured ?? Array.Empty<(int Start, int Period, double Energy, double Repeat)>()).Select(m => $"{m.Start},{m.Period},{m.Energy:R},{m.Repeat:R}"));
                }
            }
            finally { PeriodicAnalysis.Diagnostics = false; }
        }

        // ---- The time-domain path -----------------------------------------------------------------------

        static void NeedsTheLibrary()
        {
            if (Environment.GetEnvironmentVariable("NEO_STRETCH_OFF") == "1") Assert.Ignore("the library is turned off by NEO_STRETCH_OFF");
            Assert.That(NeoStretch.Available, Is.True, "the neostretch library is not here: " + NeoStretch.Failure);
        }

        /// <summary>
        /// Clicks, as tools/warp_quality.py counts them: the peak of the signal above 1.5 kHz (a 4th-order Butterworth high-pass) in each
        /// millisecond that stands 4 dB over the loudest such peak of the reference within 12 ms either side, and over -60 dBFS; spikes within
        /// 5 ms of the first of a run are one.
        /// </summary>
        internal static int Clicks(float[] reference, float[] x)
        {
            static float[] Peaks(float[] y)
            {
                // Two biquads (RBJ high-pass at 1.5 kHz, Q 0.5412 and 1.3065 make a 4th-order Butterworth), run forward and then backward over
                // the signal (zero phase, as scipy's sosfiltfilt does in the tool): a causal pass finds a third as many clicks in the same audio.
                var h = (float[])y.Clone();
                foreach (double q in new[] { 0.5412, 1.3065 })
                {
                    double w0 = 2 * Math.PI * 1500 / Rate, alpha = Math.Sin(w0) / (2 * q), c = Math.Cos(w0);
                    double b0 = (1 + c) / 2, b1 = -(1 + c), b2 = (1 + c) / 2, a0 = 1 + alpha, a1 = -2 * c, a2 = 1 - alpha;
                    for (int pass = 0; pass < 2; pass++)
                    {
                        Array.Reverse(h);
                        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
                        for (int i = 0; i < h.Length; i++)
                        {
                            double v = h[i], o = (b0 * v + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2) / a0;
                            x2 = x1; x1 = v; y2 = y1; y1 = o;
                            h[i] = (float)o;
                        }
                    }
                }
                int n = Rate / 1000, m = h.Length / n;
                var peaks = new float[m];
                for (int i = 0; i < m; i++)
                {
                    float best = 0;
                    for (int j = i * n; j < (i + 1) * n; j++) best = Math.Max(best, Math.Abs(h[j]));
                    peaks[i] = best;
                }
                return peaks;
            }
            var pr = Peaks(reference);
            var px = Peaks(x);
            int count = 0, last = -100;
            double over = Math.Pow(10, 4 / 20.0), floor = Math.Pow(10, -60 / 20.0);
            for (int i = 0; i < Math.Min(pr.Length, px.Length); i++)
            {
                float cover = 0;
                for (int j = Math.Max(0, i - 12); j <= Math.Min(pr.Length - 1, i + 12); j++) cover = Math.Max(cover, pr[j]);
                if (px[i] > over * cover && px[i] > floor)
                {
                    if (i - last > 5)
                    {
                        count++;
                        last = i;
                    }
                }
            }
            return count;
        }

        [Test]
        public void TheAnalysisFindsABassLineAndNothingElse()
        {
            var bass = PeriodicAnalysis.Of(Excerpt("bass", Bpm, 1));
            Assert.That(bass, Is.Not.Null);
            Assert.That(bass!.Use, Is.True, $"a bass line: voiced {bass.VoicedShare:F2}, low {bass.LowShare:F2}");
            Assert.That(bass.Onsets.Length, Is.EqualTo(7), "one onset per note after the first, not one per decaying harmonic");
            for (int k = 0; k < bass.Onsets.Length; k++)
                Assert.That(bass.Onsets[k], Is.EqualTo((k + 1) * 22050).Within(300), $"note {k + 2} starts on its beat");
            foreach (var name in new[] { "drums", "pad" })
            {
                var info = PeriodicAnalysis.Of(Excerpt(name, Bpm, 1));
                Assert.That(info?.Use ?? false, Is.False, $"{name} stay with the stretcher (voiced {info?.VoicedShare:F2}, low {info?.LowShare:F2})");
            }
            Assert.That(PeriodicAnalysis.Of(NeoStretchTests.Tone(0.2)), Is.Null.Or.Property("Use").False, "a fraction of a second is nothing to analyse");
        }

        [Test]
        public void TheSelectorTakesLinesThatRepeatLikeAnOscillatorAndLeavesPlayedOnesToTheStretcher()
        {
            // Strictly periodic lines: another waveform, a pulse, a filtered sawtooth, and the first one with a little noise under it.
            foreach (var kind in new[] { "square", "pulse", "filtered-saw", "saw-noisy" })
            {
                var info = PeriodicAnalysis.Of(SynthBass(kind, Bpm, 8))!;
                Assert.That(info.Use, Is.True, $"{kind}: voiced {info.VoicedShare:F2}, fidelity {info.Fidelity:F3}, lowest tenth {info.FidelityLow:F3}");
                Assert.That(info.FidelityFrames, Is.GreaterThanOrEqualTo(8), $"{kind}: enough steady frames were measured");
            }
            // Not strictly periodic: two oscillators a few cents apart (they beat) and a pitch that swings (vibrato). Both are lines the old path plays as before.
            foreach (var kind in new[] { "detuned", "saw-vibrato" })
            {
                var info = PeriodicAnalysis.Of(SynthBass(kind, Bpm, 8))!;
                Assert.That(info.VoicedShare, Is.GreaterThanOrEqualTo(PeriodicAnalysis.MinVoiced), $"{kind}: a bass line by the old tests");
                Assert.That(info.LowShare, Is.GreaterThanOrEqualTo(PeriodicAnalysis.MinLow), $"{kind}: and low");
                Assert.That(info.Use, Is.False, $"{kind}: fidelity {info.Fidelity:F3}, lowest tenth {info.FidelityLow:F3}");
            }
            // A line too fast for the old frames (eighth notes at 120 BPM): stays with the stretcher as it did.
            Assert.That(PeriodicAnalysis.Of(SynthBass("saw-eighths", Bpm, 8))?.Use ?? false, Is.False);
        }

        [Test]
        public void SpeedingUpTheSelectorKeepsTheLinesThePathIsBetterOnAndLeavesTheResonantOnesToTheStretcher()
        {
            // Where the path beats the stretcher at 10, 25 and 50 % faster (WP1c): a strong fundamental and weak, flat harmonics.
            foreach (var (name, source) in new[] { ("saw+sub", Excerpt("bass", Bpm, 1)), ("square", SynthBass("square", Bpm, 8)), ("pulse", SynthBass("pulse", Bpm, 8)), ("noisy saw", SynthBass("saw-noisy", Bpm, 8)) })
            {
                var info = PeriodicAnalysis.Of(source)!;
                Assert.That(info.Use && info.FasterOk, Is.True, $"{name}: low share {info.LowShare:F3}");
            }
            // Where it is not: a resonant low-passed saw (swept fast or slowly, opened wider, or held still) keeps the path slowing down, not speeding up.
            foreach (var kind in new[] { "filtered-saw", "filtered-saw-2k", "filtered-saw-4k", "filtered-saw-slow", "filtered-saw-static" })
            {
                var info = PeriodicAnalysis.Of(SynthBass(kind, Bpm, 8))!;
                Assert.That(info.Use, Is.True, $"{kind}: still the path's, slowing down");
                Assert.That(info.FasterOk, Is.False, $"{kind}: low share {info.LowShare:F3}, the stretcher's when faster");
            }
            // The choice is made where the clip starts: the same clip is the path's at the audio's tempo and slower, the stretcher's faster.
            var filtered = NeoStretchTests.Clip(SynthBass("filtered-saw", Bpm, 8), Bpm, WarpMode.Pro);
            PeriodicAnalysis.Of(filtered.Source);
            double perSample(double bpm) => bpm / 60.0 / Rate;
            Assert.That(PeriodicVoice.Qualifies(filtered, 1.0, Rate, 0, perSample(Bpm * 0.8)), Is.True, "20 % slower");
            Assert.That(PeriodicVoice.Qualifies(filtered, 1.0, Rate, 0, perSample(Bpm)), Is.True, "the same tempo");
            Assert.That(PeriodicVoice.Qualifies(filtered, 1.0, Rate, 0, perSample(Bpm * 1.25)), Is.False, "25 % faster");
            Assert.That(PeriodicVoice.Qualifies(filtered, 1.0, Rate, 0, perSample(Bpm * 1.25), playing: true), Is.True, "a voice already on the path stays on it until its next start");
            var saw = NeoStretchTests.Clip(Excerpt("bass", Bpm, 1), Bpm, WarpMode.Pro);
            PeriodicAnalysis.Of(saw.Source);
            Assert.That(PeriodicVoice.Qualifies(saw, 1.0, Rate, 0, perSample(Bpm * 1.25)), Is.True, "saw+sub 25 % faster keeps the path");
        }

        [Test]
        public void ARealBassStaysWithTheStretcherAtEveryTempo()
        {
            // The sampled basses of WP1b (WP1B_REAL_DIR, never in the repository) are left to the stretcher by the selector, faster as slower.
            string? real = Environment.GetEnvironmentVariable("WP1B_REAL_DIR");
            if (real == null) Assert.Ignore("WP1B_REAL_DIR is not set");
            foreach (var (name, source) in RealExcerpts(real!).Where(e => e.Name.Contains("bass")))
            {
                var info = PeriodicAnalysis.Of(source);
                Assert.That(info?.Use ?? false, Is.False, name);
            }
        }

        [Test, Combinatorial]
        public void AResonantLineGoesToTheStretcherWhenTheSongIsFasterAndPlaysAsItSampleForSample([Values(1.1, 1.25, 1.5)] double tempo, [Values("Pro", "Complex")] string modeName)
        {
            var mode = Enum.Parse<WarpMode>(modeName);
            if (mode == WarpMode.Pro) NeedsTheLibrary();
            foreach (var kind in new[] { "filtered-saw", "filtered-saw-static" })
            {
                var source = SynthBass(kind, Bpm, 8);
                var now = NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, mode), Bpm * tempo, 0, 8);
                Assert.That(now, Is.EqualTo(RenderOld(source, Bpm, Bpm * tempo, 0, 8, mode)), $"{kind} {tempo} {modeName}: the stretcher, sample for sample");
            }
        }

        [Test, Combinatorial]
        public void ASlowerSongKeepsThePathOnEveryLineItHadItOn([Values(0.8, 0.9)] double tempo, [Values("Pro", "Complex")] string modeName)
        {
            var mode = Enum.Parse<WarpMode>(modeName);
            if (mode == WarpMode.Pro) NeedsTheLibrary();
            foreach (var kind in new[] { "filtered-saw", "square" })
            {
                var source = SynthBass(kind, Bpm, 8);
                var now = NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, mode), Bpm * tempo, 0, 8);
                Assert.That(now, Is.Not.EqualTo(RenderOld(source, Bpm, Bpm * tempo, 0, 8, mode)), $"{kind} {tempo} {modeName}: the path");
            }
        }

        [Test, Combinatorial]
        public void ALineThePathIsBetterOnKeepsItWhenFaster([Values(1.1, 1.25, 1.5)] double tempo, [Values("Pro", "Complex")] string modeName)
        {
            var mode = Enum.Parse<WarpMode>(modeName);
            if (mode == WarpMode.Pro) NeedsTheLibrary();
            foreach (var source in new[] { Excerpt("bass", Bpm, 1), SynthBass("square", Bpm, 8), SynthBass("pulse", Bpm, 8) })
            {
                var now = NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, mode), Bpm * tempo, 0, 8);
                Assert.That(now, Is.Not.EqualTo(RenderOld(source, Bpm, Bpm * tempo, 0, 8, mode)), $"{tempo} {modeName}: the path");
            }
        }

        [Test]
        public void ALineTheSelectorLeavesAloneRendersAsTheStretcherAlone()
        {
            NeedsTheLibrary();
            foreach (var kind in new[] { "detuned", "saw-vibrato" })
                foreach (var tempo in new[] { 0.8, 1.25 })
                {
                    var source = SynthBass(kind, Bpm, 8);
                    var now = NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, WarpMode.Pro), Bpm * tempo, 0, 8);
                    Assert.That(now, Is.EqualTo(RenderOld(source, Bpm, Bpm * tempo, 0, 8)), $"{kind} at {tempo}: Pro is the stretcher, sample for sample");
                    var c = NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, WarpMode.Complex), Bpm * tempo, 0, 8);
                    Assert.That(c, Is.EqualTo(RenderOld(source, Bpm, Bpm * tempo, 0, 8, WarpMode.Complex)), $"{kind} at {tempo}: and Complex is the phase vocoder");
                }
        }

        [Test]
        public void ABareSineBassHasNothingAboveTheFundamentalToRepeat()
        {
            // The band that is measured starts at 300 Hz: a sine bass has nothing there, and nothing for a time-domain path to keep (the
            // phase vocoder does a sine exactly).
            var sine = new float[44100 * 4];
            for (int i = 0; i < sine.Length; i++) sine[i] = (float)(0.4 * Math.Sin(2 * Math.PI * 55 * i / 44100.0));
            var info = PeriodicAnalysis.Of(new AudioSource(sine, sine, 44100));
            Assert.That(info?.Use ?? false, Is.False, "a bare sine: no harmonics to repeat");
        }

        [Test, Combinatorial]
        public void ABassLineStretchedInProHasNoClicksTheIdealLacks([Values(0.8, 1.25)] double tempo)
        {
            NeedsTheLibrary();
            var source = Excerpt("bass", Bpm, 1);
            var ideal = Excerpt("bass", Bpm * tempo, 1).Left;
            var now = NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, WarpMode.Pro), Bpm * tempo, 0, 8);
            var before = RenderOld(source, Bpm, Bpm * tempo, 0, 8);
            int clicksNow = Clicks(ideal, now), clicksBefore = Clicks(ideal, before);
            TestContext.Out.WriteLine($"tempo {tempo}: clicks {clicksBefore} before, {clicksNow} now (the ideal's own: {Clicks(ideal, ideal)})");
            Assert.That(clicksBefore, Is.GreaterThan(10), "the detector flags the stretcher's bass, as the blind test heard it");
            Assert.That(clicksNow, Is.LessThanOrEqualTo(2), "the time-domain path keeps the cycle");
        }

        [Test, Combinatorial]
        public void TheTimeDomainPathKeepsTheNotesOnTheirBeats([Values(0.8, 1.25)] double tempo)
        {
            NeedsTheLibrary();
            var source = Excerpt("bass", Bpm, 1);
            var now = NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, WarpMode.Pro), Bpm * tempo, 0, 8);
            for (int k = 1; k < 8; k++)
            {
                int at = (int)Math.Round(k * 22050 / tempo);
                double before = 0, after = 0;
                for (int i = at - 700; i < at - 100; i++) before = Math.Max(before, Math.Abs(now[i]));
                for (int i = at + 100; i < at + 700; i++) after = Math.Max(after, Math.Abs(now[i]));
                Assert.That(after, Is.GreaterThan(0.1), $"note {k + 1} sounds at its beat");
                Assert.That(before, Is.LessThan(0.08 * Math.Max(0.2, after)), $"and not before it (tempo {tempo})");
            }
        }

        [Test]
        public void ATransposedBassStaysWithTheStretcher()
        {
            NeedsTheLibrary();
            var source = Excerpt("bass", Bpm, 1);
            var a = NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, WarpMode.Pro, 7), Bpm, 0, 8);
            var b = RenderOld(source, Bpm, Bpm, 7, 8);
            Assert.That(a, Is.EqualTo(b), "a transposition is not the time-domain path's job: the output is the stretcher's, sample for sample");
        }

        [Test]
        public void TheTimeDomainPathDoesNotDependOnTheBlockSize()
        {
            NeedsTheLibrary();
            var source = Excerpt("bass", Bpm, 1);
            var big = NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, WarpMode.Pro), Bpm * 0.8, 0, 8, 1024);
            foreach (int block in new[] { 64, 256, 333 })
            {
                var x = NeoStretchTests.Play(NeoStretchTests.Clip(source, Bpm, WarpMode.Pro), Bpm * 0.8, 0, 8, block);
                double err = 0, sig = 0;
                for (int i = 0; i < x.Length; i++) { err += Math.Pow(x[i] - big[i], 2); sig += Math.Pow(big[i], 2); }
                Assert.That(10 * Math.Log10(err / sig + 1e-20), Is.LessThan(-60), $"block {block} against 1024");
            }
        }

        [Test]
        public void TheTimeDomainPathAllocatesNothingAndIsCheap()
        {
            NeedsTheLibrary();
            var source = Excerpt("bass", Bpm, 1);
            var p = NeoStretchTests.Clip(source, Bpm, WarpMode.Pro);
            var voice = new ClipVoice();
            voice.Prepare(p);
            var l = new float[256];
            var r = new float[256];
            double bps = Bpm * 0.8 / 60.0 / Rate;
            double beat = 0;
            for (int b = 0; b < 50; b++) { Array.Clear(l); Array.Clear(r); voice.Render(p, beat, bps, Rate, l, r, 0, 256); beat += 256 * bps; }
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            double worst = double.MaxValue, mean = 0;
            for (int run = 0; run < 3; run++)                           // the best of three: another process taking the core is not this path's time
            {
                double w = 0;
                long total = 0;
                for (int b = 0; b < 250; b++)
                {
                    Array.Clear(l); Array.Clear(r);
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    voice.Render(p, beat, bps, Rate, l, r, 0, 256);
                    long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                    w = Math.Max(w, (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                    total += t1 - t0;
                    beat += 256 * bps;
                }
                if (w < worst) { worst = w; mean = total * 1000.0 / System.Diagnostics.Stopwatch.Frequency / 250; }
            }
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            TestContext.Out.WriteLine($"time-domain voice, 256-sample blocks at 44.1 kHz: mean {mean:F3} ms, worst {worst:F3} ms of 5.8 ms; allocated {bytes} bytes");
            Assert.That(bytes, Is.EqualTo(0), "nothing allocated while it plays");
            Assert.That(worst, Is.LessThan(5.8), "a block of it in less than the block's time, whatever else the machine is doing");
        }

        [Test]
        public void TheStartOfABassClipFromItsMiddleHasNoStepAndTheLoopSeamIsSmooth()
        {
            NeedsTheLibrary();
            var source = Excerpt("bass", Bpm, 1);
            var p = NeoStretchTests.Clip(source, Bpm, WarpMode.Pro);
            // From the middle of a note, at another tempo, then round the end of the audio.
            var x = NeoStretchTests.Play(p, Bpm * 0.8, 3.3, 8);
            double step = 0;
            for (int i = 20; i < x.Length - 20; i++) step = Math.Max(step, Math.Abs(x[i] - x[i - 1]));
            Assert.That(step, Is.LessThan(0.35), "the saw's own edge is 0.3 at most; a splice would be more");
            Assert.That(x.Take(200).Max(Math.Abs), Is.GreaterThan(0.02), "it starts sounding where it starts");
        }
    }
}
