#nullable disable
// Neo Amp (docs/specs/neo-amp.md): the whole machine as one engine. Mono in, the same signal on both outputs.
//
//   L/R ─► Input (Left / Right / Both summed) ─► Trim ─┬─► tuner (when it is on)
//                                                       └─► noise gate ─► [ AmpCore: stages ► tone stack ► power stage ] ─► cabinet ─► mic, cuts ─► Output ─► mute
//   L/R ──────────────────────────────────────────────────────────────────────────────────────────── Mix and Amount (FxStage) ──────────┘
//
// A change of voicing or of the oversampling starts the other AmpCore on the same input, from rest, and crossfades to it over 30 ms (equal
// power); the old one stops when the fade is done. A change of cabinet builds the new response off the audio thread (AmpBuilder), then crossfades
// from the old one to it over 30 ms; "Off" is the amp itself. The cabinet is the shared StereoConvolver (zero latency); the same mono signal
// runs through both its channels. Nothing is allocated on the audio thread.
//
// Latency: the oversampler's, made the same at every quality by a pad after each core (FastOversampler.MaxLatency); the cabinet adds none.
// Mix and Amount: the shared FxStage (Amount scales the Gain in dB and the Master drive; 0 % is the input, bit for bit, delayed by the latency).

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.Amp
{
    /// <summary>A wave of the song read in: mono or stereo samples at a rate, with the version of what was read.</summary>
    internal sealed class AmpClip
    {
        public readonly string Name;
        public readonly float[] Left, Right;
        public readonly int Rate, Version;

        public AmpClip(string name, float[] left, float[] right, int rate, int version)
        {
            Name = name ?? "";
            Left = left;
            Right = right;
            Rate = rate;
            Version = version;
        }
    }

    internal enum AmpWaveState { Missing, Loading, Ready }

    /// <summary>Where the engine gets the song's waves from (the machine's reader; a test's stand-in).</summary>
    internal interface IAmpWaves
    {
        AmpWaveState State(int slot);
        AmpClip Get(int slot);
        void Want(int slot);
    }

    internal enum AmpCabStatus
    {
        /// <summary>The cabinet playing is the one the parameters ask for.</summary>
        Ready,
        /// <summary>A new cabinet is being prepared.</summary>
        Preparing,
        /// <summary>The wave is being read from the song.</summary>
        WaveLoading,
        /// <summary>The wave is not in the song: the closed 4x12 plays instead.</summary>
        WaveMissing,
    }

    internal readonly struct CabKey : IEquatable<CabKey>
    {
        public readonly int Rate, Cab, Slot, WaveVersion;
        public CabKey(int rate, int cab, int slot, int version) { Rate = rate; Cab = cab; Slot = slot; WaveVersion = version; }
        public bool Equals(CabKey o) => Rate == o.Rate && Cab == o.Cab && Slot == o.Slot && WaveVersion == o.WaveVersion;
        public override bool Equals(object o) => o is CabKey k && Equals(k);
        public override int GetHashCode() => HashCode.Combine(Rate, Cab, Slot, WaveVersion);
    }

    /// <summary>A cabinet's convolver and what describes it.</summary>
    internal sealed class CabBuilt
    {
        public StereoConvolver Convolver;
        public float[] Ir;
        public CabKey Key;
        public string Name;
        public bool FellBack;
        public int Rate;
    }

    internal sealed class AmpEngine
    {
        public const int MaxBlock = 256;
        public static int Latency => FastOversampler.MaxLatency;
        /// <summary>Length of the crossfade between voicings and between cabinets, seconds.</summary>
        public const double FadeSeconds = 0.03;

        /// <summary>What Amount scales: the Gain in dB (from 0 dB) and the power stage's Master drive (from 0).</summary>
        public static readonly AmountRule[] AmountRules =
        {
            AmountRule.Lin(P.Gain, 0), AmountRule.Lin(P.Master, 0),
        };

        readonly ParamValues values;
        readonly FxStage fx;
        readonly ParamValues eff;
        readonly IAmpWaves waves;
        int rate = 48000;

        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();

        // ---- input, trim, gate
        readonly float[] mono = new float[MaxBlock], gated = new float[MaxBlock];
        SmoothRamp trim;
        bool trimStarted;
        float gateEnv, gateGain = 1f;
        bool gateOpen = true;
        double gateAtt = 0.1, gateRelFactor = 0.999, envFall = 0.999;

        // ---- the amp cores
        readonly AmpCore[] cores = { new AmpCore(), new AmpCore() };
        readonly DelayLineF[] pads = { new DelayLineF(FastOversampler.MaxLatency + 2), new DelayLineF(FastOversampler.MaxLatency + 2) };
        readonly float[] coreOut = new float[MaxBlock], coreOld = new float[MaxBlock];
        readonly float[] gainArr = new float[MaxBlock], powerArr = new float[MaxBlock];
        SmoothRamp gainRamp;
        bool rampsStarted;
        int active, fade, fadeLength;
        bool coresConfigured;

        // ---- the cabinet
        int cabState;   // 0 idle, 1 requested, 2 building, 3 built, 4 a request being written
        CabKey wanted, requested;
        bool haveWanted, requestedFell;
        CabBuilt incoming;
        volatile CabBuilt currentPublished;
        CabBuilt cabCur, cabOld;
        bool directCur = true;       // the cabinet playing is "Off"
        int cabFade;
        bool cabStale;
        readonly float[] wetL = new float[MaxBlock], wetR = new float[MaxBlock], oldL = new float[MaxBlock], oldR = new float[MaxBlock];
        volatile AmpCabStatus status;

        // ---- mic, cuts, output
        Biquad micShelf, micLow, lowCut, highCut;
        double smPos, smLow = 20, smHigh = 20000;
        bool shelvesStarted;
        double micApplied = double.NaN, lowApplied = double.NaN, highApplied = double.NaN;
        bool lowOn, highOn;
        SmoothRamp outGain;
        bool outStarted;
        float muteGain = 1f;
        float muteStep;

        // ---- tuner
        AmpTuner tuner;

        public AmpEngine(ParamValues values, IAmpWaves waves = null)
        {
            this.values = values;
            this.waves = waves;
            AmpBuilder.Register(this);
            fx = new FxStage(values, P.Amount, P.Mix, AmountRules, maxLatency: Latency, maxBlock: MaxBlock);
            eff = fx.Effective;
            SetSampleRate(48000);
        }

        /// <summary>The values the engine reads (Amount applied).</summary>
        public ParamValues Effective => eff;

        public void SetSampleRate(float sr)
        {
            int r = (int)Math.Round(sr);
            if (r != rate || !coresConfigured || tuner == null)
            {
                rate = r;
                // A cabinet made for another rate is no good: the new one is asked for at once.
                if (cabCur != null && cabCur.Rate != rate) { cabCur = null; cabOld = null; cabFade = 0; directCur = true; currentPublished = null; }
                haveWanted = false;
                tuner = new AmpTuner(rate, MaxBlock);
            }
            fx.SetSampleRate(sr);
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            fadeLength = Math.Max(1, (int)Math.Round(FadeSeconds * rate));
            trim.Setup(0.02, sr); gainRamp.Setup(0.02, sr); powerRamps[0].Setup(0.02, sr); powerRamps[1].Setup(0.02, sr); outGain.Setup(0.02, sr);
            gateAtt = 1.0 - PortableMath.Exp(-1.0 / (0.002 * rate));
            envFall = PortableMath.Exp(-1.0 / (0.02 * rate));
            muteStep = (float)(1.0 / (0.01 * rate));
            smoothSetup();
            coresConfigured = false;
            Reset();
        }

        double smoothK;
        void smoothSetup() { smoothK = 1.0 - PortableMath.Exp(-(double)32 / (0.03 * rate)); }

        /// <summary>Back to the start: every state is cleared (the cabinet that plays stays).</summary>
        public void Reset()
        {
            fx.Reset();
            trimStarted = false; rampsStarted = false; outStarted = false; shelvesStarted = false;
            gateEnv = 0; gateGain = 1f; gateOpen = true;
            fade = 0;
            coresConfigured = false;
            cabStale = true;
            micShelf.Reset(); micLow.Reset(); lowCut.Reset(); highCut.Reset();
            micApplied = lowApplied = highApplied = double.NaN;
            muteGain = values.On(P.Tuner) ? 0f : 1f;
            tuner?.Reset();
            for (int i = 0; i < 2; i++) pads[i].Reset();
        }

        // ---- the cabinet's hand-over (as Neo Space's response)

        /// <summary>The cabinet playing now (null while "Off" plays or before the first is ready).</summary>
        public CabBuilt CurrentCab => currentPublished;

        public AmpCabStatus Status => status;

        /// <summary>The tuner's reading (any thread, no lock).</summary>
        public TunerReading Tuner => tuner.Read();

        /// <summary>The cabinet asked for is still being made, or the wave is being read: a render waits for this to be false (call <see cref="Poll"/> first).</summary>
        public bool Pending
        {
            get
            {
                int s = Volatile.Read(ref cabState);
                if (s == 1 || s == 2 || s == 4) return true;
                return waves != null && values[P.Cabinet] == AmpCabs.WaveCab && waves.State(values[P.CabWave] + 1) == AmpWaveState.Loading;
            }
        }

        /// <summary>Samples the cabinet's tail and the filters ring for after the input stops: the machine rests after this much silence.</summary>
        public int TailSamples
        {
            get
            {
                int n = currentPublished?.Ir?.Length ?? 0;
                return n + (cabOld?.Ir?.Length ?? 0) + (int)(0.3 * rate) + 4 * MaxBlock;
            }
        }

        readonly int[] snap = new int[P.Count];

        /// <summary>Asks for the cabinet the parameters name now (the audio thread, or any other: it only writes a request).</summary>
        public void Poll()
        {
            for (int i = 0; i < snap.Length; i++) snap[i] = values[i];
            Ask(snap);
        }

        /// <summary>The same for the values the host holds now (the song's thread: a value just set reaches the engine only at the next tick).</summary>
        public void Poll(Func<int, int> hostValue)
        {
            var a = new int[P.Count];
            for (int i = 0; i < a.Length; i++) a[i] = hostValue(i);
            Ask(a);
        }

        void Ask(int[] v)
        {
            int cab = v[P.Cabinet];
            int slot = 0, version = 0;
            bool fell = false;
            if (cab == AmpCabs.WaveCab)
            {
                slot = v[P.CabWave] + 1;
                if (waves != null) waves.Want(slot);
                var st = waves == null ? AmpWaveState.Missing : waves.State(slot);
                if (st == AmpWaveState.Ready)
                {
                    var clip = waves.Get(slot);
                    if (clip != null) version = clip.Version;
                    else st = AmpWaveState.Loading;
                }
                if (st == AmpWaveState.Loading)
                {
                    status = AmpCabStatus.WaveLoading;
                    return;   // keep what plays until the wave is in
                }
                if (st == AmpWaveState.Missing)
                {
                    cab = AmpCabs.Fallback;
                    slot = 0;
                    fell = true;
                }
            }
            else waves?.Want(0);
            var key = new CabKey(rate, cab, slot, version);
            if (haveWanted && key.Equals(wanted))
            {
                status = fell ? AmpCabStatus.WaveMissing : (Volatile.Read(ref cabState) == 0 && (cab == AmpCabs.Off ? directCur : cabCur != null && cabCur.Key.Equals(key)) ? AmpCabStatus.Ready : AmpCabStatus.Preparing);
                return;
            }
            if (cab == AmpCabs.Off)
            {
                if (Volatile.Read(ref cabState) != 0 || cabFade > 0) { status = AmpCabStatus.Preparing; return; }
                // Nothing to build: the amp plays direct.
                wanted = key; haveWanted = true;
                if (!directCur)
                {
                    cabOld = cabCur; cabCur = null; directCur = true; currentPublished = null;
                    cabFade = fadeLength;
                    if (cabStale) { cabOld = null; cabFade = 0; }
                }
                status = AmpCabStatus.Ready;
                return;
            }
            if (Volatile.Read(ref cabState) != 0) { status = AmpCabStatus.Preparing; return; }
            if (Interlocked.CompareExchange(ref cabState, 4, 0) != 0) return;
            requested = key;
            requestedFell = fell;
            wanted = key;
            haveWanted = true;
            status = fell ? AmpCabStatus.WaveMissing : AmpCabStatus.Preparing;
            Volatile.Write(ref cabState, 1);
            AmpBuilder.Kick();
        }

        internal bool HasWork => Volatile.Read(ref cabState) == 1;

        // The builder's cache of the wave's samples is the clip itself (read once per version); a built-in cabinet is made fresh (cheap).
        /// <summary>The builder's job (never the audio thread's): makes the cabinet of the request.</summary>
        internal void Work()
        {
            if (Interlocked.CompareExchange(ref cabState, 2, 1) != 1) return;
            try
            {
                var key = requested;
                float[] ir;
                string name;
                if (key.Cab == AmpCabs.WaveCab)
                {
                    var clip = waves?.Get(key.Slot);
                    if (clip == null || clip.Left == null || clip.Left.Length < 16) throw new InvalidOperationException("no wave");
                    ir = AmpCabs.FromWave(clip.Left, clip.Right, clip.Rate, key.Rate);
                    name = clip.Name;
                }
                else
                {
                    ir = AmpCabs.Build(key.Cab, key.Rate);
                    name = AmpCabs.Names[key.Cab];
                }
                var built = new CabBuilt
                {
                    Convolver = new StereoConvolver(ir, ir),
                    Ir = ir, Key = key, Name = name, FellBack = requestedFell, Rate = key.Rate,
                };
                Volatile.Write(ref incoming, built);
                Volatile.Write(ref cabState, 3);
            }
            catch (Exception)
            {
                // A cabinet that cannot be made leaves the last one playing (and is asked for again only when a parameter moves).
                Volatile.Write(ref cabState, 0);
                haveWanted = false;
            }
        }

        /// <summary>For tests (one thread): asks for the cabinet the parameters name and has it made and taken, here and now.</summary>
        public void BuildNow()
        {
            for (int guard = 0; guard < 8; guard++)
            {
                WaitForBuilder();
                if (Volatile.Read(ref cabState) == 3) TakeIncoming();
                Poll();
                int s = Volatile.Read(ref cabState);
                if (s == 1) Work();
                else if (s == 0) return;
            }
        }

        /// <summary>Test hook: waits until the builder has finished.</summary>
        public void WaitForBuilder()
        {
            var until = Environment.TickCount64 + 60000;
            while (Environment.TickCount64 < until)
            {
                int s = Volatile.Read(ref cabState);
                if (s == 0 || s == 3) return;
                Thread.Sleep(1);
            }
        }

        void TakeIncoming()
        {
            if (Volatile.Read(ref cabState) != 3) return;
            if (cabFade > 0) return;      // the last change is still fading: this one waits for it
            var next = Volatile.Read(ref incoming);
            Volatile.Write(ref incoming, null);
            if (!cabStale)
            {
                cabOld = cabCur;           // null = the direct sound fades out
                cabFade = fadeLength;
            }
            else { cabOld = null; cabFade = 0; }
            cabCur = next;
            directCur = false;
            currentPublished = next;
            if (status == AmpCabStatus.Preparing && next.Key.Equals(wanted)) status = AmpCabStatus.Ready;
            Volatile.Write(ref cabState, 0);
        }

        /// <summary>The machine did not run for a while (the input was silent for longer than the tail): the states are cleared at the next block.</summary>
        public void Skipped() { cabStale = true; for (int i = 0; i < 2; i++) cores[i].Reset(); }

        // ---- the audio

        /// <summary>One block, in place (at most <see cref="MaxBlock"/> samples).</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            InMeter.Feed(left, right, n);
            fx.Begin(left, right, n);
            Poll();
            TakeIncoming();
            if (cabStale)
            {
                cabCur?.Convolver.Reset();
                cabOld = null; cabFade = 0;
                cabStale = false;
            }

            // Input channel and trim.
            int ch = eff.Choice(P.InputCh);
            float trimTarget = (float)DspMath.DbToGain(eff.Value(P.Trim));
            if (!trimStarted) { trim.Reset(trimTarget); trimStarted = true; } else trim.SetTarget(trimTarget);
            for (int i = 0; i < n; i++)
            {
                float x = ch == 0 ? left[i] : ch == 1 ? right[i] : left[i] + right[i];
                mono[i] = x * trim.Next();
            }

            // Tuner (before the gate).
            bool tunerOn = eff.On(P.Tuner);
            if (tunerOn)
            {
                tuner.Reference = 415 + eff[P.Reference];
                tuner.Process(mono, n);
            }

            RunGate(n);
            RunAmp(n);
            RunCabinet(n);
            RunTone(n);

            // Output level.
            float outTarget = (float)DspMath.DbToGain(eff.Value(P.Output));
            if (!outStarted) { outGain.Reset(outTarget); outStarted = true; } else outGain.SetTarget(outTarget);
            for (int i = 0; i < n; i++)
            {
                float y = mono[i] * outGain.Next();
                left[i] = y;
                right[i] = y;
            }
            fx.End(left, right, n, Latency);

            // The tuner's mute.
            float muteTarget = tunerOn ? 0f : 1f;
            if (muteGain != muteTarget || muteGain != 1f)
            {
                for (int i = 0; i < n; i++)
                {
                    if (muteGain < muteTarget) muteGain = Math.Min(muteTarget, muteGain + muteStep);
                    else if (muteGain > muteTarget) muteGain = Math.Max(muteTarget, muteGain - muteStep);
                    if (muteGain != 1f) { left[i] *= muteGain; right[i] *= muteGain; }
                }
            }
            OutMeter.Feed(left, right, n);
        }

        void RunGate(int n)
        {
            double thr = eff.Value(P.GateThresh);
            bool gateOff = values[P.GateThresh] <= Params.Table[P.GateThresh].Min;
            if (gateOff)
            {
                Array.Copy(mono, gated, n);
                gateOpen = true; gateGain = 1f;
                return;
            }
            float open = (float)DspMath.DbToGain(thr);
            float close = open * 0.5f;      // 6 dB of hysteresis
            double relMs = eff.Value(P.GateRelease);
            // The release is the time to fall 60 dB.
            double rel = PortableMath.Exp(-6.907755278982137 / (relMs * 0.001 * rate));
            for (int i = 0; i < n; i++)
            {
                float a = mono[i] < 0 ? -mono[i] : mono[i];
                gateEnv = a > gateEnv ? a : (float)(gateEnv * envFall);
                if (gateOpen) { if (gateEnv < close) gateOpen = false; }
                else if (gateEnv > open) gateOpen = true;
                if (gateOpen) gateGain += (float)((1f - gateGain) * gateAtt);
                else
                {
                    gateGain = (float)(gateGain * rel);
                    if (gateGain < 1e-6f) gateGain = 0f;
                }
                gated[i] = mono[i] * gateGain;
            }
            if (gateEnv < 1e-20f) gateEnv = 0f;
        }

        void RunAmp(int n)
        {
            int wantVoicing = eff.Choice(P.Voicing);
            int wantFactor = 2 << eff.Choice(P.Quality);   // 2, 4, 8
            if (!coresConfigured)
            {
                cores[active].Configure(wantVoicing, wantFactor, rate);
                powerStarted[0] = powerStarted[1] = false;
                cores[1 - active].Reset();
                fade = 0;
                pads[0].Reset(); pads[1].Reset();
                coresConfigured = true;
            }
            else if (fade == 0 && (cores[active].VoicingIndex != wantVoicing || cores[active].Factor != wantFactor))
            {
                // Start the other core from rest on the new voicing; the playing one fades out under it.
                int spare = 1 - active;
                cores[spare].Configure(wantVoicing, wantFactor, rate);
                powerStarted[spare] = false;
                pads[spare].Reset();
                active = spare;
                fade = fadeLength;
            }

            // The Gain knob's linear gain and the power stage's drive, per sample (both cores read the same).
            double gainDb = eff.Value(P.Gain);
            float gTarget = (float)DspMath.DbToGain(gainDb);
            float master = eff.F(P.Master);
            if (!rampsStarted)
            {
                gainRamp.Reset(gTarget);
                rampsStarted = true;
            }
            else gainRamp.SetTarget(gTarget);
            for (int i = 0; i < n; i++) gainArr[i] = gainRamp.Next();
            // The power drive depends on the voicing; the old core's own range is used for its output (set per core below).
            var s = new AmpSettings
            {
                Bass = eff.F(P.Bass), Mid = eff.F(P.Mid), Treble = eff.F(P.Treble), Sag = eff.F(P.Sag), Presence = eff.F(P.Presence), Resonance = eff.F(P.Resonance),
            };
            double gainNorm = Math.Clamp(gainDb / 40.0, 0, 1);

            FillPower(active, powerArr, n, cores[active].Definition, master);
            cores[active].Process(gated, coreOut, n, s, gainArr, powerArr, gainNorm);
            Pad(active, coreOut, n);
            if (fade > 0)
            {
                int old = 1 - active;
                // The outgoing core, on the same input, with its own power range.
                FillPower(old, powerArr, n, cores[old].Definition, master);
                cores[old].Process(gated, coreOld, n, s, gainArr, powerArr, gainNorm);
                Pad(old, coreOld, n);
                for (int i = 0; i < n; i++)
                {
                    float x = 1f - fade / (float)fadeLength;
                    float up = (float)PortableMath.Sin(0.5 * Math.PI * x), down = (float)PortableMath.Cos(0.5 * Math.PI * x);
                    coreOut[i] = coreOld[i] * down + coreOut[i] * up;
                    if (fade > 0) fade--;
                }
            }
            Array.Copy(coreOut, mono, n);
        }

        void Pad(int core, float[] x, int n)
        {
            int pad = Latency - cores[core].Latency;
            if (pad <= 0) return;
            var pl = pads[core];
            for (int i = 0; i < n; i++)
            {
                pl.Write(x[i]);
                x[i] = pl.Read(pad);
            }
        }

        void FillPower(int core, float[] dst, int n, Voicing v, float master)
        {
            // Master 0 … 1 → the voicing's drive range in dB; each core smooths its own (a fresh core starts at its target).
            double db = v.PowerMinDb + (v.PowerMaxDb - v.PowerMinDb) * master;
            float target = (float)DspMath.DbToGain(db);
            if (!powerStarted[core]) { powerRamps[core].Reset(target); powerStarted[core] = true; } else powerRamps[core].SetTarget(target);
            for (int i = 0; i < n; i++) dst[i] = powerRamps[core].Next();
        }

        readonly SmoothRamp[] powerRamps = new SmoothRamp[2];
        readonly bool[] powerStarted = new bool[2];

        void RunCabinet(int n)
        {
            var cur = cabCur;
            if (cabFade <= 0)
            {
                cabOld = null;
                if (cur == null) return;      // Off: the amp itself
                Array.Copy(mono, wetL, n);
                Array.Copy(mono, wetR, n);
                cur.Convolver.Process(wetL, wetR, n);
                Array.Copy(wetL, mono, n);
                return;
            }
            // A change is fading: the old signal (a cabinet, or the amp direct) falls as cos, the new one rises as sin.
            if (cur != null)
            {
                Array.Copy(mono, wetL, n);
                Array.Copy(mono, wetR, n);
                cur.Convolver.Process(wetL, wetR, n);
            }
            else Array.Copy(mono, wetL, n);
            if (cabOld != null)
            {
                Array.Copy(mono, oldL, n);
                Array.Copy(mono, oldR, n);
                cabOld.Convolver.Process(oldL, oldR, n);
            }
            else Array.Copy(mono, oldL, n);
            for (int i = 0; i < n; i++)
            {
                float x = cabFade > 0 ? 1f - cabFade / (float)fadeLength : 1f;
                float up = (float)PortableMath.Sin(0.5 * Math.PI * x), down = (float)PortableMath.Cos(0.5 * Math.PI * x);
                mono[i] = oldL[i] * down + wetL[i] * up;
                if (cabFade > 0) cabFade--;
            }
            if (cabFade <= 0) cabOld = null;
        }

        /// <summary>Mic position and the two cuts, per 32-sample chunk with their controls smoothed.</summary>
        void RunTone(int n)
        {
            double pos = eff.F(P.MicPos);
            bool lowIsOff = values[P.LowCut] <= Params.Table[P.LowCut].Min;
            bool highIsOff = values[P.HighCut] >= Params.Table[P.HighCut].Max;
            double lowHz = lowIsOff ? 20 : eff.Value(P.LowCut);
            double highHz = highIsOff ? 20000 : eff.Value(P.HighCut);
            bool cabOn = cabCur != null || cabOld != null || cabFade > 0;
            if (!shelvesStarted) { smPos = pos; smLow = lowHz; smHigh = highHz; shelvesStarted = true; }
            for (int c0 = 0; c0 < n; c0 += 32)
            {
                int m = Math.Min(32, n - c0);
                smPos += (pos - smPos) * smoothK;
                smLow *= PortableMath.Pow(lowHz / smLow, smoothK);
                smHigh *= PortableMath.Pow(highHz / smHigh, smoothK);
                if (Math.Abs(pos - smPos) < 1e-6) smPos = pos;
                if (Math.Abs(lowHz / smLow - 1) < 1e-5) smLow = lowHz;
                if (Math.Abs(highHz / smHigh - 1) < 1e-5) smHigh = highHz;
                if (Math.Abs(smPos - micApplied) > 1e-4 || double.IsNaN(micApplied))
                {
                    micApplied = smPos;
                    // Centre: a little brighter; edge: darker.
                    micShelf.Set(BiquadType.HighShelf, 2800, rate, 0.7, 2.5 - 11.0 * smPos);
                    micLow.Set(BiquadType.LowPass, Math.Min(16000, rate * 0.45) * (1.0 - 0.45 * smPos), rate, 0.6);
                }
                if (Math.Abs(smLow - lowApplied) > smLow * 1e-3 || double.IsNaN(lowApplied)) { lowApplied = smLow; lowCut.Set(BiquadType.HighPass, smLow, rate, 0.7071); }
                if (Math.Abs(smHigh - highApplied) > smHigh * 1e-3 || double.IsNaN(highApplied)) { highApplied = smHigh; highCut.Set(BiquadType.LowPass, Math.Min(smHigh, rate * 0.45), rate, 0.7071); }
                for (int i = c0; i < c0 + m; i++)
                {
                    float x = mono[i];
                    if (cabOn) { x = micShelf.Process(x); x = micLow.Process(x); }
                    if (!lowIsOff) x = lowCut.Process(x);
                    if (!highIsOff) x = highCut.Process(x);
                    mono[i] = x;
                }
            }
            micShelf.Flush(); micLow.Flush(); lowCut.Flush(); highCut.Flush();
            if (lowIsOff) lowCut.Reset();
            if (highIsOff) highCut.Reset();
            if (!cabOn) { micShelf.Reset(); micLow.Reset(); }
        }

        /// <summary>The first AmpCore in play (tests read its tone stack and makeup).</summary>
        internal AmpCore ActiveCore => cores[active];
    }

    /// <summary>The one background thread that makes the engines' cabinets.</summary>
    internal static class AmpBuilder
    {
        static readonly object gate = new object();
        static readonly List<WeakReference<AmpEngine>> engines = new List<WeakReference<AmpEngine>>();
        static readonly ManualResetEventSlim signal = new ManualResetEventSlim(false);
        static Thread worker;

        public static void Register(AmpEngine engine)
        {
            lock (gate)
            {
                engines.RemoveAll(w => !w.TryGetTarget(out _));
                engines.Add(new WeakReference<AmpEngine>(engine));
                if (worker == null)
                {
                    var t = new Thread(Run) { IsBackground = true, Name = "Neo Amp builder", Priority = ThreadPriority.BelowNormal };
                    worker = t;
                    t.Start();
                }
            }
        }

        /// <summary>Wakes the builder (any thread, allocation-free).</summary>
        public static void Kick() => signal.Set();

        static void Run()
        {
            var live = new List<AmpEngine>();
            while (true)
            {
                signal.Wait();
                signal.Reset();
                live.Clear();
                lock (gate)
                    foreach (var w in engines)
                        if (w.TryGetTarget(out var e)) live.Add(e);
                foreach (var e in live)
                    if (e.HasWork) e.Work();
            }
        }
    }
}
