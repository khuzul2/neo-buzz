#nullable disable
// Neo Organ (docs/specs/neo-organ.md): the stages after the tone generator. A smoother that lands exactly on its target, the
// scanner vibrato and chorus (a modulated delay line), a tube-style overdrive with level compensation, and the rotary speaker
// (a horn and a drum rotor, each with its own speeds and inertia, two virtual microphones, amplitude and Doppler modulation).
// Pure DSP, allocation-free after construction, the portable maths (no libm).

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.Organ
{
    /// <summary>A one-pole smoother that lands exactly on its target (so a settled gain is exact and nothing decays into denormals).</summary>
    internal struct Sm
    {
        public float V, T, A;

        public void Setup(double seconds, double sampleRate) => A = DspMath.OnePoleCoeff(seconds, sampleRate);

        public void Reset(float v) { V = T = v; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Next()
        {
            float d = T - V;
            if (d > -1e-6f && d < 1e-6f) { V = T; return V; }
            V += d * A;
            return V;
        }

        /// <summary>The value after <paramref name="n"/> samples of smoothing at once (control rate).</summary>
        public float Advance(int n)
        {
            float d = T - V;
            if (d > -1e-6f && d < 1e-6f) { V = T; return V; }
            V += d * (1f - PortableMathF.Pow(1f - A, n));
            return V;
        }

        public bool Moving => V != T;
    }

    /// <summary>
    /// The scanner vibrato and chorus: a delay line whose length a triangle sweeps at 6.8 Hz. The three depths are 0.27, 0.54 and 1.08 ms
    /// peak to peak, which moves the pitch by about ±6, ±12 and ±24 cents (2 · depth · rate). V: the delayed sound alone. C: half the
    /// delayed sound and half the dry sound.
    /// </summary>
    internal sealed class Scanner
    {
        public const float RateHz = 6.8f;
        public static readonly float[] DepthMs = { 0.27f, 0.54f, 1.08f };
        public const float BaseDelay = 2f;           // samples; keeps the cubic read inside the line

        FracDelay line;
        double phase;
        double inc;
        Sm depth, wet;
        float sr = 48000;

        public Scanner() { SetSampleRate(48000); }

        public void SetSampleRate(float rate)
        {
            sr = rate;
            line = new FracDelay((int)(0.002 * rate) + 16);
            inc = RateHz / rate;
            depth.Setup(0.02, rate);
            wet.Setup(0.02, rate);
        }

        public void Reset()
        {
            line.Reset();
            phase = 0;
            depth.Reset(depth.T);
            wet.Reset(wet.T);
        }

        /// <summary>On or off, and which of the six settings (0 … 2 are V1 … V3, 3 … 5 are C1 … C3).</summary>
        public void Set(bool on, int mode)
        {
            mode = Math.Clamp(mode, 0, 5);
            depth.T = DepthMs[mode % 3] * 0.001f * sr;
            wet.T = !on ? 0f : mode < 3 ? 1f : 0.5f;
        }

        /// <summary>Whether the stage changes the sound (or is still fading in or out).</summary>
        public bool Engaged => wet.T > 0 || wet.V > 0;

        public void Snap() { depth.V = depth.T; wet.V = wet.T; }

        public void Advance(int n) { phase += inc * n; phase -= Math.Floor(phase); }

        public void Process(float[] x, int n)
        {
            for (int i = 0; i < n; i++)
            {
                float dry = x[i];
                line.Write(dry);
                phase += inc;
                if (phase >= 1) phase -= 1;
                float tri = (float)(phase < 0.5 ? 2 * phase : 2 - 2 * phase);       // 0 … 1 … 0
                float w = wet.Next();
                float d = BaseDelay + depth.Next() * tri;
                x[i] = w == 0f ? dry : dry + (line.ReadCubic(d) - dry) * w;
            }
        }
    }

    /// <summary>
    /// A tube-style overdrive: a tanh shaper with a little bias (even harmonics), run at twice the sample rate, with its gain
    /// compensated so that a sine at <see cref="Reference"/> keeps its peak level at every setting. At 0 the pre-gain is 0.1 and
    /// the shaper is linear to better than 0.001 %; the gain grows by a decade per third of the knob to 100.
    /// </summary>
    internal sealed class TubeDrive
    {
        public const float Reference = 0.2f;

        readonly FastOversampler os = new FastOversampler(OrganEngine.MaxBlock);
        DcBlocker dc;
        Sm amount;
        float g0 = 0.1f, b0, n0 = 1f, tb0;
        bool primed;

        public TubeDrive() { os.SetFactor(2); SetSampleRate(48000); }

        public void SetSampleRate(float rate)
        {
            amount.Setup(0.03, rate);
            dc.Set(rate, 12);
            Reset();
        }

        public void Reset()
        {
            os.Reset();
            dc.Reset();
            Shape(amount.V, out g0, out b0, out n0, out tb0);
        }

        public void Set(float d) { amount.T = d; }

        public void Snap() { amount.V = amount.T; }

        /// <summary>Pre-gain, bias and the compensating gain for a setting 0 … 1.</summary>
        internal static void Shape(float d, out float g, out float b, out float norm, out float tanhB)
        {
            g = 0.1f * PortableMathF.Pow(10f, 3f * d);
            b = 0.3f * d;
            tanhB = DspMath.FastTanh(b);
            float peak = 0.5f * (DspMath.FastTanh(g * Reference + b) - DspMath.FastTanh(-g * Reference + b));
            norm = Reference / Math.Max(peak, 1e-6f);
        }

        public void Process(float[] x, int n)
        {
            amount.Advance(n);
            Shape(amount.V, out float g1, out float b1, out float n1, out float tb1);
            float[] hi = os.Up(x, 0, n);
            int m = 2 * n;
            float inv = 1f / m;
            for (int j = 0; j < m; j++)
            {
                float t = (j + 1) * inv;
                float g = g0 + (g1 - g0) * t, b = b0 + (b1 - b0) * t, norm = n0 + (n1 - n0) * t, tb = tb0 + (tb1 - tb0) * t;
                hi[j] = (DspMath.FastTanh(g * hi[j] + b) - tb) * norm;
            }
            os.Down(x, 0, n);
            for (int i = 0; i < n; i++) x[i] = dc.Process(x[i]);
            g0 = g1; b0 = b1; n0 = n1; tb0 = tb1;
        }
    }

    /// <summary>One rotor: its angle in turns and its speed in turns per second.</summary>
    internal struct Rotor
    {
        public double Angle, Omega;

        /// <summary>
        /// Moves the rotor <paramref name="dt"/> seconds towards <paramref name="target"/> (turns per second) with a first-order lag of
        /// time constant <paramref name="tau"/>. A negative target is a brake: the rotor reaches rest in finite time and holds still.
        /// Returns the turns it travelled.
        /// </summary>
        public double Advance(double target, double tau, double dt)
        {
            if (target < 0 && Omega <= 0) { Omega = 0; return 0; }
            double a = PortableMath.Exp(-dt / tau);
            double w1 = target + (Omega - target) * a;
            double turned;
            if (target < 0 && w1 <= 0)
            {
                double a0 = -target / (Omega - target);                       // e^(-t0 / tau) when the speed crosses zero
                double t0 = -tau * PortableMath.Log(a0);
                turned = target * t0 + (Omega - target) * tau * (1 - a0);
                w1 = 0;
            }
            else turned = target * dt + (Omega - target) * tau * (1 - a);
            Omega = w1;
            Angle += turned;
            Angle -= Math.Floor(Angle);
            return turned;
        }
    }

    /// <summary>
    /// The rotary speaker. A crossover at 800 Hz (Linkwitz-Riley, 24 dB/oct) sends the highs to the horn and the lows to the drum.
    /// Each rotor turns at its own speed with its own inertia (see <see cref="Rotor"/>). Two virtual microphones stand at ±spread · 90°
    /// from the rotor's axis of start; each hears the rotor's directivity ((1+cos)/2 for the drum, its square for the horn, mixed with
    /// the depth) and the distance to the moving source (a delay of r · cos / c, the Doppler effect). The two microphones are the stereo
    /// pair. The level is compensated so the average power equals the dry sound's at any depth.
    /// </summary>
    internal sealed class RotarySpeaker
    {
        public const int Chunk = 16;
        const double Crossover = 800;
        const double HornRadius = 0.18, DrumRadius = 0.09, SpeedOfSound = 343;

        readonly Biquad[] low = new Biquad[2], high = new Biquad[2];
        FracDelay hornLine, drumLine;
        float sr = 48000;
        Rotor horn, drum;
        float baseDelay, hornAmp, drumAmp;
        Sm mix, depth, dop, spread, balance;
        double hornTarget, drumTarget, hornTau = 0.5, drumTau = 2.5;
        int sub;                                                   // samples left in the current chunk
        // Per chunk, interpolated per sample.
        double hc, hs, hdc, hds, dc, ds, ddc, dds;                 // the rotors' phasors and their per-sample rotations
        float cosS, sinS, dCos, dSin, gh, gd, dGh, dGd, compH, compD, dCompH, dCompD;
        float cosS1 = 1, sinS1, gh1 = 1, gd1 = 1, compH1 = 1, compD1 = 1;

        /// <summary>Current speeds in turns per second (for the editor and the tests).</summary>
        public volatile float HornHz, DrumHz;

        public RotarySpeaker()
        {
            SetSampleRate(48000);
        }

        public void SetSampleRate(float rate)
        {
            sr = rate;
            baseDelay = (float)(2 * 2 * HornRadius / SpeedOfSound * rate) + 3f;
            hornAmp = (float)(HornRadius / SpeedOfSound * rate);
            drumAmp = (float)(DrumRadius / SpeedOfSound * rate);
            hornLine = new FracDelay((int)(baseDelay + 2 * hornAmp) + 8);
            drumLine = new FracDelay((int)(baseDelay + 2 * drumAmp) + 8);
            mix.Setup(0.03, rate);
            depth.Setup(0.03, rate);
            dop.Setup(0.03, rate);
            spread.Setup(0.03, rate);
            balance.Setup(0.03, rate);
            // Linkwitz-Riley: two Butterworth sections in a row.
            for (int i = 0; i < 2; i++)
            {
                low[i].Set(BiquadType.LowPass, Crossover, rate, 0.70710678);
                high[i].Set(BiquadType.HighPass, Crossover, rate, 0.70710678);
            }
            Reset();
        }

        public void Reset()
        {
            hornLine.Reset();
            drumLine.Reset();
            for (int i = 0; i < 2; i++) { low[i].Reset(); high[i].Reset(); }
            horn = new Rotor { Angle = 0, Omega = 0 };
            drum = new Rotor { Angle = 0.25, Omega = 0 };
            horn.Omega = hornTarget < 0 ? 0 : hornTarget;
            drum.Omega = drumTarget < 0 ? 0 : drumTarget;
            sub = 0;
            cosS1 = 1; sinS1 = 0; gh1 = gd1 = compH1 = compD1 = 1;
            HornHz = (float)horn.Omega;
            DrumHz = (float)drum.Omega;
            Snap();
        }

        public void Snap() { mix.V = mix.T; depth.V = depth.T; dop.V = dop.T; spread.V = spread.T; balance.V = balance.T; }

        /// <summary>The panel: on or off, the rotors' targets (a negative speed is the brake), their time constants and the controls.</summary>
        public void Set(bool on, double hornHz, double drumHz, double hornTauSec, double drumTauSec, float depthV, float dopplerV, float spreadV, float balanceV)
        {
            mix.T = on ? 1f : 0f;
            hornTarget = hornHz; drumTarget = drumHz;
            hornTau = Math.Max(0.01, hornTauSec); drumTau = Math.Max(0.01, drumTauSec);
            depth.T = depthV; dop.T = dopplerV; spread.T = spreadV; balance.T = balanceV;
        }

        /// <summary>Whether the speaker is on or still fading.</summary>
        public bool Engaged => mix.T > 0 || mix.V > 0;

        /// <summary>The rotors keep turning while nothing sounds: their speeds and angles move on by <paramref name="n"/> samples.</summary>
        public void Idle(int n)
        {
            double dt = n / (double)sr;
            horn.Advance(hornTarget, hornTau, dt);
            drum.Advance(drumTarget, drumTau, dt);
            HornHz = (float)horn.Omega;
            DrumHz = (float)drum.Omega;
        }

        static float HornPower(float d) => (1 - d) * (1 - d) + 2 * (1 - d) * d * 0.375f + d * d * 0.2734375f;

        static float DrumPower(float d) => (1 - d) * (1 - d) + 2 * (1 - d) * d * 0.5f + d * d * 0.375f;

        void StartChunk()
        {
            double dt = Chunk / (double)sr;
            double t0 = horn.Angle, u0 = drum.Angle;
            double turnedH = horn.Advance(hornTarget, hornTau, dt);
            double turnedD = drum.Advance(drumTarget, drumTau, dt);
            HornHz = (float)horn.Omega;
            DrumHz = (float)drum.Omega;
            hc = PortableMath.Cos(2 * Math.PI * t0); hs = PortableMath.Sin(2 * Math.PI * t0);
            dc = PortableMath.Cos(2 * Math.PI * u0); ds = PortableMath.Sin(2 * Math.PI * u0);
            double stepH = 2 * Math.PI * turnedH / Chunk, stepD = 2 * Math.PI * turnedD / Chunk;
            hdc = PortableMath.Cos(stepH); hds = PortableMath.Sin(stepH);
            ddc = PortableMath.Cos(stepD); dds = PortableMath.Sin(stepD);
            // The microphones' angle, the balance and the level compensation: now, and where they will be at the chunk's end.
            cosS = cosS1; sinS = sinS1; gh = gh1; gd = gd1; compH = compH1; compD = compD1;
            float s = spread.Advance(Chunk) * (MathF.PI / 2);
            cosS1 = (float)PortableMath.Cos(s); sinS1 = (float)PortableMath.Sin(s);
            float bal = balance.Advance(Chunk);
            gh1 = PortableMathF.Pow(10f, 6f * bal / 20f); gd1 = PortableMathF.Pow(10f, -6f * bal / 20f);
            float d = depth.V;
            compH1 = 1f / MathF.Sqrt(HornPower(d)); compD1 = 1f / MathF.Sqrt(DrumPower(d * 0.6f));
            float k = 1f / Chunk;
            dCos = (cosS1 - cosS) * k; dSin = (sinS1 - sinS) * k;
            dGh = (gh1 - gh) * k; dGd = (gd1 - gd) * k;
            dCompH = (compH1 - compH) * k; dCompD = (compD1 - compD) * k;
            sub = Chunk;
        }

        /// <summary>Mono in, stereo out (the dry sound in both channels when the speaker is off).</summary>
        public void Process(float[] x, float[] left, float[] right, int n)
        {
            for (int i = 0; i < n; i++)
            {
                if (sub == 0) StartChunk();
                sub--;
                float input = x[i];
                float lo = low[1].Process(low[0].Process(input));
                float hi = high[1].Process(high[0].Process(input));
                hornLine.Write(hi);
                drumLine.Write(lo);

                float m = mix.Next();
                float dep = depth.Next();
                float dp = dop.Next();
                // Microphone angles: left at -s, right at +s (cos s, ∓ sin s).
                cosS += dCos; sinS += dSin; gh += dGh; gd += dGd; compH += dCompH; compD += dCompD;
                float hcF = (float)hc, hsF = (float)hs, dcF = (float)dc, dsF = (float)ds;
                // cos(φ - θ) and sin(φ - θ), θ = -s (left) and +s (right).
                float cHL = hcF * cosS - hsF * sinS, sHL = hsF * cosS + hcF * sinS;
                float cHR = hcF * cosS + hsF * sinS, sHR = hsF * cosS - hcF * sinS;
                float cDL = dcF * cosS - dsF * sinS, sDL = dsF * cosS + dcF * sinS;
                float cDR = dcF * cosS + dsF * sinS, sDR = dsF * cosS - dcF * sinS;
                float dh = 1f - dep, dd = 1f - dep * 0.6f;
                float eh = 0.5f + 0.5f * cHL, er = 0.5f + 0.5f * cHR;
                float hL = (dh + dep * eh * eh) * hornLine.ReadCubic(baseDelay - dp * 2f * hornAmp * 0.5f * sHL);
                float hR = (dh + dep * er * er) * hornLine.ReadCubic(baseDelay - dp * 2f * hornAmp * 0.5f * sHR);
                float fl = 0.5f + 0.5f * cDL, fr = 0.5f + 0.5f * cDR;
                float dL = (dd + dep * 0.6f * fl) * drumLine.ReadCubic(baseDelay - dp * 2f * drumAmp * 0.5f * sDL);
                float dR = (dd + dep * 0.6f * fr) * drumLine.ReadCubic(baseDelay - dp * 2f * drumAmp * 0.5f * sDR);
                float wl = gh * compH * hL + gd * compD * dL;
                float wr = gh * compH * hR + gd * compD * dR;
                left[i] = input + (wl - input) * m;
                right[i] = input + (wr - input) * m;

                // The phasors turn on.
                double nhc = hc * hdc - hs * hds, nhs = hs * hdc + hc * hds;
                hc = nhc; hs = nhs;
                double ndc = dc * ddc - ds * dds, nds = ds * ddc + dc * dds;
                dc = ndc; ds = nds;
            }
            low[0].Flush(); low[1].Flush(); high[0].Flush(); high[1].Flush();
        }
    }
}
