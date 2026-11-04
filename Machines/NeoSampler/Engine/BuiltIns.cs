#nullable disable
// Neo Sampler (docs/specs/machines.md §5): built-in samples, made by code (no audio files, no
// licences): a drum kit, looped single waves for synth sounds and tests, and a few instruments.
// Zone Wave 201 and up reach them. Synthesised at 48 kHz with the portable maths and seeded noise,
// so they are the same on every platform and never change (songs name them by number: the list's
// order is permanent; new ones only at the end). Made once per process, on a background thread
// (the machine is Busy meanwhile, so renders wait).

using System;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.Sampler
{
    internal static class BuiltIns
    {
        public const int Rate = 48000;

        /// <summary>Names, in Wave order from 201 (permanent).</summary>
        public static readonly string[] Names =
        {
            "Kick", "Kick 808", "Snare", "Clap", "Rim", "Closed hat", "Open hat", "Low tom", "Mid tom", "High tom",
            "Crash", "Ride", "Cowbell", "Shaker", "Clave",
            "Sine", "Saw", "Square", "E-piano soft", "E-piano hard", "Strings", "Bass", "Bell", "Pluck", "Vox", "Noise",
        };

        public const int Count = 26;
        static readonly SampleSlot[] slots = new SampleSlot[Count];
        static int started;
        static volatile bool ready;
        static readonly object buildLock = new object();

        /// <summary>Whether every built-in is made.</summary>
        public static bool Ready => ready;

        /// <summary>The built-in <paramref name="n"/> (1-based: Wave 200 + n), or null while it is being made.</summary>
        public static SampleSlot Get(int n)
        {
            if (n < 1 || n > Count) return null;
            var s = Volatile.Read(ref slots[n - 1]);
            if (s == null) StartBuilding();
            return s;
        }

        /// <summary>Starts making them in the background (once).</summary>
        public static void StartBuilding()
        {
            if (Interlocked.Exchange(ref started, 1) != 0) return;
            var t = new Thread(() => BuildNow()) { IsBackground = true, Name = "Neo Sampler built-ins", Priority = ThreadPriority.BelowNormal };
            t.Start();
        }

        /// <summary>Makes them now, on this thread (tests; the background thread).</summary>
        public static void BuildNow()
        {
            Interlocked.Exchange(ref started, 1);
            lock (buildLock)
            {
                if (ready) return;
                for (int i = 0; i < Count; i++)
                    if (Volatile.Read(ref slots[i]) == null) Volatile.Write(ref slots[i], Make(i));
                ready = true;
            }
        }

        static SampleSlot Make(int i)
        {
            string name = Names[i];
            switch (i)
            {
                case 0: return One(name, Kick(0.6, 150, 48, 0.040, 0.26, 1u), 60);
                case 1: return One(name, Kick(2.0, 110, 52, 0.022, 0.85, 2u), 60);
                case 2: return One(name, Snare(), 60);
                case 3: return One(name, Clap(), 60);
                case 4: return One(name, Rim(), 60);
                case 5: return One(name, Metal(0.25, 0.045, 7000, 9u), 60);
                case 6: return One(name, Metal(1.2, 0.38, 6500, 10u), 60);
                case 7: return One(name, Tom(82, 11u), 60);
                case 8: return One(name, Tom(122, 12u), 60);
                case 9: return One(name, Tom(172, 13u), 60);
                case 10: return One(name, Cymbal(2.5, 1.1, 4500, 14u, false), 60);
                case 11: return One(name, Cymbal(2.5, 1.4, 3000, 15u, true), 60);
                case 12: return One(name, Cowbell(), 60);
                case 13: return One(name, Shaker(), 60);
                case 14: return One(name, Clave(), 60);
                case 15: return Sine(name);
                case 16: return Harmonic(name, odd: false);
                case 17: return Harmonic(name, odd: true);
                case 18: return EPiano(name, hard: false);
                case 19: return EPiano(name, hard: true);
                case 20: return Strings(name);
                case 21: return One(name, Bass(), 36);
                case 22: return One(name, Bell(), 72);
                case 23: return One(name, Pluck(), 60);
                case 24: return Vox(name);
                default: return NoiseLoop(name);
            }
        }

        // ── helpers ─────────────────────────────────────────────────────────

        static SampleSlot One(string name, float[] x, int root) =>
            new SampleSlot(name, new[] { SampleLayer.Build(name, Normalise(x), null, Rate, root) });

        static float[] Normalise(float[] x, float peak = 0.891f)
        {
            float m = 0;
            foreach (var v in x) m = Math.Max(m, Math.Abs(v));
            if (m > 0) for (int i = 0; i < x.Length; i++) x[i] *= peak / m;
            return x;
        }

        static double Sin(double cycles) => PortableMath.Sin(2 * Math.PI * (cycles - Math.Floor(cycles)));

        static double Decay(double t, double tau) => PortableMath.Exp(-t / tau);

        /// <summary>A loop baked to have no seam: its last <paramref name="fade"/> frames blend into the ones before its start (equal power).</summary>
        static void BakeLoop(float[] x, int ls, int le, int fade)
        {
            fade = Math.Min(fade, Math.Min(ls, le - ls));
            for (int i = 0; i < fade; i++)
            {
                double th = 0.5 * Math.PI * (i + 1) / (fade + 1);
                int a = le - fade + i, b = ls - fade + i;
                x[a] = (float)(x[a] * PortableMath.Cos(th) + x[b] * PortableMath.Sin(th));
            }
        }

        // ── drums ───────────────────────────────────────────────────────────

        static float[] Kick(double seconds, double from, double to, double sweep, double tau, uint seed)
        {
            int n = (int)(seconds * Rate);
            var x = new float[n];
            var rng = new Rng(seed);
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;
                double f = to + (from - to) * Decay(t, sweep);
                phase += f / Rate;
                double body = Sin(phase) * Decay(t, tau);
                double click = rng.NextBipolar() * Decay(t, 0.0015) * 0.35;
                double v = body + click;
                x[i] = (float)(v / (1 + 0.3 * Math.Abs(v)));
            }
            return Fade(x, 0.02);
        }

        static float[] Snare()
        {
            int n = (int)(0.5 * Rate);
            var x = new float[n];
            var rng = new Rng(3u);
            var hp = new Svf { Mode = SvfMode.HighPass };
            hp.Set(900, Rate, 0.7);
            var bp = new Svf { Mode = SvfMode.BandPass };
            bp.Set(4800, Rate, 0.9);
            double p1 = 0, p2 = 0;
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;
                p1 += (185 + 40 * Decay(t, 0.01)) / Rate;
                p2 += (332 + 50 * Decay(t, 0.01)) / Rate;
                double tone = (0.6 * Sin(p1) + 0.35 * Sin(p2)) * Decay(t, 0.07);
                float w = rng.NextBipolar();
                double noise = (hp.Process(w) * 0.8 + bp.Process(w) * 0.5) * Decay(t, 0.16);
                x[i] = (float)(tone + noise);
            }
            return Fade(x, 0.03);
        }

        static float[] Clap()
        {
            int n = (int)(0.5 * Rate);
            var x = new float[n];
            var rng = new Rng(4u);
            var bp = new Svf { Mode = SvfMode.BandPass };
            bp.Set(1250, Rate, 1.6);
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;
                double env = 0;
                for (int k = 0; k < 3; k++)
                {
                    double tk = t - k * 0.011;
                    if (tk >= 0) env = Math.Max(env, Decay(tk, 0.006));
                }
                if (t >= 0.022) env = Math.Max(env, 0.8 * Decay(t - 0.022, 0.14));
                x[i] = (float)(bp.Process(rng.NextBipolar()) * env * 2.5);
            }
            return Fade(x, 0.03);
        }

        static float[] Rim()
        {
            int n = (int)(0.15 * Rate);
            var x = new float[n];
            var rng = new Rng(5u);
            var hp = new Svf { Mode = SvfMode.HighPass };
            hp.Set(2500, Rate, 0.7);
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;
                double tone = (0.7 * Sin(1720 * t) + 0.5 * Sin(455 * t)) * Decay(t, 0.022);
                double click = hp.Process(rng.NextBipolar()) * Decay(t, 0.003);
                x[i] = (float)(tone + click);
            }
            return Fade(x, 0.01);
        }

        // The 808's six square waves: a metallic cluster for hats and cymbals.
        static readonly double[] MetalHz = { 205.3, 304.4, 369.6, 522.7, 540.0, 800.0 };

        static float[] MetalRaw(int n, double[] hz, uint seed)
        {
            var osc = new BlepOscillator[hz.Length];
            var rng = new Rng(seed);
            for (int k = 0; k < hz.Length; k++)
            {
                osc[k] = new BlepOscillator();
                osc[k].Reset(rng.NextUnipolar());
                osc[k].SetShape(3f, 0.5f);
            }
            var x = new float[n];
            for (int i = 0; i < n; i++)
            {
                float s = 0;
                for (int k = 0; k < hz.Length; k++) s += osc[k].Next(hz[k] / Rate, out _, out _);
                x[i] = s;
            }
            return x;
        }

        static float[] Metal(double seconds, double tau, double hpHz, uint seed)
        {
            int n = (int)(seconds * Rate);
            var raw = MetalRaw(n, MetalHz, seed);
            var rng = new Rng(seed * 7919u);
            var bp = new Svf { Mode = SvfMode.BandPass };
            bp.Set(10000, Rate, 1.2);
            var hp = new Svf { Mode = SvfMode.HighPass };
            hp.Set(hpHz, Rate, 0.7);
            var x = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;
                float v = bp.Process(raw[i] * 0.4f + rng.NextBipolar() * 0.25f);
                x[i] = (float)(hp.Process(v) * Decay(t, tau) * Math.Min(1.0, t / 0.0005));
            }
            return Fade(x, Math.Min(0.05, seconds / 4));
        }

        static float[] Cymbal(double seconds, double tau, double hpHz, uint seed, bool ride)
        {
            int n = (int)(seconds * Rate);
            var hz = ride ? new[] { 263.0, 400.0, 421.0, 474.0, 587.0, 845.0 } : new[] { 205.3, 304.4, 369.6, 522.7, 540.0, 800.0, 1131.0, 1486.0 };
            var raw = MetalRaw(n, hz, seed);
            var rng = new Rng(seed * 104729u);
            var hp = new Svf { Mode = SvfMode.HighPass };
            hp.Set(hpHz, Rate, 0.7);
            var x = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;
                double noise = rng.NextBipolar() * (ride ? 0.25 : 0.6);
                double ping = ride ? 0.35 * Sin(2950 * t) * Decay(t, 0.5) : 0;
                double v = hp.Process((float)(raw[i] * 0.3 + noise)) + ping;
                x[i] = (float)(v * Decay(t, tau) * Math.Min(1.0, t / 0.001));
            }
            return Fade(x, 0.2);
        }

        static float[] Tom(double hz, uint seed)
        {
            int n = (int)(0.8 * Rate);
            var x = new float[n];
            var rng = new Rng(seed);
            var lp = new Svf { Mode = SvfMode.LowPass };
            lp.Set(3000, Rate, 0.7);
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;
                phase += hz * (1 + 0.6 * Decay(t, 0.03)) / Rate;
                double body = Sin(phase) * Decay(t, 0.28);
                double skin = lp.Process(rng.NextBipolar()) * Decay(t, 0.02) * 0.3;
                x[i] = (float)(body + skin);
            }
            return Fade(x, 0.05);
        }

        static float[] Cowbell()
        {
            int n = (int)(0.4 * Rate);
            var raw = MetalRaw(n, new[] { 540.0, 800.0 }, 16u);
            var bp = new Svf { Mode = SvfMode.BandPass };
            bp.Set(2640, Rate, 0.8);
            var x = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;
                double env = t < 0.01 ? 1 : 0.6 * Decay(t - 0.01, 0.12) + 0.4 * Decay(t - 0.01, 0.015);
                x[i] = (float)(bp.Process(raw[i]) * env);
            }
            return Fade(x, 0.03);
        }

        static float[] Shaker()
        {
            int n = (int)(0.2 * Rate);
            var rng = new Rng(17u);
            var hp = new Svf { Mode = SvfMode.HighPass };
            hp.Set(6000, Rate, 0.8);
            var x = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;
                double env = Math.Min(1.0, t / 0.012) * Decay(t, 0.05);
                x[i] = (float)(hp.Process(rng.NextBipolar()) * env);
            }
            return Fade(x, 0.02);
        }

        static float[] Clave()
        {
            int n = (int)(0.15 * Rate);
            var x = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;
                x[i] = (float)(Sin(2500 * t) * Decay(t, 0.028) * Math.Min(1.0, t / 0.0003));
            }
            return Fade(x, 0.01);
        }

        static float[] Fade(float[] x, double seconds)
        {
            int f = Math.Min(x.Length, (int)(seconds * Rate));
            for (int i = 0; i < f; i++) x[x.Length - 1 - i] *= (float)(i / (double)f);
            return x;
        }

        // ── looped waves ────────────────────────────────────────────────────

        /// <summary>A4 (440 Hz) for half a second, its second half a loop of exactly 110 cycles.</summary>
        static SampleSlot Sine(string name)
        {
            int n = Rate / 2;
            var x = new float[n];
            for (int i = 0; i < n; i++) x[i] = (float)(0.891 * Sin(440.0 * i / Rate));
            return new SampleSlot(name, new[] { SampleLayer.Build(name, x, null, Rate, 69, n / 2, n, WaveLoop.Forward) });
        }

        /// <summary>
        /// A band-limited saw (or square) at C3: 30 cycles in exactly 11 008 samples (130.814 Hz, 0.02
        /// cent sharp), every harmonic up to 21.6 kHz, played once then looped.
        /// </summary>
        static SampleSlot Harmonic(string name, bool odd)
        {
            const int period = 11008, cycles = 30;
            int top = (int)(0.45 * Rate / (cycles * (double)Rate / period));
            var one = new double[period];
            for (int h = 1; h <= top; h++)
            {
                if (odd && (h & 1) == 0) continue;
                double a = 1.0 / h;
                long step = (long)h * cycles;
                for (int i = 0; i < period; i++) one[i] += a * Sin((step * i % period) / (double)period);
            }
            var x = new float[2 * period];
            double peak = 0;
            foreach (var v in one) peak = Math.Max(peak, Math.Abs(v));
            for (int i = 0; i < x.Length; i++) x[i] = (float)(0.7 * one[i % period] / peak);
            return new SampleSlot(name, new[] { SampleLayer.Build(name, x, null, Rate, 48, period, 2 * period, WaveLoop.Forward) });
        }

        static SampleSlot NoiseLoop(string name)
        {
            int n = Rate;
            var x = new float[n];
            var rng = new Rng(26u);
            for (int i = 0; i < n; i++) x[i] = rng.NextBipolar() * 0.6f;
            BakeLoop(x, n / 2, n, n / 8);
            return new SampleSlot(name, new[] { SampleLayer.Build(name, x, null, Rate, 60, n / 2, n, WaveLoop.Forward) });
        }

        // ── instruments ─────────────────────────────────────────────────────

        /// <summary>A tine piano by two-operator FM, at C2, C4 and C6 (one layer each): soft or hard.</summary>
        static SampleSlot EPiano(string name, bool hard)
        {
            int[] roots = { 36, 60, 84 };
            var layers = new SampleLayer[roots.Length];
            for (int k = 0; k < roots.Length; k++)
            {
                double hz = 440.0 * PortableMath.Pow(2.0, (roots[k] - 69) / 12.0);
                double seconds = roots[k] >= 84 ? 1.5 : 2.5;
                int n = (int)(seconds * Rate);
                var x = new float[n];
                double tau = 1.4 * PortableMath.Pow(2.0, -(roots[k] - 60) / 24.0);
                double index = hard ? 2.4 : 0.9;
                double pc = 0, pm = 0, pt = 0;
                for (int i = 0; i < n; i++)
                {
                    double t = i / (double)Rate;
                    pm += hz / Rate;
                    pc += hz / Rate;
                    pt += 14 * hz / Rate;
                    double mod = index * Decay(t, 0.35) * Sin(pm);
                    double body = Sin(pc + mod / (2 * Math.PI)) * Decay(t, tau);
                    double tine = (hard ? 0.25 : 0.12) * Sin(pt) * Decay(t, 0.012);
                    double v = (body + tine) * Math.Min(1.0, t / 0.001);
                    if (hard) v = v * 1.3 / (1 + 0.3 * Math.Abs(v));
                    x[i] = (float)v;
                }
                layers[k] = SampleLayer.Build(name, Normalise(Fade(x, 0.1), hard ? 0.891f : 0.5f), null, Rate, roots[k]);
            }
            return new SampleSlot(name, layers);
        }

        /// <summary>A string ensemble in stereo (five detuned saws a side through a low-pass), at C3 and C5, its sustain looped (baked seamless).</summary>
        static SampleSlot Strings(string name)
        {
            int[] roots = { 48, 72 };
            var layers = new SampleLayer[roots.Length];
            double[] detune = { -0.13, -0.06, 0.0, 0.07, 0.12 };
            for (int k = 0; k < roots.Length; k++)
            {
                double hz = 440.0 * PortableMath.Pow(2.0, (roots[k] - 69) / 12.0);
                int n = 3 * Rate;
                var l = new float[n];
                var r = new float[n];
                for (int side = 0; side < 2; side++)
                {
                    var y = side == 0 ? l : r;
                    var rng = new Rng((uint)(31 + 7 * k + side));
                    var osc = new BlepOscillator[detune.Length];
                    for (int v = 0; v < osc.Length; v++)
                    {
                        osc[v] = new BlepOscillator();
                        osc[v].Reset(rng.NextUnipolar());
                        osc[v].SetShape(2f, 0.5f);
                    }
                    var lp = new Svf { Mode = SvfMode.LowPass };
                    lp.Set(Math.Min(9000, hz * 22), Rate, 0.6);
                    var inc = new double[osc.Length];
                    for (int i = 0; i < n; i++)
                    {
                        double t = i / (double)Rate;
                        if ((i & 15) == 0)
                            for (int v = 0; v < osc.Length; v++)
                            {
                                double cents = (detune[v] + (side == 0 ? -0.02 : 0.02)) * 100 * (1 + 0.15 * Sin(0.21 * (v + 1) * t + 0.3 * v));
                                inc[v] = hz * PortableMath.Pow(2.0, cents / 1200) / Rate;
                            }
                        float s = 0;
                        for (int v = 0; v < osc.Length; v++) s += osc[v].Next(inc[v], out _, out _);
                        double attack = Math.Min(1.0, t / 0.25);
                        y[i] = (float)(lp.Process(s * 0.2f) * attack);
                    }
                }
                int ls = (int)(1.0 * Rate), le = (int)(2.8 * Rate);
                BakeLoop(l, ls, le, Rate / 4);
                BakeLoop(r, ls, le, Rate / 4);
                float peak = 0;
                for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
                for (int i = 0; i < n; i++) { l[i] *= 0.708f / peak; r[i] *= 0.708f / peak; }
                layers[k] = SampleLayer.Build(name, l, r, Rate, roots[k], ls, le, WaveLoop.Forward);
            }
            return new SampleSlot(name, layers);
        }

        static float[] Bass()
        {
            int n = (int)(1.5 * Rate);
            double hz = 440.0 * PortableMath.Pow(2.0, (36 - 69) / 12.0);
            var osc = new BlepOscillator();
            osc.SetShape(2f, 0.5f);
            var lp = new Ladder();
            var x = new float[n];
            double sub = 0;
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;
                if ((i & 15) == 0)
                {
                    double cut = 120 + 2400 * Decay(t, 0.12);
                    lp.Set(Svf.Prewarp((float)cut, Rate), 0.25f);
                }
                sub += hz / Rate;
                float v = osc.Next(hz / Rate, out _, out _) * 0.6f + (float)(0.5 * Sin(sub));
                x[i] = (float)(lp.Process(v, 1f, false) * Decay(t, 0.7) * Math.Min(1.0, t / 0.002));
            }
            return Fade(x, 0.08);
        }

        static float[] Bell()
        {
            int n = 3 * Rate;
            double hz = 440.0 * PortableMath.Pow(2.0, (72 - 69) / 12.0);
            double[] ratio = { 0.5, 1.0, 1.183, 1.506, 2.0, 2.74, 3.76, 5.4 };
            double[] amp = { 0.25, 1.0, 0.45, 0.35, 0.3, 0.2, 0.12, 0.08 };
            double[] tau = { 1.6, 1.3, 0.9, 0.7, 0.55, 0.35, 0.22, 0.12 };
            var x = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;
                double v = 0;
                for (int p = 0; p < ratio.Length; p++) v += amp[p] * Sin(hz * ratio[p] * t) * Decay(t, tau[p]);
                x[i] = (float)(v * Math.Min(1.0, t / 0.0008));
            }
            return Fade(x, 0.2);
        }

        /// <summary>Karplus–Strong at C4 (the delay tuned with a first-order allpass).</summary>
        static float[] Pluck()
        {
            int n = (int)(1.5 * Rate);
            double hz = 440.0 * PortableMath.Pow(2.0, (60 - 69) / 12.0);
            double period = Rate / hz - 0.5;   // the averaging filter's half-sample delay
            int d = (int)Math.Floor(period);
            double frac = period - d;
            double c = (1 - frac) / (1 + frac);   // allpass for the fraction
            var line = new double[d + 2];
            var rng = new Rng(24u);
            var lp = new Svf { Mode = SvfMode.LowPass };
            lp.Set(3500, Rate, 0.7);
            for (int i = 0; i < line.Length; i++) line[i] = lp.Process(rng.NextBipolar());
            var x = new float[n];
            int w = 0;
            double prev = 0, apIn = 0, apOut = 0;
            for (int i = 0; i < n; i++)
            {
                double y = line[w];
                double avg = 0.4985 * (y + prev);
                prev = y;
                double ap = c * avg + apIn - c * apOut;
                apIn = avg;
                apOut = ap;
                line[w] = ap;
                w = (w + 1) % (d + 1);
                x[i] = (float)y;
            }
            return Fade(x, 0.1);
        }

        /// <summary>An "aah": a saw through three vowel formants, at C4, its sustain looped (baked seamless).</summary>
        static SampleSlot Vox(string name)
        {
            int n = 3 * Rate;
            double hz = 440.0 * PortableMath.Pow(2.0, (60 - 69) / 12.0);
            var osc = new[] { new BlepOscillator(), new BlepOscillator() };
            osc[0].SetShape(2f, 0.5f);
            osc[1].SetShape(2f, 0.5f);
            osc[1].Reset(0.37);
            double[] f = { 730, 1090, 2440 };
            double[] g = { 1.0, 0.5, 0.25 };
            var bp = new Svf[3];
            for (int k = 0; k < 3; k++)
            {
                bp[k] = new Svf { Mode = SvfMode.BandPass };
                bp[k].Set(f[k], Rate, 6 + 2 * k);
            }
            var x = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;
                double vib = 1 + 0.004 * Sin(5.2 * t) * Math.Min(1.0, t / 0.6);
                float s = 0.5f * (osc[0].Next(hz * vib / Rate, out _, out _) + osc[1].Next(hz * vib * 1.003 / Rate, out _, out _));
                double v = 0;
                for (int k = 0; k < 3; k++) v += g[k] * bp[k].Process(s);
                x[i] = (float)(v * Math.Min(1.0, t / 0.15));
            }
            int ls = (int)(1.0 * Rate), le = (int)(2.8 * Rate);
            BakeLoop(x, ls, le, Rate / 4);
            return new SampleSlot(name, new[] { SampleLayer.Build(name, Normalise(x, 0.708f), null, Rate, 60, ls, le, WaveLoop.Forward) });
        }
    }
}
