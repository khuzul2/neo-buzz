#nullable disable
// Fork (docs/specs/stem-separation.md): separating a recording into stems with an installed model. The audio is converted to the model's
// rate (Resampler), run in segments through each of the model's ONNX files (OverlapAdd with OnnxStemPass), and the stems go back to the
// rate the recording came in. Results are cached per (audio, model) on disk (StemCache). Runs on a worker thread; progress and cancel.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ReBuzz.Core.AudioLibrary.Stems
{
    /// <summary>The stems of one separation: name to the left and right signals, at the recording's own rate and length.</summary>
    internal sealed class StemResult
    {
        public Dictionary<string, (float[] Left, float[] Right)> Stems { get; } = new(StringComparer.Ordinal);
        public bool FromCache { get; set; }
        public double Seconds { get; set; }
    }

    internal static class StemSeparator
    {
        /// <summary>The longest recording a separation takes (the stems of an hour would not fit in memory).</summary>
        public const int MaxMinutes = 20;

        /// <summary>The canonical stem names, in the order the app lists them.</summary>
        public static readonly string[] AllStems = { "vocals", "drums", "bass", "other" };

        /// <summary>Normalises a list of requested stems (null, empty or "all" is all four); throws on a name that isn't one.</summary>
        public static string[] Normalise(IEnumerable<string> requested)
        {
            var list = (requested ?? Enumerable.Empty<string>()).Select(s => (s ?? "").Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToList();
            if (list.Count == 0 || list.Contains("all")) return AllStems;
            foreach (var s in list)
                if (!AllStems.Contains(s)) throw new ArgumentException($"\"{s}\" isn't a stem: vocals, drums, bass, other or all.");
            return AllStems.Where(list.Contains).ToArray();
        }

        public static IChunkModel Open(StemModelManifest m, StemPass pass)
        {
            var options = OnnxStemPass.MakeOptions();
            return m.Family == StemModelManifest.FamilyDemucs ? new HtDemucsPass(m, pass, options) : new SpecMaskPass(m, pass, options);
        }

        /// <summary>
        /// Separates the recording. <paramref name="cacheKey"/> (the audio's content id) names the cached result; null does without the
        /// cache. Throws <see cref="OperationCanceledException"/> when cancelled, and an exception with a readable message when the model
        /// can't run.
        /// </summary>
        public static StemResult Separate(StemModelManifest model, float[] left, float[] right, int rate, IEnumerable<string> stems,
            string cacheKey, Action<double> progress, CancellationToken cancel, Func<StemModelManifest, StemPass, IChunkModel> open = null)
        {
            var wanted = Normalise(stems);
            int length = left.Length;
            if (length == 0) throw new ArgumentException("There is no audio to separate.");
            if (length > (long)MaxMinutes * 60 * rate) throw new ArgumentException($"A separation takes at most {MaxMinutes} minutes of audio.");
            var started = System.Diagnostics.Stopwatch.StartNew();
            var result = new StemResult();
            if (cacheKey != null && StemCache.TryLoad(cacheKey, model, wanted, length, rate, result))
            {
                result.FromCache = true;
                progress?.Invoke(1);
                return result;
            }
            bool mono = ReferenceEquals(left, right);
            float[] mL = Resampler.Resample(left, rate, model.SampleRate);
            float[] mR = mono ? mL : Resampler.Resample(right, rate, model.SampleRate);
            int mLength = mL.Length;
            var passes = model.Passes.Where(p => p.Outputs.Keys.Any(wanted.Contains)).ToList();
            for (int pi = 0; pi < passes.Count; pi++)
            {
                cancel.ThrowIfCancellationRequested();
                var pass = passes[pi];
                using var disposable = (open ?? Open)(model, pass) as IDisposable;
                var chunk = disposable as IChunkModel;
                var targets = (chunk as OnnxStemPass)?.Targets ?? model.Stems.Where(pass.Outputs.ContainsKey).ToArray();
                var results = new float[chunk.Stems * 2][];
                for (int i = 0; i < results.Length; i++) results[i] = new float[mLength];
                double done = pi, count = passes.Count;
                OverlapAdd.Run(chunk, mL, mR, mLength, model.Overlap, results, f => progress?.Invoke((done + f) / count), cancel);
                for (int t = 0; t < targets.Length; t++)
                {
                    if (!wanted.Contains(targets[t])) continue;
                    var l = Fit(Resampler.Resample(results[t * 2], model.SampleRate, rate), length);
                    var r = mono ? l : Fit(Resampler.Resample(results[t * 2 + 1], model.SampleRate, rate), length);
                    result.Stems[targets[t]] = (l, r);
                    results[t * 2] = results[t * 2 + 1] = null;
                }
            }
            progress?.Invoke(1);
            result.Seconds = started.Elapsed.TotalSeconds;
            if (cacheKey != null) StemCache.Save(cacheKey, model, result, rate);
            return result;
        }

        /// <summary>The array at exactly <paramref name="length"/> samples (resampling can be a sample off).</summary>
        static float[] Fit(float[] x, int length)
        {
            if (x.Length == length) return x;
            var y = new float[length];
            Array.Copy(x, y, Math.Min(length, x.Length));
            return y;
        }
    }
}
