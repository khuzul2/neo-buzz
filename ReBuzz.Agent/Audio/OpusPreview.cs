#nullable disable
// Agent server (docs/specs/mastering-tools.md §5.15, M6b): the Opus preview. A render goes through an Opus encoder and decoder at a chosen
// bitrate (64 to 256 kb/s) with Concentus (pure C#, BSD-3-Clause-style licence; the version is pinned in ReBuzz.Agent.csproj), and the decoded
// audio is measured: its true peak, the loss above the codec's cutoff and the loudness of the null residual (original minus decoded).
// A proxy for lossy coding in general (the platforms use Vorbis and AAC). Offline work for the agent's tool tasks: never on the audio thread.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Concentus;
using Concentus.Enums;
using NeoBuzz.Analysis;
using ReBuzz.Agent.Mcp;

namespace ReBuzz.Agent.Audio
{
    public sealed class OpusResult
    {
        public int Kbps;
        public StereoAudio Decoded;
        /// <summary>The codec's lookahead plus the resampler's, aligned away (samples at the audio's rate).</summary>
        public int DelaySamples;
        public double OriginalTruePeakDb, DecodedTruePeakDb;
        /// <summary>Where the decoded spectrum falls away (Hz), or NaN if it keeps the original's content up to its top.</summary>
        public double CutoffHz;
        /// <summary>The decoded power above the cutoff against the original's (dB, 0 or below); NaN without a cutoff.</summary>
        public double LossAboveCutoffDb;
        public double OriginalLufs, ResidualLufs;
        /// <summary>The residual's loudness against the original's (dB, negative: how far below the original the codec's change sits).</summary>
        public double ResidualBelowDb => ResidualLufs - OriginalLufs;
        public double Bytes, Seconds;
        /// <summary>The bitrate the encoder actually produced (kb/s).</summary>
        public double ActualKbps => Seconds > 0 ? Bytes * 8 / Seconds / 1000 : 0;
    }

    public static class OpusPreview
    {
        public const int MinKbps = 64, MaxKbps = 256;
        const int OpusRate = 48000, Frame = 960;       // 20 ms

        /// <summary>Throws a ToolError for a bitrate outside 64 to 256 kb/s.</summary>
        public static int Validate(double kbps)
        {
            if (double.IsNaN(kbps) || kbps < MinKbps || kbps > MaxKbps) throw new ToolError($"opus_kbps: {MinKbps} to {MaxKbps}.");
            return (int)Math.Round(kbps);
        }

        public static OpusResult Run(StereoAudio audio, int kbps, System.Threading.CancellationToken cancel = default)
        {
            Validate(kbps);
            if (audio.Frames == 0) throw new ToolError("Nothing to encode: the audio is empty.");
            bool stereo = audio.Right != null;
            int rate = audio.Rate;
            // Concentus is pure managed code here: no native library, so the same bits everywhere.
            OpusCodecFactory.AttemptToUseNativeLibrary = false;

            float[] l = audio.Left, r = stereo ? audio.Right : null;
            if (rate != OpusRate)
            {
                l = Resample(l, rate, OpusRate);
                if (stereo) r = Resample(r, rate, OpusRate);
            }
            int channels = stereo ? 2 : 1;
            int frames = (l.Length + Frame - 1) / Frame + 2;          // the tail past the end, for the codec's own lookahead
            var inter = new float[frames * Frame * channels];
            for (int i = 0; i < l.Length; i++)
            {
                if (stereo) { inter[2 * i] = Clamp(l[i]); inter[2 * i + 1] = Clamp(r[i]); }
                else inter[i] = Clamp(l[i]);
            }

            var outL = new float[frames * Frame];
            var outR = stereo ? new float[frames * Frame] : null;
            double bytes = 0;
            using (var enc = OpusCodecFactory.CreateEncoder(OpusRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO))
            using (var dec = OpusCodecFactory.CreateDecoder(OpusRate, channels))
            {
                enc.Bitrate = kbps * 1000;
                enc.UseVBR = true;
                enc.Complexity = 10;
                enc.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;
                var packet = new byte[4000];
                var pcm = new float[Frame * channels];
                for (int f = 0; f < frames; f++)
                {
                    if ((f & 63) == 0) cancel.ThrowIfCancellationRequested();
                    int n = enc.Encode(inter.AsSpan(f * Frame * channels, Frame * channels), Frame, packet, packet.Length);
                    bytes += n;
                    dec.Decode(packet.AsSpan(0, n), pcm, Frame, false);
                    for (int i = 0; i < Frame; i++)
                    {
                        if (stereo) { outL[f * Frame + i] = pcm[2 * i]; outR[f * Frame + i] = pcm[2 * i + 1]; }
                        else outL[f * Frame + i] = pcm[i];
                    }
                }
            }

            // Back to the audio's rate, aligned with the original: the lookahead and the resamplers' delay found by correlation.
            if (rate != OpusRate)
            {
                outL = Resample(outL, OpusRate, rate);
                if (stereo) outR = Resample(outR, OpusRate, rate);
            }
            int delay = FindDelay(audio.Left, stereo ? audio.Right : null, outL, outR, rate);
            var dl = Take(outL, delay, audio.Frames);
            var dr = stereo ? Take(outR, delay, audio.Frames) : null;
            var decoded = new StereoAudio { Left = dl, Right = dr, Rate = rate };

            var lo = Loudness.Measure(audio.Left, audio.Right ?? audio.Left, rate);
            var ld = Loudness.Measure(dl, dr ?? dl, rate);
            var resL = new float[audio.Frames];
            var resR = stereo ? new float[audio.Frames] : null;
            for (int i = 0; i < resL.Length; i++)
            {
                resL[i] = audio.Left[i] - dl[i];
                if (stereo) resR[i] = audio.Right[i] - dr[i];
            }
            var lr = Loudness.Measure(resL, resR ?? resL, rate);

            var po = Welch.Measure(audio.Left, audio.Right, rate);
            var pd = Welch.Measure(dl, dr, rate);
            var (cut, loss) = Cutoff(po, pd);
            return new OpusResult
            {
                Kbps = kbps, Decoded = decoded, DelaySamples = delay,
                OriginalTruePeakDb = lo.TruePeakDb, DecodedTruePeakDb = ld.TruePeakDb,
                CutoffHz = cut, LossAboveCutoffDb = loss,
                OriginalLufs = lo.Integrated, ResidualLufs = lr.Integrated,
                Bytes = bytes, Seconds = audio.Seconds,
            };
        }

        /// <summary>The measurements as the tools report them.</summary>
        public static JsonObject Json(OpusResult r)
        {
            var o = new JsonObject
            {
                ["kbps"] = r.Kbps,
                ["actual_kbps"] = Math.Round(r.ActualKbps, 1),
                ["true_peak_dbtp"] = AudioReport.Db(r.DecodedTruePeakDb, 2),
                ["original_true_peak_dbtp"] = AudioReport.Db(r.OriginalTruePeakDb, 2),
                ["true_peak_change_db"] = AudioReport.Num(r.DecodedTruePeakDb - r.OriginalTruePeakDb, 2),
                ["cutoff_hz"] = AudioReport.Num(r.CutoffHz, 0),
                ["loss_above_cutoff_db"] = AudioReport.Num(r.LossAboveCutoffDb, 1),
                ["residual_lufs"] = AudioReport.Db(r.ResidualLufs, 1),
                ["residual_below_original_db"] = AudioReport.Num(r.ResidualBelowDb, 1),
                ["aligned_by_samples"] = r.DelaySamples,
            };
            o["read"] = Line(r);
            return o;
        }

        /// <summary>A plain-language read of the measurements.</summary>
        public static string Line(OpusResult r)
        {
            var c = CultureInfo.InvariantCulture;
            double dTp = r.DecodedTruePeakDb - r.OriginalTruePeakDb;
            string peak = double.IsInfinity(r.DecodedTruePeakDb) ? "silent" :
                $"true peak {r.DecodedTruePeakDb.ToString("+0.0;-0.0", c)} dBTP ({dTp.ToString("+0.0;-0.0", c)} dB against the original)" +
                (r.DecodedTruePeakDb > -1 ? ", over the -1 dBTP ceiling the codec can push a master past: leave headroom" : "");
            string cut = double.IsNaN(r.CutoffHz) ? "no content lost at the top" :
                $"nothing above about {(r.CutoffHz / 1000).ToString("0.#", c)} kHz ({r.LossAboveCutoffDb.ToString("0.#", c)} dB there)";
            string res = double.IsInfinity(r.ResidualLufs) ? "a null residual of nothing (bit-identical)" :
                $"null residual {r.ResidualLufs.ToString("0.0", c)} LUFS ({r.ResidualBelowDb.ToString("0.0", c)} dB against the original)";
            return $"Opus at {r.Kbps} kb/s: {peak}; {cut}; {res}.";
        }

        static float Clamp(float x) => x > 1f ? 1f : x < -1f ? -1f : x;

        static float[] Take(float[] x, int from, int n)
        {
            var o = new float[n];
            Array.Copy(x, from, o, 0, Math.Max(0, Math.Min(n, x.Length - from)));
            return o;
        }

        /// <summary>
        /// The lowest frequency above which every 500 Hz band the original fills is at least 20 dB down in the decoded audio, and the
        /// decoded power from there up against the original's.
        /// </summary>
        internal static (double Hz, double LossDb) Cutoff(Psd orig, Psd dec)
        {
            const double step = 500, floor = 1e-12;       // bands under -120 dBFS of power count as empty
            double top = Math.Min(orig.Nyquist, 24000);
            int bands = (int)Math.Floor((top - 2000) / step);
            double cut = double.NaN;
            for (int b = bands - 1; b >= 0; b--)
            {
                double lo = 2000 + b * step, hi = lo + step;
                double po = orig.Band(orig.Mean, lo, hi), pd = dec.Band(dec.Mean, lo, hi);
                if (po < floor) continue;                  // nothing to lose here
                if (pd <= po * 0.01) cut = lo;             // 20 dB down: lost
                else break;                                 // content still there: the cutoff is above
            }
            if (double.IsNaN(cut)) return (double.NaN, double.NaN);
            double sumO = orig.Band(orig.Mean, cut, top), sumD = dec.Band(dec.Mean, cut, top);
            return (cut, sumO > 0 ? 10 * Math.Log10(Math.Max(sumD, 1e-30) / sumO) : double.NaN);
        }

        /// <summary>The decoded signal's delay against the original (samples): the codec's lookahead and the resamplers', by correlation.</summary>
        static int FindDelay(float[] oL, float[] oR, float[] dL, float[] dR, int rate)
        {
            int guess = (int)Math.Round(0.0065 * rate);
            int range = (int)Math.Round(0.012 * rate);
            int n = Math.Min(oL.Length, 4 * rate);
            int start = Math.Max(0, (oL.Length - n) / 2);
            double best = double.NegativeInfinity;
            int bestLag = guess;
            for (int lag = Math.Max(0, guess - range); lag <= guess + range; lag++)
            {
                if (start + n + lag > dL.Length) break;
                double c = 0;
                for (int i = 0; i < n; i++)
                {
                    c += oL[start + i] * dL[start + i + lag];
                    if (oR != null) c += oR[start + i] * dR[start + i + lag];
                }
                if (c > best) { best = c; bestLag = lag; }
            }
            return bestLag;
        }

        /// <summary>Resamples one channel with Concentus's resampler (quality 8 of 10), flushing its tail so the length is the exact ratio.</summary>
        static float[] Resample(float[] x, int from, int to)
        {
            using var rs = ResamplerFactory.CreateResampler(1, from, to, 8);
            long total = (long)Math.Ceiling(x.Length * (double)to / from);
            var o = new float[total + 4096];
            int ip = 0, op = 0;
            var zeros = new float[8192];
            while (op < total)
            {
                bool tail = ip >= x.Length;
                int inLen = tail ? zeros.Length : Math.Min(8192, x.Length - ip);
                int outLen = o.Length - op;
                if (outLen <= 0) break;
                rs.Process(0, tail ? zeros.AsSpan(0, inLen) : x.AsSpan(ip, inLen), ref inLen, o.AsSpan(op, outLen), ref outLen);
                if (!tail) ip += inLen;
                op += outLen;
                if (inLen == 0 && outLen == 0) break;
            }
            var res = new float[total];
            Array.Copy(o, res, Math.Min(total, op));
            return res;
        }
    }
}
