#nullable disable
// Fork (docs/specs/mcp-server.md §12.3, §12.11, U4): the manifest editor's response plot, only where it
// can be honest. A curve is drawn only for a machine whose design is known from its source and
// computed here exactly as it filters (its small-signal response at the current settings):
//  - Pedal EQ: the Audio EQ Cookbook's biquads (ExternalMachines/source/thepedal/pedal-eq/PedalEQMachine.cs):
//    low shelf and high shelf at slope 1, two peaks with their Q, in series, or the soloed bands summed in
//    parallel, then the output gain; Bypass passes the input.
//  - Pedal Filter: Zavalishin's TPT state-variable filter, 2 or 4 poles (pedal-filter/PedalFilter.cs), with its
//    resonance compensation and DC blocker, mixed with the dry signal; or its comb (FIR or IIR) at the
//    cutoff's period. Drive and the LFO are left out (the curve is at the LFO's centre, without saturation).
// Every other effect with frequency parameters in filter or EQ roles gets markers: a line at each of those
// frequencies and a caption saying that no curve is drawn. A curve guessed from roles alone (a closed
// machine's) would be made up, so none is. Generators get nothing: a synth's filter moves with its envelope,
// key tracking and velocity, so a line at the knob's cutoff would say little; its knobs say that already.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace NeoBuzz.Editor
{
    /// <summary>The response plot of a machine's face.</summary>
    internal sealed class FaceResponse
    {
        /// <summary>A curve computed from the machine's known design; false: markers only.</summary>
        public bool Computed { get; init; }
        /// <summary>The magnitude in dB at a frequency in Hz, from the current settings (computed plots).</summary>
        public Func<double, double> DbAt { get; init; }
        /// <summary>Parameters in Hz marked as lines (on a computed plot too: its bands and corners).</summary>
        public IReadOnlyList<FaceParam> Markers { get; init; } = Array.Empty<FaceParam>();
        /// <summary>What the plot rests on, shown under it.</summary>
        public string Caption { get; init; } = "";
        public double MinDb { get; init; } = -24;
        public double MaxDb { get; init; } = 24;
        public double DbStep { get; init; } = 6;
    }

    internal static class ResponseModels
    {
        /// <summary>The gears whose response is computed from their design.</summary>
        public static readonly IReadOnlyList<string> Computed = new[] { "Pedal EQ", "Pedal Filter" };

        /// <summary>A filter or EQ frequency the plot marks.</summary>
        public static bool IsMarked(FaceParam p) =>
            p.Dimension == "Hz" && (p.Role.StartsWith("filter.", StringComparison.Ordinal) || p.Role.StartsWith("eq.", StringComparison.Ordinal));

        /// <param name="raw">A parameter's raw value now.</param>
        /// <param name="sampleRate">The engine's sample rate now.</param>
        /// <param name="generator">A generator: no markers without a model (its filter moves with each note).</param>
        public static FaceResponse For(string gear, IReadOnlyList<FaceParam> ps, Func<FaceParam, int> raw, Func<double> sampleRate, bool generator = false)
        {
            var markers = ps.Where(IsMarked).ToList();
            try
            {
                switch (gear)
                {
                    case "Pedal EQ":
                        if (PedalEq(ps, markers, raw, sampleRate) is FaceResponse eq) return eq;
                        break;
                    case "Pedal Filter":
                        if (PedalFilter(ps, markers, raw, sampleRate) is FaceResponse filter) return filter;
                        break;
                }
            }
            catch (Exception)
            {
                // A manifest without the roles the model needs: markers instead.
            }
            if (markers.Count == 0 || generator) return null;
            return new FaceResponse
            {
                Computed = false,
                Markers = markers,
                Caption = "Markers only: its response isn't computed here (no known design to compute it from), so no curve is drawn; " +
                          "the lines mark the frequencies it is set to.",
            };
        }

        static FaceParam Role(IReadOnlyList<FaceParam> ps, string role) => ps.FirstOrDefault(p => p.Role == role);

        static FaceParam Need(IReadOnlyList<FaceParam> ps, string role) =>
            Role(ps, role) ?? throw new InvalidOperationException("no " + role);

        // ------------------------------------------------------------------ Pedal EQ

        static FaceResponse PedalEq(IReadOnlyList<FaceParam> ps, List<FaceParam> markers, Func<FaceParam, int> raw, Func<double> sampleRate)
        {
            var lsF = Need(ps, "eq.low.freq"); var lsG = Need(ps, "eq.low.gain");
            var lmF = Need(ps, "eq.lowmid.freq"); var lmG = Need(ps, "eq.lowmid.gain"); var lmQ = Need(ps, "eq.lowmid.q");
            var hmF = Need(ps, "eq.highmid.freq"); var hmG = Need(ps, "eq.highmid.gain"); var hmQ = Need(ps, "eq.highmid.q");
            var hsF = Need(ps, "eq.high.freq"); var hsG = Need(ps, "eq.high.gain");
            var output = Role(ps, "level");
            var bypass = Role(ps, "bypass");
            // The four solo switches, in band order (their plain names say which).
            var solos = ps.Where(p => p.Role == "solo").ToList();
            double V(FaceParam p) => p.SafeValue(raw(p));
            return new FaceResponse
            {
                Computed = true,
                Markers = markers,
                Caption = "Computed from Pedal EQ's design (its source): Audio EQ Cookbook biquads, shelves at slope 1, in series " +
                          "(soloed bands in parallel), at the engine's sample rate.",
                DbAt = hz =>
                {
                    if (bypass != null && raw(bypass) > bypass.Min) return 0;
                    double sr = Math.Max(1, sampleRate());
                    double nyq = sr * 0.499;
                    var bands = new[]
                    {
                        Biquad.LowShelf(Math.Min(V(lsF), nyq), V(lsG), sr),
                        Biquad.Peak(Math.Min(V(lmF), nyq), V(lmG), V(lmQ), sr),
                        Biquad.Peak(Math.Min(V(hmF), nyq), V(hmG), V(hmQ), sr),
                        Biquad.HighShelf(Math.Min(V(hsF), nyq), V(hsG), sr),
                    };
                    double w = 2 * Math.PI * hz / sr;
                    Complex h;
                    bool anySolo = solos.Count == 4 && solos.Any(s => raw(s) > s.Min);
                    if (anySolo)
                    {
                        h = Complex.Zero;
                        for (int b = 0; b < 4; b++) if (raw(solos[b]) > solos[b].Min) h += bands[b].At(w);
                    }
                    else
                    {
                        h = Complex.One;
                        foreach (var b in bands) h *= b.At(w);
                    }
                    double gain = output != null ? V(output) : 0;
                    return Db(h.Magnitude) + (double.IsFinite(gain) ? gain : 0);
                },
            };
        }

        // ------------------------------------------------------------------ Pedal Filter

        static FaceResponse PedalFilter(IReadOnlyList<FaceParam> ps, List<FaceParam> markers, Func<FaceParam, int> raw, Func<double> sampleRate)
        {
            var mode = Need(ps, "filter.type");
            var slope = Need(ps, "filter.slope");
            var cutoff = Need(ps, "filter.cutoff");
            var resonance = Need(ps, "filter.resonance");
            var mix = Role(ps, "mix.wet");
            return new FaceResponse
            {
                Computed = true,
                Markers = markers,
                MinDb = -48,
                MaxDb = 18,
                Caption = "Computed from Pedal Filter's design (its source): a TPT state-variable filter (or its comb) with its resonance " +
                          "compensation and DC blocker, mixed with the dry signal; small-signal, at the LFO's centre, without Drive.",
                DbAt = hz =>
                {
                    double sr = Math.Max(1, sampleRate());
                    double fc = Math.Clamp(cutoff.SafeValue(raw(cutoff)), 20.0, sr * 0.499);
                    // Resonance 0 … 100 (%): R = 1 − res/100 · 0.995; the comb's feedback res/100 · 0.99.
                    double res = resonance.SafeValue(raw(resonance));
                    if (resonance.Dimension == "%" && resonance.Fraction) res *= 100;
                    double R = 1.0 - res / 100.0 * 0.995;
                    double wet = mix == null ? 1 : mix.Level(raw(mix));
                    int m = raw(mode) - mode.Min;
                    bool second = raw(slope) > slope.Min;
                    double w = 2 * Math.PI * hz / sr;
                    var z1 = Complex.FromPolarCoordinates(1, -w);   // z⁻¹
                    Complex filtered;
                    if (m == 4)
                    {
                        // Comb: y = x + fb·x[n−N] (FIR) or y = x + fb·y[n−N] (IIR), N = sr / cutoff.
                        double fb = res / 100.0 * 0.99;
                        var delay = Complex.FromPolarCoordinates(1, -w * Math.Clamp(sr / fc, 1.0, 65536 - 2.0));
                        filtered = second ? 1 / (1 - fb * delay) : 1 + fb * delay;
                    }
                    else
                    {
                        // TPT SVF = the bilinear transform of 1/(s² + 2Rs + 1), s = j·tan(ω/2)/g, g = tan(π fc / sr).
                        double g = Math.Tan(Math.PI * fc / sr);
                        var s = new Complex(0, Math.Tan(w / 2) / g);
                        var d = s * s + 2 * R * s + 1;
                        Complex one = m switch
                        {
                            0 => 1 / d,                    // low pass
                            1 => s * s / d,                // high pass
                            2 => s / d,                    // band pass (the SVF's, peak 1/(2R))
                            _ => (s * s + 1) / d,          // band reject (low + high)
                        };
                        filtered = second ? one * one : one;
                        filtered *= Math.Min(1.0, 2.0 * R);
                    }
                    // The DC blocker on the filtered path: (1 − z⁻¹) / (1 − 0.9995 z⁻¹).
                    filtered *= (1 - z1) / (1 - 0.9995 * z1);
                    return Db(((1 - wet) + wet * filtered).Magnitude);
                },
            };
        }

        static double Db(double magnitude) => 20 * Math.Log10(Math.Max(1e-9, magnitude));

        /// <summary>A biquad's coefficients, normalised by a0 (the Audio EQ Cookbook's).</summary>
        internal readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2)
        {
            public static readonly Biquad Identity = new Biquad(1, 0, 0, 0, 0);

            /// <summary>The response at ω (radians per sample).</summary>
            public Complex At(double w)
            {
                var z1 = Complex.FromPolarCoordinates(1, -w);
                var z2 = z1 * z1;
                return (B0 + B1 * z1 + B2 * z2) / (1 + A1 * z1 + A2 * z2);
            }

            public static Biquad Peak(double freq, double gainDb, double q, double sr)
            {
                if (Math.Abs(gainDb) < 1e-6) return Identity;
                double a = Math.Pow(10.0, gainDb / 40.0);
                double w0 = 2.0 * Math.PI * freq / sr;
                double alpha = Math.Sin(w0) / (2.0 * q);
                double cw = Math.Cos(w0);
                double a0 = 1.0 + alpha / a;
                return new Biquad((1.0 + alpha * a) / a0, -2.0 * cw / a0, (1.0 - alpha * a) / a0, -2.0 * cw / a0, (1.0 - alpha / a) / a0);
            }

            public static Biquad LowShelf(double freq, double gainDb, double sr)
            {
                if (Math.Abs(gainDb) < 1e-6) return Identity;
                double a = Math.Pow(10.0, gainDb / 40.0);
                double w0 = 2.0 * Math.PI * freq / sr;
                double cw = Math.Cos(w0), sw = Math.Sin(w0), sq = Math.Sqrt(a), alpha = sw / Math.Sqrt(2.0);
                double a0 = (a + 1) + (a - 1) * cw + 2 * sq * alpha;
                return new Biquad(
                    a * ((a + 1) - (a - 1) * cw + 2 * sq * alpha) / a0,
                    2 * a * ((a - 1) - (a + 1) * cw) / a0,
                    a * ((a + 1) - (a - 1) * cw - 2 * sq * alpha) / a0,
                    -2 * ((a - 1) + (a + 1) * cw) / a0,
                    ((a + 1) + (a - 1) * cw - 2 * sq * alpha) / a0);
            }

            public static Biquad HighShelf(double freq, double gainDb, double sr)
            {
                if (Math.Abs(gainDb) < 1e-6) return Identity;
                double a = Math.Pow(10.0, gainDb / 40.0);
                double w0 = 2.0 * Math.PI * freq / sr;
                double cw = Math.Cos(w0), sw = Math.Sin(w0), sq = Math.Sqrt(a), alpha = sw / Math.Sqrt(2.0);
                double a0 = (a + 1) - (a - 1) * cw + 2 * sq * alpha;
                return new Biquad(
                    a * ((a + 1) + (a - 1) * cw + 2 * sq * alpha) / a0,
                    -2 * a * ((a - 1) + (a + 1) * cw) / a0,
                    a * ((a + 1) + (a - 1) * cw - 2 * sq * alpha) / a0,
                    2 * ((a - 1) - (a + 1) * cw) / a0,
                    ((a + 1) - (a - 1) * cw - 2 * sq * alpha) / a0);
            }
        }
    }
}
