#nullable disable
// Neo Filter (docs/specs/machines.md §5): what the parameters mean, in one place. The engine takes
// its coefficients, its LFO and its saturator from these functions; the editor draws, and the tests
// check, the response they give (one function, as Neo EQ's and Neo Delay's).
//
// The filters run at four times the sample rate, between two polyphase IIR halfbands each way
// (NeoBuzz.Dsp IirHalfband, minimal latency), so the drive, the ladder's tanh and the SVF's
// resonance limiter make no aliases worth measuring. Small signals see a linear filter, so the
// response at a rate fs is exactly
//     |H(f)| = Π over the two stages of |halfband(f)|² × |filter at 4 fs (f)|,
// the filter being the bilinear transform of its analog prototype, prewarped at the cutoff:
//  - SVF (one section; two alike in series for 24 dB/oct): s² + s/Q + 1 over low-pass 1,
//    band-pass s (its peak Q), high-pass s², notch s² + 1, peak (1 + s)² (unity on both sides, 2Q
//    at the cutoff: the magnitude of LP − HP, minimum phase, so it mixes with the dry signal);
//  - ladder: four one-pole stages 1/(1 + s) in a loop of gain k, the input made up by 1 + k/2
//    (Neo Poly's): LP24 (1 + k/2) / ((1 + s)⁴ + k), LP12 (1 + k/2)(1 + s)² / ((1 + s)⁴ + k).
// Resonance is the Q of the resonant pole pair: the SVF's own Q; for the ladder, the Q of the loop's
// dominant pair, whose poles are s = −1 + k^(1/4) e^(±jπ/4), so k = 4u⁴ with
// u = √(4Q² − 1) / (1 + √(4Q² − 1)): Q 0.5 is k = 0, and k → 4 as Q grows. At the top of the
// range the filter self-oscillates: the ladder at k = 4.2 (Neo Poly's top, bounded by its tanh),
// the SVF with its damping a little negative and bounded by its limiter.

using System;
using System.Numerics;
using NeoBuzz.Dsp;

namespace NeoBuzz.Filter
{
    internal enum FilterType { Ladder24, Ladder12, LowPass, BandPass, HighPass, Notch, Peak }

    internal enum FilterLfoShape { Sine, Triangle, SawUp, SawDown, Square, SampleHold, SmoothRandom }

    internal enum LfoRetrig { Free, Song, Env }

    /// <summary>A setting as the engine runs it once its smoothers have settled.</summary>
    internal struct FilterSetting
    {
        public FilterType Type;
        public bool Steep;                    // 24 dB/oct (SVF low-pass, band-pass, high-pass, notch)
        public double CutoffHz;
        public double Q;
        public bool SelfOsc;                  // Resonance at its top
        public double DriveDb;
        public double EnvOctaves, EnvSensitivityDb, EnvAttack, EnvRelease;
        public double LfoHz;                  // the LFO's rate (Sync resolved at the tempo)
        public bool LfoSynced;
        public double LfoBeats;               // one cycle in beats (synced)
        public FilterLfoShape LfoShape;
        public double LfoOctaves, LfoSpread;  // depth; the right channel's phase against the left's (cycles)
        public LfoRetrig Retrig;
        public double LfoPhase;               // cycles
        public double Mix, Gain;
    }

    /// <summary>Where the song is (valid in Work): its tick, the samples into it, the tick's length and the tempo.</summary>
    internal struct FilterTransport
    {
        public bool Playing;
        public int Tick, PosInTick, SamplesPerTick, TicksPerBeat;

        /// <summary>The song's position in beats, <paramref name="offset"/> samples into the block.</summary>
        public double Beats(int offset)
        {
            if (SamplesPerTick <= 0 || TicksPerBeat <= 0) return 0;
            return (Tick + (PosInTick + (double)offset) / SamplesPerTick) / TicksPerBeat;
        }
    }

    internal static class FilterDesign
    {
        /// <summary>The filters' rate over the sample rate.</summary>
        internal const int Oversampling = 4;

        /// <summary>The lowest and highest cutoff the modulation reaches (the highest also below 0.48 of the sample rate).</summary>
        internal const double MinHz = 8, MaxHz = 24000;

        /// <summary>The ladder's loop gain at self-oscillation (Neo Poly's top: it rings at the cutoff, bounded by the tanh).</summary>
        internal const double LadderSelfOscK = 4.2;

        /// <summary>The SVF's damping at self-oscillation: a little negative, so it starts by itself and grows until the limiter holds it.</summary>
        internal const double SvfSelfOscK = -0.1;

        /// <summary>
        /// The SVF's resonance limiter: past <see cref="LimitThreshold"/> of band amplitude (full scale; at
        /// self-oscillation <see cref="OscThreshold"/>) the damping grows by <see cref="LimitGain"/> × the
        /// excess squared, so a resonance can't ring louder than a little over it, whatever comes in.
        /// </summary>
        internal const double LimitThreshold = 2.0, OscThreshold = 0.25, LimitGain = 10;

        /// <summary>LFO sync's note values in beats (a beat is a quarter note; Free: 0).</summary>
        internal static readonly double[] SyncBeats =
        {
            0, 1.0 / 8, 1.0 / 6, 1.0 / 4, 3.0 / 8, 1.0 / 3, 1.0 / 2, 3.0 / 4, 2.0 / 3, 1, 1.5, 4.0 / 3, 2, 3, 4, 8, 16, 32, 64,
        };

        public static bool IsLadder(FilterType t) => t is FilterType.Ladder24 or FilterType.Ladder12;

        /// <summary>Whether the 24 dB/oct slope applies to a type (the SVF's low-pass, band-pass, high-pass and notch).</summary>
        public static bool TakesSlope(FilterType t) => t is FilterType.LowPass or FilterType.BandPass or FilterType.HighPass or FilterType.Notch;

        /// <summary>The setting the parameters ask for, at a tempo (for LFO sync).</summary>
        public static FilterSetting From(ParamValues v, double tempo)
        {
            var type = (FilterType)v.Choice(P.Type);
            int sync = Math.Clamp(v.Choice(P.LfoSync), 0, SyncBeats.Length - 1);
            double beats = SyncBeats[sync];
            double bps = Math.Max(1, tempo) / 60.0;
            return new FilterSetting
            {
                Type = type,
                Steep = v.Choice(P.Slope) == 1 && TakesSlope(type),
                CutoffHz = v.Value(P.Cutoff),
                Q = v.Value(P.Resonance),
                SelfOsc = v[P.Resonance] >= Params.Table[P.Resonance].Max,
                DriveDb = v.Value(P.Drive),
                EnvOctaves = v.Value(P.EnvAmount),
                EnvSensitivityDb = v.Value(P.EnvSens),
                EnvAttack = v.Value(P.EnvAttack),
                EnvRelease = v.Value(P.EnvRelease),
                LfoSynced = sync > 0,
                LfoBeats = beats,
                LfoHz = sync > 0 ? bps / beats : v.Value(P.LfoRate),
                LfoShape = (FilterLfoShape)v.Choice(P.LfoShape),
                LfoOctaves = v.Value(P.LfoDepth),
                LfoSpread = v.Value(P.LfoSpread) / 360.0,
                Retrig = (LfoRetrig)v.Choice(P.LfoRetrig),
                LfoPhase = v.Value(P.LfoPhase) / 360.0,
                Mix = v.Value(P.Mix),
                Gain = DspMath.DbToGain(v.Value(P.Output)),
            };
        }

        // ---- resonance ------------------------------------------------------------------------

        /// <summary>The ladder's loop gain k whose dominant pole pair has Q <paramref name="q"/> (0.5 → 0, growing towards 4).</summary>
        public static double LadderK(double q)
        {
            double r = Math.Sqrt(Math.Max(0, 4 * q * q - 1));
            double u = r / (1 + r);
            return 4 * u * u * u * u;
        }

        /// <summary>The Q of the ladder's dominant pole pair at loop gain k (0 … 4).</summary>
        public static double LadderQ(double k)
        {
            double u = Math.Sqrt(Math.Sqrt(Math.Clamp(k, 0, 4) / 4));
            double sigma = 1 - u;
            return sigma <= 0 ? double.PositiveInfinity : Math.Sqrt(sigma * sigma + u * u) / (2 * sigma);
        }

        /// <summary>The ladder's loop gain at a setting (the self-oscillating top blended in by <paramref name="osc"/>, 0 … 1).</summary>
        public static double LadderLoop(double q, double osc) => (1 - osc) * LadderK(q) + osc * LadderSelfOscK;

        /// <summary>The SVF's damping 1/Q at a setting (the self-oscillating top blended in by <paramref name="osc"/>).</summary>
        public static double SvfDamping(double q, double osc) => (1 - osc) / q + osc * SvfSelfOscK;

        // ---- the oversampling -----------------------------------------------------------------

        /// <summary>
        /// The two halfband stages for a sample rate: base → 2× and 2× → 4×, each 100 dB down in its stopband
        /// and flat up to 20 kHz (or 0.45 of the rate, below 44.4 kHz).
        /// </summary>
        public static IirHalfbandDesign[] Halfbands(double sampleRate)
        {
            double pass = Math.Min(20000, 0.45 * sampleRate) / (2 * sampleRate);     // of the 2× rate
            double t1 = Math.Clamp(0.5 - 2 * pass, 0.02, 0.45);
            double t2 = Math.Clamp(0.5 - pass, 0.02, 0.45);
            return new[] { new IirHalfbandDesign(100, t1), new IirHalfbandDesign(100, t2) };
        }

        /// <summary>
        /// One halfband's response at <paramref name="theta"/> radians per sample of its high rate, as the resampler
        /// runs it (float coefficients): ½ (A0(z²) + z⁻¹ A1(z²)), the even coefficients' allpasses in A0, the odd ones' in A1.
        /// </summary>
        public static Complex Halfband(IirHalfbandDesign d, double theta)
        {
            var z1 = Unit(-2 * theta);    // z⁻² of the high rate: the branches' own delay
            Complex a0 = 1, a1 = 1;
            for (int i = 0; i < d.Coefs.Length; i++)
            {
                double c = (float)d.Coefs[i];
                var ap = (c + z1) / (1 + c * z1);
                if ((i & 1) == 0) a0 *= ap;
                else a1 *= ap;
            }
            return 0.5 * (a0 + Unit(-theta) * a1);
        }

        static Complex Unit(double angle) => new Complex(PortableMath.Cos(angle), PortableMath.Sin(angle));

        /// <summary>The up-and-down resampling's magnitude at <paramref name="hz"/>: each stage's halfband twice.</summary>
        public static double ResamplingGain(IirHalfbandDesign[] stages, double hz, double sampleRate)
        {
            double g = 1;
            for (int s = 0; s < stages.Length; s++)
            {
                double theta = 2 * Math.PI * hz / (sampleRate * (2 << s));
                double m = Halfband(stages[s], theta).Magnitude;
                g *= m * m;
            }
            return g;
        }

        // ---- the response ---------------------------------------------------------------------

        /// <summary>The filter's prewarped frequency at the filters' rate: tan(π f / 4 fs).</summary>
        public static double Prewarp(double hz, double sampleRate) =>
            PortableMath.Tan(Math.PI * Math.Clamp(hz, MinHz, Math.Min(MaxHz, 0.48 * sampleRate)) / (Oversampling * sampleRate));

        /// <summary>The cutoff the engine runs for a cutoff in Hz: clamped to its reach at the sample rate.</summary>
        public static double Reach(double hz, double sampleRate) => Math.Clamp(hz, MinHz, Math.Min(MaxHz, 0.48 * sampleRate));

        /// <summary>
        /// The filter alone (at its rate, no resampling) at <paramref name="hz"/>: the analog prototype at
        /// s = j tan(π f / 4 fs) / g. Small signals; at self-oscillation the response just below it.
        /// </summary>
        public static Complex Core(FilterType type, bool steep, double cutoffHz, double q, double hz, double sampleRate, double osc = 0)
        {
            double g = Prewarp(cutoffHz, sampleRate);
            double f = Math.Clamp(hz, 0, Oversampling * sampleRate * 0.5 * 0.999999);
            double w = PortableMath.Tan(Math.PI * f / (Oversampling * sampleRate)) / g;
            var s = new Complex(0, w);
            if (IsLadder(type))
            {
                double k = Math.Min(LadderLoop(q, osc), 3.999999);
                var p = 1 / (1 + s);
                var p2 = p * p;
                var h = (1 + 0.5 * k) / (1 + k * p2 * p2);
                return type == FilterType.Ladder24 ? h * p2 * p2 : h * p2;
            }
            double kd = Math.Max(SvfDamping(q, osc), 1e-6);
            var den = s * s + kd * s + 1;
            Complex one = type switch
            {
                FilterType.LowPass => 1 / den,
                FilterType.BandPass => s / den,
                FilterType.HighPass => s * s / den,
                FilterType.Notch => (s * s + 1) / den,
                _ => (1 + s) * (1 + s) / den,
            };
            return steep ? one * one : one;
        }

        /// <summary>The magnitude the engine gives small signals at <paramref name="hz"/>: the resampling and the filter.</summary>
        public static double Magnitude(FilterType type, bool steep, double cutoffHz, double q, double hz, double sampleRate, IirHalfbandDesign[] stages, double osc = 0) =>
            ResamplingGain(stages, hz, sampleRate) * Core(type, steep, cutoffHz, q, hz, sampleRate, osc).Magnitude;

        public static double Db(FilterType type, bool steep, double cutoffHz, double q, double hz, double sampleRate, IirHalfbandDesign[] stages, double osc = 0) =>
            20 * PortableMath.Log10(Math.Max(1e-15, Magnitude(type, steep, cutoffHz, q, hz, sampleRate, stages, osc)));

        // ---- the LFO --------------------------------------------------------------------------

        /// <summary>
        /// The LFO's value (−1 … 1) at <paramref name="cycle"/> whole cycles plus <paramref name="frac"/>: the random shapes
        /// draw their points from the cycle's number, so a sweep locked to the song is the same on every pass.
        /// </summary>
        public static double Wave(FilterLfoShape shape, long cycle, double frac)
        {
            switch (shape)
            {
                case FilterLfoShape.Sine: return PortableMath.Sin(2 * Math.PI * frac);
                case FilterLfoShape.Triangle: return frac < 0.25 ? 4 * frac : frac < 0.75 ? 2 - 4 * frac : 4 * frac - 4;
                case FilterLfoShape.SawUp: return 2 * frac - 1;
                case FilterLfoShape.SawDown: return 1 - 2 * frac;
                case FilterLfoShape.Square: return frac < 0.5 ? 1 : -1;
                case FilterLfoShape.SampleHold: return Random(cycle);
                default:
                {
                    double a = Random(cycle - 1), b = Random(cycle);
                    return a + (b - a) * (0.5 - 0.5 * PortableMath.Cos(Math.PI * frac));
                }
            }
        }

        /// <summary>A random number in [−1, 1) for a cycle's number (a hash, the same everywhere).</summary>
        public static double Random(long cycle)
        {
            ulong x = unchecked((ulong)cycle * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL);
            x ^= x >> 30;
            x = unchecked(x * 0xBF58476D1CE4E5B9UL);
            x ^= x >> 27;
            x = unchecked(x * 0x94D049BB133111EBUL);
            x ^= x >> 31;
            return (x >> 11) * (2.0 / 9007199254740992.0) - 1;
        }

        // ---- the drive ------------------------------------------------------------------------

        /// <summary>
        /// The drive's saturator, before the filter: s(u)/d with u = d·x and s(u) = u / √(1 + u²), so small signals
        /// pass at unity and the curve bends from about 1/d (−Drive dBFS). Its antiderivative √(1 + u²) gives the
        /// first-order antiderivative anti-aliasing the engine runs: (s's mean over a step) = (u₀ + u₁) / (√(1+u₀²) + √(1+u₁²)).
        /// </summary>
        public static double Saturate(double x, double d)
        {
            double u = d * x;
            return u / Math.Sqrt(1 + u * u) / d;
        }

        /// <summary>The drive's share of saturation: 0 at 0 dB (clean), 1 from 3 dB up, so Drive moves continuously.</summary>
        public static double DriveShare(double db) => Math.Clamp(db / 3, 0, 1);

        // ---- for the editor and the tests -----------------------------------------------------

        /// <summary>A short summary: "Ladder 24 · 2.00 kHz · Q 0.71".</summary>
        public static string Summary(in FilterSetting s)
        {
            string type = s.Type switch
            {
                FilterType.Ladder24 => "Ladder 24",
                FilterType.Ladder12 => "Ladder 12",
                FilterType.LowPass => "SVF low-pass",
                FilterType.BandPass => "SVF band-pass",
                FilterType.HighPass => "SVF high-pass",
                FilterType.Notch => "SVF notch",
                _ => "SVF peak",
            };
            if (TakesSlope(s.Type)) type += s.Steep ? " 24" : " 12";
            string q = s.SelfOsc ? "self-osc" : "Q " + s.Q.ToString(s.Q < 10 ? "0.00" : "0.0", System.Globalization.CultureInfo.InvariantCulture);
            return $"{type} · {ParamDef.Freq(s.CutoffHz)} · {q}";
        }
    }
}
