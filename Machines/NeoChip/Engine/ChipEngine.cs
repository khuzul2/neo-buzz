#nullable disable
// Neo Chip DSP engine (docs/specs/neo-chip.md). Pure C#, no Buzz or UI references, bit-identical
// across x64 and arm64 (PortableMath/PortableMathF only), zero heap allocation per block,
// denormal-safe. Three band-limited/raw pulse channels and one 15-bit/7-bit LFSR noise channel,
// 16-step (4-bit) ADSR and hardware-style envelopes, per-track sample-accurate arpeggio, pitch
// sweep, vibrato, and optional 4-bit per-channel DAC quantisation mixed linearly.

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.Chip
{
    internal enum ChipEnvMode { Adsr, SawDown, SawUp, Triangle, DecayShot, AttackHold }
    internal enum ChipArpMode { Off, Up, Down, UpDown, AsWritten }
    internal enum ChipVoiceMode { Poly, Mono, Unison }

    /// <summary>
    /// Maximal-length linear-feedback shift register for the noise channel.
    /// Long mode (15-bit, x^15 + x^14 + 1): exact period 32767 clock steps.
    /// Short mode (7-bit, x^7 + x^6 + 1): exact period 127 clock steps (metallic).
    /// </summary>
    internal struct ChipNoiseLfsr
    {
        public const int LongPeriod = 32767;
        public const int ShortPeriod = 127;

        uint state;

        public void Reset(uint seed = 1u) => state = seed == 0u ? 1u : seed;

        public uint State => state;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Step(bool shortMode)
        {
            uint mask = shortMode ? 0x7Fu : 0x7FFFu;
            uint s = state & mask;
            if (s == 0u) s = 1u;
            uint bit = (s ^ (s >> 1)) & 1u;
            s = (s >> 1) | (bit << (shortMode ? 6 : 14));
            state = s;
            return (int)(s & 1u);
        }
    }

    /// <summary>
    /// Pulse oscillator in [-1, +1] with 48-tap windowed-sinc BLEP anti-aliasing (BlepTable.Shared)
    /// and a phase-aligned naive raw path. Tracks high/low state sample-by-sample so moving pulse
    /// width under PWM or duty automation always emits a matching +-2 BLEP step.
    /// </summary>
    internal sealed class ChipPulseOsc
    {
        const int DelayLen = 32;
        const int DelayMask = DelayLen - 1;

        double phase;
        bool high = true;
        BlepBuffer blep;
        readonly float[] rawDelay = new float[DelayLen];
        int rawPos;

        public ChipPulseOsc()
        {
            blep.Init();
        }

        public void Reset()
        {
            phase = 0.0;
            high = true;
            blep.Reset();
            Array.Clear(rawDelay, 0, DelayLen);
            rawPos = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Next(double inc, double pw, float rawMix)
        {
            if (inc < 0.0) inc = 0.0;
            else if (inc > 0.49) inc = 0.49;
            if (pw < 0.04) pw = 0.04;
            else if (pw > 0.96) pw = 0.96;

            // If moving pulse width crossed the current phase between samples, emit the step now.
            bool startHigh = phase < pw;
            if (startHigh != high)
            {
                blep.AddStep(startHigh ? 2f : -2f, 1.0f);
                high = startHigh;
            }

            if (inc > 0.0)
            {
                double p = phase;
                double next = high ? pw : 1.0;
                double end = p + inc;
                if (end < next)
                {
                    phase = end;
                }
                else
                {
                    double t = 0.0;
                    int guard = 0;
                    while (guard++ < 16)
                    {
                        next = high ? pw : 1.0;
                        double dt = (next - p) / inc;
                        if (t + dt > 1.0)
                        {
                            p += (1.0 - t) * inc;
                            break;
                        }
                        t += dt;
                        float ago = (float)DspMath.Clamp(1.0 - t, 0.0, 1.0);
                        if (high)
                        {
                            blep.AddStep(-2f, ago);
                            high = false;
                            p = pw;
                        }
                        else
                        {
                            blep.AddStep(2f, ago);
                            high = true;
                            p = 0.0;
                        }
                    }
                    if (p >= 1.0) p -= Math.Floor(p);
                    phase = p;
                }
            }

            float naive = high ? 1f : -1f;
            float blepOut = blep.Next(naive);
            rawDelay[rawPos] = naive;
            float rawOut = rawDelay[(rawPos - BlepTable.Z) & DelayMask];
            rawPos = (rawPos + 1) & DelayMask;
            return rawMix <= 0f ? blepOut : rawMix >= 1f ? rawOut : blepOut + (rawOut - blepOut) * rawMix;
        }
    }

    /// <summary>
    /// 4-bit (16-level) stepped envelope supporting ADSR and five hardware-style shapes
    /// (repeating saw down, saw up, triangle; one-shot decay, attack-hold). Output is strictly
    /// step / 15f for step in 0..15.
    /// </summary>
    internal struct ChipEnvelope
    {
        public enum EnvState { Idle, Attack, Decay, Sustain, Release, Hardware }

        EnvState state;
        ChipEnvMode hwMode;
        double level;       // 0..1 continuous accumulator for ADSR stages
        double relStart;    // level when release started
        double hwPhase;     // 0..1 hardware-envelope phase
        int currentStep;    // 0..15

        public bool IsActive => state != EnvState.Idle;
        public EnvState State => state;
        public int CurrentStep => currentStep;

        public void Reset()
        {
            state = EnvState.Idle;
            level = 0.0;
            relStart = 0.0;
            hwPhase = 0.0;
            currentStep = 0;
        }

        public void NoteOn(ChipEnvMode mode)
        {
            hwMode = mode;
            if (mode == ChipEnvMode.Adsr)
            {
                state = EnvState.Attack;
                level = 0.0;
                currentStep = 0;
            }
            else
            {
                state = EnvState.Hardware;
                hwPhase = 0.0;
                currentStep = mode is ChipEnvMode.SawDown or ChipEnvMode.DecayShot ? 15 : 0;
                level = currentStep / 15.0;
            }
        }

        public void NoteOff()
        {
            if (state == EnvState.Idle || state == EnvState.Release) return;
            relStart = currentStep / 15.0;
            level = relStart;
            if (relStart <= 0.0)
            {
                state = EnvState.Idle;
                currentStep = 0;
            }
            else
            {
                state = EnvState.Release;
            }
        }

        public void Kill()
        {
            Reset();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Next(ChipEnvMode mode, double attackSec, double decaySec, int sustainStep, double releaseSec, double periodSec, double sampleRate)
        {
            switch (state)
            {
                case EnvState.Idle:
                    currentStep = 0;
                    return 0f;

                case EnvState.Attack:
                {
                    double samples = Math.Max(1.0, attackSec * sampleRate);
                    level += 1.0 / samples;
                    if (level >= 1.0)
                    {
                        level = 1.0;
                        currentStep = 15;
                        state = EnvState.Decay;
                    }
                    else
                    {
                        currentStep = Math.Clamp((int)Math.Floor(level * 15.0), 0, 15);
                    }
                    return currentStep * (1f / 15f);
                }

                case EnvState.Decay:
                {
                    int sus = Math.Clamp(sustainStep, 0, 15);
                    double susLevel = sus / 15.0;
                    if (sus >= 15)
                    {
                        level = 1.0;
                        currentStep = 15;
                        state = EnvState.Sustain;
                        return 1f;
                    }
                    double samples = Math.Max(1.0, decaySec * sampleRate);
                    level -= (1.0 - susLevel) / samples;
                    if (level <= susLevel)
                    {
                        level = susLevel;
                        currentStep = sus;
                        state = sus == 0 ? EnvState.Idle : EnvState.Sustain;
                    }
                    else
                    {
                        currentStep = Math.Clamp((int)Math.Ceiling(level * 15.0), sus, 15);
                    }
                    return currentStep * (1f / 15f);
                }

                case EnvState.Sustain:
                {
                    int sus = Math.Clamp(sustainStep, 0, 15);
                    currentStep = sus;
                    level = sus / 15.0;
                    if (sus == 0) state = EnvState.Idle;
                    return currentStep * (1f / 15f);
                }

                case EnvState.Release:
                {
                    double samples = Math.Max(1.0, releaseSec * sampleRate);
                    level -= Math.Max(1e-6, relStart) / samples;
                    if (level <= 0.0)
                    {
                        level = 0.0;
                        currentStep = 0;
                        state = EnvState.Idle;
                    }
                    else
                    {
                        currentStep = Math.Clamp((int)Math.Ceiling(level * 15.0), 0, 15);
                    }
                    return currentStep * (1f / 15f);
                }

                case EnvState.Hardware:
                {
                    ChipEnvMode activeMode = mode != ChipEnvMode.Adsr ? mode : hwMode;
                    double periodSamples = Math.Max(16.0, periodSec * sampleRate);
                    double p = hwPhase;
                    switch (activeMode)
                    {
                        case ChipEnvMode.SawDown:
                            currentStep = 15 - Math.Min(15, (int)(p * 16.0));
                            hwPhase += 1.0 / periodSamples;
                            if (hwPhase >= 1.0) hwPhase -= Math.Floor(hwPhase);
                            break;
                        case ChipEnvMode.SawUp:
                            currentStep = Math.Min(15, (int)(p * 16.0));
                            hwPhase += 1.0 / periodSamples;
                            if (hwPhase >= 1.0) hwPhase -= Math.Floor(hwPhase);
                            break;
                        case ChipEnvMode.Triangle:
                        {
                            int idx = Math.Min(31, (int)(p * 32.0));
                            currentStep = idx < 16 ? idx : 31 - idx;
                            hwPhase += 1.0 / periodSamples;
                            if (hwPhase >= 1.0) hwPhase -= Math.Floor(hwPhase);
                            break;
                        }
                        case ChipEnvMode.DecayShot:
                            if (p >= 1.0)
                            {
                                currentStep = 0;
                                state = EnvState.Idle;
                            }
                            else
                            {
                                currentStep = 15 - Math.Min(15, (int)(p * 16.0));
                                hwPhase += 1.0 / periodSamples;
                            }
                            break;
                        case ChipEnvMode.AttackHold:
                            if (p >= 1.0)
                            {
                                currentStep = 15;
                            }
                            else
                            {
                                currentStep = Math.Min(15, (int)(p * 16.0));
                                hwPhase += 1.0 / periodSamples;
                                if (hwPhase > 1.0) hwPhase = 1.0;
                            }
                            break;
                        default:
                            currentStep = 0;
                            state = EnvState.Idle;
                            break;
                    }
                    level = currentStep / 15.0;
                    return currentStep * (1f / 15f);
                }
            }
            return 0f;
        }
    }

    /// <summary>
    /// Neo Chip sound engine: 3 pulse channels (indices 0..2) and 1 noise channel (index 3).
    /// Implements IChannelNoteSink so tracker tracks 1..4 map directly to channels 0..3 while MIDI /
    /// piano-roll notes route through VoiceMode (Poly/Mono/Unison) and NoiseMidiCh.
    /// </summary>
    internal sealed class ChipEngine : IChannelNoteSink
    {
        public const int MaxBlock = 256;
        public const int PulseCount = 3;
        public const int ChannelCount = 4;

        /// <summary>
        /// Per-channel linear mix headroom (0.20f) combined with cos/sin equal-power pan (0.7071 at
        /// center, 1.0 at hard pan), guaranteeing a 3-note pulse chord + noise hit at velocity 127,
        /// Volume 0 dB and Lo-res DAC on peaks at or below 0 dBFS across all duty settings.
        /// </summary>
        public const float ChannelScale = 0.20f;

        static readonly double[] DutyValues = { 0.125, 0.25, 0.50, 0.75 };
        static readonly float[] DefaultSpreadOffsets = { -0.70f, 0.70f, -0.35f, 0.25f };
        static readonly float[] UnisonSpreadOffsets = { -1.0f, 0.0f, 1.0f, 0.0f };

        sealed class ChannelState
        {
            public readonly bool IsNoise;
            public readonly ChipPulseOsc Pulse;
            public ChipNoiseLfsr Lfsr;
            public double NoisePhase;
            public float NoiseHold = 1f;
            public ChipEnvelope Env;
            public StealFade Steal;

            // Pending note waiting for steal fade completion.
            public bool Pending;
            public int PendingNote;
            public float PendingVel;
            public ChipEnvMode PendingEnvMode;

            // Active voice ownership & tracking.
            public int OwnerKey = -1;
            public bool Held;
            public bool Sustained;
            public long NoteSeq;
            public int BaseNote = 60;
            public float Velocity = 1f;

            // Per-channel PWM LFO, arpeggio, pitch sweep, and vibrato.
            public double PwmPhase;
            public long ArpSamples;
            public int LastArpOffset;
            public double SweepSemi;
            public double VibElapsed;
            public double VibPhase;
            public float VibFade;

            // Per-channel smoothers.
            public Smoother DutySmooth;
            public SmoothRamp PanL;
            public SmoothRamp PanR;
            public Smoother DetuneSmooth;

            public ChannelState(bool isNoise, uint seed)
            {
                IsNoise = isNoise;
                if (!isNoise) Pulse = new ChipPulseOsc();
                Lfsr.Reset(seed);
            }

            public bool IsSounding => Env.IsActive || Pending;

            public void StartImmediate(int note, float vel, ChipEnvMode envMode)
            {
                BaseNote = note;
                Velocity = vel;
                Pending = false;
                Steal.Cancel();
                ArpSamples = 0;
                LastArpOffset = 0;
                SweepSemi = 0.0;
                VibElapsed = 0.0;
                VibPhase = 0.0;
                VibFade = 0f;
                Env.NoteOn(envMode);
            }

            public void Trigger(int note, float vel, ChipEnvMode envMode, float sampleRate, bool stealFade)
            {
                if (stealFade && Env.IsActive)
                {
                    Pending = true;
                    PendingNote = note;
                    PendingVel = vel;
                    PendingEnvMode = envMode;
                    Steal.Start(sampleRate, 0.002f);
                }
                else
                {
                    StartImmediate(note, vel, envMode);
                }
            }

            public void Release()
            {
                Pending = false;
                Steal.Cancel();
                Env.NoteOff();
            }

            public void HardKill()
            {
                Pending = false;
                Held = false;
                Sustained = false;
                OwnerKey = -1;
                Steal.Cancel();
                Env.Kill();
            }
        }

        // Fixed-size mono/unison/noise MIDI note stack (zero allocation).
        struct NoteStack
        {
            const int Cap = 32;
            int[] keysField;
            int[] notesField;
            float[] velsField;
            public int Count;

            public void Init()
            {
                // Allocated once in engine constructor.
                keysField = new int[Cap];
                notesField = new int[Cap];
                velsField = new float[Cap];
                Count = 0;
            }

            public void Clear() => Count = 0;

            public void Push(int key, int note, float vel)
            {
                Remove(key);
                if (Count < Cap)
                {
                    keysField[Count] = key;
                    notesField[Count] = note;
                    velsField[Count] = vel;
                    Count++;
                }
            }

            public bool Remove(int key, out bool wasTop)
            {
                wasTop = Count > 0 && keysField[Count - 1] == key;
                bool found = false;
                for (int i = 0; i < Count; i++)
                {
                    if (keysField[i] != key) continue;
                    found = true;
                    for (int j = i; j < Count - 1; j++)
                    {
                        keysField[j] = keysField[j + 1];
                        notesField[j] = notesField[j + 1];
                        velsField[j] = velsField[j + 1];
                    }
                    Count--;
                    i--;
                }
                return found;
            }

            public void Remove(int key) => Remove(key, out _);

            public (int Key, int Note, float Vel) Top => (keysField[Count - 1], notesField[Count - 1], velsField[Count - 1]);
        }

        readonly ParamValues values;
        readonly ChannelState[] channels = new ChannelState[ChannelCount];
        NoteStack pulseStack;
        NoteStack noiseStack;

        double sampleRate = 48000.0;
        bool initialized;
        int rrCursor;
        long noteSeq;
        bool sustainDown;
        float pitchBendSemi;
        ChipVoiceMode lastVoiceMode = ChipVoiceMode.Poly;

        // Global smoothers.
        SmoothRamp volumeRamp;
        SmoothRamp rawMixRamp;
        SmoothRamp dacMixRamp;
        Smoother noisePitchSmooth;
        Smoother velDepthSmooth;
        Smoother sweepSmooth;
        Smoother vibDepthSmooth;

        // Final stereo ~5 Hz DC blocker (flushed with DspMath.Flush).
        double dcR;
        double dcPrevXL, dcPrevYL, dcPrevXR, dcPrevYR;

        public ChipEngine(ParamValues values)
        {
            this.values = values;
            for (int c = 0; c < ChannelCount; c++)
                channels[c] = new ChannelState(c == 3, (uint)(1 + c));
            pulseStack.Init();
            noiseStack.Init();
            SetSampleRate(48000f);
        }

        public double Tempo { get; set; } = 120.0;
        public readonly LevelMeter OutMeter = new LevelMeter();

        // Diagnostics for headless unit tests.
        internal int ActivePulseMask
        {
            get
            {
                int mask = 0;
                for (int i = 0; i < PulseCount; i++)
                    if (channels[i].IsSounding) mask |= 1 << i;
                return mask;
            }
        }

        internal int HeldPulseMask
        {
            get
            {
                int mask = 0;
                for (int i = 0; i < PulseCount; i++)
                    if (channels[i].Held) mask |= 1 << i;
                return mask;
            }
        }

        internal bool NoiseActive => channels[3].IsSounding;
        internal int ChannelNote(int ch) => channels[ch].BaseNote;
        internal int ChannelOwnerKey(int ch) => channels[ch].OwnerKey;
        internal int ChannelEnvStep(int ch) => channels[ch].Env.CurrentStep;
        internal int ChannelArpOffset(int ch) => channels[ch].LastArpOffset;

        public void SetSampleRate(float sr)
        {
            sampleRate = sr > 0f ? sr : 48000.0;
            OutMeter.SetSampleRate((float)sampleRate);
            dcR = PortableMath.Exp(-DspMath.TwoPiD * 5.0 / sampleRate);
            volumeRamp.Setup(0.010, sampleRate);
            rawMixRamp.Setup(0.010, sampleRate);
            dacMixRamp.Setup(0.010, sampleRate);
            noisePitchSmooth.Setup(0.010, sampleRate);
            velDepthSmooth.Setup(0.010, sampleRate);
            sweepSmooth.Setup(0.010, sampleRate);
            vibDepthSmooth.Setup(0.010, sampleRate);
            for (int c = 0; c < ChannelCount; c++)
            {
                channels[c].DutySmooth.Setup(0.010, sampleRate);
                channels[c].PanL.Setup(0.010, sampleRate);
                channels[c].PanR.Setup(0.010, sampleRate);
                channels[c].DetuneSmooth.Setup(0.010, sampleRate);
            }
            initialized = false;
        }

        ChipEnvMode CurrentEnvMode => (ChipEnvMode)Math.Clamp(values.Choice(P.EnvMode), 0, 5);
        ChipVoiceMode CurrentVoiceMode => (ChipVoiceMode)Math.Clamp(values.Choice(P.VoiceMode), 0, 2);

        // ------------------------------------------------------------------ INoteSink / IChannelNoteSink

        /// <summary>Tracker tracks 0..2 drive pulse channels 0..2; track 3 drives the noise channel.</summary>
        public void NoteOn(int key, int note, float velocity)
        {
            if (key >= 0 && key < PulseCount)
            {
                var ch = channels[key];
                ch.OwnerKey = key;
                ch.Held = true;
                ch.Sustained = false;
                ch.NoteSeq = ++noteSeq;
                ch.Trigger(note, velocity, CurrentEnvMode, (float)sampleRate, stealFade: ch.IsSounding);
                return;
            }
            if (key == 3)
            {
                var ch = channels[3];
                ch.OwnerKey = key;
                ch.Held = true;
                ch.Sustained = false;
                ch.NoteSeq = ++noteSeq;
                ch.Trigger(note, velocity, CurrentEnvMode, (float)sampleRate, stealFade: ch.IsSounding);
                return;
            }
            // Fallback for callers invoking INoteSink with MIDI keys (>= 1000): channel 0.
            NoteOn(key, note, velocity, 0);
        }

        public void NoteOff(int key)
        {
            if (key >= 0 && key < ChannelCount)
            {
                var ch = channels[key];
                if (ch.OwnerKey == key && ch.Held)
                {
                    if (sustainDown) ch.Sustained = true;
                    else { ch.Held = false; ch.Release(); }
                }
                return;
            }
            NoteOff(key, 0, 0);
        }

        public void NoteOn(int key, int note, float velocity, int channel)
        {
            int noiseChSetting = values.Choice(P.NoiseMidiCh); // 0 = All, 1..16 = MIDI ch 1..16
            bool toNoise = noiseChSetting == 0 || (channel + 1) == noiseChSetting;
            if (toNoise)
            {
                noiseStack.Push(key, note, velocity);
                var nch = channels[3];
                nch.OwnerKey = key;
                nch.Held = true;
                nch.Sustained = false;
                nch.NoteSeq = ++noteSeq;
                nch.Trigger(note, velocity, CurrentEnvMode, (float)sampleRate, stealFade: nch.IsSounding);
                return;
            }

            ChipVoiceMode vm = CurrentVoiceMode;
            CheckVoiceModeChange(vm);
            ChipEnvMode envMode = CurrentEnvMode;

            if (vm == ChipVoiceMode.Poly)
            {
                // Release any channel already owned by this exact MIDI key.
                for (int i = 0; i < PulseCount; i++)
                {
                    if (channels[i].OwnerKey == key && channels[i].Held)
                    {
                        channels[i].Held = false;
                        channels[i].Sustained = false;
                        channels[i].Release();
                    }
                }
                int pick = PickPolyChannel(out bool stolen);
                var ch = channels[pick];
                ch.OwnerKey = key;
                ch.Held = true;
                ch.Sustained = false;
                ch.NoteSeq = ++noteSeq;
                ch.Trigger(note, velocity, envMode, (float)sampleRate, stealFade: stolen || ch.IsSounding);
                rrCursor = (pick + 1) % PulseCount;
            }
            else if (vm == ChipVoiceMode.Mono)
            {
                pulseStack.Push(key, note, velocity);
                var ch = channels[0];
                ch.OwnerKey = key;
                ch.Held = true;
                ch.Sustained = false;
                ch.NoteSeq = ++noteSeq;
                ch.Trigger(note, velocity, envMode, (float)sampleRate, stealFade: ch.IsSounding);
            }
            else // Unison
            {
                pulseStack.Push(key, note, velocity);
                long seq = ++noteSeq;
                for (int i = 0; i < PulseCount; i++)
                {
                    var ch = channels[i];
                    ch.OwnerKey = key;
                    ch.Held = true;
                    ch.Sustained = false;
                    ch.NoteSeq = seq;
                    ch.Trigger(note, velocity, envMode, (float)sampleRate, stealFade: ch.IsSounding);
                }
            }
        }

        public void NoteOff(int key, int note, int channel)
        {
            // Check noise stack first (handles MIDI channel changes cleanly while notes are held).
            if (noiseStack.Remove(key, out bool noiseWasTop))
            {
                if (noiseWasTop)
                {
                    var nch = channels[3];
                    if (noiseStack.Count > 0 && nch.IsSounding)
                    {
                        var top = noiseStack.Top;
                        nch.OwnerKey = top.Key;
                        nch.Held = true;
                        nch.Sustained = false;
                        nch.NoteSeq = ++noteSeq;
                        nch.Trigger(top.Note, top.Vel, CurrentEnvMode, (float)sampleRate, stealFade: true);
                    }
                    else if (noiseStack.Count == 0 && nch.Held)
                    {
                        if (sustainDown) nch.Sustained = true;
                        else { nch.Held = false; nch.Release(); }
                    }
                    else if (noiseStack.Count > 0)
                    {
                        nch.OwnerKey = noiseStack.Top.Key;
                    }
                }
                return;
            }

            ChipVoiceMode vm = CurrentVoiceMode;
            if (vm == ChipVoiceMode.Poly)
            {
                for (int i = 0; i < PulseCount; i++)
                {
                    var ch = channels[i];
                    if (ch.OwnerKey != key || !ch.Held) continue;
                    if (sustainDown) ch.Sustained = true;
                    else { ch.Held = false; ch.Release(); }
                }
            }
            else
            {
                if (!pulseStack.Remove(key, out bool wasTop) || !wasTop) return;
                int count = vm == ChipVoiceMode.Unison ? PulseCount : 1;
                bool anySounding = false;
                for (int i = 0; i < count; i++) if (channels[i].IsSounding) anySounding = true;
                if (pulseStack.Count > 0 && anySounding)
                {
                    var top = pulseStack.Top;
                    long seq = ++noteSeq;
                    ChipEnvMode envMode = CurrentEnvMode;
                    for (int i = 0; i < count; i++)
                    {
                        var ch = channels[i];
                        ch.OwnerKey = top.Key;
                        ch.Held = true;
                        ch.Sustained = false;
                        ch.NoteSeq = seq;
                        ch.Trigger(top.Note, top.Vel, envMode, (float)sampleRate, stealFade: ch.IsSounding);
                    }
                }
                else if (pulseStack.Count > 0)
                {
                    var top = pulseStack.Top;
                    for (int i = 0; i < count; i++) channels[i].OwnerKey = top.Key;
                }
                else
                {
                    for (int i = 0; i < PulseCount; i++)
                    {
                        var ch = channels[i];
                        if (!ch.Held) continue;
                        if (sustainDown) ch.Sustained = true;
                        else { ch.Held = false; ch.Release(); }
                    }
                }
            }
        }

        public bool Control(int channel, int ctrl, int value) => false;

        public void ModWheel(float value) { }

        public void PitchBend(float value) => pitchBendSemi = DspMath.Clamp(value, -1f, 1f) * 2f;

        public void Sustain(bool down)
        {
            if (sustainDown == down) return;
            sustainDown = down;
            if (down) return;
            for (int c = 0; c < ChannelCount; c++)
            {
                var ch = channels[c];
                if (!ch.Sustained) continue;
                ch.Sustained = false;
                if (ch.Held)
                {
                    ch.Held = false;
                    ch.Release();
                }
            }
        }

        public void Aftertouch(float value) { }

        public void AllNotesOff(bool hard)
        {
            pulseStack.Clear();
            noiseStack.Clear();
            sustainDown = false;
            for (int c = 0; c < ChannelCount; c++)
            {
                if (hard) channels[c].HardKill();
                else
                {
                    channels[c].Sustained = false;
                    if (channels[c].Held) { channels[c].Held = false; channels[c].Release(); }
                    else if (channels[c].Pending) channels[c].Release();
                }
            }
        }

        void CheckVoiceModeChange(ChipVoiceMode vm)
        {
            if (vm == lastVoiceMode) return;
            lastVoiceMode = vm;
            pulseStack.Clear();
            for (int i = 0; i < PulseCount; i++)
            {
                channels[i].Sustained = false;
                if (channels[i].Held) { channels[i].Held = false; channels[i].Release(); }
            }
        }

        int PickPolyChannel(out bool stolen)
        {
            // 1. Free (inactive and not held) channel starting from rrCursor.
            for (int k = 0; k < PulseCount; k++)
            {
                int i = (rrCursor + k) % PulseCount;
                if (!channels[i].IsSounding && !channels[i].Held)
                {
                    stolen = false;
                    return i;
                }
            }
            // 2. Released (not held, in release tail) channel: prefer oldest.
            int bestRel = -1;
            long bestRelSeq = long.MaxValue;
            for (int k = 0; k < PulseCount; k++)
            {
                int i = (rrCursor + k) % PulseCount;
                if (channels[i].Held) continue;
                if (channels[i].NoteSeq < bestRelSeq)
                {
                    bestRelSeq = channels[i].NoteSeq;
                    bestRel = i;
                }
            }
            if (bestRel >= 0)
            {
                stolen = true;
                return bestRel;
            }
            // 3. All 3 held: steal the oldest held note (smallest NoteSeq).
            int oldest = 0;
            long oldestSeq = channels[0].NoteSeq;
            for (int i = 1; i < PulseCount; i++)
            {
                if (channels[i].NoteSeq < oldestSeq)
                {
                    oldestSeq = channels[i].NoteSeq;
                    oldest = i;
                }
            }
            stolen = true;
            return oldest;
        }

        // ------------------------------------------------------------------ Tempo sync helpers

        /// <summary>Hardware envelope sync division duration in seconds.</summary>
        public static double EnvSyncSeconds(int syncChoice, double bpm)
        {
            double beat = 60.0 / Math.Clamp(bpm, 20.0, 400.0);
            return syncChoice switch
            {
                1 => beat * 0.0625,          // 1/64
                2 => beat / 12.0,            // 1/32T
                3 => beat * 0.125,           // 1/32
                4 => beat / 6.0,             // 1/16T
                5 => beat * 0.25,            // 1/16
                6 => beat / 3.0,             // 1/8T
                7 => beat * 0.5,             // 1/8
                8 => beat * (2.0 / 3.0),     // 1/4T
                9 => beat,                   // 1/4
                10 => beat * 2.0,            // 1/2
                11 => beat * 4.0,            // 1/1
                _ => 0.2,
            };
        }

        /// <summary>Arpeggio sync division duration in seconds (1/16 to 1/64, straight or triplet).</summary>
        public static double ArpSyncSeconds(int syncChoice, double bpm)
        {
            double beat = 60.0 / Math.Clamp(bpm, 20.0, 400.0);
            return syncChoice switch
            {
                1 => beat * 0.25,            // 1/16
                2 => beat / 6.0,             // 1/16T
                3 => beat * 0.125,           // 1/32
                4 => beat / 12.0,            // 1/32T
                5 => beat * 0.0625,          // 1/64
                6 => beat / 24.0,            // 1/64T
                _ => 1.0 / 12.0,
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int ArpOffsetFor(ChipArpMode mode, int steps, int s0, int s1, int s2, long stepIdx)
        {
            if (mode == ChipArpMode.Off) return 0;
            if (steps <= 1) return s0;

            if (mode == ChipArpMode.AsWritten)
            {
                int idx = (int)(stepIdx % steps);
                return idx == 0 ? s0 : idx == 1 ? s1 : s2;
            }

            // Sort the active steps (2 or 3 integers, zero allocation).
            int a = s0, b = s1, c = s2;
            if (steps == 2)
            {
                if (a > b) { int t = a; a = b; b = t; }
                int idx = (int)(stepIdx & 1L);
                if (mode == ChipArpMode.Down) return idx == 0 ? b : a;
                return idx == 0 ? a : b; // Up and UpDown both alternate [a, b]
            }

            // 3-element sorting network.
            if (a > b) { int t = a; a = b; b = t; }
            if (b > c) { int t = b; b = c; c = t; }
            if (a > b) { int t = a; a = b; b = t; }

            if (mode == ChipArpMode.Up)
            {
                int idx = (int)(stepIdx % 3L);
                return idx == 0 ? a : idx == 1 ? b : c;
            }
            if (mode == ChipArpMode.Down)
            {
                int idx = (int)(stepIdx % 3L);
                return idx == 0 ? c : idx == 1 ? b : a;
            }
            // UpDown: [a, b, c, b] (no repeated endpoints).
            int u = (int)(stepIdx & 3L);
            return u == 0 ? a : u == 2 ? c : b;
        }

        // ------------------------------------------------------------------ Audio processing

        public bool Process(float[] left, float[] right, int n)
        {
            ChipVoiceMode vm = CurrentVoiceMode;
            CheckVoiceModeChange(vm);

            // Read parameters once per block and update smoother targets.
            double volDb = values.Value(P.Volume);
            float volTarget = double.IsNegativeInfinity(volDb) ? 0f : (float)DspMath.DbToGain(volDb);
            float rawTarget = values.On(P.Raw) ? 1f : 0f;
            float dacTarget = values.On(P.LoResDac) ? 1f : 0f;
            float spread = (float)values.Value(P.Spread);
            float velDepthTarget = (float)values.Value(P.VelDepth);
            float detuneCents = (float)values.Value(P.Detune);
            float noisePitchTarget = (float)values.Value(P.NoisePitch);
            bool noiseShort = values.Choice(P.NoiseMode) == 1;

            ChipEnvMode envMode = CurrentEnvMode;
            double envAttack = values.Value(P.EnvAttack);
            double envDecay = values.Value(P.EnvDecay);
            int envSustain = (int)values.Value(P.EnvSustain);
            double envRelease = values.Value(P.EnvRelease);
            int envSync = values.Choice(P.EnvSync);
            double envPeriod = envSync > 0 ? EnvSyncSeconds(envSync, Tempo) : values.Value(P.EnvPeriod);

            ChipArpMode arpMode = (ChipArpMode)Math.Clamp(values.Choice(P.ArpMode), 0, 4);
            int arpSteps = Math.Clamp((int)values.Value(P.ArpSteps), 1, 3);
            int arpS1 = (int)values.Value(P.ArpStep1);
            int arpS2 = (int)values.Value(P.ArpStep2);
            int arpS3 = (int)values.Value(P.ArpStep3);
            int arpSync = values.Choice(P.ArpSync);
            double arpStepSamples = Math.Max(1.0, (arpSync > 0 ? ArpSyncSeconds(arpSync, Tempo) : 1.0 / Math.Max(0.5, values.Value(P.ArpRate))) * sampleRate);

            float sweepTarget = (float)values.Value(P.Sweep);
            float vibDepthTarget = (float)values.Value(P.VibDepth);
            double vibRate = values.Value(P.VibRate);
            double vibDelaySec = values.Value(P.VibDelay) * 0.001;

            // Per-channel targets: base duty, PWM, pan (combined with stereo spread).
            var spreadTable = vm == ChipVoiceMode.Unison ? UnisonSpreadOffsets : DefaultSpreadOffsets;
            double p0Base = DutyValues[Math.Clamp(values.Choice(P.P1Duty), 0, 3)];
            double p1Base = DutyValues[Math.Clamp(values.Choice(P.P2Duty), 0, 3)];
            double p2Base = DutyValues[Math.Clamp(values.Choice(P.P3Duty), 0, 3)];
            float p0Pwm = (float)values.Value(P.P1Pwm);
            float p1Pwm = (float)values.Value(P.P2Pwm);
            float p2Pwm = (float)values.Value(P.P3Pwm);
            double p0PwmRate = values.Value(P.P1PwmRate);
            double p1PwmRate = values.Value(P.P2PwmRate);
            double p2PwmRate = values.Value(P.P3PwmRate);

            volumeRamp.SetTarget(volTarget);
            rawMixRamp.SetTarget(rawTarget);
            dacMixRamp.SetTarget(dacTarget);
            noisePitchSmooth.Target = noisePitchTarget;
            velDepthSmooth.Target = velDepthTarget;
            sweepSmooth.Target = sweepTarget;
            vibDepthSmooth.Target = vibDepthTarget;

            for (int c = 0; c < ChannelCount; c++)
            {
                float chPan = c switch
                {
                    0 => (float)values.Value(P.P1Pan),
                    1 => (float)values.Value(P.P2Pan),
                    2 => (float)values.Value(P.P3Pan),
                    _ => (float)values.Value(P.NoisePan),
                };
                float pan = DspMath.Clamp(chPan + spread * spreadTable[c], -1f, 1f);
                float angle = (pan + 1f) * 0.25f * DspMath.Pi;
                float gL = PortableMathF.Cos(angle);
                float gR = PortableMathF.Sin(angle);
                channels[c].PanL.SetTarget(gL);
                channels[c].PanR.SetTarget(gR);

                float det = vm == ChipVoiceMode.Unison ? (c == 0 ? -detuneCents : c == 2 ? detuneCents : 0f) : 0f;
                channels[c].DetuneSmooth.Target = det;
                if (c < PulseCount)
                {
                    double baseDuty = c == 0 ? p0Base : c == 1 ? p1Base : p2Base;
                    channels[c].DutySmooth.Target = (float)baseDuty;
                }
            }

            if (!initialized)
            {
                initialized = true;
                volumeRamp.Reset(volTarget);
                rawMixRamp.Reset(rawTarget);
                dacMixRamp.Reset(dacTarget);
                noisePitchSmooth.Reset(noisePitchTarget);
                velDepthSmooth.Reset(velDepthTarget);
                sweepSmooth.Reset(sweepTarget);
                vibDepthSmooth.Reset(vibDepthTarget);
                for (int c = 0; c < ChannelCount; c++)
                {
                    channels[c].PanL.Reset(channels[c].PanL.Target);
                    channels[c].PanR.Reset(channels[c].PanR.Target);
                    channels[c].DetuneSmooth.Reset(channels[c].DetuneSmooth.Target);
                    if (c < PulseCount) channels[c].DutySmooth.Reset(channels[c].DutySmooth.Target);
                }
            }

            // Early idle check: if no channel is active and DC blocker has settled, output zeros fast.
            bool anyActive = false;
            for (int c = 0; c < ChannelCount; c++)
                if (channels[c].IsSounding) { anyActive = true; break; }

            if (!anyActive && Math.Abs(dcPrevYL) < 1e-7 && Math.Abs(dcPrevYR) < 1e-7)
            {
                dcPrevXL = dcPrevYL = dcPrevXR = dcPrevYR = 0.0;
                Array.Clear(left, 0, n);
                Array.Clear(right, 0, n);
                // Keep smoothers tracking parameter automation while idle.
                for (int i = 0; i < n; i++)
                {
                    volumeRamp.Next();
                    rawMixRamp.Next();
                    dacMixRamp.Next();
                }
                noisePitchSmooth.Advance(n);
                velDepthSmooth.Advance(n);
                sweepSmooth.Advance(n);
                vibDepthSmooth.Advance(n);
                for (int c = 0; c < ChannelCount; c++)
                {
                    channels[c].DutySmooth.Advance(n);
                    channels[c].DetuneSmooth.Advance(n);
                    for (int i = 0; i < n; i++) { channels[c].PanL.Next(); channels[c].PanR.Next(); }
                }
                OutMeter.Feed(left, right, n);
                return false;
            }

            double invSr = 1.0 / sampleRate;
            float vibFadeStep = (float)(invSr / 0.020);
            bool sound = false;

            for (int i = 0; i < n; i++)
            {
                float vol = volumeRamp.Next();
                float rawMix = rawMixRamp.Next();
                float dacMix = dacMixRamp.Next();
                float baseNoisePitch = noisePitchSmooth.Next();
                float velDepth = velDepthSmooth.Next();
                float sweepRate = sweepSmooth.Next();
                float vibDepth = vibDepthSmooth.Next();

                float sumL = 0f, sumR = 0f;

                for (int c = 0; c < ChannelCount; c++)
                {
                    var ch = channels[c];
                    float gL = ch.PanL.Next();
                    float gR = ch.PanR.Next();
                    float detCents = ch.DetuneSmooth.Next();
                    float baseDuty = c < PulseCount ? ch.DutySmooth.Next() : 0.5f;

                    if (!ch.IsSounding) continue;

                    // Steal fade-out before starting a stolen note.
                    float stealGain = 1f;
                    if (ch.Steal.Fading)
                    {
                        if (!ch.Steal.Next(out stealGain))
                        {
                            if (ch.Pending)
                                ch.StartImmediate(ch.PendingNote, ch.PendingVel, ch.PendingEnvMode);
                        }
                    }

                    // 16-step envelope.
                    float envGain = ch.Env.Next(envMode, envAttack, envDecay, envSustain, envRelease, envPeriod, sampleRate);
                    if (!ch.Env.IsActive && !ch.Pending) continue;

                    // Per-track sample-accurate arpeggio.
                    int arpOff = 0;
                    if (arpMode != ChipArpMode.Off)
                    {
                        long stepIdx = (long)Math.Floor(ch.ArpSamples / arpStepSamples);
                        arpOff = ArpOffsetFor(arpMode, arpSteps, arpS1, arpS2, arpS3, stepIdx);
                    }
                    ch.ArpSamples++;
                    ch.LastArpOffset = arpOff;

                    // Pitch sweep (semitones per second, clamped to safe range).
                    if (sweepRate != 0f)
                        ch.SweepSemi = DspMath.Clamp(ch.SweepSemi + sweepRate * invSr, -48.0, 48.0);

                    // Vibrato (delay in ms, smooth 20 ms fade-in).
                    double vibSemi = 0.0;
                    ch.VibElapsed += invSr;
                    if (vibDepth > 0f && ch.VibElapsed >= vibDelaySec)
                    {
                        if (ch.VibFade < 1f) ch.VibFade = Math.Min(1f, ch.VibFade + vibFadeStep);
                        ch.VibPhase += vibRate * invSr;
                        if (ch.VibPhase >= 1.0) ch.VibPhase -= Math.Floor(ch.VibPhase);
                        vibSemi = ch.VibFade * vibDepth * 0.01 * PortableMathF.Sin((float)(DspMath.TwoPiD * ch.VibPhase));
                    }

                    double modSemi = arpOff + ch.SweepSemi + vibSemi + pitchBendSemi + detCents * 0.01;
                    float oscOut;

                    if (!ch.IsNoise)
                    {
                        double pwmDepth = c == 0 ? p0Pwm : c == 1 ? p1Pwm : p2Pwm;
                        double pwmRate = c == 0 ? p0PwmRate : c == 1 ? p1PwmRate : p2PwmRate;
                        double pw = baseDuty;
                        if (pwmDepth > 0.0)
                        {
                            ch.PwmPhase += pwmRate * invSr;
                            if (ch.PwmPhase >= 1.0) ch.PwmPhase -= Math.Floor(ch.PwmPhase);
                            pw = DspMath.Clamp(baseDuty + 0.375 * pwmDepth * PortableMathF.Sin((float)(DspMath.TwoPiD * ch.PwmPhase)), 0.05, 0.95);
                        }
                        double midi = DspMath.Clamp(ch.BaseNote + modSemi, 0.0, 127.0);
                        double freq = DspMath.NoteToHz(midi);
                        oscOut = ch.Pulse.Next(freq * invSr, pw, rawMix);
                    }
                    else
                    {
                        double clockHz = DspMath.Clamp(baseNoisePitch * PortableMath.Pow(2.0, (ch.BaseNote - 69.0 + modSemi) / 12.0), 20.0, 8.0 * sampleRate);
                        ch.NoisePhase += clockHz * invSr;
                        if (ch.NoisePhase >= 1.0)
                        {
                            float stepSum = 0f;
                            int steps = 0;
                            while (ch.NoisePhase >= 1.0 && steps < 16)
                            {
                                ch.NoisePhase -= 1.0;
                                int bit = ch.Lfsr.Step(noiseShort);
                                ch.NoiseHold = bit != 0 ? 1f : -1f;
                                stepSum += ch.NoiseHold;
                                steps++;
                            }
                            if (ch.NoisePhase >= 1.0) ch.NoisePhase -= Math.Floor(ch.NoisePhase);
                            oscOut = stepSum / steps;
                        }
                        else
                        {
                            oscOut = ch.NoiseHold;
                        }
                    }

                    float velGain = (1f - velDepth) + velDepth * ch.Velocity;
                    float chSample = oscOut * envGain * velGain * stealGain;

                    // Optional 4-bit per-channel DAC quantisation (exact 0 at silence), mixed linearly.
                    if (dacMix > 0f)
                    {
                        float q = MathF.Round(DspMath.Clamp(chSample, -1f, 1f) * 7f) * (1f / 7f);
                        chSample = dacMix >= 1f ? q : chSample + (q - chSample) * dacMix;
                    }

                    float scaled = chSample * ChannelScale;
                    sumL += scaled * gL;
                    sumR += scaled * gR;
                }

                // Master volume and gentle ~5 Hz stereo DC blocker (after mix / Lo-res DAC).
                double xL = sumL * vol;
                double xR = sumR * vol;
                double yL = DspMath.Flush(xL - dcPrevXL + dcR * dcPrevYL);
                double yR = DspMath.Flush(xR - dcPrevXR + dcR * dcPrevYR);
                dcPrevXL = xL;
                dcPrevYL = yL;
                dcPrevXR = xR;
                dcPrevYR = yR;

                float fL = (float)yL;
                float fR = (float)yR;
                left[i] = fL;
                right[i] = fR;

                if (MathF.Abs(fL) > 1e-6f || MathF.Abs(fR) > 1e-6f) sound = true;
            }

            // Flush smoother states against denormals.
            noisePitchSmooth.Value = DspMath.Flush(noisePitchSmooth.Value);
            velDepthSmooth.Value = DspMath.Flush(velDepthSmooth.Value);
            sweepSmooth.Value = DspMath.Flush(sweepSmooth.Value);
            vibDepthSmooth.Value = DspMath.Flush(vibDepthSmooth.Value);
            for (int c = 0; c < ChannelCount; c++)
            {
                channels[c].DutySmooth.Value = DspMath.Flush(channels[c].DutySmooth.Value);
                channels[c].DetuneSmooth.Value = DspMath.Flush(channels[c].DetuneSmooth.Value);
            }

            OutMeter.Feed(left, right, n);
            return sound || anyActive;
        }
    }
}
