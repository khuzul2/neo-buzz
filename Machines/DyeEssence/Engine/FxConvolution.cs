#nullable disable
// Dye Essence's effects rack (docs/specs/dye-essence.md section 4.10), P4b2: the convolution reverb and the vocoder, the two
// types whose data is built in the background after the host exists.
//  - FxUserData: the clips the machine state holds for them (an impulse response, a modulator), published as immutable
//    snapshots with a version, read by the builder.
//  - ConvolutionHost: a StereoConvolver (NeoBuzz.Dsp: zero latency, non-uniform partitions) of a response that the type's
//    parameters shape (ImpulseResponses.Shape). A change of any parameter but Output asks the rack's builder for a new
//    convolver (at most one request is outstanding; the audio thread only writes the request, flips a flag and, when the builder
//    is done, takes the new one and crossfades from the old over 2048 samples).
//  - VocoderHost: a ChannelVocoder whose modulator is the internal voice (VoiceModulator) or the user's clip, looped (resampled
//    to the host's rate by the builder). Dye Essence is a generator: ReBuzz gives it no input, so there is no external modulator
//    here; the kernels in NeoBuzz.Dsp take any mono modulator for a machine that has one.

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.DyeEssence
{
    /// <summary>An imported clip: mono or stereo samples at a rate, with the name it was loaded under.</summary>
    internal sealed class FxClip
    {
        public readonly string Name;
        public readonly float[] Left, Right;
        public readonly int Rate, Version;

        public FxClip(string name, float[] left, float[] right, int rate, int version)
        {
            Name = name ?? "";
            Left = left;
            Right = right;
            Rate = rate;
            Version = version;
        }
    }

    /// <summary>The rack's user clips (UI thread writes, the builder reads).</summary>
    internal sealed class FxUserData
    {
        /// <summary>The longest impulse response kept, in frames (10 s at 48 kHz) and the longest modulator (20 s at 48 kHz).</summary>
        public const int MaxIrFrames = 480000, MaxModulatorFrames = 960000;

        FxClip ir, modulator;
        int versions;

        public FxClip Ir => Volatile.Read(ref ir);
        public FxClip Modulator => Volatile.Read(ref modulator);
        public int IrVersion => Ir?.Version ?? 0;
        public int ModulatorVersion => Modulator?.Version ?? 0;

        public void SetIr(string name, float[] left, float[] right, int rate) =>
            Volatile.Write(ref ir, left == null ? null : new FxClip(name, left, right, rate, Interlocked.Increment(ref versions)));

        public void SetModulator(string name, float[] mono, int rate) =>
            Volatile.Write(ref modulator, mono == null ? null : new FxClip(name, mono, null, rate, Interlocked.Increment(ref versions)));
    }

    internal sealed class ConvolutionHost : FxNewHost
    {
        /// <summary>What a convolver is built for: the raw values of the parameters that shape the response, and the user clip's version.</summary>
        readonly struct Key : IEquatable<Key>
        {
            public readonly int Ir, Size, PreDelay, Decay, Damping, LowCut, HighCut, Width, Reverse, UserVersion;

            public Key(ParamValues v, FxUserData user)
            {
                Ir = v[FxNew.Conv.Ir]; Size = v[FxNew.Conv.Size]; PreDelay = v[FxNew.Conv.PreDelay]; Decay = v[FxNew.Conv.Decay];
                Damping = v[FxNew.Conv.Damping]; LowCut = v[FxNew.Conv.LowCut]; HighCut = v[FxNew.Conv.HighCut]; Width = v[FxNew.Conv.Width];
                Reverse = v[FxNew.Conv.Reverse];
                UserVersion = Ir == FxNew.Conv.UserChoice ? user.IrVersion : 0;
            }

            public bool Equals(Key o) => Ir == o.Ir && Size == o.Size && PreDelay == o.PreDelay && Decay == o.Decay && Damping == o.Damping
                && LowCut == o.LowCut && HighCut == o.HighCut && Width == o.Width && Reverse == o.Reverse && UserVersion == o.UserVersion;
        }

        const int FadeSamples = 2048, Block = DyeEngine.MaxBlock;
        // 0 idle, 1 requested, 2 building, 3 built (waiting for the audio thread), 4 a request being written.
        int state;
        Key wanted, requested;
        bool haveWanted;
        StereoConvolver incoming;
        StereoConvolver current, old;
        int fade;
        bool ran;
        readonly FxUserData user;
        readonly float[] tmpL = new float[Block], tmpR = new float[Block];
        float gain = 1;
        // The builder's cache of the base response (built-in or the user's, at the host's rate).
        int baseIr = -1, baseVersion = -1;
        float[] baseL, baseR;

        public ConvolutionHost(float rate, FxUserData user) : base(FxType.Convolution, rate, FxNew.Conv.Table) { this.user = user; }

        public override int Latency => 0;

        public override bool IsReady => Volatile.Read(ref current) != null || Volatile.Read(ref incoming) != null;

        public override bool HasWork => Volatile.Read(ref state) == 1;

        /// <summary>The convolver is the one the parameters ask for, or the request for it is on its way.</summary>
        public override bool Settled => Volatile.Read(ref state) != 1 && Volatile.Read(ref state) != 2 && Volatile.Read(ref state) != 4;

        public override void Reset()
        {
            current?.Reset();
            old = null;
            fade = 0;
            ran = false;
        }

        /// <summary>Asks for a new convolver when the parameters want another response than the last one asked for (any thread, no allocation).</summary>
        public override void Poll()
        {
            if (Volatile.Read(ref state) != 0) return;
            var key = new Key(V, user);
            if (haveWanted && key.Equals(wanted)) return;
            if (Interlocked.CompareExchange(ref state, 4, 0) != 0) return;
            requested = key;
            wanted = key;
            haveWanted = true;
            Volatile.Write(ref state, 1);
            FxBuilder.Kick();
        }

        /// <summary>The builder's job (never the audio thread's).</summary>
        public override void Work()
        {
            if (Interlocked.CompareExchange(ref state, 2, 1) != 1) return;
            try
            {
                var key = requested;
                var d = FxNew.Conv.Table;
                if (key.Ir != baseIr || key.UserVersion != baseVersion)
                {
                    BaseResponse(key.Ir, out baseL, out baseR);
                    baseIr = key.Ir;
                    baseVersion = key.UserVersion;
                }
                var s = new ImpulseResponses.ShapeSettings
                {
                    Size = d[FxNew.Conv.Size].Value(key.Size),
                    PreDelay = d[FxNew.Conv.PreDelay].Value(key.PreDelay) <= 0.0011 ? 0 : d[FxNew.Conv.PreDelay].Value(key.PreDelay),
                    Decay = d[FxNew.Conv.Decay].Value(key.Decay),
                    Damping = d[FxNew.Conv.Damping].Value(key.Damping),
                    LowCutHz = d[FxNew.Conv.LowCut].Value(key.LowCut),
                    HighCutHz = d[FxNew.Conv.HighCut].Value(key.HighCut),
                    Width = d[FxNew.Conv.Width].Value(key.Width),
                    Reverse = key.Reverse != 0,
                };
                ImpulseResponses.Shape(baseL, baseR, Rate, s, out var l, out var r);
                Volatile.Write(ref incoming, new StereoConvolver(l, r));
                Volatile.Write(ref state, 3);
            }
            catch (Exception)
            {
                // A response that cannot be built leaves the last one playing (and is asked for again only when a parameter moves).
                Volatile.Write(ref state, 0);
            }
        }

        void BaseResponse(int ir, out float[] l, out float[] r)
        {
            var clip = ir == FxNew.Conv.UserChoice ? user.Ir : null;
            if (clip != null && clip.Left != null && clip.Left.Length > 16)
            {
                double step = clip.Rate / (double)Rate;
                l = ImpulseResponses.Resample(clip.Left, step);
                r = clip.Right == null ? l : ImpulseResponses.Resample(clip.Right, step);
                return;
            }
            // No user response imported: the hall stands in.
            ImpulseResponses.Generate(ir == FxNew.Conv.UserChoice ? 4 : ir, Rate, out l, out r);
        }

        public override void Process(float[] l, float[] r, int n)
        {
            if (Volatile.Read(ref state) == 3)
            {
                var next = Volatile.Read(ref incoming);
                Volatile.Write(ref incoming, null);
                if (current != null && ran)
                {
                    old = current;
                    fade = FadeSamples;
                }
                current = next;
                Volatile.Write(ref state, 0);
            }
            if (current == null) return;
            ran = true;
            float target = (float)DspMath.DbToGain(Phys(FxNew.Conv.Output));
            for (int done = 0, m; done < n; done += m)
            {
                m = Math.Min(Block, n - done);
                if (done == 0 && m == n) Run(l, r, m, target, 0);
                else
                {
                    // A block longer than the rack's (not expected): in pieces through the scratch.
                    Array.Copy(l, done, tmpL, 0, m);
                    Array.Copy(r, done, tmpR, 0, m);
                    RunTmp(m, target);
                    Array.Copy(tmpL, 0, l, done, m);
                    Array.Copy(tmpR, 0, r, done, m);
                }
            }
        }

        void RunTmp(int m, float target) => Run(tmpL, tmpR, m, target, 1);

        float[] oldL = new float[Block], oldR = new float[Block];

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Run(float[] l, float[] r, int m, float target, int scratch)
        {
            if (old != null)
            {
                Array.Copy(l, oldL, m);
                Array.Copy(r, oldR, m);
                old.Process(oldL, oldR, m);
            }
            current.Process(l, r, m);
            float step = (target - gain) / Math.Max(1, m);
            for (int i = 0; i < m; i++)
            {
                gain += step;
                float yl = l[i], yr = r[i];
                if (old != null)
                {
                    float x = fade > 0 ? 1f - fade / (float)FadeSamples : 1f;
                    yl = oldL[i] * (1 - x) + yl * x;
                    yr = oldR[i] * (1 - x) + yr * x;
                    if (fade > 0) fade--;
                }
                l[i] = yl * gain;
                r[i] = yr * gain;
            }
            gain = target;
            if (old != null && fade <= 0) old = null;
        }
    }

    internal sealed class VocoderHost : FxNewHost
    {
        const int Block = DyeEngine.MaxBlock;
        readonly ChannelVocoder voc;
        readonly VoiceModulator voice;
        readonly FxUserData user;
        readonly float[] mod = new float[Block];
        // The user's clip at the host's rate (the builder makes it; the audio thread takes it when the state says so).
        // 0 idle, 1 requested, 2 building, 3 built, 4 a request being written.
        int state;
        int wantedVersion = -1;
        float[] incoming, data;
        double cursor;

        public VocoderHost(float rate, FxUserData user) : base(FxType.Vocoder, rate, FxNew.Voc.Table)
        {
            this.user = user;
            voc = new ChannelVocoder(rate);
            voice = new VoiceModulator(rate);
        }

        public override int Latency => 0;

        public override bool HasWork => Volatile.Read(ref state) == 1;

        public override bool Settled
        {
            get
            {
                int s = Volatile.Read(ref state);
                return s != 1 && s != 2 && s != 4;
            }
        }

        public override void Reset()
        {
            voc.Reset();
            voice.Reset();
            cursor = 0;
        }

        public override void Poll()
        {
            if (Volatile.Read(ref state) != 0) return;
            // Only a user clip that the Sample source will play is worth resampling.
            int version = V.Choice(FxNew.Voc.Source) == 1 ? user.ModulatorVersion : 0;
            if (version == wantedVersion) return;
            if (Interlocked.CompareExchange(ref state, 4, 0) != 0) return;
            wantedVersion = version;
            if (version == 0)
            {
                // Nothing to build: the clip is dropped by the audio thread at its next block.
                Volatile.Write(ref incoming, Array.Empty<float>());
                Volatile.Write(ref state, 3);
                return;
            }
            Volatile.Write(ref state, 1);
            FxBuilder.Kick();
        }

        public override void Work()
        {
            if (Interlocked.CompareExchange(ref state, 2, 1) != 1) return;
            try
            {
                var clip = user.Modulator;
                var data = clip == null ? Array.Empty<float>() : ImpulseResponses.Resample(clip.Left, clip.Rate / (double)Rate);
                // The band levels the vocoder follows are the modulator's own: a clip is brought to a peak of 0.5 (like the internal voice).
                float peak = 0;
                foreach (float v in data) peak = Math.Max(peak, Math.Abs(v));
                if (peak > 1e-6f)
                    for (int i = 0; i < data.Length; i++) data[i] *= 0.5f / peak;
                Volatile.Write(ref incoming, data);
            }
            catch (Exception)
            {
                Volatile.Write(ref incoming, Array.Empty<float>());
            }
            Volatile.Write(ref state, 3);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public override void Process(float[] l, float[] r, int n)
        {
            if (Volatile.Read(ref state) == 3)
            {
                data = Volatile.Read(ref incoming);
                Volatile.Write(ref incoming, null);
                cursor = 0;
                Volatile.Write(ref state, 0);
            }
            double lowRaw = Phys(FxNew.Voc.Low), highRaw = Phys(FxNew.Voc.High);
            voc.Bands = (int)Math.Round(Phys(FxNew.Voc.Bands));
            voc.FormantSemitones = (float)Phys(FxNew.Voc.Formant);
            voc.LowHz = (float)lowRaw;
            voc.HighHz = (float)Math.Max(highRaw, lowRaw * 4);
            voc.AttackMs = (float)(Phys(FxNew.Voc.Attack) * 1000);
            voc.ReleaseMs = (float)(Phys(FxNew.Voc.Release) * 1000);
            voc.Sibilance = (float)Phys(FxNew.Voc.Sibilance);
            voc.Output = (float)DspMath.DbToGain(Phys(FxNew.Voc.Output));
            float speed = (float)Phys(FxNew.Voc.Speed);
            voice.Speed = speed;
            bool sample = V.Choice(FxNew.Voc.Source) == 1 && data != null && data.Length > 16;
            for (int done = 0, m; done < n; done += m)
            {
                m = Math.Min(Block, n - done);
                if (sample)
                {
                    var d = data;
                    double c = cursor;
                    for (int i = 0; i < m; i++)
                    {
                        int k = (int)c;
                        float f = (float)(c - k);
                        int k1 = k + 1 == d.Length ? 0 : k + 1;
                        mod[i] = d[k] + (d[k1] - d[k]) * f;
                        c += speed;
                        if (c >= d.Length) c -= d.Length;
                    }
                    cursor = c;
                }
                else voice.Render(mod, m);
                if (done == 0 && m == n) voc.Process(l, r, mod, m);
                else
                {
                    var tl = new ReadOnlySpan<float>(l, done, m);
                    var tr = new ReadOnlySpan<float>(r, done, m);
                    tl.CopyTo(chunkL);
                    tr.CopyTo(chunkR);
                    voc.Process(chunkL, chunkR, mod, m);
                    chunkL.AsSpan(0, m).CopyTo(new Span<float>(l, done, m));
                    chunkR.AsSpan(0, m).CopyTo(new Span<float>(r, done, m));
                }
            }
        }

        readonly float[] chunkL = new float[Block], chunkR = new float[Block];
    }
}
