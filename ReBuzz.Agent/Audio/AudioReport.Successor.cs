#nullable disable
// Agent server (docs/specs/mcp-server.md §12.5, U5): machine_replace's verdict. The sound before and
// after a replacement measured against the pair's tolerance (its successor mapping): integrated
// loudness, and each third octave over the audible range, level-matched (the shape, apart from the
// loudness), ignoring bands far below the loudest in both.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using NeoBuzz.Analysis;
using ReBuzz.Agent.Model;

namespace ReBuzz.Agent.Audio
{
    public static partial class AudioReport
    {
        /// <summary>
        /// {loudness_db {before, after, difference}, worst_band {hz, difference_db}, bands_db {hz: difference},
        /// tolerance, within (null when either side is silent), reads[]}.
        /// </summary>
        /// <param name="beforeInput">With <paramref name="afterInput"/>: what went into the machine in each render (an effect's inputs).
        /// Then each side is read against its own input (output minus input, in loudness and per band), so a source that plays
        /// differently from render to render (noise, free-running oscillators) doesn't count as a difference of the machines.</param>
        public static JsonObject Tolerance(StereoAudio before, StereoAudio after, SuccessorTolerance tol, string what = "the sound",
            StereoAudio beforeInput = null, StereoAudio afterInput = null)
        {
            var x = new Analysed(before);
            var y = new Analysed(after);
            var xi = beforeInput == null ? null : new Analysed(beforeInput);
            var yi = afterInput == null ? null : new Analysed(afterInput);
            bool relative = xi != null && yi != null && !xi.Silent && !yi.Silent;
            var result = new JsonObject
            {
                ["tolerance"] = new JsonObject
                {
                    ["loudness_db"] = tol.LoudnessDb,
                    ["band_db"] = tol.BandDb is double b ? b : null,
                    ["from_hz"] = tol.FromHz,
                    ["to_hz"] = tol.ToHz,
                },
            };
            var reads = new JsonArray();
            result["reads"] = reads;
            if (x.Silent || y.Silent)
            {
                result["within"] = null;
                result["silent"] = x.Silent && y.Silent ? "both" : x.Silent ? "before" : "after";
                reads.Add(x.Silent && y.Silent ? $"Nothing played through {what} in the range rendered: there is nothing to compare. Render a part where it plays (from/to)."
                        : x.Silent ? $"Before, {what} was silent (a placeholder, or nothing playing through it): there is nothing to compare."
                        : $"After, {what} is silent: the replacement doesn't sound. Undo, and check the connections and settings.");
                return result;
            }
            var lx = x.LoudnessResult;
            var ly = y.LoudnessResult;
            var sx = x.SpectrumResult;
            var sy = y.SpectrumResult;
            bool byLoudness = !double.IsInfinity(lx.Integrated) && !double.IsInfinity(ly.Integrated)
                              && (!relative || (!double.IsInfinity(xi.LoudnessResult.Integrated) && !double.IsInfinity(yi.LoudnessResult.Integrated)));
            double Level(Analysed a) => byLoudness ? a.LoudnessResult.Integrated : a.SpectrumResult.TotalDb;
            double la = Level(x), lb = Level(y);
            double dl = lb - la;
            // Against its input: the machine's gain (output over input) after against before.
            if (relative) dl = (lb - Level(yi)) - (la - Level(xi));
            result["loudness_db"] = new JsonObject
            {
                ["before"] = Num(la, 2), ["after"] = Num(lb, 2), ["difference"] = Num(dl, 2),
                ["by"] = (byLoudness ? "integrated LUFS" : "total level (too short for integrated loudness)") + (relative ? ", each against its input" : ""),
            };
            double maxX = sx.ThirdOctaveDb.Where(v => !double.IsInfinity(v)).DefaultIfEmpty(double.NegativeInfinity).Max();
            double maxY = sy.ThirdOctaveDb.Where(v => !double.IsInfinity(v)).DefaultIfEmpty(double.NegativeInfinity).Max();
            var bands = new JsonObject();
            double worst = 0, worstHz = double.NaN;
            int checkedBands = 0;
            for (int i = 0; i < sx.ThirdOctaveHz.Length; i++)
            {
                double hz = sx.ThirdOctaveHz[i];
                if (hz < tol.FromHz || hz > tol.ToHz) continue;
                double a = sx.ThirdOctaveDb[i], c = sy.ThirdOctaveDb[i];
                if (double.IsInfinity(a) || double.IsInfinity(c) || a < maxX - tol.FloorDb || c < maxY - tol.FloorDb) continue;
                double d = c - a - dl;
                if (relative)
                {
                    double ai = xi.SpectrumResult.ThirdOctaveDb[i], ci = yi.SpectrumResult.ThirdOctaveDb[i];
                    double maxI = xi.SpectrumResult.ThirdOctaveDb.Where(v => !double.IsInfinity(v)).DefaultIfEmpty(double.NegativeInfinity).Max();
                    // A band the input hardly reaches says nothing about the machine's response there.
                    if (double.IsInfinity(ai) || double.IsInfinity(ci) || ai < maxI - tol.FloorDb) continue;
                    d = (c - ci) - (a - ai) - dl;
                }
                checkedBands++;
                bands[hz.ToString(System.Globalization.CultureInfo.InvariantCulture)] = Num(d, 1);
                if (Math.Abs(d) > Math.Abs(worst)) { worst = d; worstHz = hz; }
            }
            result["bands_db"] = bands;
            result["bands_checked"] = checkedBands;
            if (!double.IsNaN(worstHz)) result["worst_band"] = new JsonObject { ["hz"] = worstHz, ["difference_db"] = Num(worst, 1) };
            bool loudOk = Math.Abs(dl) <= tol.LoudnessDb;
            bool bandsOk = tol.BandDb is not double limit || checkedBands == 0 || Math.Abs(worst) <= limit;
            result["loudness_within"] = loudOk;
            result["bands_within"] = bandsOk;
            result["within"] = loudOk && bandsOk;
            reads.Add($"Loudness {Signed(dl)} dB after against before{(relative ? ", each against its input" : "")} ({F(lb)} vs {F(la)} {(byLoudness ? "LUFS" : "dB")}): " +
                      (loudOk ? $"within ±{F(tol.LoudnessDb)} dB." : $"outside ±{F(tol.LoudnessDb)} dB."));
            if (tol.BandDb is double bandLimit && checkedBands > 0)
                reads.Add($"Level-matched, the third octaves from {F(tol.FromHz, 0)} Hz to {F(tol.ToHz / 1000, 1)} kHz differ by at most {F(Math.Abs(worst))} dB " +
                          $"(at {F(worstHz, 0)} Hz): {(bandsOk ? "within" : "outside")} ±{F(bandLimit)} dB.");
            return result;
        }
    }
}
