#nullable disable
// Neo Patch NP0 (docs/specs/neo-patch.md §4.4): the runtime. Notes go through Neo's VoiceAllocator (the one Dye Essence's engine
// uses: poly, mono, legato, glide, sustain, steals with a fade) onto 16 voices; every block of 64 samples the voices' state is
// written into the context and the compiled patch's jobs run once across all voices. A new compiled patch is handed over from any
// thread (Publish) and taken at the next block boundary, with a one-block crossfade from the old one. Nothing on the audio path
// allocates; the master section is a DC blocker, a soft safety limiter and a guard.

using System;
using System.Diagnostics;
using System.Threading;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;

namespace NeoBuzz.Patch
{
    internal sealed class PatchVoice : IVoice
    {
        readonly float sr;
        public bool Sounding, Gate, Fresh;
        public float Cur, Target, Vel, Loud, GainEnd = 1f;
        bool pending;
        float pNote, pVel, pGlide;
        StealFade fade;
        int quiet, releaseBlocks;

        public PatchVoice(float sampleRate) { sr = sampleRate; }

        public bool IsActive => Sounding || pending;
        public float Loudness => Loud;

        public void NoteOn(float note, float velocity, int unisonIndex, int unisonCount, float glideFrom)
        {
            if (Sounding)
            {
                pending = true; pNote = note; pVel = velocity; pGlide = glideFrom;
                fade.Start(sr);
            }
            else Begin(note, velocity, glideFrom);
        }

        void Begin(float note, float velocity, float glideFrom)
        {
            Cur = float.IsNaN(glideFrom) ? note : glideFrom;
            Target = note; Vel = velocity;
            Gate = true; Sounding = true; Fresh = true;
            quiet = 0; releaseBlocks = 0; GainEnd = 1f;
            pending = false;
            fade.Cancel();
        }

        public void Legato(float note, bool glide) { Target = note; if (!glide) Cur = note; }
        public void NoteOff() { Gate = false; releaseBlocks = 0; }
        public void Kill() { Sounding = false; pending = false; Gate = false; fade.Cancel(); GainEnd = 0f; }

        /// <summary>Writes this voice's slot of the context for the next block; <paramref name="glideK"/> is the block's glide step (1: none).</summary>
        public void Prepare(PatchContext c, int v, float glideK)
        {
            if (pending && !fade.Fading) Begin(pNote, pVel, pGlide);
            c.Active[v] = Sounding;
            float g0 = Sounding ? GainEnd : 0f;
            if (Sounding && fade.Fading)
            {
                float g = GainEnd;
                for (int i = 0; i < PatchContext.Block; i++)
                {
                    if (!fade.Next(out g)) { g = 0f; break; }
                }
                GainEnd = g;
            }
            c.Gain0[v] = g0;
            c.Gain1[v] = Sounding ? GainEnd : 0f;
            c.Pitch0[v] = Cur;
            Cur += (Target - Cur) * glideK;
            c.Pitch1[v] = Cur;
            c.Gate[v] = Gate ? 1f : 0f;
            c.Vel[v] = Vel;
            c.Peak[v] = 0f;
        }

        public void Finish(PatchContext c, int v)
        {
            Fresh = false;
            Loud = c.Peak[v];
            if (Sounding && !Gate)
            {
                releaseBlocks++;
                quiet = Loud < 1e-5f ? quiet + 1 : 0;
                // Out of sound after two quiet blocks, or after 20 s of release (a patch that never falls silent).
                if (quiet >= 2 || releaseBlocks > 20 * sr / PatchContext.Block) Sounding = false;
            }
        }
    }

    internal sealed class PatchRuntime
    {
        public const int Block = PatchContext.Block;
        readonly PatchContext ctx = new PatchContext();
        readonly PatchVoice[] voices = new PatchVoice[PatchContext.MaxVoices];
        readonly VoiceAllocator alloc;
        readonly float[] fadeL = new float[Block], fadeR = new float[Block];
        readonly float[] ringL = new float[Block], ringR = new float[Block];
        int ringPos = Block;
        CompiledPatch cur, fading, pending;
        DcBlocker dcL, dcR;
        float sampleRate;
        float glideSeconds;
        long nonFinite;

        public PatchRuntime(float sampleRate = 48000f, LaneKind lanes = LaneKind.Auto, int tick = 16)
        {
            this.sampleRate = sampleRate;
            ctx.SampleRate = sampleRate;
            ctx.Tick = tick;
            ctx.Kind = lanes == LaneKind.Auto ? (Avx2Lanes.Supported ? LaneKind.Avx2 : LaneKind.Pair)
                : lanes == LaneKind.Avx2 && !Avx2Lanes.Supported ? LaneKind.Pair : lanes;
            var iv = new IVoice[voices.Length];
            for (int i = 0; i < voices.Length; i++) iv[i] = voices[i] = new PatchVoice(sampleRate);
            alloc = new VoiceAllocator(iv) { Polyphony = 8 };
            dcL.Set(sampleRate); dcR.Set(sampleRate);
        }

        public LaneKind Lanes => ctx.Kind;
        public float Volume = 0.5f;
        /// <summary>Set by a test: each job's accumulated time of the current patch, in Stopwatch ticks.</summary>
        public long[] Profile;
        /// <summary>Notes that can sound at once, 1 … 16.</summary>
        public int Voices { get => alloc.Polyphony; set => alloc.Polyphony = Math.Clamp(value, 1, PatchContext.MaxVoices); }
        public float Glide { get => glideSeconds; set { glideSeconds = Math.Max(0f, value); alloc.GlideAlways = glideSeconds > 0f; } }
        public VoiceMode Mode { get => alloc.Mode; set => alloc.Mode = value; }
        /// <summary>Samples the guard has had to zero (NaN, infinities, runaway) at the output.</summary>
        public long NonFinite => nonFinite;
        public int ActiveVoices { get { int n = 0; for (int i = 0; i < voices.Length; i++) if (voices[i].Sounding) n++; return n; } }

        // ------------------------------------------------------------ control thread

        /// <summary>Compiles a graph (allocates: not for the audio thread). The patch isn't heard until <see cref="Publish"/>.</summary>
        public CompileResult Compile(PatchGraph g) => PatchCompiler.Compile(g, ctx);

        /// <summary>Hands a compiled patch to the audio thread, which takes it at the next block boundary (an allocation-free exchange).</summary>
        public void Publish(CompiledPatch p) => Interlocked.Exchange(ref pending, p);

        /// <summary>Compile and publish. The compile error, or null.</summary>
        public string SetGraph(PatchGraph g)
        {
            var r = Compile(g);
            if (r.Patch != null) Publish(r.Patch);
            return r.Error;
        }

        public void NoteOn(int key, float note, float velocity) => alloc.NoteOn(key, note, velocity);
        public void NoteOff(int key) => alloc.NoteOff(key);
        public void AllNotesOff(bool hard) => alloc.AllNotesOff(hard);

        // ------------------------------------------------------------ audio thread

        /// <summary>Renders any number of samples through 64-sample blocks (notes and a new patch land on a block's start).</summary>
        public void Process(float[] left, float[] right, int n)
        {
            for (int i = 0; i < n; i++)
            {
                if (ringPos == Block) { ProcessBlock(ringL, ringR); ringPos = 0; }
                left[i] = ringL[ringPos];
                right[i] = ringR[ringPos];
                ringPos++;
            }
        }

        public void ProcessBlock(float[] left, float[] right)
        {
            var p = Interlocked.Exchange(ref pending, null);
            if (p != null) { fading = cur; cur = p; }

            float glideK = glideSeconds > 0f ? (float)(1.0 - PortableMath.Exp(-Block / (glideSeconds * sampleRate))) : 1f;
            for (int g = 0; g < PatchContext.Groups; g++) ctx.GroupOn[g] = false;
            for (int v = 0; v < voices.Length; v++)
            {
                voices[v].Prepare(ctx, v, glideK);
                if (ctx.Active[v]) ctx.GroupOn[v / PatchContext.Width] = true;
            }

            if (fading != null)
            {
                Run(fading, false);
                Array.Copy(ctx.OutL, fadeL, Block);
                Array.Copy(ctx.OutR, fadeR, Block);
            }
            if (cur != null) Run(cur, true);
            else { Array.Clear(ctx.OutL); Array.Clear(ctx.OutR); }
            if (fading != null)
            {
                for (int i = 0; i < Block; i++)
                {
                    float t = (i + 1) * (1f / Block);
                    ctx.OutL[i] = fadeL[i] * (1f - t) + ctx.OutL[i] * t;
                    ctx.OutR[i] = fadeR[i] * (1f - t) + ctx.OutR[i] * t;
                }
                fading = null;
            }

            for (int v = 0; v < voices.Length; v++) voices[v].Finish(ctx, v);

            for (int i = 0; i < Block; i++)
            {
                left[i] = Master(ctx.OutL[i], ref dcL);
                right[i] = Master(ctx.OutR[i], ref dcR);
            }
        }

        void Run(CompiledPatch p, bool last)
        {
            // A patch's first block restarts the state of the voices sounding (a swap), as a new note does.
            for (int v = 0; v < voices.Length; v++) ctx.Fresh[v] = voices[v].Fresh || (p.First && ctx.Active[v]);
            Array.Clear(ctx.OutL);
            Array.Clear(ctx.OutR);
            var jobs = p.Jobs;
            var prof = Profile;
            if (prof != null && prof.Length >= jobs.Length && last)
            {
                // Profiling (tests): each job's time in Stopwatch ticks.
                for (int i = 0; i < jobs.Length; i++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    jobs[i].Run(ctx);
                    prof[i] += Stopwatch.GetTimestamp() - t0;
                }
            }
            else for (int i = 0; i < jobs.Length; i++) jobs[i].Run(ctx);
            p.First = false;
        }

        float Master(float x, ref DcBlocker dc)
        {
            if (!(Math.Abs(x) < 1e6f)) { x = 0f; nonFinite++; }
            x = dc.Process(x) * Volume;
            // Soft safety limiter: untouched to 0.9, then a tanh knee that never reaches 1.
            float a = Math.Abs(x);
            if (a > 0.9f)
            {
                float t = 0.9f + 0.1f * DspMath.FastTanh((a - 0.9f) * 10f);
                x = x < 0f ? -t : t;
            }
            return x;
        }
    }
}
