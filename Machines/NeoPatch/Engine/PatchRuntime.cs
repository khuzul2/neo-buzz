#nullable disable
// Neo Patch NP1 (docs/specs/neo-patch.md §4.1, §4.4): the runtime. Notes (patterns, MIDI, MPE) go through Neo's VoiceAllocator (the
// one Dye Essence's engine uses: poly, glide, sustain, steals with a fade) onto 16 voices; every block of 64 samples the voices'
// state is written into the context and the compiled patch's jobs run once across all voices (global nodes in a one-voice view). A
// new compiled patch is handed over from any thread (Publish) and taken at the next block boundary: the nodes that survived the
// edit carry their state with them, and the first block after a swap ramps from the last sample of the block before, so a changed
// structure never steps. Nothing on the audio path allocates; the master section is a DC blocker, a soft safety limiter and a guard.

using System;
using System.Diagnostics;
using System.Threading;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Sampler;

namespace NeoBuzz.Patch
{
    internal sealed class PatchVoice : IVoice
    {
        readonly float sr;
        readonly PatchRuntime rt;
        public bool Sounding, Gate, Fresh;
        public float Cur, Target, Vel, Loud, GainEnd = 1f;
        /// <summary>The MIDI channel of the note (0: the main channel, or a pattern note; 1 … 15: an MPE member).</summary>
        public int Channel;
        bool pending;
        float pNote, pVel, pGlide;
        int pChannel;
        StealFade fade;
        int quiet, releaseBlocks;
        float lastPress, lastSlide, lastBend;

        public PatchVoice(float sampleRate, PatchRuntime runtime) { sr = sampleRate; rt = runtime; }

        public bool IsActive => Sounding || pending;
        public float Loudness => Loud;

        public void NoteOn(float note, float velocity, int unisonIndex, int unisonCount, float glideFrom)
        {
            if (Sounding)
            {
                pending = true; pNote = note; pVel = velocity; pGlide = glideFrom; pChannel = rt.NoteChannel;
                fade.Start(sr);
            }
            else { Channel = rt.NoteChannel; Begin(note, velocity, glideFrom); }
        }

        void Begin(float note, float velocity, float glideFrom)
        {
            Cur = float.IsNaN(glideFrom) ? note : glideFrom;
            Target = note; Vel = velocity;
            Gate = true; Sounding = true; Fresh = true;
            quiet = 0; releaseBlocks = 0; GainEnd = 1f;
            pending = false;
            lastPress = lastSlide = float.NaN;
            fade.Cancel();
        }

        public void Legato(float note, bool glide) { Target = note; if (!glide) Cur = note; }
        public void NoteOff() { Gate = false; releaseBlocks = 0; }
        public void Kill() { Sounding = false; pending = false; Gate = false; fade.Cancel(); GainEnd = 0f; }

        /// <summary>Writes this voice's slot of the context for the next block; <paramref name="glideK"/> is the block's glide step (1: none).</summary>
        public void Prepare(PatchContext c, int v, float glideK)
        {
            if (pending && !fade.Fading) { Channel = pChannel; Begin(pNote, pVel, pGlide); }
            c.VActive[v] = Sounding;
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
            float bend = rt.BendFor(Channel);
            c.Pitch0[v] = Cur + (float.IsNaN(lastBend) ? bend : lastBend);
            Cur += (Target - Cur) * glideK;
            c.Pitch1[v] = Cur + bend;
            lastBend = bend;
            float press = rt.PressureFor(Channel), slide = rt.SlideFor(Channel);
            c.Press0[v] = float.IsNaN(lastPress) ? press : lastPress; c.Press1[v] = press; lastPress = press;
            c.Slide0[v] = float.IsNaN(lastSlide) ? slide : lastSlide; c.Slide1[v] = slide; lastSlide = slide;
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

    internal sealed class PatchRuntime : IChannelNoteSink, IDisposable
    {
        public const int Block = PatchContext.Block;
        /// <summary>The bend range of the main channel, semitones; MPE member channels bend by 48 (the MPE default).</summary>
        public const float BendRange = 2f, MemberBendRange = 48f;
        readonly PatchContext ctx = new PatchContext();
        readonly PatchVoice[] voices = new PatchVoice[PatchContext.MaxVoices];
        readonly VoiceAllocator alloc;
        readonly float[] ringL = new float[Block], ringR = new float[Block];
        int ringPos = Block;
        CompiledPatch cur, pending, lastPublished;
        PatchGraph lastGraph;
        DcBlocker dcL, dcR;
        float sampleRate;
        float glideSeconds;
        long nonFinite;
        bool ramp;
        float lastL, lastR, lastL2, lastR2;
        readonly float[] macroTarget = new float[16];
        float macroK;

        // Notes and controllers.
        internal int NoteChannel;
        readonly float[] chBend = new float[16], chPress = new float[16], chSlide = new float[16];
        float gBend, gPress, gMod;
        readonly int[] heldKey = new int[16];
        readonly float[] heldNote = new float[16], heldVel = new float[16];
        int heldCount;
        float lastNote = 60f, lastVel = 0.8f, gCur = 60f, gTarget = 60f;

        // Compiling in the background (the Voices setting or the sample rate changed under a compiled patch).
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        volatile bool wantRecompile, compiling, stopWorker;
        Thread worker;
        public readonly object GraphLock = new object();

        public PatchRuntime(float sampleRate = 48000f, LaneKind lanes = LaneKind.Auto, int tick = 16)
        {
            this.sampleRate = sampleRate;
            ctx.SampleRate = sampleRate;
            ctx.Tick = tick;
            ctx.VoiceSlots = 8;
            ctx.Kind = lanes == LaneKind.Auto ? (Avx2Lanes.Supported ? LaneKind.Avx2 : LaneKind.Pair)
                : lanes == LaneKind.Avx2 && !Avx2Lanes.Supported ? LaneKind.Pair : lanes;
            var iv = new IVoice[voices.Length];
            for (int i = 0; i < voices.Length; i++) iv[i] = voices[i] = new PatchVoice(sampleRate, this);
            alloc = new VoiceAllocator(iv) { Polyphony = 8 };
            dcL.Set(sampleRate); dcR.Set(sampleRate);
            macroK = (float)(1.0 - PortableMath.Exp(-Block / (0.02 * sampleRate)));
        }

        public LaneKind Lanes => ctx.Kind;
        public float SampleRateNow => sampleRate;
        public float Volume = 0.5f;
        /// <summary>Set by a test: each job's accumulated time of the current patch, in Stopwatch ticks.</summary>
        public long[] Profile;
        /// <summary>Notes that can sound at once, 1 … 16.</summary>
        public int Voices
        {
            get => alloc.Polyphony;
            set
            {
                alloc.Polyphony = Math.Clamp(value, 1, PatchContext.MaxVoices);
                if (alloc.Polyphony > ctx.VoiceSlots && lastGraph != null) RequestRecompile();
            }
        }
        public float Glide { get => glideSeconds; set { glideSeconds = Math.Max(0f, value); alloc.GlideAlways = glideSeconds > 0f; } }
        public VoiceMode Mode { get => alloc.Mode; set => alloc.Mode = value; }
        /// <summary>The patch's "audio-rate modulation" switch: control ports are read for every sample, not every control interval.</summary>
        public bool AudioRateModulation { get => ctx.AudioMod; set => ctx.AudioMod = value; }
        /// <summary>The control interval, samples (16 by default).</summary>
        public int ControlInterval => ctx.Tick;
        /// <summary>The user's own sample, the Sample node's source "user" (null: none).</summary>
        public SampleLayer UserSample { get => ctx.UserSample; set => ctx.UserSample = value; }
        /// <summary>Samples the guard has had to zero (NaN, infinities, runaway) at the output.</summary>
        public long NonFinite => nonFinite;
        public int ActiveVoices { get { int n = 0; for (int i = 0; i < voices.Length; i++) if (voices[i].Sounding) n++; return n; } }
        /// <summary>Nodes that took their state over from the patch before, at the last compile.</summary>
        public int LastAdopted => lastPublished?.Adopted ?? 0;
        public CompiledPatch Current => lastPublished;

        public void SetSampleRate(float sr)
        {
            if (sr == sampleRate) return;
            sampleRate = sr;
            ctx.SampleRate = sr;
            dcL.Set(sr); dcR.Set(sr);
            macroK = (float)(1.0 - PortableMath.Exp(-Block / (0.02 * sr)));
            if (lastGraph != null) RequestRecompile();
        }

        /// <summary>A macro, 0 … 1 (smoothed over about 20 ms).</summary>
        public void SetMacro(int i, float value01)
        {
            if ((uint)i < 16u) macroTarget[i] = float.IsFinite(value01) ? Math.Clamp(value01, 0f, 1f) : 0f;
        }

        // ------------------------------------------------------------ control thread

        /// <summary>Compiles a graph (allocates: not for the audio thread). The patch isn't heard until <see cref="Publish"/>.</summary>
        public CompileResult Compile(PatchGraph g)
        {
            ctx.VoiceSlots = Math.Max(ctx.VoiceSlots, alloc.Polyphony);
            Kernels.Warm();
            return PatchCompiler.Compile(g, ctx, lastPublished);
        }

        /// <summary>Hands a compiled patch to the audio thread, which takes it at the next block boundary (an allocation-free exchange).</summary>
        public void Publish(CompiledPatch p)
        {
            lastPublished = p;
            Interlocked.Exchange(ref pending, p);
        }

        /// <summary>Compile and publish; the nodes that are in the patch already take over their state. The compile error, or null.</summary>
        public string SetGraph(PatchGraph g)
        {
            lock (GraphLock)
            {
                lastGraph = g;
                var r = Compile(g);
                if (r.Patch != null) Publish(r.Patch);
                return r.Error;
            }
        }

        /// <summary>Compiles the graph again (a node setting that only a compile applies, or after the graph was edited); false when there is none.</summary>
        public string Recompile() => lastGraph == null ? null : SetGraph(lastGraph);

        /// <summary>True from the request of a background compile (the sample rate or the Voices setting changed under a compiled patch) until its patch is
        /// published. An offline render waits for it (<see cref="PatchEngine.Busy"/>): a patch that lands at some block of the render, by thread timing, moves the samples.</summary>
        /// <summary>Tests only: the background compile waits this long first (a slow machine).</summary>
        internal int TestCompileDelayMs;

        public bool Compiling => wantRecompile || compiling;

        void RequestRecompile()
        {
            if (worker == null)
            {
                worker = new Thread(Worker) { IsBackground = true, Name = "Neo Patch compiler" };
                worker.Start();
            }
            wantRecompile = true;
            wake.Set();
        }

        void Worker()
        {
            while (!stopWorker)
            {
                wake.WaitOne();
                if (stopWorker) return;
                if (!wantRecompile) continue;
                compiling = true;
                wantRecompile = false;
                try { if (TestCompileDelayMs > 0) Thread.Sleep(TestCompileDelayMs); Recompile(); } catch (Exception) { }
                finally { compiling = false; }
            }
        }

        public void Dispose()
        {
            stopWorker = true;
            wake.Set();
        }

        // ------------------------------------------------------------ notes (audio thread, at a block's start)

        public void NoteOn(int key, int note, float velocity) => NoteOnF(key, note, velocity, 0);

        public void NoteOn(int key, int note, float velocity, int channel) => NoteOnF(key, note, velocity, channel);

        void NoteOnF(int key, float note, float velocity, int channel)
        {
            NoteChannel = channel;
            if (heldCount == heldKey.Length) { Array.Copy(heldKey, 1, heldKey, 0, heldCount - 1); Array.Copy(heldNote, 1, heldNote, 0, heldCount - 1); Array.Copy(heldVel, 1, heldVel, 0, heldCount - 1); heldCount--; }
            heldKey[heldCount] = key; heldNote[heldCount] = note; heldVel[heldCount] = velocity; heldCount++;
            lastNote = note; lastVel = velocity;
            gTarget = note;
            alloc.NoteOn(key, note, velocity);
            NoteChannel = 0;
        }

        /// <summary>For tests and the host: a note at a fractional pitch.</summary>
        public void NoteOnPitch(int key, float note, float velocity) => NoteOnF(key, note, velocity, 0);

        public void NoteOff(int key) => NoteOff(key, 0, 0);

        public void NoteOff(int key, int note, int channel)
        {
            for (int i = 0; i < heldCount; i++)
                if (heldKey[i] == key)
                {
                    Array.Copy(heldKey, i + 1, heldKey, i, heldCount - i - 1);
                    Array.Copy(heldNote, i + 1, heldNote, i, heldCount - i - 1);
                    Array.Copy(heldVel, i + 1, heldVel, i, heldCount - i - 1);
                    heldCount--;
                    break;
                }
            if (heldCount > 0) gTarget = heldNote[heldCount - 1];
            alloc.NoteOff(key);
        }

        public void AllNotesOff(bool hard) { heldCount = 0; alloc.AllNotesOff(hard); }
        public void ModWheel(float value) => gMod = value;
        public void PitchBend(float value) => gBend = value;
        public void Sustain(bool down) => alloc.Sustain(down);
        public void Aftertouch(float value) => gPress = value;

        public bool Control(int channel, int ctrl, int value)
        {
            channel = Math.Clamp(channel, 0, 15);
            switch (ctrl)
            {
                case 0xE0:
                case 0xFF:
                    if (channel == 0) return false;
                    chBend[channel] = Math.Clamp((value - 8192) / 8192f, -1f, 1f);
                    return true;
                case 0xD0:
                case 0xFC:
                    if (channel == 0) return false;
                    chPress[channel] = Math.Clamp(value, 0, 127) / 127f;
                    return true;
                case 74:
                    chSlide[channel] = Math.Clamp(value, 0, 127) / 127f;
                    return true;
            }
            return false;
        }

        internal float BendFor(int channel) => gBend * BendRange + (channel > 0 ? chBend[channel] * MemberBendRange : 0f);
        internal float PressureFor(int channel) => channel > 0 ? Math.Max(chPress[channel], gPress) : gPress;
        internal float SlideFor(int channel) => channel > 0 ? chSlide[channel] : Math.Max(gMod, chSlide[0]);

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
            if (p != null) { ramp = cur != null; cur = p; }

            float glideK = glideSeconds > 0f ? (float)(1.0 - PortableMath.Exp(-Block / (glideSeconds * sampleRate))) : 1f;
            for (int g = 0; g < PatchContext.Groups; g++) ctx.VGroupOn[g] = false;
            bool any = false;
            for (int v = 0; v < voices.Length; v++)
            {
                voices[v].Prepare(ctx, v, glideK);
                ctx.VFresh[v] = voices[v].Fresh;
                if (ctx.VActive[v]) { ctx.VGroupOn[v / PatchContext.Width] = true; any = true; }
            }
            ctx.GPitch0 = gCur;
            gCur += (gTarget - gCur) * glideK;
            ctx.GPitch1 = gCur;
            ctx.GGate = heldCount > 0 ? 1f : 0f;
            ctx.GVel = heldCount > 0 ? heldVel[heldCount - 1] : lastVel;
            for (int m = 0; m < 16; m++) ctx.Macros[m] += (macroTarget[m] - ctx.Macros[m]) * macroK;

            Array.Clear(ctx.OutL);
            Array.Clear(ctx.OutR);
            if (cur != null && (any || cur.HasGlobal)) Run(cur);
            if (ramp)
            {
                // The first block of a new patch: the step from where the last block was heading to where this one starts is taken out,
                // decaying to nothing over the block (a surviving node continues exactly, so a tone goes on; a changed structure doesn't click).
                float dL = (lastL + (lastL - lastL2)) - ctx.OutL[0], dR = (lastR + (lastR - lastR2)) - ctx.OutR[0];
                for (int i = 0; i < Block; i++)
                {
                    float k = 1f - i * (1f / Block);
                    ctx.OutL[i] += dL * k;
                    ctx.OutR[i] += dR * k;
                }
                ramp = false;
            }
            lastL2 = ctx.OutL[Block - 2];
            lastR2 = ctx.OutR[Block - 2];
            lastL = ctx.OutL[Block - 1];
            lastR = ctx.OutR[Block - 1];

            for (int v = 0; v < voices.Length; v++) voices[v].Finish(ctx, v);

            for (int i = 0; i < Block; i++)
            {
                left[i] = Master(ctx.OutL[i], ref dcL);
                right[i] = Master(ctx.OutR[i], ref dcR);
            }
        }

        void Run(CompiledPatch p)
        {
            var maps = p.MacroMaps;
            for (int i = 0; i < maps.Length; i++)
            {
                var m = maps[i];
                m.Node.Set(m.Control, m.Min + (m.Max - m.Min) * ctx.Macros[m.Macro]);
            }
            var jobs = p.Jobs;
            var prof = Profile;
            bool profile = prof != null && prof.Length >= jobs.Length;
            for (int i = 0; i < jobs.Length; i++)
            {
                var j = jobs[i];
                long t0 = profile ? Stopwatch.GetTimestamp() : 0;
                if (j.Global)
                {
                    ctx.EnterGlobal(!j.Started);
                    j.Run(ctx);
                    ctx.LeaveGlobal();
                }
                else if (!j.Started)
                {
                    // A new node (or a new patch): the voices sounding start its state fresh, as a new note does.
                    for (int v = 0; v < voices.Length; v++) ctx.VFresh[v] = ctx.VFresh[v] || ctx.VActive[v];
                    j.Run(ctx);
                    for (int v = 0; v < voices.Length; v++) ctx.VFresh[v] = voices[v].Fresh;
                }
                else j.Run(ctx);
                j.Started = true;
                if (profile) prof[i] += Stopwatch.GetTimestamp() - t0;
            }
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
