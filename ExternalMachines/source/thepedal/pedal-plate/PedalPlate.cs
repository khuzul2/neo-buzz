// Pedal Plate – ReBuzz managed effect machine
// Dattorro (1997) plate reverb algorithm — efficient, stereo, CPU-friendly.
//
// Build:  dotnet build PedalPlate.csproj -c Release
// Deploy: copy "Pedal Plate.NET.dll"  →  <ReBuzz>\Gear\Effects\

using System;
using Buzz.MachineInterface;

namespace WDE.PedalPlate
{
    [MachineDecl(Name = "Pedal Plate", ShortName = "PedalPlate", Author = "WDE",
                 MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public class PedalPlateMachine : IBuzzMachine
    {
        IBuzzMachineHost host;

        // ── Parameters ────────────────────────────────────────────────────────

        /// <summary>Wet/dry blend. 0 = fully dry, 100 = fully wet.</summary>
        [ParameterDecl(MinValue = 0, MaxValue = 100, DefValue = 50,
                       Description = "Wet/dry mix (0 = dry, 100 = wet)")]
        public int Mix { get; set; }

        /// <summary>Pre-delay before the reverb tank, 0–500 ms.</summary>
        [ParameterDecl(MinValue = 0, MaxValue = 500, DefValue = 0,
                       Description = "Pre-delay (ms)")]
        public int PreDelayMs { get; set; }

        /// <summary>Reverb tail decay. Higher = longer sustain.</summary>
        [ParameterDecl(MinValue = 0, MaxValue = 100, DefValue = 75,
                       Description = "Decay (0 = short, 100 = long)")]
        public int Decay { get; set; }

        /// <summary>High-frequency damping inside the tank. Higher = darker/warmer.</summary>
        [ParameterDecl(MinValue = 0, MaxValue = 100, DefValue = 50,
                       Description = "Damping (0 = bright, 100 = dark)")]
        public int Damping { get; set; }

        /// <summary>Scales all delay-line lengths — effectively changes room size.</summary>
        [ParameterDecl(MinValue = 10, MaxValue = 200, DefValue = 100,
                       Description = "Size (10 = tiny, 100 = normal, 200 = huge)")]
        public int Size { get; set; }

        /// <summary>LFO speed for tank modulation. Higher = more movement/chorus.</summary>
        [ParameterDecl(MinValue = 0, MaxValue = 100, DefValue = 20,
                       Description = "Modulation rate (0 = static, 100 = fast)")]
        public int ModRate { get; set; }

        /// <summary>LFO depth for tank modulation. Higher = lush but can introduce pitch wobble.</summary>
        [ParameterDecl(MinValue = 0, MaxValue = 100, DefValue = 30,
                       Description = "Modulation depth (0 = none, 100 = deep)")]
        public int ModDepth { get; set; }

        /// <summary>High-pass cutoff on the input signal before diffusion. Keeps bass out of the tank.</summary>
        [ParameterDecl(MinValue = 0, MaxValue = 100, DefValue = 10,
                       Description = "Low cut (0 = off, 100 = ~800 Hz high-pass)")]
        public int LowCut { get; set; }

        /// <summary>Feeds wet signal back into the input. Thickens and lengthens the tail; capped just below
        /// where the loop would self-oscillate (see Work).</summary>
        [ParameterDecl(MinValue = 0, MaxValue = 95, DefValue = 0,
                       Description = "Feedback (0 = none, 95 = near-infinite)")]
        public int Feedback { get; set; }

        // ── DSP state ─────────────────────────────────────────────────────────

        // Input diffusion — 4 all-pass stages (Dattorro §4)
        float[] _ap1, _ap2, _ap3, _ap4;
        int     _pp1, _pp2, _pp3, _pp4;

        // Left tank: modulated AP → delay → LP → AP → delay
        float[] _apL1, _dL1, _apL2, _dL2;
        int     _pAL1, _pDL1, _pAL2, _pDL2;
        float   _lpL;

        // Right tank: modulated AP → delay → LP → AP → delay
        float[] _apR1, _dR1, _apR2, _dR2;
        int     _pAR1, _pDR1, _pAR2, _pDR2;
        float   _lpR;

        // Pre-delay ring buffer (stereo summed to mono going in)
        float[] _preBuf;
        int     _preWr;

        // LFO phases [0, 2π) — left and right start in quadrature
        float _lfoL, _lfoR;

        // 1-pole high-pass filter state (one per input channel)
        float _hpL, _hpR;

        // Feedback: mono average of the previous wet output sample
        float _fbSample;

        int _sr;
        int _lastSize = -1;

        // ── Silence / idle detection ──────────────────────────────────────────
        // When the input has been silent for long enough that the reverb tail
        // has also fully drained, Work() returns false so ReBuzz can stop
        // calling us and save CPU.
        //
        // Strategy:
        //   • Each block, measure the peak of the input signal.
        //   • While peak > SILENCE_THRESHOLD, hold _tailCountdown at its max.
        //   • Once peak ≤ SILENCE_THRESHOLD, count down by n each block.
        //   • When the countdown hits zero → tail has decayed → return false.
        //
        // MAX_TAIL_SEC is a generous upper bound: Decay is capped at 0.94 and
        // Feedback below the outer loop's edge (see Work), so the energy in the
        // tank always converges in finite time — 8 seconds outlasts the usual
        // tails. Fork (neo-buzz, docs/specs/mcp-server.md §12.13, U6): the longest
        // (Decay 100: −53 dBFS at 8 s) rang on beyond it and were cut, since ReBuzz
        // passes an effect's input on when Work returns false; now it also waits
        // for the last block's output to be under OUTPUT_SILENT (−120 dBFS).
        // Where it was by 8 s, as at the defaults (−142 dBFS), nothing changes.

        const float SILENCE_THRESHOLD = 1e-5f;   // ~−100 dBFS
        const float MAX_TAIL_SEC      = 8f;       // seconds to wait after input goes silent
        const float OUTPUT_SILENT     = 32768e-6f; // −120 dBFS of Buzz's ±32768 (U6)
        float _lastOutPeak = float.MaxValue;      // the last block's output peak while the input is silent (U6)

        int _tailCountdown;   // samples remaining before we declare silence

        // Fork (neo-buzz, U6): Mix glides to a new setting (Glide, below).
        Glide _gWet;
        bool  _glidesSet;
        int   _glideSr;

        // ── Constructor ───────────────────────────────────────────────────────

        public PedalPlateMachine(IBuzzMachineHost host)
        {
            this.host = host;
        }

        // ── Initialisation ────────────────────────────────────────────────────

        void Init()
        {
            _sr       = host.MasterInfo.SamplesPerSec;
            _lastSize = Size;

            float s = Size / 100f;

            // Dattorro canonical lengths at 29 761 Hz, resampled + scaled.
            // Extra headroom (+32) accommodates maximum LFO modulation depth.
            _ap1 = Alloc(142, s); _pp1 = 0;
            _ap2 = Alloc(107, s); _pp2 = 0;
            _ap3 = Alloc(379, s); _pp3 = 0;
            _ap4 = Alloc(277, s); _pp4 = 0;

            _apL1 = Alloc(672,  s); _pAL1 = 0;
            _dL1  = Alloc(4453, s); _pDL1 = 0;
            _apL2 = Alloc(1800, s); _pAL2 = 0;
            _dL2  = Alloc(3720, s); _pDL2 = 0;

            _apR1 = Alloc(908,  s); _pAR1 = 0;
            _dR1  = Alloc(4217, s); _pDR1 = 0;
            _apR2 = Alloc(2656, s); _pAR2 = 0;
            _dR2  = Alloc(3163, s); _pDR2 = 0;

            _lpL = _lpR = 0f;

            _preBuf = new float[_sr / 2 + 4];   // max 500 ms
            _preWr  = 0;

            _lfoL = 0f;
            _lfoR = MathF.PI * 0.5f;             // 90° apart to avoid comb artefacts

            _hpL = _hpR = 0f;
            _fbSample = 0f;

            _tailCountdown = (int)(MAX_TAIL_SEC * _sr);
        }

        float[] Alloc(int dattorroLen, float sizeScale)
        {
            int n = Math.Max(4, (int)(dattorroLen * _sr / 29761f * sizeScale) + 32);
            return new float[n];
        }

        // ── DSP helpers ───────────────────────────────────────────────────────

        // Fork (neo-buzz, docs/specs/mcp-server.md §12.13, U6): every value a loop writes back (the
        // all-passes' stores, the damping and low-cut states) settles to exact zero below Tiny,
        // 1e-15 of full scale (−300 dBFS; samples here are ±32768). After the input stopped, the
        // low cut's state and then the diffusers decayed into denormal floats within a second and
        // stayed there, which cost up to 18 ms of work a second against 1.6 while playing, until
        // the silence countdown (MAX_TAIL_SEC) stopped the machine. Only a sound already under about −160 dBFS
        // can bring a state that low, so what is heard is unchanged; the tank now empties to zero.
        const float Tiny = 32768e-15f;

        // Standard Schroeder all-pass
        static float AP(float[] buf, ref int pos, float x, float g)
        {
            float d = buf[pos];
            float v = x - g * d;
            if (MathF.Abs(v) < Tiny) v = 0f;
            buf[pos] = v;
            pos = pos + 1 < buf.Length ? pos + 1 : 0;
            return d + g * v;
        }

        // Modulated all-pass (Dattorro §4): a Schroeder all-pass whose delay the
        // LFO moves, len - 1 - modOffset samples (modOffset 0..16; Alloc leaves
        // 32 spare). The feedback and the output use the same modulated tap, so
        // it stays an all-pass.
        // Fork (neo-buzz): it read 1-17 samples behind the write position, and
        // fed back the unmodulated tap: a comb with a gain up to 5.7 inside the
        // tank's loop, which diverged at the default Decay.
        static float ModAP(float[] buf, ref int pos, float x, float g, float modOffset)
        {
            int len = buf.Length;

            // The sample written k samples ago is at pos - k; buf[pos] is the oldest (k = len).
            float delay = len - 1f - modOffset;
            int   whole = (int)delay;
            float frac  = delay - whole;
            int   r0    = pos - whole;  if (r0 < 0) r0 += len;
            int   r1    = r0 - 1;       if (r1 < 0) r1 += len;   // one sample older
            float d = buf[r0] * (1f - frac) + buf[r1] * frac;

            float v = x - g * d;
            if (MathF.Abs(v) < Tiny) v = 0f;
            buf[pos] = v;
            pos = pos + 1 < len ? pos + 1 : 0;
            return d + g * v;
        }

        // Simple delay line
        static float Del(float[] buf, ref int pos, float x)
        {
            float d = buf[pos];
            buf[pos] = x;
            pos = pos + 1 < buf.Length ? pos + 1 : 0;
            return d;
        }

        // ── Work ──────────────────────────────────────────────────────────────

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            if (mode == WorkModes.WM_NOIO) return false;

            if (_preBuf == null || _sr != host.MasterInfo.SamplesPerSec || _lastSize != Size)
                Init();

            // ── Silence / idle gate ───────────────────────────────────────────
            // Measure input block peak (cheap: no per-sample branch in hot loop)
            float inputPeak = 0f;
            for (int i = 0; i < n; i++)
            {
                float v = MathF.Max(MathF.Abs(input[i].L), MathF.Abs(input[i].R));
                if (v > inputPeak) inputPeak = v;
            }

            if (inputPeak > SILENCE_THRESHOLD)
            {
                // Active signal — keep the tail window fully open
                _tailCountdown = (int)(MAX_TAIL_SEC * _sr);
            }
            else
            {
                // Silent input — drain the countdown; the tank is still ringing
                _tailCountdown -= n;
                if (_tailCountdown <= 0)
                {
                    // Tank has fully decayed: nothing left to output
                    _tailCountdown = 0;
                    if (_lastOutPeak < OUTPUT_SILENT) return false;
                }
            }

            // ── Block-rate parameter conversion ───────────────────────────────
            float wet  = Mix      / 100f;
            float dry  = 1f - wet;
            // Fork (neo-buzz, U6): glide to Mix; the first time, and at a new rate, start there.
            if (!_glidesSet || _sr != _glideSr) { _gWet.Snap(wet); _glidesSet = true; _glideSr = _sr; }
            else _gWet.Set(wet, (int)(Glide.Ms * 0.001f * _sr));
            bool gliding = _gWet.Left > 0;
            float dec  = Decay    / 100f * 0.94f;   // keep < 1 for stability
            float damp = Damping  / 100f * 0.88f;
            float dmp1 = 1f - damp;
            float fb   = Feedback / 100f;

            // Fork (neo-buzz, docs/specs/machine-safety.md §1): the feedback loop runs through the
            // tank, whose gain from its input to the feedback tap is at most
            // 0.35·(1 + 3·dec)/(1 − dec²) at any frequency (each half's delays and all-passes have
            // gain 1, the damping low-pass at most 1, and the four taps add up in the worst phase).
            // Above 1/that the loop can grow, and does: at the default Decay from Feedback ≈ 50 with
            // Damping 0, 55 at the default Damping, 65 with the default modulation too (slowly: the
            // profiler's 4 s render saw only 79 and up); at full Decay from about 10. So the feedback
            // stays under 98 % of 1/that, and the whole loop's gain under 0.98 at every frequency, for
            // every setting. Below the cap nothing changes, bit for bit (every Feedback up to 79 at a
            // Decay up to 55; up to 45 at the default, 75).
            float fbMax = 0.98f * (1f - dec * dec) / (0.35f * (1f + 3f * dec));
            if (fb > fbMax) fb = fbMax;

            int preDelaySamples = Math.Clamp(
                (int)(PreDelayMs * _sr / 1000f), 0, _preBuf.Length - 1);

            // Modulation: rate 0 → 5 Hz, depth 0 → 16 samples
            float lfoInc   = ModRate  / 100f * 5f * MathF.Tau / _sr;
            float lfoDepth = ModDepth / 100f * 16f;

            // High-pass: LowCut 0 → 100 maps to 20 → 800 Hz (exponential)
            float hpFc    = MathF.Exp(LowCut / 100f * MathF.Log(800f / 20f)) * 20f;
            float hpAlpha = 1f - MathF.Exp(-MathF.Tau * hpFc / _sr);

            // Dattorro coefficients
            const float gIn1 = 0.75f;
            const float gIn2 = 0.625f;
            const float gTk1 = 0.70f;
            const float gTk2 = 0.50f;
            const float kOut = 0.35f;

            for (int i = 0; i < n; i++)
            {
                if (gliding)
                {
                    wet = _gWet.Next();
                    dry = 1f - wet;
                }

                // ── LFO advance ───────────────────────────────────────────────
                _lfoL += lfoInc; if (_lfoL >= MathF.Tau) _lfoL -= MathF.Tau;
                _lfoR += lfoInc; if (_lfoR >= MathF.Tau) _lfoR -= MathF.Tau;
                float modL = (MathF.Sin(_lfoL) * 0.5f + 0.5f) * lfoDepth;
                float modR = (MathF.Sin(_lfoR) * 0.5f + 0.5f) * lfoDepth;

                // ── High-pass filter on stereo inputs ─────────────────────────
                _hpL += hpAlpha * (input[i].L - _hpL);
                _hpR += hpAlpha * (input[i].R - _hpR);
                if (MathF.Abs(_hpL) < Tiny) _hpL = 0f;
                if (MathF.Abs(_hpR) < Tiny) _hpR = 0f;
                float filtL = input[i].L - _hpL;
                float filtR = input[i].R - _hpR;

                // ── Mono sum + feedback injection ─────────────────────────────
                float inM = (filtL + filtR) * 0.5f + _fbSample * fb;

                // ── Pre-delay ─────────────────────────────────────────────────
                _preBuf[_preWr] = inM;
                int rd = _preWr - preDelaySamples;
                if (rd < 0) rd += _preBuf.Length;
                float sig = _preBuf[rd];
                _preWr = _preWr + 1 < _preBuf.Length ? _preWr + 1 : 0;

                // ── Input diffusion (4 all-pass stages) ───────────────────────
                sig = AP(_ap1, ref _pp1, sig, gIn1);
                sig = AP(_ap2, ref _pp2, sig, gIn1);
                sig = AP(_ap3, ref _pp3, sig, gIn2);
                sig = AP(_ap4, ref _pp4, sig, gIn2);

                // ── Cross-feed taps (read before advancing) ───────────────────
                float xfeedR = _dR2[_pDR2];
                float xfeedL = _dL2[_pDL2];

                // ── Left tank ─────────────────────────────────────────────────
                float aL1 = ModAP(_apL1, ref _pAL1, sig + dec * xfeedR, gTk1, modL);
                float dL1 = Del  (_dL1,  ref _pDL1, aL1);
                _lpL       = dL1 * dmp1 + _lpL * damp;
                if (MathF.Abs(_lpL) < Tiny) _lpL = 0f;
                float aL2 = AP   (_apL2, ref _pAL2, _lpL * dec, gTk2);
                float dL2 = Del  (_dL2,  ref _pDL2, aL2);

                // ── Right tank ────────────────────────────────────────────────
                float aR1 = ModAP(_apR1, ref _pAR1, sig + dec * xfeedL, gTk1, modR);
                float dR1 = Del  (_dR1,  ref _pDR1, aR1);
                _lpR       = dR1 * dmp1 + _lpR * damp;
                if (MathF.Abs(_lpR) < Tiny) _lpR = 0f;
                float aR2 = AP   (_apR2, ref _pAR2, _lpR * dec, gTk2);
                float dR2 = Del  (_dR2,  ref _pDR2, aR2);

                // ── Output taps (Dattorro stereo spread) ──────────────────────
                float outL = (dL1 + dL2 - aR2 + dR2) * kOut;
                float outR = (dR1 + dR2 - aL2 + dL2) * kOut;

                // ── Capture feedback for next sample ──────────────────────────
                _fbSample = (outL + outR) * 0.5f;

                output[i].L = dry * input[i].L + wet * outL;
                output[i].R = dry * input[i].R + wet * outR;
            }

            // Fork (neo-buzz, U6): what the countdown above waits for; read only while the input is silent.
            _lastOutPeak = float.MaxValue;
            if (inputPeak <= SILENCE_THRESHOLD)
            {
                float peak = 0f;
                for (int i = 0; i < n; i++)
                    peak = MathF.Max(peak, MathF.Max(MathF.Abs(output[i].L), MathF.Abs(output[i].R)));
                _lastOutPeak = peak;
            }
            return true;
        }
    }

    // Fork (neo-buzz, docs/specs/mcp-server.md §12.13, U6): a setting that glides to a new value in a straight line over
    // Ms milliseconds, sample by sample, and lands on it exactly, so a setting that doesn't move is the same number as
    // before. Automation and pattern events change parameters once a tick or subtick; applied once a block they stepped
    // (3 dB at a time on a quarter-second ramp of 24 dB), which clicks.
    struct Glide
    {
        public const float Ms = 20f;
        public float Value, Target, Step;
        public int Left;

        public void Snap(float v) { Value = Target = v; Left = 0; }

        public void Set(float v, int samples)
        {
            if (v == Target) return;
            Target = v;
            if (samples <= 1) { Snap(v); return; }
            Left = samples;
            Step = (v - Value) / samples;
        }

        public float Next()
        {
            if (Left > 0) Value = --Left == 0 ? Target : Value + Step;
            return Value;
        }
    }
}
