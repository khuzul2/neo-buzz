#nullable disable
// Agent server, live readings (docs/specs/mcp-server.md §5.8, A7): a window of what a tap carries
// while the user plays the song. One thread delivers (the audio thread for a connection's tap, the
// UI thread for the master's); the meter copies into arrays it allocated up front, so the audio
// thread never allocates or waits. The window is then measured with the library analyze uses, so
// live numbers and render numbers mean the same thing.

using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using NeoBuzz.Analysis;

namespace ReBuzz.Agent.Audio
{
    public sealed class LiveMeter
    {
        /// <summary>The longest window, in seconds.</summary>
        public const double MaxSeconds = 30;

        readonly float[] left, right;
        readonly float scale;
        int frames;

        /// <param name="source">What it listens to ("Master", a machine's name).</param>
        /// <param name="capacityFrames">The window, in frames.</param>
        /// <param name="scale">From the tap's samples to full scale = 1 (Buzz's taps carry ±32768).</param>
        public LiveMeter(string source, int capacityFrames, float scale)
        {
            Source = source;
            left = new float[Math.Max(1, capacityFrames)];
            right = new float[left.Length];
            this.scale = scale;
        }

        public string Source { get; }
        public int Capacity => left.Length;
        /// <summary>Frames captured so far (any thread).</summary>
        public int Frames => Volatile.Read(ref frames);
        public bool Full => Frames >= left.Length;

        /// <summary>The delivering thread: a block of samples, interleaved L R when <paramref name="stereo"/>. Past the window, nothing.</summary>
        public void Add(float[] samples, bool stereo) => Add(samples, 0, samples?.Length ?? 0, stereo, 1f);

        /// <summary>As <see cref="Add(float[], bool)"/>, for <paramref name="count"/> samples from <paramref name="offset"/>, times <paramref name="gain"/>.</summary>
        public void Add(float[] samples, int offset, int count, bool stereo, float gain)
        {
            if (samples == null) return;
            int n = frames;
            if (n >= left.Length) return;
            float g = scale * gain;
            int end = Math.Min(samples.Length, offset + count);
            if (stereo)
            {
                for (int i = offset; i + 1 < end && n < left.Length; i += 2, n++)
                {
                    left[n] = samples[i] * g;
                    right[n] = samples[i + 1] * g;
                }
            }
            else
            {
                for (int i = offset; i < end && n < left.Length; i++, n++)
                    left[n] = right[n] = samples[i] * g;
            }
            Volatile.Write(ref frames, n);
        }

        /// <summary>What was captured (after the delivering thread has let go of the meter).</summary>
        public StereoAudio Audio(int rate)
        {
            int n = Frames;
            return new StereoAudio { Left = left.AsSpan(0, n).ToArray(), Right = right.AsSpan(0, n).ToArray(), Rate = rate };
        }

        /// <summary>
        /// The readings of a window: now (momentary over the last 400 ms, short-term over the last
        /// 3 s when the window is that long), over the window (integrated, loudest moment, peaks,
        /// RMS) and whether it was silent.
        /// </summary>
        public static JsonObject Report(StereoAudio audio)
        {
            var o = new JsonObject { ["seconds"] = Math.Round(audio.Seconds, 2) };
            if (audio.Frames == 0)
            {
                o["silent"] = true;
                return o;
            }
            var r = Loudness.Measure(audio.Left, audio.Right, audio.Rate);
            double sum = 0;
            for (int i = 0; i < audio.Frames; i++)
            {
                double l = audio.Left[i], rr = audio.Right?[i] ?? l;
                sum += (l * l + rr * rr) / 2;
            }
            double rms = 10 * Math.Log10(Math.Max(sum / audio.Frames, 1e-30));
            bool silent = r.SamplePeakDb < -90;
            o["silent"] = silent;
            o["momentary_lufs"] = Lufs(r.Momentary.Length > 0 ? r.Momentary[^1] : double.NegativeInfinity);
            if (audio.Seconds >= 3 && r.ShortTerm.Length > 0) o["short_term_lufs"] = Lufs(r.ShortTerm[^1]);
            o["integrated_lufs"] = Lufs(r.Integrated);
            o["momentary_max_lufs"] = Lufs(r.MomentaryMax);
            o["true_peak_dbtp"] = AudioReport.Db(r.TruePeakDb, 2);
            o["sample_peak_dbfs"] = AudioReport.Db(r.SamplePeakDb, 2);
            o["rms_dbfs"] = AudioReport.Db(rms < -200 ? double.NegativeInfinity : rms, 1);
            o["clipped_samples"] = r.Clipped;
            return o;
        }

        /// <summary>One line: "Master: −14.2 LUFS now (momentary), peak −3.1 dBTP, 0 clipped".</summary>
        public static string Line(string source, JsonObject report)
        {
            if (report["silent"]?.GetValue<bool>() == true) return $"{source}: silent";
            string Text(string key, string unit) => report[key] is JsonNode n ? $"{n.ToJsonString().Trim('"')} {unit}" : "";
            string line = $"{source}: {Text("momentary_lufs", "LUFS")} now, {Text("integrated_lufs", "LUFS")} over the window, " +
                          $"peak {Text("true_peak_dbtp", "dBTP")}";
            int clipped = report["clipped_samples"]?.GetValue<int>() ?? 0;
            return clipped > 0 ? line + $", {clipped} clipped samples" : line;
        }

        static JsonNode Lufs(double x) => double.IsNegativeInfinity(x) || double.IsNaN(x) ? JsonValue.Create("-inf") : JsonValue.Create(Math.Round(x, 1));
    }
}
