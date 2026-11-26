#nullable disable
// Neo Grain (docs/specs/neo-grain.md): what the grains are cut from. A GrainSource is mono audio with a margin on both sides (a wrapped copy for
// a looping source, zeros for a song wave) so the interpolator can read past its ends. The three built-in sources are made by code from our own
// maths (no recordings): a chord of detuned soft saws whose partials drift in level, a set of struck bells with the free-bar mode ratios of the
// published literature (Fletcher and Rossing), and filtered noise. Each loops without a seam (its frequencies are whole cycles of its length).

using System;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.Grain
{
    internal sealed class GrainSource
    {
        /// <summary>Frames of margin each side of the audio.</summary>
        public const int Pad = 112;
        public const int OverviewColumns = 256;

        /// <summary>Mono audio: <see cref="Pad"/> zeros (or the wrapped tail), the <see cref="Length"/> frames, <see cref="Pad"/> zeros (or the wrapped head).</summary>
        public readonly float[] Data;
        public readonly int Length;
        public readonly double Rate;
        /// <summary>Loops without a seam: a grain that runs past the end carries on from the start.</summary>
        public readonly bool Loop;
        public readonly string Name;
        /// <summary>The largest absolute value in each of <see cref="OverviewColumns"/> parts, 0 to 1 (the editor's waveform).</summary>
        public readonly float[] Overview = new float[OverviewColumns];

        public GrainSource(string name, float[] mono, int length, double rate, bool loop)
        {
            Name = name;
            Length = Math.Max(1, length);
            Rate = rate > 0 ? rate : 44100;
            Loop = loop;
            Data = new float[Length + 2 * Pad];
            for (int i = 0; i < length; i++)
            {
                float x = mono[i];
                Data[Pad + i] = float.IsFinite(x) ? x : 0f;
            }
            if (loop)
            {
                for (int i = 0; i < Pad; i++)
                {
                    Data[Pad - 1 - i] = Data[Pad + ((Length - 1 - i) % Length + Length) % Length];
                    Data[Pad + Length + i] = Data[Pad + (i % Length)];
                }
            }
            float top = 0;
            for (int c = 0; c < OverviewColumns; c++)
            {
                int a = (int)((long)c * Length / OverviewColumns), b = Math.Max(a + 1, (int)((long)(c + 1) * Length / OverviewColumns));
                float m = 0;
                for (int i = a; i < b && i < Length; i++) m = Math.Max(m, Math.Abs(Data[Pad + i]));
                Overview[c] = m;
                top = Math.Max(top, m);
            }
            if (top > 0)
                for (int c = 0; c < OverviewColumns; c++) Overview[c] /= top;
        }

        /// <summary>A song wave: two channels (the right may be null) mixed to mono.</summary>
        public static GrainSource FromWave(string name, float[] left, float[] right, int length, double rate)
        {
            var mono = new float[length];
            for (int i = 0; i < length; i++) mono[i] = right == null ? left[i] : 0.5f * (left[i] + right[i]);
            return new GrainSource(name, mono, length, rate, false);
        }

        // ── the built-in sources ────────────────────────────────────────────

        public const int BuiltInCount = 3;
        public const int BuiltInRate = 48000;
        public const int BuiltInSeconds = 6;
        public static readonly string[] BuiltInNames = { "Chord", "Bells", "Noise" };

        static readonly GrainSource[] builtIns = new GrainSource[BuiltInCount];
        static readonly object builtLock = new object();

        /// <summary>A built-in source (0 chord, 1 bells, 2 noise), made once per process; not for the audio thread (the engine takes them at construction).</summary>
        public static GrainSource BuiltIn(int kind)
        {
            kind = Math.Clamp(kind, 0, BuiltInCount - 1);
            var s = Volatile.Read(ref builtIns[kind]);
            if (s != null) return s;
            lock (builtLock)
            {
                if (builtIns[kind] == null)
                    Volatile.Write(ref builtIns[kind], kind == 0 ? MakeChord() : kind == 1 ? MakeBells() : MakeNoise());
                return builtIns[kind];
            }
        }

        /// <summary>A note's frequency snapped to a whole number of cycles in the source's length, so the loop has no seam.</summary>
        static double Snap(double hz) => Math.Max(1.0, Math.Round(hz * BuiltInSeconds)) / BuiltInSeconds;

        static GrainSource MakeChord()
        {
            int n = BuiltInRate * BuiltInSeconds;
            var x = new float[n];
            // C major with a ninth over a low C: MIDI 48 (C2), 60, 64, 67, 71, 74; C3 = 60 sounds at 261.6 Hz.
            int[] notes = { 48, 60, 64, 67, 71, 74 };
            double[] level = { 0.9, 1.0, 0.8, 0.8, 0.6, 0.5 };
            for (int k = 0; k < notes.Length; k++)
            {
                double f0 = DspMath.NoteToHz(notes[k] + 0.0);
                for (int copy = 0; copy < 2; copy++)
                {
                    double detune = copy == 0 ? -4.0 : 4.0;
                    for (int h = 1; h <= 7; h++)
                    {
                        double f = Snap(f0 * PortableMath.Pow(2.0, detune / 1200.0) * h);
                        if (f > 12000) break;
                        // The partial's level swells and fades a whole number of times over the source, so scanning it changes its colour.
                        int cycles = 1 + (k + h + copy) % 3;
                        double phase = 0.7 * (k * 7 + h * 3 + copy);
                        double amp = level[k] * 0.12 / PortableMath.Pow(h, 1.25);
                        Oscillate(x, f, amp, phase, cycles, h);
                    }
                }
            }
            return Finish("Chord", x, 0.5f, true);
        }

        /// <summary>Adds a sine of frequency <paramref name="f"/> whose level swings between 0.35 and 1 times <paramref name="amp"/>, <paramref name="cycles"/> times over the source.</summary>
        static void Oscillate(float[] x, double f, double amp, double phase, int cycles, int salt)
        {
            int n = x.Length;
            double w = 2 * Math.PI * f / BuiltInRate;
            double c = PortableMath.Cos(w), s = PortableMath.Sin(w);
            double re = PortableMath.Cos(phase), im = PortableMath.Sin(phase);
            double mw = 2 * Math.PI * cycles / n, mphase = salt * 1.3;
            double mc = PortableMath.Cos(mw), ms = PortableMath.Sin(mw), mre = PortableMath.Cos(mphase), mim = PortableMath.Sin(mphase);
            for (int i = 0; i < n; i++)
            {
                double env = 0.675 + 0.325 * mim;
                x[i] += (float)(amp * env * im);
                double nr = re * c - im * s; im = re * s + im * c; re = nr;
                double mr = mre * mc - mim * ms; mim = mre * ms + mim * mc; mre = mr;
                if ((i & 1023) == 1023)
                {
                    double g = 1.0 / Math.Sqrt(re * re + im * im); re *= g; im *= g;
                    double mg = 1.0 / Math.Sqrt(mre * mre + mim * mim); mre *= mg; mim *= mg;
                }
            }
        }

        static GrainSource MakeBells()
        {
            int n = BuiltInRate * BuiltInSeconds;
            var x = new float[n];
            // Free-bar mode ratios (Fletcher and Rossing, The Physics of Musical Instruments): 1, 2.756, 5.404, 8.933, 13.34, 18.64.
            double[] ratio = { 1.0, 2.756, 5.404, 8.933, 13.34 };
            double[] gain = { 1.0, 0.6, 0.4, 0.25, 0.15 };
            double[] t60 = { 2.4, 1.6, 1.0, 0.6, 0.35 };
            // A pentatonic phrase of strikes spread over the six seconds; a strike's tail wraps round to the start.
            int[] notes = { 72, 79, 76, 84, 74, 81, 77, 88 };
            double[] when = { 0.0, 0.7, 1.4, 2.2, 2.9, 3.7, 4.5, 5.2 };
            int tail = BuiltInRate * 3;
            for (int b = 0; b < notes.Length; b++)
            {
                double f0 = DspMath.NoteToHz(notes[b]);
                int start = (int)(when[b] * BuiltInRate);
                for (int m = 0; m < ratio.Length; m++)
                {
                    double f = f0 * ratio[m];
                    if (f > 14000) continue;
                    double w = 2 * Math.PI * f / BuiltInRate;
                    double r = PortableMath.Exp(-3.0 * 2.302585092994046 / (t60[m] * BuiltInRate));
                    double re = 1.0, im = 0.0, amp = 0.09 * gain[m], c = r * PortableMath.Cos(w), s = r * PortableMath.Sin(w), a = 1.0;
                    for (int i = 0; i < tail; i++)
                    {
                        // A raised-cosine strike of 2 ms, so the bell does not start with a click.
                        double fade = i < 96 ? 0.5 - 0.5 * PortableMath.Cos(Math.PI * i / 96.0) : 1.0;
                        int at = (start + i) % n;
                        x[at] += (float)(amp * fade * im);
                        double nr = re * c - im * s; im = re * s + im * c; re = nr;
                    }
                    _ = a;
                }
                // The first sample of the strike sits where the phase is zero, so the sine starts at zero.
            }
            return Finish("Bells", x, 0.5f, true);
        }

        static GrainSource MakeNoise()
        {
            int n = BuiltInRate * BuiltInSeconds;
            int over = BuiltInRate / 4;
            var y = new float[n + over];
            var rng = new Rng(0x6D2B79F5u);
            double lp1 = 0, lp2 = 0;
            for (int i = 0; i < y.Length; i++)
            {
                // Wind: white noise through two one-pole low-passes whose corner drifts slowly between 500 Hz and 3 kHz.
                double t = (double)i / BuiltInRate;
                double corner = 1200.0 * PortableMath.Pow(2.0, 1.3 * PortableMath.Sin(2 * Math.PI * t / 3.0) + 0.5 * PortableMath.Sin(2 * Math.PI * t / 1.5 + 1.0));
                double k = 1.0 - PortableMath.Exp(-2 * Math.PI * corner / BuiltInRate);
                lp1 += k * (rng.NextBipolar() - lp1);
                lp2 += k * (lp1 - lp2);
                y[i] = (float)lp2;
            }
            // Seamless loop: the last part of the run fades into the start (equal power).
            var x = new float[n];
            Array.Copy(y, x, n);
            for (int i = 0; i < over; i++)
            {
                double p = (i + 0.5) / over;
                double a = PortableMath.Sin(0.5 * Math.PI * p), b = PortableMath.Cos(0.5 * Math.PI * p);
                x[i] = (float)(a * y[i] + b * y[n + i]);
            }
            return Finish("Noise", x, 0.45f, true);
        }

        static GrainSource Finish(string name, float[] x, float peak, bool loop)
        {
            float top = 0;
            for (int i = 0; i < x.Length; i++) top = Math.Max(top, Math.Abs(x[i]));
            if (top > 0)
            {
                float g = peak / top;
                for (int i = 0; i < x.Length; i++) x[i] *= g;
            }
            return new GrainSource(name, x, x.Length, BuiltInRate, loop);
        }
    }
}
