#nullable disable
// Fork (docs/specs/stem-separation.md): one ONNX file of a stem model run as a chunk model: the spectrogram of a segment is made by
// our Stft, the network (ONNX Runtime, MIT) answers, and the answer goes back through our inverse. Two families:
//   htdemucs   inputs  mix [1,2,L] and cac [1,4,bins,frames] (Demucs's complex-as-channels spectrogram of the segment, padded as
//              HTDemucs._spec does), outputs freq [1,S,4,bins,frames] and time [1,S,2,L]; the stem is the inverse STFT of freq plus time.
//   spec-mask  inputs spec_real, spec_imag [1,2,bins,frames], outputs out_spec_real, out_spec_imag [1,S,2,bins,frames]: the separated
//              spectrograms (a RoFormer's masks already applied); the stem is their inverse STFT.
// Buffers are made once per pass and wrapped as OrtValues, so a segment allocates nothing. The audio thread never comes here.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.ML.OnnxRuntime;

namespace ReBuzz.Core.AudioLibrary.Stems
{
    internal abstract class OnnxStemPass : IChunkModel, IDisposable
    {
        protected readonly StemModelManifest Manifest;
        protected readonly InferenceSession Session;
        readonly RunOptions runOptions = new();
        readonly List<IDisposable> owned = new();

        protected OnnxStemPass(StemModelManifest manifest, StemPass pass, SessionOptions options)
        {
            Manifest = manifest;
            Targets = manifest.Stems.Where(pass.Outputs.ContainsKey).ToArray();
            Indexes = Targets.Select(t => pass.Outputs[t]).ToArray();
            Session = new InferenceSession(System.IO.Path.Combine(manifest.Folder, pass.File), options);
        }

        /// <summary>The stems this file gives, in the order of the chunk output.</summary>
        public string[] Targets { get; }
        protected int[][] Indexes { get; }
        public int Segment => Manifest.Segment;
        public int Stems => Targets.Length;

        public abstract void Process(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<float> output, CancellationToken cancel);

        /// <summary>Session options: the CPU provider everywhere; CoreML on macOS when NEOBUZZ_STEMS_PROVIDER=coreml asks for it.</summary>
        public static SessionOptions MakeOptions()
        {
            var o = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
            if (string.Equals(Environment.GetEnvironmentVariable("NEOBUZZ_STEMS_PROVIDER"), "coreml", StringComparison.OrdinalIgnoreCase)
                && RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                try { o.AppendExecutionProvider_CoreML(); }
                catch (Exception) { /* not in this build of ONNX Runtime: the CPU provider runs it */ }
            }
            return o;
        }

        protected OrtValue Wrap(float[] data, params long[] shape)
        {
            var v = OrtValue.CreateTensorValueFromMemory(data, shape);
            owned.Add(v);
            return v;
        }

        protected void Run(string[] inputNames, OrtValue[] inputs, string[] outputNames, OrtValue[] outputs, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            using var registration = cancel.Register(() => runOptions.Terminate = true);
            try
            {
                Session.Run(runOptions, inputNames, inputs, outputNames, outputs);
            }
            catch (OnnxRuntimeException) when (cancel.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancel);
            }
        }

        public void Dispose()
        {
            foreach (var o in owned) o.Dispose();
            owned.Clear();
            runOptions.Dispose();
            Session.Dispose();
        }
    }

    /// <summary>
    /// The transform around Demucs's network as HTDemucs does it: _spec and _magnitude (the signal reflect-padded by 1.5 hops and to a whole
    /// number of hops, an STFT with the centre padded too, the Nyquist bin and the frames of the padding dropped, the real and imaginary
    /// parts as channels), and _ispec (a zero Nyquist bin and two zero frames added, the inverse, the padding cut). Normalised STFT, hop = size / 4.
    /// </summary>
    internal sealed class DemucsTransform
    {
        readonly Stft stft;
        readonly int segment, nfft, hop, pad, padded;
        readonly float[] padA, padB, wave;

        public DemucsTransform(int segment, int nfft, int hop)
        {
            if (nfft != 4 * hop) throw new ArgumentException("Demucs's transform has hop = nfft / 4.");
            this.segment = segment;
            this.nfft = nfft;
            this.hop = hop;
            stft = new Stft(nfft, hop, normalized: true);
            Bins = nfft / 2;                                       // the Nyquist bin is dropped
            Frames = (segment + hop - 1) / hop;
            pad = hop / 2 * 3;
            padded = Frames * hop + 2 * pad;
            padA = new float[padded];
            padB = new float[padded + nfft];
            wave = new float[nfft + hop * (Frames + 3)];
        }

        public int Bins { get; }
        public int Frames { get; }

        /// <summary>One channel of the segment to its spectrogram: re and im are [bin * Frames + frame].</summary>
        public void Forward(ReadOnlySpan<float> x, Span<float> re, Span<float> im)
        {
            Stft.PadReflect(x, padA, pad, pad + Frames * hop - segment);
            Stft.PadReflect(padA.AsSpan(0, padded), padB, nfft / 2, nfft / 2);
            stft.Analyze(padB.AsSpan(0, padded + nfft), 2, Frames, Bins, re, im);
        }

        /// <summary>The signal of a spectrogram (re and im [bin * Frames + frame]) <b>added</b> to <paramref name="destination"/> (segment samples).</summary>
        public void AddInverse(ReadOnlySpan<float> re, ReadOnlySpan<float> im, Span<float> destination)
        {
            stft.Synthesize(re, im, Frames + 4, Bins, Frames, -2, wave);
            int start = nfft / 2 + pad;
            for (int i = 0; i < segment; i++) destination[i] += wave[start + i];
        }
    }

    /// <summary>Demucs's hybrid network (HTDemucs): see the file's header.</summary>
    internal sealed class HtDemucsPass : OnnxStemPass
    {
        readonly DemucsTransform transform;
        readonly int bins, frames;
        readonly float[] mix, cac, freq, time, sumRe, sumIm;
        readonly OrtValue mixValue, cacValue, freqValue, timeValue;
        static readonly string[] InNames = { "mix", "cac" }, OutNames = { "freq", "time" };
        readonly OrtValue[] inputs, outputs;

        public HtDemucsPass(StemModelManifest m, StemPass pass, SessionOptions options) : base(m, pass, options)
        {
            transform = new DemucsTransform(m.Segment, m.Nfft, m.Hop);
            bins = transform.Bins;
            frames = transform.Frames;
            int s = m.OutputStems;
            mix = new float[2 * m.Segment];
            cac = new float[4 * bins * frames];
            freq = new float[s * 4 * bins * frames];
            time = new float[s * 2 * m.Segment];
            sumRe = new float[bins * frames];
            sumIm = new float[bins * frames];
            mixValue = Wrap(mix, 1, 2, m.Segment);
            cacValue = Wrap(cac, 1, 4, bins, frames);
            freqValue = Wrap(freq, 1, s, 4, bins, frames);
            timeValue = Wrap(time, 1, s, 2, m.Segment);
            inputs = new[] { mixValue, cacValue };
            outputs = new[] { freqValue, timeValue };
        }

        public override void Process(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<float> output, CancellationToken cancel)
        {
            int seg = Manifest.Segment, plane = bins * frames;
            left.CopyTo(mix.AsSpan(0, seg));
            right.CopyTo(mix.AsSpan(seg, seg));
            for (int ch = 0; ch < 2; ch++)
                transform.Forward(ch == 0 ? left : right, cac.AsSpan(ch * 2 * plane, plane), cac.AsSpan((ch * 2 + 1) * plane, plane));
            Run(InNames, inputs, OutNames, outputs, cancel);
            for (int t = 0; t < Targets.Length; t++)
            {
                for (int ch = 0; ch < 2; ch++)
                {
                    Array.Clear(sumRe, 0, sumRe.Length);
                    Array.Clear(sumIm, 0, sumIm.Length);
                    var dest = output.Slice((t * 2 + ch) * seg, seg);
                    dest.Clear();
                    foreach (int idx in Indexes[t])
                    {
                        int baseRe = ((idx * 4) + ch * 2) * plane, baseIm = baseRe + plane;
                        for (int i = 0; i < plane; i++)
                        {
                            sumRe[i] += freq[baseRe + i];
                            sumIm[i] += freq[baseIm + i];
                        }
                        int tb = (idx * 2 + ch) * seg;
                        for (int i = 0; i < seg; i++) dest[i] += time[tb + i];
                    }
                    transform.AddInverse(sumRe, sumIm, dest);
                }
            }
        }
    }

    /// <summary>A network that takes a spectrogram and gives the separated spectrograms (BS-RoFormer): see the file's header.</summary>
    internal sealed class SpecMaskPass : OnnxStemPass
    {
        readonly Stft stft;
        readonly int bins, frames;
        readonly float[] specRe, specIm, outRe, outIm, padded, sumRe, sumIm, wave;
        readonly OrtValue inRe, inIm, outReValue, outImValue;
        static readonly string[] InNames = { "spec_real", "spec_imag" }, OutNames = { "out_spec_real", "out_spec_imag" };
        readonly OrtValue[] inputs, outputs;

        public SpecMaskPass(StemModelManifest m, StemPass pass, SessionOptions options) : base(m, pass, options)
        {
            stft = new Stft(m.Nfft, m.Hop, normalized: false);
            bins = stft.Bins;
            frames = 1 + m.Segment / m.Hop;
            int s = m.OutputStems;
            specRe = new float[2 * bins * frames];
            specIm = new float[2 * bins * frames];
            outRe = new float[s * 2 * bins * frames];
            outIm = new float[s * 2 * bins * frames];
            padded = new float[m.Segment + m.Nfft];
            sumRe = new float[bins * frames];
            sumIm = new float[bins * frames];
            wave = new float[m.Nfft + m.Hop * (frames - 1)];
            inRe = Wrap(specRe, 1, 2, bins, frames);
            inIm = Wrap(specIm, 1, 2, bins, frames);
            outReValue = Wrap(outRe, 1, s, 2, bins, frames);
            outImValue = Wrap(outIm, 1, s, 2, bins, frames);
            inputs = new[] { inRe, inIm };
            outputs = new[] { outReValue, outImValue };
        }

        public override void Process(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<float> output, CancellationToken cancel)
        {
            int seg = Manifest.Segment, nfft = Manifest.Nfft, plane = bins * frames;
            for (int ch = 0; ch < 2; ch++)
            {
                Stft.PadReflect(ch == 0 ? left : right, padded, nfft / 2, nfft / 2);
                stft.Analyze(padded, 0, frames, bins, specRe.AsSpan(ch * plane, plane), specIm.AsSpan(ch * plane, plane));
            }
            Run(InNames, inputs, OutNames, outputs, cancel);
            for (int t = 0; t < Targets.Length; t++)
            {
                for (int ch = 0; ch < 2; ch++)
                {
                    Array.Clear(sumRe, 0, sumRe.Length);
                    Array.Clear(sumIm, 0, sumIm.Length);
                    foreach (int idx in Indexes[t])
                    {
                        int b = (idx * 2 + ch) * plane;
                        for (int i = 0; i < plane; i++)
                        {
                            sumRe[i] += outRe[b + i];
                            sumIm[i] += outIm[b + i];
                        }
                    }
                    stft.Synthesize(sumRe, sumIm, frames, bins, frames, 0, wave);
                    wave.AsSpan(nfft / 2, seg).CopyTo(output.Slice((t * 2 + ch) * seg, seg));
                }
            }
        }
    }
}
