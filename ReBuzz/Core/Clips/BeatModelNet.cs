#nullable disable
// Fork addition (neo-buzz, docs/specs/neo-audio.md, AW2 as built): the forward pass of the beat and downbeat network, in pure C# (like DrumModel, and
// for the same reasons: no native library, the same numbers everywhere, linkable into the tests and the evaluation tools). An exact port of
// tools/aw2/model.py (Net.forward); the weights are a flat file written by tools/aw2/export.py:
//   "AW2M", int32 format (1), int32 model version, int32 c1 c2 c3 d k ratio blocks tempoBins, int32 dilation per block, then float32 tensors in the order
//   of Net.load below (PyTorch layouts). Pure (ReBuzz.SessionTests links this file).

using System;
using System.IO;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace ReBuzz.Core.Clips
{
    internal sealed class BeatModelNet
    {
        public const int Mels = BeatFront.Mels;
        public int Version, C1, C2, C3, D, K, Ratio, Blocks, TempoBins;
        public int[] Dil;
        float[] w1, b1, w2, b2, w3, b3, wp, bp, ln0w, ln0b, lnfw, lnfb, wh, bh, wt, bt;
        float[][] dw, dwb, lnw, lnb, p1w, p1b, p2w, p2b;

        public static BeatModelNet Load(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            if (r.ReadByte() != 'A' || r.ReadByte() != 'W' || r.ReadByte() != '2' || r.ReadByte() != 'M') throw new InvalidDataException("not a beat model");
            if (r.ReadInt32() != 1) throw new InvalidDataException("unknown beat model format");
            var n = new BeatModelNet { Version = r.ReadInt32(), C1 = r.ReadInt32(), C2 = r.ReadInt32(), C3 = r.ReadInt32(), D = r.ReadInt32(), K = r.ReadInt32(), Ratio = r.ReadInt32(), Blocks = r.ReadInt32(), TempoBins = r.ReadInt32() };
            if (n.C1 % 4 != 0 || n.C2 % 4 != 0 || n.C3 % 4 != 0) throw new InvalidDataException("the convolution widths must be multiples of 4");
            n.Dil = new int[n.Blocks];
            for (int i = 0; i < n.Blocks; i++) n.Dil[i] = r.ReadInt32();
            float[] T(int count)
            {
                var a = new float[count];
                var bytes = r.ReadBytes(count * 4);
                if (bytes.Length != count * 4) throw new InvalidDataException("beat model is cut short");
                Buffer.BlockCopy(bytes, 0, a, 0, bytes.Length);
                return a;
            }
            int d = n.D, f = n.C3 * (Mels / 16);
            n.w1 = T(n.C1 * 9); n.b1 = T(n.C1);
            n.w2 = T(n.C2 * n.C1 * 9); n.b2 = T(n.C2);
            n.w3 = T(n.C3 * n.C2 * 9); n.b3 = T(n.C3);
            n.wp = T(d * f); n.bp = T(d);
            n.ln0w = T(d); n.ln0b = T(d);
            int B = n.Blocks;
            n.dw = new float[B][]; n.dwb = new float[B][]; n.lnw = new float[B][]; n.lnb = new float[B][];
            n.p1w = new float[B][]; n.p1b = new float[B][]; n.p2w = new float[B][]; n.p2b = new float[B][];
            for (int i = 0; i < B; i++)
            {
                n.dw[i] = T(d * n.K); n.dwb[i] = T(d); n.lnw[i] = T(d); n.lnb[i] = T(d);
                n.p1w[i] = T(d * n.Ratio * d); n.p1b[i] = T(d * n.Ratio);
                n.p2w[i] = T(d * d * n.Ratio); n.p2b[i] = T(d);
            }
            n.lnfw = T(d); n.lnfb = T(d);
            n.wh = T(2 * d); n.bh = T(2);
            n.wt = T(n.TempoBins * d); n.bt = T(n.TempoBins);
            n.w1t = Transpose(n.w1, n.C1, 1); n.w2t = Transpose(n.w2, n.C2, n.C1); n.w3t = Transpose(n.w3, n.C3, n.C2);
            return n;
        }

        float[] w1t, w2t, w3t;

        /// <summary>Weights (out, in, 3 freq, 3 time) as ((in * 3 + freq) * 3 + time) rows of out values, so the output channels are one contiguous vector.</summary>
        static float[] Transpose(float[] w, int co, int ci)
        {
            var t = new float[w.Length];
            for (int c = 0; c < co; c++)
                for (int i = 0; i < ci; i++)
                    for (int df = 0; df < 3; df++)
                        for (int dt = 0; dt < 3; dt++)
                            t[((i * 3 + df) * 3 + dt) * co + c] = w[((c * ci + i) * 3 + df) * 3 + dt];
            return t;
        }

        /// <summary>One frame of a 3x3 convolution (zero padded, over bands and frames) of src laid out [frame][channel][band]: out[co][band], before the activation.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void Conv3(float[] src, int T, int ci, int F, float[] wt, float[] bias, int co, int t, float[] o)
        {
            int cv = co / 4;
            Span<Vector128<float>> acc = stackalloc Vector128<float>[cv];
            for (int f = 0; f < F; f++)
            {
                for (int k = 0; k < cv; k++) acc[k] = Vector128.Create(bias.AsSpan(4 * k, 4));
                for (int dt = 0; dt < 3; dt++)
                {
                    int tt = t + dt - 1;
                    if (tt < 0 || tt >= T) continue;
                    long baseIn = (long)tt * ci * F;
                    for (int i = 0; i < ci; i++)
                        for (int df = 0; df < 3; df++)
                        {
                            int ff = f + df - 1;
                            if (ff < 0 || ff >= F) continue;
                            var vx = Vector128.Create(src[baseIn + (long)i * F + ff]);
                            int wb = ((i * 3 + df) * 3 + dt) * co;
                            for (int k = 0; k < cv; k++) acc[k] += vx * Vector128.Create(wt.AsSpan(wb + 4 * k, 4));
                        }
                }
                for (int k = 0; k < cv; k++)
                    for (int l = 0; l < 4; l++) o[(4 * k + l) * F + f] = acc[k].GetElement(l);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Gelu(float x) => 0.5f * x * (1f + MathF.Tanh(0.7978845608028654f * (x + 0.044715f * x * x * x)));

        /// <summary>The network's outputs for features (frames x 128 bytes): per frame the beat and downbeat logits, and the features before the heads (frames x D) when asked.</summary>
        public void Forward(byte[] q, int frames, float[] logits, float[] features)
        {
            int T = frames, d = D, c1 = C1, c2 = C2, c3 = C3;
            int f1 = Mels / 4, f2 = Mels / 16;
            var xin = new float[(long)T * Mels];
            Parallel.For(0, (T + 255) / 256, blk =>
            {
                for (int t = blk * 256; t < Math.Min(T, blk * 256 + 256); t++)
                    for (int f = 0; f < Mels; f++) xin[(long)t * Mels + f] = q[(long)t * Mels + f] / (float)BeatFront.ByteScale / 4f;
            });
            // Layer 1 (1 -> c1, 3x3), GELU, max over 4 bands.
            var p1 = new float[(long)T * c1 * f1];
            Parallel.For(0, (T + 127) / 128, blk =>
            {
                var o = new float[c1 * Mels];
                for (int t = blk * 128; t < Math.Min(T, blk * 128 + 128); t++)
                {
                    Conv3(xin, T, 1, Mels, w1t, b1, c1, t, o);
                    for (int c = 0; c < c1; c++)
                        for (int g = 0; g < f1; g++)
                        {
                            float m = float.MinValue;
                            for (int k = 0; k < 4; k++) m = Math.Max(m, Gelu(o[c * Mels + 4 * g + k]));
                            p1[((long)t * c1 + c) * f1 + g] = m;
                        }
                }
            });
            xin = null;
            // Layer 2 (c1 -> c2), GELU, max over 4 bands.
            var p2 = new float[(long)T * c2 * f2];
            Parallel.For(0, (T + 127) / 128, blk =>
            {
                var o = new float[c2 * f1];
                for (int t = blk * 128; t < Math.Min(T, blk * 128 + 128); t++)
                {
                    Conv3(p1, T, c1, f1, w2t, b2, c2, t, o);
                    for (int c = 0; c < c2; c++)
                        for (int g = 0; g < f2; g++)
                        {
                            float m = float.MinValue;
                            for (int k = 0; k < 4; k++) m = Math.Max(m, Gelu(o[c * f1 + 4 * g + k]));
                            p2[((long)t * c2 + c) * f2 + g] = m;
                        }
                }
            });
            p1 = null;
            // Layer 3 (c2 -> c3), GELU, then the projection to D and the first LayerNorm.
            var X = new float[(long)T * d];
            var Y = new float[(long)T * d];
            int flat = c3 * f2;
            Parallel.For(0, (T + 127) / 128, blk =>
            {
                var o = new float[flat];
                for (int t = blk * 128; t < Math.Min(T, blk * 128 + 128); t++)
                {
                    Conv3(p2, T, c2, f2, w3t, b3, c3, t, o);
                    for (int i = 0; i < flat; i++) o[i] = Gelu(o[i]);
                    var x = X.AsSpan((int)((long)t * d), d);
                    for (int j = 0; j < d; j++) x[j] = Dot(wp.AsSpan(j * flat, flat), o) + bp[j];
                    LayerNorm(x, ln0w, ln0b);
                }
            });
            p2 = null;
            for (int b = 0; b < Blocks; b++)
            {
                int dil = Dil[b], k = K, half = k / 2;
                var dwb_ = dw[b]; var dwbias = dwb[b];
                Parallel.For(0, (T + 255) / 256, blk =>
                {
                    for (int t = blk * 256; t < Math.Min(T, blk * 256 + 256); t++)
                    {
                        var y = Y.AsSpan((int)((long)t * d), d);
                        dwbias.CopyTo(y);
                        for (int j = 0; j < k; j++)
                        {
                            int tt = t + (j - half) * dil;
                            if (tt < 0 || tt >= T) continue;
                            var xs = X.AsSpan((int)((long)tt * d), d);
                            for (int c = 0; c < d; c++) y[c] += dwb_[c * k + j] * xs[c];
                        }
                        LayerNorm(y, lnw[b], lnb[b]);
                    }
                });
                int h = d * Ratio;
                var w1_ = p1w[b]; var b1_ = p1b[b]; var w2_ = p2w[b]; var b2_ = p2b[b];
                Parallel.For(0, (T + 255) / 256, blk =>
                {
                    var z = new float[h];
                    for (int t = blk * 256; t < Math.Min(T, blk * 256 + 256); t++)
                    {
                        var y = Y.AsSpan((int)((long)t * d), d);
                        for (int j = 0; j < h; j++) z[j] = Gelu(Dot(w1_.AsSpan(j * d, d), y) + b1_[j]);
                        var x = X.AsSpan((int)((long)t * d), d);
                        for (int j = 0; j < d; j++) x[j] += Dot(w2_.AsSpan(j * h, h), z) + b2_[j];
                    }
                });
            }
            Parallel.For(0, (T + 255) / 256, blk =>
            {
                for (int t = blk * 256; t < Math.Min(T, blk * 256 + 256); t++)
                {
                    var x = X.AsSpan((int)((long)t * d), d);
                    LayerNorm(x, lnfw, lnfb);
                    logits[2 * t] = Dot(wh.AsSpan(0, d), x) + bh[0];
                    logits[2 * t + 1] = Dot(wh.AsSpan(d, d), x) + bh[1];
                }
            });
            if (features != null) Array.Copy(X, features, Math.Min(X.Length, features.Length));
        }

        /// <summary>The tempo distribution (softmax over the bins) of features (frames x D) between two frames.</summary>
        public float[] Tempo(float[] features, int from, int to)
        {
            int d = D;
            var mean = new float[d];
            for (int t = from; t < to; t++)
                for (int c = 0; c < d; c++) mean[c] += features[(long)t * d + c];
            float inv = 1f / Math.Max(1, to - from);
            for (int c = 0; c < d; c++) mean[c] *= inv;
            var p = new float[TempoBins];
            float max = float.MinValue;
            for (int j = 0; j < TempoBins; j++) { p[j] = Dot(wt.AsSpan(j * d, d), mean) + bt[j]; max = Math.Max(max, p[j]); }
            float sum = 0;
            for (int j = 0; j < TempoBins; j++) { p[j] = MathF.Exp(p[j] - max); sum += p[j]; }
            for (int j = 0; j < TempoBins; j++) p[j] /= sum;
            return p;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        {
            int n = a.Length, i = 0;
            float s = 0;
            if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
            {
                var acc = Vector<float>.Zero;
                int vc = Vector<float>.Count;
                for (; i <= n - vc; i += vc) acc += new Vector<float>(a.Slice(i)) * new Vector<float>(b.Slice(i));
                s = Vector.Sum(acc);
            }
            for (; i < n; i++) s += a[i] * b[i];
            return s;
        }

        static void LayerNorm(Span<float> x, float[] w, float[] b)
        {
            int n = x.Length;
            float mean = 0;
            for (int i = 0; i < n; i++) mean += x[i];
            mean /= n;
            float var = 0;
            for (int i = 0; i < n; i++) { float v = x[i] - mean; var += v * v; }
            float inv = 1f / MathF.Sqrt(var / n + 1e-5f);
            for (int i = 0; i < n; i++) x[i] = (x[i] - mean) * inv * w[i] + b[i];
        }
    }
}
