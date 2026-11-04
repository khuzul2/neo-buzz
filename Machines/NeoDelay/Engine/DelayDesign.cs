#nullable disable
// Neo Delay (docs/specs/machines.md §5): what the parameters mean, in one place. The engine takes
// its delays, filter coefficients and saturator from these functions; the editor draws, and the
// tests check, the echoes and the response they give.
//
// The loop, per line: w = S(x + fb · F(R(w))), where R reads the line at the delay (linear
// interpolation: a convex combination of two past samples), F is the loop's filters (the high cut,
// two one-poles y = x + a(y₁ − x), each a convex combination too; the low cut, a Butterworth
// high-pass) and S the tape saturator (|S(u)| ≤ |u|, slope ≤ 1, never past Ceiling × threshold).
// Ping-pong and Cross take the other line's echo instead of the line's own.
//  - Bounded, always: whatever comes in and however the time, the modulation or any parameter
//    moves, nothing the line holds passes the saturator's ceiling (+6 dB over the threshold, so
//    +6 dBFS at Drive 0 dB), and an echo is at most what the line holds times the filters' gain.
//  - Decaying, with the low cut off: with no input every sample written is at most |fb| ≤ 0.99
//    times the loudest the loop holds (the lines and the one-poles' states), whatever the time
//    and the wow do: each pass shrinks by 0.99 or more (the argument of Neo Ensemble's delay loop).
//  - Decaying, with the low cut on: a high-pass's taps sum to up to 2 in magnitude, so the
//    argument above doesn't hold for it; its gain is at most 1 at every frequency, though, as is
//    every other factor's (the read at a fixed delay, the one-poles), and |S(u)| ≤ |u|: with the
//    time still, the loop's energy shrinks by fb² or more a pass (the small-gain theorem). While the
//    time glides or the wow moves the tests measure the decay (machines.md, Neo Delay).

using System;
using System.Collections.Generic;
using System.Numerics;
using NeoBuzz.Dsp;

namespace NeoBuzz.Delay
{
    internal enum DelayMode { Stereo, PingPong, Cross }

    internal enum DelayFeel { Straight, Dotted, Triplet }

    /// <summary>A setting as the engine runs it once its smoothers have settled.</summary>
    internal struct DelaySetting
    {
        public DelayMode Mode;
        public double SecondsL, SecondsR;     // the lines' delays
        public bool Synced;
        public double Beats;                  // the note value in beats (Synced)
        public double Feedback;               // −0.99 … 0.99
        public bool LowCutOn;
        public double LowCutHz;
        public double HighCutA;               // the high cut's one-pole coefficient (0: open)
        public double HighCutHz;
        public double Threshold;              // the saturator's knee (linear): 10^(−Drive/20)
        public double ModSeconds, ModRate;
        public double DuckDb, DuckThresholdDb, DuckRelease;
        public double Width, Mix, Gain;
    }

    /// <summary>One echo of an impulse: when, on which output channel, how loud (linear, the filters left out).</summary>
    internal readonly record struct Echo(double Seconds, int Channel, double Gain);

    internal static class DelayDesign
    {
        /// <summary>Sync's note values in beats (a beat is a quarter note; Free: 0).</summary>
        internal static readonly double[] SyncBeats = { 0, 1.0 / 16, 1.0 / 8, 1.0 / 4, 1.0 / 2, 1, 2, 4, 8 };

        /// <summary>Feel: straight, dotted (× 1.5), triplet (× 2/3).</summary>
        internal static readonly double[] FeelFactor = { 1, 1.5, 2.0 / 3 };

        internal const double MaxFeedback = 0.99;
        /// <summary>The shortest and longest delay a line runs (Time's range, and Offset's room to double it).</summary>
        internal const double MinSeconds = 0.001, MaxSeconds = 10;
        /// <summary>The wow at most moves this far either way (Mod depth's top), and never more than half the delay.</summary>
        internal const double MaxModSeconds = 0.005;
        /// <summary>The saturator: linear up to Knee × threshold, then bending smoothly towards Ceiling × threshold, never above it.</summary>
        internal const float Knee = 1f, Ceiling = 2f;
        /// <summary>The low cut's Q: Butterworth (maximally flat, so its gain is at most 1 at every frequency).</summary>
        internal const double LowCutQ = 0.70710678118654752;
        /// <summary>Ducking reaches its depth at the threshold and fades in over this many dB below it.</summary>
        internal const double DuckKneeDb = 12;
        /// <summary>The input envelope's attack (s): ducking catches a transient within a millisecond.</summary>
        internal const double DuckAttack = 0.0005;

        /// <summary>A note value in beats.</summary>
        public static double Beats(int sync, int feel) => SyncBeats[Math.Clamp(sync, 0, SyncBeats.Length - 1)] * FeelFactor[Math.Clamp(feel, 0, 2)];

        /// <summary>The setting the parameters ask for, at a tempo (for Sync).</summary>
        public static DelaySetting From(ParamValues v, double tempo, double sampleRate)
        {
            int sync = v.Choice(P.Sync), feel = v.Choice(P.Feel);
            double beats = Beats(sync, feel);
            double left = sync > 0 ? beats * 60.0 / Math.Max(1, tempo) : v.Value(P.Time);
            double right = left * (1 + v.Value(P.Offset) / 100);
            bool lowOn = v[P.LowCut] > Params.Table[P.LowCut].Min;
            bool highOpen = v[P.HighCut] >= Params.Table[P.HighCut].Max;
            return new DelaySetting
            {
                Mode = (DelayMode)v.Choice(P.Mode),
                SecondsL = Math.Clamp(left, MinSeconds, MaxSeconds),
                SecondsR = Math.Clamp(right, MinSeconds, MaxSeconds),
                Synced = sync > 0,
                Beats = beats,
                Feedback = Math.Clamp(v.Value(P.Feedback) / 100.0, -MaxFeedback, MaxFeedback),
                LowCutOn = lowOn,
                LowCutHz = v.Value(P.LowCut),
                HighCutA = highOpen ? 0 : HighCutCoefficient(v.Value(P.HighCut), sampleRate),
                HighCutHz = highOpen ? double.PositiveInfinity : v.Value(P.HighCut),
                Threshold = DspMath.DbToGain(-v.Value(P.Drive)),
                ModSeconds = v.Value(P.ModDepth) / 1000.0,
                ModRate = v.Value(P.ModRate),
                DuckDb = v.Value(P.Duck),
                DuckThresholdDb = v.Value(P.DuckThresh),
                DuckRelease = v.Value(P.DuckRelease),
                Width = v.Value(P.Width) / 100.0,
                Mix = v.Value(P.Mix),
                Gain = DspMath.DbToGain(v.Value(P.Output)),
            };
        }

        /// <summary>The delay in samples a line settles on: the time rounded to a whole sample, so a settled line reads exactly.</summary>
        public static double Samples(double seconds, double sampleRate) =>
            Math.Clamp(Math.Round(seconds * sampleRate), Math.Ceiling(MinSeconds * sampleRate), Math.Floor(MaxSeconds * sampleRate));

        /// <summary>The longest a line must hold (samples): the longest delay plus the wow and the interpolation's room.</summary>
        public static int LineSamples(double sampleRate) => (int)Math.Ceiling((MaxSeconds + MaxModSeconds) * sampleRate) + 8;

        // ---- the filters ----------------------------------------------------------------------

        /// <summary>
        /// The high cut's one-pole y = x + a(y₁ − x) such that two in series are −3 dB at <paramref name="hz"/>
        /// exactly (each −1.5 dB): (1 − a)² = g²(1 − 2a cos ω + a²), g² = 1/√2, so a = B − √(B² − 1),
        /// B = (1 − g² cos ω)/(1 − g²). Each tap is ≥ 0 and they sum to 1: a convex combination.
        /// </summary>
        public static double HighCutCoefficient(double hz, double sampleRate)
        {
            const double g2 = 0.70710678118654752;
            double c = PortableMath.Cos(2 * Math.PI * Math.Min(hz, 0.49 * sampleRate) / sampleRate);
            double b = (1 - g2 * c) / (1 - g2);
            return b - Math.Sqrt(b * b - 1);
        }

        /// <summary>The low cut's TPT state-variable coefficient g = tan(πf/fs) (the bilinear transform, prewarped: −3 dB at f exactly).</summary>
        public static double LowCutG(double hz, double sampleRate) => PortableMath.Tan(Math.PI * Math.Clamp(hz, 1, 0.45 * sampleRate) / sampleRate);

        // ---- the saturator --------------------------------------------------------------------

        /// <summary>
        /// The tape saturator at a threshold t (the knee, linear): u up to t, then t·(1 + r/(1 + r)) above it
        /// (r the excess over t, in units of t), so its slope is 1 at the knee and falls after it, |S(u)| ≤ |u|,
        /// and it never passes Ceiling·t. NaN is 0; infinity the ceiling.
        /// </summary>
        public static float Saturate(float u, float t)
        {
            float a = Math.Abs(u);
            if (a <= Knee * t) return u;
            if (!(a < 1e30f)) return float.IsNaN(u) ? 0f : u < 0 ? -Ceiling * t : Ceiling * t;
            float r = (a - Knee * t) / ((Ceiling - Knee) * t);
            float y = t * (Knee + (Ceiling - Knee) * (r / (1f + r)));
            return u < 0 ? -y : y;
        }

        // ---- responses (the editor's picture, the tests' reference) ----------------------------

        static Complex Z1(double w) => new Complex(PortableMath.Cos(w), -PortableMath.Sin(w));

        /// <summary>The high cut: two one-poles, ((1 − a)/(1 − a z⁻¹))².</summary>
        public static Complex HighCut(double a, Complex z1)
        {
            Complex h = (1 - a) / (1 - a * z1);
            return h * h;
        }

        /// <summary>The low cut: the bilinear transform of s²/(s² + s/Q + 1), prewarped at the corner (g = tan(πf/fs)).</summary>
        public static Complex LowCut(double g, double w)
        {
            // s = (1 − z⁻¹)/(1 + z⁻¹)/g = j·tan(ω/2)/g.
            var s = new Complex(0, PortableMath.Tan(Math.Min(w, Math.PI * 0.999999) / 2) / g);
            return s * s / (s * s + s / LowCutQ + 1);
        }

        /// <summary>One pass through the loop's filters at ω (rad/sample): what the first echo is over the input.</summary>
        public static Complex Pass(double highCutA, bool lowCutOn, double lowCutG, double w)
        {
            Complex h = HighCut(highCutA, Z1(w));
            return lowCutOn ? h * LowCut(lowCutG, w) : h;
        }

        /// <summary>One pass's level in dB at <paramref name="hz"/> for a setting (what the editor draws).</summary>
        public static double PassDb(in DelaySetting s, double sampleRate, double hz)
        {
            double w = 2 * Math.PI * Math.Clamp(hz, 1, 0.4999 * sampleRate) / sampleRate;
            double m = Pass(s.HighCutA, s.LowCutOn, LowCutG(s.LowCutHz, sampleRate), w).Magnitude;
            return m > 1e-12 ? 20 * PortableMath.Log10(m) : -240;
        }

        /// <summary>
        /// The echoes of an impulse into both inputs (a centred sound), in time order, the filters left out, the lines at the
        /// whole samples they settle on: each line
        /// gives its echo on its own side its delay after it took the sound, and feeds Feedback times it back into its
        /// own line (Stereo) or the other (Ping-pong, Cross). Ping-pong's input enters the left line only. Stops below
        /// <paramref name="floor"/>, after <paramref name="seconds"/> or <paramref name="count"/> echoes.
        /// </summary>
        public static List<Echo> Echoes(in DelaySetting s, double sampleRate, double seconds, int count, double floor = 0.001)
        {
            var result = new List<Echo>();
            var pending = new List<(double T, int Line, double G)>();
            if (s.Mode == DelayMode.PingPong) pending.Add((0, 0, 1));
            else { pending.Add((0, 0, 1)); pending.Add((0, 1, 1)); }
            while (pending.Count > 0 && result.Count < count)
            {
                int k = 0;
                for (int i = 1; i < pending.Count; i++) if (pending[i].T < pending[k].T || (pending[i].T == pending[k].T && pending[i].Line < pending[k].Line)) k = i;
                var (t, line, g) = pending[k];
                pending.RemoveAt(k);
                double at = t + Samples(line == 0 ? s.SecondsL : s.SecondsR, sampleRate) / sampleRate;
                if (at > seconds || Math.Abs(g) < floor) continue;
                result.Add(new Echo(at, line, g));
                int next = s.Mode == DelayMode.Stereo ? line : 1 - line;
                if (s.Feedback != 0) pending.Add((at, next, g * s.Feedback));
            }
            result.Sort((x, y) => x.Seconds != y.Seconds ? x.Seconds.CompareTo(y.Seconds) : x.Channel.CompareTo(y.Channel));
            return result;
        }

        /// <summary>"1/8 dotted", "1/4 triplet", "1 bar", for the editor's summary.</summary>
        public static string NoteName(int sync, int feel)
        {
            if (sync <= 0) return "free";
            string name = Params.Table[P.Sync].Names[sync];
            return feel == 1 ? name + " dotted" : feel == 2 ? name + " triplet" : name;
        }
    }
}
