#nullable disable
// Neo Ensemble (docs/specs/machines.md §5): what the parameters mean, in one place. The engine
// computes its delays and allpass coefficients with these functions; the editor draws, and the
// tests check, the response and the loop gain they give.
//
// Why the feedback can't run away, at any setting and however fast the LFO sweeps:
//  - Chorus, Flanger: the line takes v = x + S(fb · L(R(v))), where R averages the voices' reads
//    with linear interpolation (each a convex combination of two past samples), L is the Tone
//    one-pole y = x + a(y₁ − x), 0 ≤ a < 1 (each output a convex combination too) and S the
//    saturator (|S(u)| ≤ |u|). So with no input every sample is at most |fb| ≤ 0.99 times the
//    largest in the last Delay's worth: the loop shrinks by 0.99 or more per pass, whatever the
//    sweep does. The wet signal reads the same line with a cubic (Catmull-Rom) for clean highs,
//    outside the loop.
//  - Phaser: u = x + S(fb · HP(L(w))) one sample late, w = A^N(u). Each allpass stage is a
//    normalised lattice (its state update an orthogonal matrix), so swept at any speed it neither
//    adds nor loses energy; L and HP have gain at most 1 at every frequency; |S(u)| ≤ |u|. So the
//    loop's energy gain is at most |fb| ≤ 0.99 (the small-gain theorem): the tail always decays.
//  - Frozen (the LFO still), the loop's gain is below |fb| ≤ 0.99 at every frequency: each
//    factor's magnitude is at most 1 (the tests check every one, and the product over settings).
//  - The saturator also caps what goes round at Ceiling (+6 dBFS) whatever comes in.

using System;
using System.Numerics;
using NeoBuzz.Dsp;

namespace NeoBuzz.Ensemble
{
    internal enum EnsembleMode { Chorus, Flanger, Phaser }

    internal enum EnsembleShape { Sine, Triangle }

    /// <summary>A setting as the engine runs it once its smoothers have settled.</summary>
    internal struct EnsembleSetting
    {
        public EnsembleMode Mode;
        public int Voices, Stages;
        public double Rate;            // Hz
        public double Triangle;        // 0 sine … 1 triangle
        public double DepthOctaves;    // either side
        public double SpreadCycles;    // the right channel's LFO phase against the left's
        public double Log2Delay;       // seconds
        public double Log2Centre;      // Hz
        public double Feedback;        // −0.99 … 0.99
        public double ToneA;           // the Tone one-pole's coefficient (0: open)
        public double Mix, Gain;
    }

    internal static class EnsembleDesign
    {
        /// <summary>Depth 100 %: the delay (Chorus, Flanger) or the stages' frequency (Phaser) moves this many octaves either side.</summary>
        internal static readonly double[] SpanOctaves = { 1, 2, 3 };

        /// <summary>Sync's divisions in beats (Free: 0).</summary>
        internal static readonly double[] SyncBeats = { 0, 0.125, 1.0 / 6, 0.25, 1.0 / 3, 0.5, 2.0 / 3, 1, 2, 4, 8, 16, 32, 64 };

        internal const double MaxFeedback = 0.99;
        /// <summary>The feedback's saturator: linear up to Knee, then bending smoothly towards Ceiling, never above it.</summary>
        internal const float Knee = 1f, Ceiling = 2f;
        /// <summary>The phaser's loop high-pass (DC can't build up round the stages).</summary>
        internal const double LoopHighPassHz = 10;
        /// <summary>The high-pass on what enters the delay line (Chorus, Flanger): positive feedback can't boom below the audible range.</summary>
        internal const double InputHighPassHz = 10;
        /// <summary>The shortest delay a voice reads (the cubic read needs a sample on each side).</summary>
        internal const double MinDelaySamples = 2;
        /// <summary>The longest: Delay's top, two octaves up (Flanger, Depth 100 %).</summary>
        internal const double MaxDelaySeconds = 0.12;
        internal const double MinStageHz = 20, MaxStageFraction = 0.45;

        /// <summary>The setting the parameters ask for, at a tempo (for Sync).</summary>
        public static EnsembleSetting From(ParamValues v, double tempo, double sampleRate)
        {
            var mode = (EnsembleMode)v.Choice(P.Mode);
            int sync = v.Choice(P.Sync);
            return new EnsembleSetting
            {
                Mode = mode,
                Voices = Math.Clamp((int)v.Value(P.Voices), 1, 4),
                Stages = Math.Clamp((int)v.Value(P.Stages), 2, 12),
                Rate = sync > 0 ? Math.Max(1, tempo) / 60.0 / SyncBeats[sync] : v.Value(P.Rate),
                Triangle = v.Choice(P.Shape) == (int)EnsembleShape.Triangle ? 1 : 0,
                DepthOctaves = v.Value(P.Depth) * SpanOctaves[(int)mode],
                SpreadCycles = v.Value(P.Spread) / 360.0,
                Log2Delay = PortableMath.Log2(v.Value(P.Delay)),
                Log2Centre = PortableMath.Log2(v.Value(P.Centre)),
                Feedback = Math.Clamp(v.Value(P.Feedback) / 100.0, -MaxFeedback, MaxFeedback),
                ToneA = v[P.Tone] >= Params.Table[P.Tone].Max ? 0 : ToneCoefficient(v.Value(P.Tone), sampleRate),
                Mix = v.Value(P.Mix),
                Gain = DspMath.DbToGain(v.Value(P.Output)),
            };
        }

        /// <summary>The LFO at a phase (cycles): sine, triangle, or between them while Shape changes. −1 … 1.</summary>
        public static double Wave(double phase, double triangle)
        {
            phase -= Math.Floor(phase);
            double sine = PortableMath.Sin(2 * Math.PI * phase);
            if (triangle <= 0) return sine;
            double tri = phase < 0.25 ? 4 * phase : phase < 0.75 ? 2 - 4 * phase : 4 * phase - 4;
            return sine + (tri - sine) * triangle;
        }

        /// <summary>
        /// The LFO phase (cycles) of a channel's voice, the left channel's first voice at <paramref name="phase"/>.
        /// A channel's voices sit evenly round the cycle; the right channel's are Spread on from the left's,
        /// within the gap between two voices (one voice: Spread itself; two: at 180° the right's fall halfway
        /// between the left's, so the channels differ most).
        /// </summary>
        public static double VoicePhase(double phase, int channel, int voice, int voices, double spreadCycles) =>
            phase + (voice + channel * spreadCycles) / voices;

        /// <summary>A voice's delay in samples, for its LFO value (Chorus, Flanger).</summary>
        public static double DelaySamples(double log2Delay, double depthOctaves, double lfo, double sampleRate) =>
            Math.Clamp(PortableMath.Exp2(log2Delay + depthOctaves * lfo) * sampleRate, MinDelaySamples, MaxDelaySeconds * sampleRate);

        /// <summary>The stages' frequency in Hz, for an LFO value (Phaser).</summary>
        public static double StageHz(double log2Centre, double depthOctaves, double lfo, double sampleRate) =>
            Math.Clamp(PortableMath.Exp2(log2Centre + depthOctaves * lfo), MinStageHz, MaxStageFraction * sampleRate);

        /// <summary>
        /// An allpass stage's coefficient for its frequency: k = (t − 1)/(t + 1), t = tan(πf/fs), so it
        /// turns the phase by 90° there (the bilinear transform's first-order allpass, prewarped).
        /// </summary>
        public static double StageK(double hz, double sampleRate)
        {
            double t = PortableMath.Tan(Math.PI * hz / sampleRate);
            return (t - 1) / (t + 1);
        }

        /// <summary>A TPT one-pole's coefficient for a corner frequency: G = tan(πf/fs) / (1 + tan(πf/fs)).</summary>
        public static double OnePoleG(double hz, double sampleRate)
        {
            double t = PortableMath.Tan(Math.PI * Math.Clamp(hz, 1, MaxStageFraction * sampleRate) / sampleRate);
            return t / (1 + t);
        }

        /// <summary>
        /// The Tone one-pole y = x + a(y₁ − x) with its −3 dB point exactly at <paramref name="hz"/>:
        /// 1 − cos ω = K, a = 1 + K − √((1 + K)² − 1). Above about 0.49 fs it can go no higher.
        /// </summary>
        public static double ToneCoefficient(double hz, double sampleRate)
        {
            double k = 1 - PortableMath.Cos(2 * Math.PI * Math.Min(hz, 0.49 * sampleRate) / sampleRate);
            return 1 + k - Math.Sqrt((1 + k) * (1 + k) - 1);
        }

        /// <summary>The saturator in the feedback path: the identity up to Knee, then Knee + r/(1 + r) above it
        /// (r the excess), so its slope is 1 at the knee and falls after it, and it never passes Ceiling.</summary>
        public static float Saturate(float u)
        {
            float a = Math.Abs(u);
            if (a <= Knee) return u;
            if (!(a < 1e30f)) return float.IsNaN(u) ? 0f : u < 0 ? -Ceiling : Ceiling;
            float r = (a - Knee) * (1f / (Ceiling - Knee));
            float y = Knee + (Ceiling - Knee) * (r / (1f + r));
            return u < 0 ? -y : y;
        }

        // ---- responses (the editor's picture, the tests' reference): linear, the LFO frozen ----

        static Complex Z1(double w) => new Complex(PortableMath.Cos(w), -PortableMath.Sin(w));   // z⁻¹ at ω

        /// <summary>The Tone one-pole: (1 − a)/(1 − a z⁻¹).</summary>
        public static Complex Tone(double a, Complex z1) => (1 - a) / (1 - a * z1);

        /// <summary>A TPT one-pole low-pass: G(z + 1)/(z − 1 + 2G).</summary>
        public static Complex LowPass(double g, Complex z1) => g * (1 + z1) / (1 - (1 - 2 * g) * z1);

        /// <summary>A first-order allpass: (k + z⁻¹)/(1 + k z⁻¹).</summary>
        public static Complex Allpass(double k, Complex z1) => (k + z1) / (1 + k * z1);

        /// <summary>
        /// The engine's read of a voice <paramref name="tau"/> samples back: a Catmull-Rom cubic (the wet
        /// signal) or a straight line (the loop) between its two nearest samples.
        /// </summary>
        public static Complex Read(double tau, double w, bool cubic = true)
        {
            double x = Math.Max(1, tau - 1);
            int i = (int)x;
            double f = x - i;
            double c0 = 0, c1 = 1 - f, c2 = f, c3 = 0;
            if (cubic)
            {
                c0 = -0.5 * f + f * f - 0.5 * f * f * f;
                c1 = 1 - 2.5 * f * f + 1.5 * f * f * f;
                c2 = 0.5 * f + 2 * f * f - 1.5 * f * f * f;
                c3 = -0.5 * f * f + 0.5 * f * f * f;
            }
            // Read(k) is the sample k + 1 behind the one being written: the taps are i … i + 3 behind.
            Complex z1 = Z1(w);
            var zi = new Complex(PortableMath.Cos(w * i), -PortableMath.Sin(w * i));
            return zi * (c0 + z1 * (c1 + z1 * (c2 + z1 * c3)));
        }

        static Complex Power(Complex a, int n)
        {
            Complex r = Complex.One;
            for (int k = 0; k < n; k++) r *= a;
            return r;
        }

        /// <summary>
        /// The loop's transfer at ω (rad/sample) for one channel: fb · L · (the voices' linear reads averaged),
        /// or fb · z⁻¹ · HP · L · A^N for the phaser. <paramref name="taus"/>: the voices' delays (samples);
        /// <paramref name="stageK"/>: the stages' coefficient.
        /// </summary>
        public static Complex Loop(EnsembleMode mode, double fb, double toneA, double hpG, double[] taus, int voices, double stageK, int stages, double w)
        {
            Complex z1 = Z1(w);
            Complex tone = Tone(toneA, z1);
            if (mode == EnsembleMode.Phaser)
                return fb * z1 * (1 - LowPass(hpG, z1)) * tone * Power(Allpass(stageK, z1), stages);
            Complex d = Complex.Zero;
            for (int v = 0; v < voices; v++) d += Read(taus[v], w, cubic: false);
            return fb * tone * d / voices;
        }

        /// <summary>The output over the input at ω for one channel, in the linear range (no saturation). <paramref name="inG"/>:
        /// the input high-pass's coefficient (Chorus, Flanger).</summary>
        public static Complex Response(EnsembleMode mode, double fb, double toneA, double hpG, double inG, double[] taus, int voices, double stageK, int stages,
            double mix, double gain, double w)
        {
            Complex z1 = Z1(w);
            Complex loop = Loop(mode, fb, toneA, hpG, taus, voices, stageK, stages, w);
            Complex wet;
            if (mode == EnsembleMode.Phaser) wet = Power(Allpass(stageK, z1), stages) / (1 - loop);
            else
            {
                Complex d = Complex.Zero;
                for (int v = 0; v < voices; v++) d += Read(taus[v], w);
                // The input enters the line through its high-pass.
                wet = Math.Sqrt(voices) * Tone(toneA, z1) * d / voices * (1 - LowPass(inG, z1)) / (1 - loop);
            }
            return gain * ((1 - mix) + mix * wet);
        }

        /// <summary>
        /// The response of a setting at <paramref name="hz"/>, the LFO frozen at <paramref name="phase"/> (the left
        /// channel's first voice), for a channel.
        /// </summary>
        public static double Magnitude(in EnsembleSetting s, double sampleRate, double phase, int channel, double hz)
        {
            var db = new double[1];
            Curve(s, sampleRate, phase, channel, new[] { hz }, db, new double[4]);
            return PortableMath.Pow(10, db[0] / 20);
        }

        /// <summary>
        /// The response in dB at each of <paramref name="hz"/>, the LFO frozen at <paramref name="phase"/>, for a
        /// channel: what the editor draws (<paramref name="taus"/>: room for four delays).
        /// </summary>
        public static void Curve(in EnsembleSetting s, double sampleRate, double phase, int channel, double[] hz, double[] db, double[] taus)
        {
            int voices = s.Mode == EnsembleMode.Chorus ? s.Voices : 1;
            for (int v = 0; v < voices; v++)
                taus[v] = (float)DelaySamples(s.Log2Delay, s.DepthOctaves, Wave(VoicePhase(phase, channel, v, voices, s.SpreadCycles), s.Triangle), sampleRate);
            double k = (float)StageK(StageHz(s.Log2Centre, s.DepthOctaves, Wave(VoicePhase(phase, channel, 0, 1, s.SpreadCycles), s.Triangle), sampleRate), sampleRate);
            double hpG = (float)OnePoleG(LoopHighPassHz, sampleRate), inG = (float)OnePoleG(InputHighPassHz, sampleRate);
            for (int i = 0; i < hz.Length; i++)
            {
                double w = 2 * Math.PI * Math.Clamp(hz[i], 1, 0.4999 * sampleRate) / sampleRate;
                double m = Response(s.Mode, s.Feedback, (float)s.ToneA, hpG, inG, taus, voices, k, s.Stages, s.Mix, s.Gain, w).Magnitude;
                db[i] = m > 1e-12 ? 20 * PortableMath.Log10(m) : -240;
            }
        }
    }
}
