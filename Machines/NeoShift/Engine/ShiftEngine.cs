#nullable disable
// Neo Shift's engine (docs/specs/neo-shift.md): one effect with three modes, Mix and Amount from the shared FxStage (machines.md, "Mix and Amount").
//  - Four paths: Pitch Fast (DelayShifter, a window of 10 ... 100 ms, latency half the window), Pitch Smooth (PhaseVocoderShifter on a 2048-point Stft,
//    latency 2047), Frequency (FreqShifterCore) and Ring (RingCore). Only the path in use runs.
//  - The reported latency belongs to the Quality (Fast: half the Window; Smooth: 2047) and holds in every mode: the Frequency and Ring paths are
//    delayed by it, so the song's timing does not move when the Mode is automated. The dry path of Mix is delayed by it too.
//  - A change of path (Mode or Quality) starts the new path on the same input, lets it fill, then crossfades the old path's output into it over 30 ms:
//    no gap, no click.
//  - Pitch, Shift and Carrier (and every value Amount scales) glide: a one-pole per block on the value, a straight line across the block on the
//    quantity the path integrates (the ratio, the rotation, the carrier's increment).
//  - Notes: with Follow notes on, the latest note sets the shift in Pitch mode (note minus Root, scaled by Amount) and the carrier in Ring mode
//    (the note's frequency); with no note yet, the parameters apply.
//  - Wet path: the path, a low cut and a high cut, the Output level; then FxStage (Mix, Amount): at Amount 0 or Mix 0 the output is the input
//    delayed by the latency, bit for bit.
// The audio thread allocates nothing; the editor reads a spectrum snapshot through a triple buffer.

using System;
using System.Collections.Generic;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.Shift
{
    internal sealed class ShiftView
    {
        public const int Bands = 120;
        public readonly float[] In = new float[Bands], Out = new float[Bands];
        public int Mode;
        public void CopyTo(ShiftView o)
        {
            Array.Copy(In, o.In, Bands);
            Array.Copy(Out, o.Out, Bands);
            o.Mode = Mode;
        }
    }

    internal sealed class ShiftEngine
    {
        public const int MaxBlock = 256;
        public const int SmoothLatency = PhaseVocoderShifter.Size - 1;
        const double CrossfadeSeconds = 0.03, GlideSeconds = 0.025;

        /// <summary>What Amount scales (docs/specs/machines.md, "Mix and Amount").</summary>
        public static readonly AmountRule[] Rules =
        {
            AmountRule.Lin(P.Pitch, 0), AmountRule.Lin(P.Fine, 0), AmountRule.Lin(P.Detune, 0),
            AmountRule.Lin(P.Shift, 0), AmountRule.Lin(P.ShiftFine, 0), AmountRule.Lin(P.Feedback, 0), AmountRule.Lin(P.Spread, 0),
            AmountRule.Lin(P.Depth, 0),
        };

        public const int PathFast = 0, PathSmooth = 1, PathFrequency = 2, PathRing = 3;

        readonly ParamValues raw;
        readonly FxStage fx;
        double rate = 48000;

        // the paths
        DelayShifter fastL, fastR;
        Stft stft;
        PhaseVocoderShifter vocoder;
        FreqShifterCore freq;
        RingCore ring;
        FracDelay latL, latR;
        double latDelay = -1;

        readonly float[] inL = new float[MaxBlock], inR = new float[MaxBlock];
        readonly float[] bufL = new float[MaxBlock], bufR = new float[MaxBlock];
        int active = -1, incoming = -1;
        int warm, xfPos, xfLen;

        // smoothed values
        double semiL, semiR, ratioL, ratioR;
        bool primed;
        double lowHz, highHz;
        double lowApplied = -1, highApplied = -1;
        Biquad hpL, hpR, lpL, lpR;
        SmoothRamp outGain;
        double carrierNow = 440;

        // notes
        int held, lastNote = -1;
        readonly bool[] down = new bool[128];

        // outputs for the editor and agents
        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();
        readonly TripleBuffer<ShiftView> view = new TripleBuffer<ShiftView>(() => new ShiftView(), (a, b) => a.CopyTo(b));
        readonly ComplexFft fft = new ComplexFft(2048);
        readonly float[] fRe = new float[2048], fIm = new float[2048], hann = new float[2048];
        readonly float[] ringIn = new float[2048], ringOut = new float[2048];
        int ringPos, sinceView;
        readonly int[] bandOf = new int[1025];
        readonly float[] bandAcc = new float[ShiftView.Bands];
        float outPeak;
        int quiet;
        double lastShiftHz, lastRatio = 1;

        public ShiftEngine(ParamValues values)
        {
            raw = values;
            fx = new FxStage(raw, P.Amount, P.Mix, Rules, maxLatency: 16384, maxBlock: MaxBlock);
            for (int i = 0; i < 2048; i++) hann[i] = (float)(0.5 - 0.5 * PortableMath.Cos(2 * Math.PI * i / 2048));
            Build(48000);
        }

        void Build(double sr)
        {
            rate = sr;
            fastL = new DelayShifter(sr);
            fastR = new DelayShifter(sr);
            stft = new Stft(PhaseVocoderShifter.Size);
            vocoder = new PhaseVocoderShifter(sr);
            freq = new FreqShifterCore(sr);
            ring = new RingCore(sr);
            int maxLat = Math.Max(SmoothLatency, (int)Math.Ceiling(sr * 0.05)) + 16;
            latL = new FracDelay(maxLat);
            latR = new FracDelay(maxLat);
            fx.SetSampleRate(sr);
            outGain.Setup(0.02, sr);
            for (int k = 0; k <= 1024; k++)
            {
                double f = k * sr / 2048;
                double b = PortableMath.Log(Math.Max(f, 30.0) / 30.0) / PortableMath.Log(20000.0 / 30.0) * ShiftView.Bands;
                bandOf[k] = Math.Clamp((int)b, 0, ShiftView.Bands - 1);
            }
            Reset();
        }

        public void SetSampleRate(float sr)
        {
            Build(sr);
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
        }

        public void Reset()
        {
            fastL.Reset(); fastR.Reset();
            stft.Reset(); vocoder.Reset();
            freq.Reset(); ring.Reset();
            latL.Reset(); latR.Reset();
            latDelay = -1;
            hpL.Reset(); hpR.Reset(); lpL.Reset(); lpR.Reset();
            lowApplied = highApplied = -1;
            fx.Reset();
            active = incoming = -1;
            primed = false;
            Array.Clear(ringIn); Array.Clear(ringOut);
            ringPos = sinceView = 0;
            quiet = 0;
            held = 0;
            Array.Clear(down);
            lastNote = -1;
            outGain.Reset(1f);
        }

        // ------------------------------------------------------------------------------------------------ what the machine reports

        /// <summary>Samples from an input to the output now: the Quality's latency (Fast: half the Window; Smooth: 2047), in every mode.</summary>
        public int Latency => LatencyFor(raw.Choice(P.Quality), raw.Value(P.Window));

        public int LatencyFor(int quality, double windowMs) => quality == 1 ? SmoothLatency : DelayShifter.WindowSamples(windowMs, rate) / 2;

        public int Path => active;

        /// <summary>The Pitch ratio now (both channels' mean), the shift in Hz and the carrier in Hz, for the editor's picture.</summary>
        public double Ratio => 0.5 * (ratioL + ratioR);
        public double ShiftHz => lastShiftHz;
        public double CarrierHz => carrierNow;
        public ParamValues Effective => fx.Effective;

        // ------------------------------------------------------------------------------------------------ notes

        public void NoteOn(int key)
        {
            key &= 127;
            if (!down[key]) { down[key] = true; held++; }
            lastNote = key;
        }

        public void NoteOff(int key)
        {
            key &= 127;
            if (down[key]) { down[key] = false; held--; }
        }

        /// <summary>Stop of the song: no key is down and the last note is forgotten (the parameters apply again).</summary>
        public void ClearNotes()
        {
            Array.Clear(down);
            held = 0;
            lastNote = -1;
        }

        public bool AnyNote => held > 0;

        /// <summary>The note the effect follows now, or -1.</summary>
        public int FollowedNote => lastNote;

        public bool ReadView(ShiftView into) => view.Read(into);

        /// <summary>True when the output has been silent for a while and nothing rings on in the paths.</summary>
        public bool Silent => quiet > 8 + (Latency + 4096) / MaxBlock;

        // ------------------------------------------------------------------------------------------------ the block

        static int PathOf(int mode, int quality) => mode == 1 ? PathFrequency : mode == 2 ? PathRing : quality == 1 ? PathSmooth : PathFast;

        public void Process(float[] l, float[] r, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            fx.Begin(l, r, n);
            var v = fx.Effective;
            InMeter.Feed(l, r, n);
            for (int i = 0; i < n; i++) dryMono[i] = 0.5f * (l[i] + r[i]);
            int want = PathOf(raw.Choice(P.Mode), raw.Choice(P.Quality));
            int latency = Latency;
            double amount = fx.Amount;

            // ---- the values of this block
            double baseSemi = v.Value(P.Pitch);
            bool follow = raw.On(P.Follow) && lastNote >= 0;
            if (follow) baseSemi = Math.Clamp((lastNote - raw.Value(P.Root)) * amount, -24, 24);
            double fine = v.Value(P.Fine) / 100.0, det = v.Value(P.Detune) / 100.0;
            double tgtL = baseSemi + fine - det, tgtR = baseSemi + fine + det;
            double k = 1 - PortableMath.Exp(-(n / rate) / GlideSeconds);
            if (!primed)
            {
                semiL = tgtL; semiR = tgtR;
                ratioL = semiL == 0.0 ? 1.0 : PortableMath.Exp2(semiL / 12.0);
                ratioR = semiR == 0.0 ? 1.0 : PortableMath.Exp2(semiR / 12.0);
            }
            double ratioL0 = ratioL, ratioR0 = ratioR;
            semiL = Snap(semiL + (tgtL - semiL) * k, tgtL);
            semiR = Snap(semiR + (tgtR - semiR) * k, tgtR);
            ratioL = semiL == 0.0 ? 1.0 : PortableMath.Exp2(semiL / 12.0);
            ratioR = semiR == 0.0 ? 1.0 : PortableMath.Exp2(semiR / 12.0);
            double window = DelayShifter.WindowSamples(v.Value(P.Window), rate);

            double shift = v.Value(P.Shift) + v.Value(P.ShiftFine);
            double spread = v.Value(P.Spread);
            double shiftL = shift, shiftR = shift * (1 - 2 * spread);
            float feedback = (float)(v.Value(P.Feedback) / 100.0);

            double carrier = v.Value(P.Carrier);
            if (follow) carrier = DspMath.NoteToHz(lastNote);
            int shape = raw.Choice(P.Shape);
            float depth = v.F(P.Depth);
            carrierNow = carrier;
            lastShiftHz = shift;
            lastRatio = 0.5 * (ratioL + ratioR);

            // ---- the path
            if (!primed || active < 0)
            {
                active = want;
                incoming = -1;
                primed = true;
            }
            else if (incoming < 0 && want != active) StartChange(want, latency, window);
            else if (incoming >= 0 && want != incoming)
            {
                if (want == active) incoming = -1;
                else StartChange(want, latency, window);
            }
            if (incoming >= 0)
            {
                Array.Copy(l, inL, n);
                Array.Copy(r, inR, n);
            }
            Run(active, l, r, n, ratioL0, ratioL, ratioR0, ratioR, window, shiftL, shiftR, feedback, carrier, shape, depth, latency);
            if (incoming >= 0)
            {
                Run(incoming, inL, inR, n, ratioL0, ratioL, ratioR0, ratioR, window, shiftL, shiftR, feedback, carrier, shape, depth, latency);
                if (warm > 0) warm -= n;
                else
                {
                    for (int i = 0; i < n; i++)
                    {
                        float c = xfPos >= xfLen ? 1f : (float)xfPos / xfLen;
                        c = 0.5f - 0.5f * (float)PortableMath.Cos(Math.PI * c);   // a raised-cosine ramp keeps both ends smooth
                        l[i] += (inL[i] - l[i]) * c;
                        r[i] += (inR[i] - r[i]) * c;
                        xfPos++;
                    }
                    if (xfPos >= xfLen)
                    {
                        active = incoming;
                        incoming = -1;
                    }
                }
            }

            // ---- the wet path's filters and level
            Filters(v, n);
            outGain.SetTarget((float)DspMath.DbToGain(v.Value(P.Output)));
            for (int i = 0; i < n; i++)
            {
                float g = outGain.Next();
                l[i] = DspMath.Flush(lpL.Process(hpL.Process(l[i])) * g);
                r[i] = DspMath.Flush(lpR.Process(hpR.Process(r[i])) * g);
            }
            hpL.Flush(); hpR.Flush(); lpL.Flush(); lpR.Flush();

            fx.End(l, r, n, latency);

            OutMeter.Feed(l, r, n);
            float pk = 0;
            for (int i = 0; i < n; i++) pk = Math.Max(pk, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
            outPeak = pk;
            quiet = pk < 1e-6f ? quiet + 1 : 0;
            Capture(l, r, n);
        }

        static double Snap(double v, double target) => Math.Abs(v - target) < 1e-9 ? target : v;

        void StartChange(int want, int latency, double window)
        {
            incoming = want;
            ResetPath(want);
            // Its output is real once its own delay has filled.
            warm = Math.Max(latency, (int)window) + 512;
            xfPos = 0;
            xfLen = Math.Max(1, (int)(CrossfadeSeconds * rate));
        }

        void ResetPath(int p)
        {
            switch (p)
            {
                case PathFast: fastL.Reset(); fastR.Reset(); break;
                case PathSmooth: stft.Reset(); vocoder.Reset(); break;
                case PathFrequency: freq.Reset(); latL.Reset(); latR.Reset(); latDelay = -1; break;
                default: ring.Reset(); latL.Reset(); latR.Reset(); latDelay = -1; break;
            }
        }

        /// <summary>One path over a block, in place (the wet sound, before the filters).</summary>
        void Run(int path, float[] l, float[] r, int n, double rl0, double rl1, double rr0, double rr1, double window, double shiftL, double shiftR,
            float feedback, double carrier, int shape, float depth, int latency)
        {
            switch (path)
            {
                case PathFast:
                    fastL.Process(l, n, rl0, rl1, window);
                    fastR.Process(r, n, rr0, rr1, window);
                    break;
                case PathSmooth:
                    vocoder.TargetSemitonesL = semiL;
                    vocoder.TargetSemitonesR = semiR;
                    stft.Process(l, r, 0, n, vocoder);
                    break;
                case PathFrequency:
                    freq.Process(l, r, n, shiftL, shiftR, feedback);
                    Delay(l, r, n, latency);
                    break;
                default:
                    ring.Process(l, r, n, carrier, shape, depth);
                    Delay(l, r, n, latency);
                    break;
            }
        }

        // The Frequency and Ring paths have no delay of their own: they wait the latency the Quality reports. A change of the latency glides.
        void Delay(float[] l, float[] r, int n, int latency)
        {
            if (latDelay < 0) latDelay = latency;
            double step = 0.5;
            for (int i = 0; i < n; i++)
            {
                if (latDelay != latency)
                {
                    latDelay += latDelay < latency ? step : -step;
                    if (Math.Abs(latDelay - latency) <= step) latDelay = latency;
                }
                latL.Write(l[i]);
                latR.Write(r[i]);
                float d = (float)latDelay;
                l[i] = latL.ReadCubic(d);
                r[i] = latR.ReadCubic(d);
            }
        }

        void Filters(ParamValues v, int n)
        {
            double k = 1 - PortableMath.Exp(-(n / rate) / 0.02);
            double tl = v.Value(P.LowCut), th = v.Value(P.HighCut);
            if (lowApplied < 0) { lowHz = tl; highHz = th; }
            else
            {
                lowHz = tl * PortableMath.Pow(lowHz / tl, 1 - k);
                highHz = th * PortableMath.Pow(highHz / th, 1 - k);
                if (Math.Abs(lowHz / tl - 1) < 1e-6) lowHz = tl;
                if (Math.Abs(highHz / th - 1) < 1e-6) highHz = th;
            }
            if (Math.Abs(lowHz - lowApplied) > 1e-9 * lowHz)
            {
                lowApplied = lowHz;
                hpL.Set(BiquadType.HighPass, lowHz, rate, 0.7071);
                hpR.Set(BiquadType.HighPass, lowHz, rate, 0.7071);
            }
            if (Math.Abs(highHz - highApplied) > 1e-9 * highHz)
            {
                highApplied = highHz;
                lpL.Set(BiquadType.LowPass, highHz, rate, 0.7071);
                lpR.Set(BiquadType.LowPass, highHz, rate, 0.7071);
            }
        }

        // ------------------------------------------------------------------------------------------------ the editor's picture

        // The input and the output are collected a block at a time (their mean), and every 1024 samples the last 2048 of each are turned into a
        // spectrum of 120 bands, 30 Hz to 20 kHz, in dB.
        void Capture(float[] l, float[] r, int n)
        {
            for (int i = 0; i < n; i++)
            {
                ringOut[ringPos] = 0.5f * (l[i] + r[i]);
                ringIn[ringPos] = dryMono[i];
                ringPos = (ringPos + 1) & 2047;
            }
            sinceView += n;
            if (sinceView < 1024) return;
            sinceView = 0;
            var vw = view.Back;
            Bands(ringIn, vw.In);
            Bands(ringOut, vw.Out);
            vw.Mode = raw.Choice(P.Mode);
            view.Publish();
        }

        readonly float[] dryMono = new float[MaxBlock];

        void Bands(float[] x, float[] into)
        {
            for (int i = 0; i < 2048; i++)
            {
                fRe[i] = x[(ringPos + i) & 2047] * hann[i];
                fIm[i] = 0f;
            }
            fft.Transform(fRe, fIm, false);
            Array.Clear(bandAcc);
            for (int k = 1; k < 1024; k++)
            {
                float m = fRe[k] * fRe[k] + fIm[k] * fIm[k];
                int b = bandOf[k];
                if (m > bandAcc[b]) bandAcc[b] = m;
            }
            // a full-scale sine reads 0.25 * 2048 through the Hann window
            const float scale = 1f / (0.25f * 2048f);
            for (int b = 0; b < ShiftView.Bands; b++)
            {
                float a = MathF.Sqrt(bandAcc[b]) * scale;
                into[b] = a > 1e-7f ? 20f * PortableMathF.Log10(a) : -140f;
            }
        }

        public IReadOnlyDictionary<string, double> Readings() => new Dictionary<string, double>
        {
            ["latency_samples"] = Latency,
            ["mode"] = raw.Choice(P.Mode),
            ["ratio"] = lastRatio,
            ["shift_hz"] = lastShiftHz,
            ["carrier_hz"] = carrierNow,
            ["output_peak_db"] = DspMath.GainToDb(Volatile.Read(ref outPeak)),
        };
    }
}
