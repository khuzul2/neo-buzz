using NeoBuzz.Dsp;
using NeoBuzz.Vocoder;

namespace NeoBuzz.MachineTests
{
    /// <summary>What the Neo Vocoder tests and the listening pack share: the engine rendered block by block with a modulator, a sidechain carrier and notes, and test signals made in code.</summary>
    internal static class VocoderRig
    {
        public const int Rate = Measure.Rate;

        public static void Phys(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        public static void Choice(ParamValues v, int p, string name)
        {
            int i = Array.IndexOf(Params.Table[p].Names, name);
            if (i < 0) throw new ArgumentException($"{Params.Table[p].Name}: no choice {name}");
            v.Set(p, Params.Table[p].Min + i);
        }

        public static void Switch(ParamValues v, int p, bool on) => v.Set(p, on ? 1 : 0);

        /// <summary>The defaults with <paramref name="setup"/> applied.</summary>
        public static ParamValues Fresh(Action<ParamValues> setup = null)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            return v;
        }

        public static VocoderEngine Engine(ParamValues v, int rate = Rate)
        {
            var e = new VocoderEngine(v);
            e.SetSampleRate(rate);
            return e;
        }

        /// <summary>A note event for <see cref="Render"/>: at <paramref name="Sample"/> the key <paramref name="Key"/> (a MIDI note) goes on (velocity 1 … 127) or off.</summary>
        public readonly record struct Note(int Sample, bool On, int Key, int Velocity = 100);

        /// <summary>
        /// The engine over a modulator (<paramref name="l"/>, <paramref name="r"/>), an optional sidechain carrier and the notes, in blocks of <paramref name="block"/>.
        /// A note is heard by the block that holds its sample; <paramref name="atBlock"/> runs before each block with its first sample.
        /// </summary>
        public static (float[] L, float[] R) Render(VocoderEngine e, float[] l, float[] r, float[] carL = null, float[] carR = null, int block = 256,
            IList<Note> notes = null, Action<int> atBlock = null, int length = -1)
        {
            if (length < 0) length = l.Length;
            var ol = new float[length];
            var or = new float[length];
            var bl = new float[VocoderEngine.MaxBlock];
            var br = new float[VocoderEngine.MaxBlock];
            var cl = carL != null ? new float[VocoderEngine.MaxBlock] : null;
            var cr = carL != null ? new float[VocoderEngine.MaxBlock] : null;
            for (int pos = 0; pos < length; pos += block)
            {
                int n = Math.Min(block, length - pos);
                atBlock?.Invoke(pos);
                if (notes != null)
                    foreach (var nt in notes)
                        if (nt.Sample >= pos && nt.Sample < pos + n)
                        {
                            if (nt.On) e.NoteOn(1000 + nt.Key, nt.Key, nt.Velocity / 127f);
                            else e.NoteOff(1000 + nt.Key);
                        }
                for (int i = 0; i < n; i++)
                {
                    bl[i] = pos + i < l.Length ? l[pos + i] : 0f;
                    br[i] = pos + i < r.Length ? r[pos + i] : 0f;
                    if (cl != null)
                    {
                        cl[i] = pos + i < carL.Length ? carL[pos + i] : 0f;
                        cr[i] = carR != null ? (pos + i < carR.Length ? carR[pos + i] : 0f) : cl[i];
                    }
                }
                e.SidechainWired = cl != null;
                e.Process(bl, br, cl, cr, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        /// <summary>The audio call of the modulation-cost guard and the preset renders: noise in, the engine's default carrier.</summary>
        public static Action<float[], float[], int> CostCall(ParamValues v)
        {
            var e = Engine(v);
            e.NoteOn(1060, 60, 0.8f); e.NoteOn(1064, 64, 0.8f); e.NoteOn(1067, 67, 0.8f);
            return (l, r, n) => e.Process(l, r, null, null, n);
        }

        /// <summary>The preset tests' second of noise through the engine, the three notes of a C major chord held (the Synth carrier plays them; the Sidechain preset is fed a saw pad).</summary>
        public static float[] PresetRender(ParamValues v, float[] x)
        {
            var e = Engine(v);
            bool side = v.Choice(P.Carrier) == (int)VocoderCarrier.Sidechain;
            var notes = new List<Note> { new(0, true, 48, 100), new(0, true, 55, 100), new(0, true, 60, 100), new(0, true, 64, 100) };
            float[] car = side ? SawPad(x.Length, new[] { 130.81, 164.81, 196.0, 261.63 }, 0.15) : null;
            return Render(e, x, (float[])x.Clone(), car, car, 256, notes).L;
        }

        // ------------------------------------------------------------------------------------------------ signals

        public static float[] White(int length, double rms, uint seed = 7)
        {
            var rng = new Rng(seed);
            var x = new float[length];
            double a = rms * Math.Sqrt(3);
            for (int i = 0; i < length; i++) x[i] = (float)(rng.NextBipolar() * a);
            return x;
        }

        /// <summary>Noise with a spectrum of its own: white noise through an FFT, its magnitude multiplied by 10^(profile(f)/20) (the profile in dB), then scaled to <paramref name="rms"/>.</summary>
        public static float[] ShapedNoise(int length, Func<double, double> profileDb, double rms, uint seed = 11)
        {
            int n = 1; while (n < length) n <<= 1;
            var re = new double[n]; var im = new double[n];
            var rng = new Rng(seed);
            for (int i = 0; i < n; i++) re[i] = rng.NextBipolar();
            Fft.Transform(re, im, false);
            for (int k = 0; k <= n / 2; k++)
            {
                double f = (double)k * Rate / n;
                double g = k == 0 ? 0 : Math.Pow(10, profileDb(f) / 20);
                re[k] *= g; im[k] *= g;
                if (k > 0 && k < n / 2) { re[n - k] *= g; im[n - k] *= g; }
            }
            Fft.Transform(re, im, true);
            var x = new float[length];
            double sq = 0;
            for (int i = 0; i < length; i++) sq += re[i] * re[i];
            double scale = rms / Math.Sqrt(sq / length) ;
            for (int i = 0; i < length; i++) x[i] = (float)(re[i] * scale);
            return x;
        }

        /// <summary>A sum of band-limited saws (additive: every harmonic under 20 kHz at 1/n) for each pitch in <paramref name="hz"/>, at <paramref name="amplitude"/> each.</summary>
        public static float[] SawPad(int length, double[] hz, double amplitude)
        {
            var x = new float[length];
            foreach (double f0 in hz)
                for (int h = 1; h * f0 < 20000; h++)
                {
                    double w = 2 * Math.PI * h * f0 / Rate, a = amplitude * 2 / Math.PI / h;
                    for (int i = 0; i < length; i++) x[i] += (float)(a * Math.Sin(w * i));
                }
            return x;
        }

        /// <summary>A vowel-like modulator: a pulse train at <paramref name="f0"/> through resonators at the formants (Hz), peak-normalised to <paramref name="peak"/>.</summary>
        public static float[] Vowel(int length, double f0, double[] formants, double[] widths, double peak = 0.4)
        {
            var x = new float[length];
            double period = Rate / f0, next = 0;
            for (int i = 0; i < length; i++)
                if (i >= next) { x[i] = 1f; next += period; }
            var y = new float[length];
            for (int k = 0; k < formants.Length; k++)
            {
                var bp = new Biquad();
                bp.Set(BiquadType.BandPass, formants[k], Rate, formants[k] / widths[k]);
                float gain = k == 0 ? 1f : 0.6f;
                for (int i = 0; i < length; i++) y[i] += bp.Process(x[i]) * gain;
            }
            float pk = 1e-9f;
            for (int i = 0; i < length; i++) pk = Math.Max(pk, Math.Abs(y[i]));
            for (int i = 0; i < length; i++) y[i] *= (float)(peak / pk);
            return y;
        }

        public static float[] Sine(int length, double hz, double amplitude = 0.5)
        {
            var x = new float[length];
            for (int i = 0; i < length; i++) x[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate));
            return x;
        }

        // ------------------------------------------------------------------------------------------------ measures

        /// <summary>The one-sided power spectrum of <paramref name="x"/> from <paramref name="from"/> for <paramref name="len"/> samples: Hann windows of <paramref name="nfft"/> at half overlap averaged; the sum over all bins is the mean square.</summary>
        public static double[] Power(float[] x, int from, int len, int nfft = 4096)
        {
            var p = new double[nfft / 2];
            var re = new double[nfft]; var im = new double[nfft];
            var w = new double[nfft];
            double u = 0;
            for (int i = 0; i < nfft; i++) { w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / nfft); u += w[i] * w[i]; }
            int count = 0;
            for (int s = from; s + nfft <= from + len; s += nfft / 2)
            {
                for (int i = 0; i < nfft; i++) { re[i] = x[s + i] * w[i]; im[i] = 0; }
                Fft.Transform(re, im, false);
                for (int k = 1; k < nfft / 2; k++) p[k] += 2 * (re[k] * re[k] + im[k] * im[k]) / (nfft * u);
                count++;
            }
            for (int k = 0; k < p.Length; k++) p[k] /= Math.Max(1, count);
            return p;
        }

        /// <summary>The power of a spectrum <paramref name="p"/> (from <see cref="Power"/>) between <paramref name="lo"/> and <paramref name="hi"/> Hz.</summary>
        public static double BandPower(double[] p, double lo, double hi, int nfft = 4096)
        {
            double s = 0;
            for (int k = 1; k < p.Length; k++)
            {
                double f = (double)k * Rate / nfft;
                if (f >= lo && f < hi) s += p[k];
            }
            return s;
        }

        public static double Db(double power) => 10 * Math.Log10(Math.Max(1e-30, power));

        public static double Rms(float[] x, int from, int to)
        {
            double s = 0;
            for (int i = from; i < to; i++) s += (double)x[i] * x[i];
            return Math.Sqrt(s / Math.Max(1, to - from));
        }

        public static double Peak(float[] x, int from, int to)
        {
            double m = 0;
            for (int i = from; i < to; i++) m = Math.Max(m, Math.Abs(x[i]));
            return m;
        }

        public static double Pearson(double[] a, double[] b)
        {
            double ma = a.Average(), mb = b.Average(), sab = 0, saa = 0, sbb = 0;
            for (int i = 0; i < a.Length; i++) { sab += (a[i] - ma) * (b[i] - mb); saa += (a[i] - ma) * (a[i] - ma); sbb += (b[i] - mb) * (b[i] - mb); }
            return sab / Math.Sqrt(saa * sbb);
        }
    }
}
