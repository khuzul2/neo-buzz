#nullable disable
// Neo machines shared DSP (docs/specs/neo-vocoder.md §4): the filter bank of Neo Vocoder, a channel vocoder with 8 to 40 bands.
//  - The modulator (mono) goes through N band-pass filters, log-spaced from LowHz to HighHz (centres Low · (High / Low)^(i / (N − 1))).
//    Each band is a 4th-order band-pass: two identical RBJ band-pass biquads in series (peak gain 1 at the centre), whose -3 dB bandwidth is
//    the spacing between bands times Width (100 % = neighbours meet at their half-power points). The carrier goes through the same bank
//    with every centre moved by the formant shift (±12 semitones), so the carrier's bands move against the modulator's.
//  - Each modulator band's level is followed (rectified, then an attack and a release; the times are 10 to 90 % times of a step) and
//    scales the same band of the carrier. A carrier is levelled to the band: either against white noise (the gain of each band's filter
//    for white noise is computed exactly, so a white carrier gives every band the modulator's level), or, for an external carrier,
//    against its own level in the band (a fast-attack, slow-release follower), so a dark pad speaks as clearly as a bright saw.
//  - The odd bands (1st, 3rd ...) and the even bands go to separate sums, so the machine can spread them left and right.
//  - Freeze holds the followers. Filter settings that move (Low, High, Width, Formant) glide over 20 ms and the filters are designed again
//    every 16 samples while they move, so automation does not zipper. A new band count is the machine's business (it fades out, sets
//    Bands and fades in).
//  No latency. No allocation after construction. Portable maths only; band sums run in a fixed order.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    /// <summary>A level follower whose attack and release are the 10 to 90 % times of a step, in milliseconds.</summary>
    internal struct LevelFollower
    {
        public const double Ln9 = 2.1972245773362196;
        public float Level;
        float attack, release;

        /// <summary>The one-pole coefficient for a 10 to 90 % time of <paramref name="ms"/> milliseconds.</summary>
        public static float Coefficient(double ms, double sampleRate) =>
            (float)(1 - PortableMath.Exp(-Ln9 / (Math.Max(1e-3, ms) * 0.001 * sampleRate)));

        public void Set(double attackMs, double releaseMs, double sampleRate)
        {
            attack = Coefficient(attackMs, sampleRate);
            release = Coefficient(releaseMs, sampleRate);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Next(float x)
        {
            Level += (x > Level ? attack : release) * (x - Level);
            return Level;
        }

        public void Reset() { Level = 0; }
    }

    internal sealed class VocoderBank
    {
        public const int MaxBands = 40, MinBands = 8;
        /// <summary>Samples between two looks at the moving settings (and two designs of the filters while they move).</summary>
        public const int Control = 16;
        /// <summary>The alternating-sign sum of the bands measures +0.8 dB over a flat spectrum at the default width (neo-vocoder.md); this takes it back.</summary>
        public const double SumMakeup = 0.912;
        /// <summary>The top band has no upper neighbour to overlap with; its composite level reads -1.5 dB (measured, neo-vocoder.md), so it gets this.</summary>
        public const double TopTrim = 1.19;
        /// <summary>The level the white-noise carrier is made at: a white carrier of this RMS gives every band the modulator's level.</summary>
        public const float CarrierNominal = 0.25f;
        /// <summary>The follower reads |band|; a Gaussian band's RMS is this much more.</summary>
        const float RmsOfMeanAbs = 1.2533141f;
        /// <summary>The band-pass pair's -3 dB bandwidth against one stage's.</summary>
        const double CascadeBandwidth = 0.6435942529055827;

        double rate;
        // The filters: modulator and carrier coefficients per band (b0, a1, a2 of an RBJ band-pass: b = [b0 0 -b0]), two stages each, shared.
        readonly double[] mB0 = new double[MaxBands], mA1 = new double[MaxBands], mA2 = new double[MaxBands];
        readonly double[] cB0 = new double[MaxBands], cA1 = new double[MaxBands], cA2 = new double[MaxBands];
        readonly double[] mz = new double[MaxBands * 4], lz = new double[MaxBands * 4], rz = new double[MaxBands * 4];
        readonly float[] whiteGain = new float[MaxBands];     // 1 / the carrier band's RMS for unit white noise, times the nominal carrier's reciprocal
        readonly float[] pL = new float[MaxBands], pR = new float[MaxBands], sL = new float[MaxBands], sR = new float[MaxBands], pFast = new float[MaxBands];
        readonly float[] env = new float[MaxBands];
        bool captured, frozen, freezeSeen;
        int designTick;
        int armed;
        readonly float[] trim = new float[MaxBands], detect = new float[MaxBands], detect2 = new float[MaxBands], detectCoef = new float[MaxBands];
        readonly float[] outLevel = new float[MaxBands];
        readonly float[] centres = new float[MaxBands];
        float attackCoef = 0.01f, releaseCoef = 0.001f, pSlow, outSmooth;

        // Settings (any of them can change from block to block; the four that move the filters glide).
        public int Bands = 16;
        public double LowHz = 100, HighHz = 8000, WidthFraction = 1.0, ShiftSemitones;
        public double AttackMs = 3, ReleaseMs = 60;
        public bool Freeze;
        /// <summary>The carrier is levelled to the band's own level (an external carrier) instead of against white noise.</summary>
        public bool Equalise;

        double low, high, width, shift;           // what the filters are designed for, gliding toward the settings
        int builtBands;
        bool designed;
        double glideCoef;
        double builtLow, builtHigh, builtWidth, builtShift;
        double builtAttack = -1, builtRelease = -1;
        int sinceTick = Control;

        public VocoderBank(double sampleRate)
        {
            SetSampleRate(sampleRate);
        }

        /// <summary>Makes the bank for <paramref name="sampleRate"/> (everything it holds is cleared).</summary>
        public void SetSampleRate(double sampleRate)
        {
            rate = sampleRate;
            glideCoef = 1 - PortableMath.Exp(-Control / (0.02 * rate));
            pSlow = (float)(1 - PortableMath.Exp(-1.0 / (0.15 * rate)));
            outSmooth = (float)(1 - PortableMath.Exp(-1.0 / (0.01 * rate)));
            Reset();
        }

        /// <summary>Bands now in use (the settings' count once it has been applied).</summary>
        public int BuiltBands => builtBands;

        /// <summary>The modulator band levels (RMS, linear), for the editor and the tests; the first <see cref="BuiltBands"/> are valid.</summary>
        public float[] BandLevels => env;

        /// <summary>The level of each band of the output (smoothed, both channels), for the editor.</summary>
        public float[] OutputLevels => outLevel;

        /// <summary>The centre of band <paramref name="i"/> of the modulator bank as built now (Hz).</summary>
        public float Centre(int i) => centres[i];

        /// <summary>Band <paramref name="i"/>'s centre for the given settings (Hz): Low · (High / Low)^(i / (bands − 1)).</summary>
        public static double CentreFor(int bands, double low, double high, int i) =>
            bands <= 1 ? low : low * PortableMath.Pow(high / low, i / (double)(bands - 1));

        public void Reset()
        {
            Array.Clear(mz, 0, mz.Length);
            Array.Clear(lz, 0, lz.Length);
            Array.Clear(rz, 0, rz.Length);
            Array.Clear(env, 0, env.Length);
            Array.Clear(detect, 0, detect.Length); Array.Clear(detect2, 0, detect2.Length); captured = false; armed = 0; freezeSeen = false;
            Array.Clear(pL, 0, pL.Length);
            Array.Clear(pR, 0, pR.Length);
            Array.Clear(sL, 0, sL.Length);
            Array.Clear(sR, 0, sR.Length);
            Array.Clear(outLevel, 0, outLevel.Length);
            designed = false;
            builtAttack = builtRelease = -1;
            sinceTick = Control;
        }

        /// <summary>The loudest band level (for deciding that the machine can rest).</summary>
        public float MaxLevel
        {
            get
            {
                float m = 0;
                for (int i = 0; i < builtBands; i++) if (env[i] > m) m = env[i];
                return m;
            }
        }

        /// <summary>True while the filters are gliding toward new Low, High, Width or Formant settings.</summary>
        public bool Moving => designed && (low != LowHz || high != HighHz || width != WidthFraction || shift != ShiftSemitones);

        // ------------------------------------------------------------------------------------------------ design

        /// <summary>One RBJ band-pass stage (peak gain 1) at <paramref name="hz"/> with the quality <paramref name="q"/>; the band is two of them in series.</summary>
        static void Stage(double hz, double q, double sampleRate, out double b0, out double a1, out double a2)
        {
            double w0 = 2 * Math.PI * Math.Clamp(hz, 1.0, sampleRate * 0.49) / sampleRate;
            double alpha = PortableMath.Sin(w0) / (2 * q);
            double a0 = 1 + alpha;
            b0 = alpha / a0;
            a1 = -2 * PortableMath.Cos(w0) / a0;
            a2 = (1 - alpha) / a0;
        }

        // The white-noise power gain of the band: the variance of two band-pass stages in series driven by unit white noise,
        //   G = (1/π) ∫0^π |H(w)|⁴ dw, with |H|² = 1 / (1 + x²) and x = Q (Ω/Ω0 − Ω0/Ω), Ω = tan(w/2) (the RBJ band-pass is a bilinear transform
        //   prewarped at its centre). With x = tan θ this is
        //   G = (sin w0 / π) · 2Q ∫ cos²θ dθ / [ s (s − x cos w0) ],  s = sqrt(x² + 4Q²),  θ from −π/2 to π/2,
        // a smooth integral on a finite range, done by Simpson's rule on a fixed grid (64 panels) with the denominator in a form without cancellation.
        const int Panels = 64;
        static readonly double[] ThetaTan = new double[Panels + 1], ThetaCos2 = new double[Panels + 1];

        static VocoderBank()
        {
            for (int k = 0; k <= Panels; k++)
            {
                double th = -Math.PI / 2 + Math.PI * k / Panels;
                double c = PortableMath.Cos(th);
                ThetaCos2[k] = c * c;
                ThetaTan[k] = k == 0 || k == Panels ? 0 : PortableMath.Sin(th) / c;
            }
        }

        /// <summary>The power of two band-pass stages of quality <paramref name="q"/> centred at <paramref name="w0"/> radians, for unit white noise.</summary>
        public static double WhitePower(double q, double w0)
        {
            double c = PortableMath.Cos(w0), sn = PortableMath.Sin(w0), q4 = 4 * q * q, sum = 0;
            for (int k = 1; k < Panels; k++)
            {
                double x = ThetaTan[k], s = Math.Sqrt(x * x + q4);
                double xc = x * c;
                double d = xc >= 0 ? (q4 + x * x * sn * sn) / (s + xc) : s - xc;
                double f = ThetaCos2[k] / (s * d);
                sum += (k & 1) == 1 ? 4 * f : 2 * f;
            }
            double integral = sum * (Math.PI / Panels) / 3.0;
            double power = sn / Math.PI * 2 * q * integral;
            return power > 1e-30 ? power : 1e-30;
        }

        /// <summary>The white-noise power gain of one band as designed (for tests): the same Yule-Walker figure the bank levels its carrier with.</summary>
        public double WhitePowerOf(int band) => cWhite[band];

        readonly double[] cWhite = new double[MaxBands];

        void Design()
        {
            int bands = builtBands;
            double top = Math.Min(high, rate * 0.45);
            double lo = Math.Min(low, top * 0.5);
            double spacingOct = bands <= 1 ? 1 : PortableMath.Log2(top / lo) / (bands - 1);
            double bwOct = Math.Max(0.02, spacingOct * width) / CascadeBandwidth;
            // RBJ: BW (octaves) = 2 / ln 2 · asinh(1 / 2Q), so 1 / 2Q = sinh(BW ln 2 / 2).
            double h = bwOct * 0.34657359027997264;
            double sinh = 0.5 * (PortableMath.Exp(h) - PortableMath.Exp(-h));
            double q = Math.Clamp(1 / (2 * sinh), 0.3, 80.0);
            double ratio = PortableMath.Pow(2, shift / 12.0);
            double nominal = 1.0 / CarrierNominal;
            for (int i = 0; i < bands; i++)
            {
                double c = CentreFor(bands, lo, top, i);
                centres[i] = (float)c;
                Stage(c, q, rate, out mB0[i], out mA1[i], out mA2[i]);
                double cs = Math.Min(c * ratio, rate * 0.45);
                Stage(cs, q, rate, out cB0[i], out cA1[i], out cA2[i]);
                double power = WhitePower(q, 2 * Math.PI * Math.Clamp(cs, 1.0, rate * 0.49) / rate);
                cWhite[i] = power;
                trim[i] = (float)(i == bands - 1 ? TopTrim : 1.0);
                whiteGain[i] = (float)(nominal * RmsOfMeanAbs * SumMakeup * trim[i] / Math.Sqrt(power));
                // The detector: |band| is smoothed over about a third of the band's own period first (its ripple at twice the centre frequency is what an
                // attack much shorter than the period would follow, reading the peaks and not the level), then the attack and release follow that.
                detectCoef[i] = (float)(1 - PortableMath.Exp(-Math.PI * c / rate));
                // The level normaliser of an external carrier follows the band's power twice: over a few periods of the band (at least 4 ms) and over 150 ms;
                // the larger one counts, so a carrier that swells is levelled at once and one that drops is not boosted.
                double tau = Math.Max(0.004, 6.0 / cs);
                pFast[i] = (float)(1 - PortableMath.Exp(-1.0 / (tau * rate)));
            }
            builtLow = low; builtHigh = high; builtWidth = width; builtShift = shift;
            designed = true;
        }

        /// <summary>Looks at the settings (every <see cref="Control"/> samples): the glide of the four that move the filters, and the new follower times.</summary>
        void Tick(bool first)
        {
            if (first || !designed)
            {
                low = LowHz; high = HighHz; width = WidthFraction; shift = ShiftSemitones;
                builtBands = Math.Clamp(Bands, MinBands, MaxBands);
                Design();
            }
            else
            {
                bool changed = false;
                if (builtBands != Math.Clamp(Bands, MinBands, MaxBands))
                {
                    // A band count that changes: the bands under it are made anew, empty (the machine has faded out for it).
                    builtBands = Math.Clamp(Bands, MinBands, MaxBands);
                    Array.Clear(mz, 0, mz.Length);
                    Array.Clear(lz, 0, lz.Length);
                    Array.Clear(rz, 0, rz.Length);
                    Array.Clear(env, 0, env.Length);
                    Array.Clear(detect, 0, detect.Length); Array.Clear(detect2, 0, detect2.Length);
                    Array.Clear(pL, 0, pL.Length);
                    Array.Clear(pR, 0, pR.Length);
                    Array.Clear(sL, 0, sL.Length);
                    Array.Clear(sR, 0, sR.Length);
                    changed = true;
                }
                bool moved = Glide(ref low, LowHz);
                moved |= Glide(ref high, HighHz);
                moved |= Glide(ref width, WidthFraction);
                moved |= Glide(ref shift, ShiftSemitones);
                // A glide steps every 16 samples but the filters are redesigned every 64 (the design is most of the cost of a moving control), and at its end.
                if (moved && (++designTick >= 4 || !Moving)) { designTick = 0; changed = true; }
                if (changed) Design();
            }
            if (AttackMs != builtAttack || ReleaseMs != builtRelease)
            {
                attackCoef = LevelFollower.Coefficient(AttackMs, rate);
                releaseCoef = LevelFollower.Coefficient(ReleaseMs, rate);
                builtAttack = AttackMs; builtRelease = ReleaseMs;
            }
        }

        bool Glide(ref double current, double target)
        {
            if (current == target) return false;
            double d = target - current;
            if (Math.Abs(d) <= 1e-6 * Math.Max(1.0, Math.Abs(target))) current = target;
            else current += d * glideCoef;
            return true;
        }

        /// <summary>Designs the filters at the settings now (no glide): for a render's first block and the tests.</summary>
        public void Settle()
        {
            Tick(true);
        }

        // ------------------------------------------------------------------------------------------------ processing

        /// <summary>
        /// The carrier (<paramref name="carL"/>, <paramref name="carR"/>) cut into bands, each scaled by the modulator's level in the same band: the sums of the
        /// odd bands (1st, 3rd ...) and of the even bands, per channel, for <paramref name="n"/> samples. The modulator is <paramref name="mod"/> (mono).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(float[] mod, float[] carL, float[] carR, float[] oddL, float[] evenL, float[] oddR, float[] evenR, int n)
        {
            int done = 0;
            while (done < n)
            {
                if (sinceTick >= Control)
                {
                    Tick(false);
                    sinceTick = 0;
                }
                int m = Math.Min(Control - sinceTick, n - done);
                // Freeze holds the band levels; with nothing heard yet it waits for the first sound and holds what its first 150 ms showed.
                if (!Freeze) { captured = false; armed = 0; freezeSeen = false; }
                else if (!freezeSeen) { freezeSeen = true; captured = MaxLevel > 1e-3f; }   // switched on over a sound: hold it at once
                else if (!captured)
                {
                    if (MaxLevel > 1e-3f) armed += m;
                    if (armed >= (int)(0.15 * rate)) captured = true;
                }
                frozen = Freeze && captured;
                Run(mod, carL, carR, oddL, evenL, oddR, evenR, done, done + m);
                done += m;
                sinceTick += m;
            }
            Flush();
        }

        void Run(float[] mod, float[] carL, float[] carR, float[] oddL, float[] evenL, float[] oddR, float[] evenR, int from, int to)
        {
            int bands = builtBands;
            bool freeze = frozen, equalise = Equalise;
            float atk = attackCoef, rel = releaseCoef, ps = pSlow, os = outSmooth;
            const float floor = 1e-8f;
            for (int i = from; i < to; i++)
            {
                double xm = mod[i];
                float cl = carL[i], cr = carR[i];
                double xl = cl, xr = cr;
                float oL = 0, eL = 0, oR = 0, eR = 0;
                for (int b = 0; b < bands; b++)
                {
                    int s = b * 4;
                    double b0 = mB0[b], a1 = mA1[b], a2 = mA2[b];
                    // The modulator's band.
                    double y = b0 * xm + mz[s];
                    mz[s] = -a1 * y + mz[s + 1];
                    mz[s + 1] = -b0 * xm - a2 * y;
                    double y2 = b0 * y + mz[s + 2];
                    mz[s + 2] = -a1 * y2 + mz[s + 3];
                    mz[s + 3] = -b0 * y - a2 * y2;
                    float e = env[b];
                    if (!freeze)
                    {
                        float a = (float)y2;
                        float rect = a < 0 ? -a : a;
                        float dt = detect[b];
                        dt += detectCoef[b] * (rect - dt);
                        detect[b] = dt;
                        float d2 = detect2[b];
                        d2 += detectCoef[b] * (dt - d2);
                        detect2[b] = d2;
                        dt = d2;
                        e += (dt > e ? atk : rel) * (dt - e);
                        env[b] = e;
                    }
                    // The carrier's band, left and right.
                    double c0 = cB0[b], k1 = cA1[b], k2 = cA2[b];
                    double u = c0 * xl + lz[s];
                    lz[s] = -k1 * u + lz[s + 1];
                    lz[s + 1] = -c0 * xl - k2 * u;
                    double u2 = c0 * u + lz[s + 2];
                    lz[s + 2] = -k1 * u2 + lz[s + 3];
                    lz[s + 3] = -c0 * u - k2 * u2;
                    double v = c0 * xr + rz[s];
                    rz[s] = -k1 * v + rz[s + 1];
                    rz[s + 1] = -c0 * xr - k2 * v;
                    double v2 = c0 * v + rz[s + 2];
                    rz[s + 2] = -k1 * v2 + rz[s + 3];
                    rz[s + 3] = -c0 * v - k2 * v2;
                    float bl = (float)u2, br = (float)v2;
                    float gl, gr;
                    if (equalise)
                    {
                        float ql = bl * bl, qr = br * br;
                        float pf = pFast[b];
                        float p1 = pL[b] + pf * (ql - pL[b]), p2 = pR[b] + pf * (qr - pR[b]);
                        float s1 = sL[b] + ps * (ql - sL[b]), s2 = sR[b] + ps * (qr - sR[b]);
                        pL[b] = p1; pR[b] = p2; sL[b] = s1; sR[b] = s2;
                        float m1 = p1 > s1 ? p1 : s1, m2 = p2 > s2 ? p2 : s2;
                        float scale = e * RmsOfMeanAbs * trim[b];
                        gl = scale / MathF.Sqrt(m1 > floor ? m1 : floor);
                        gr = scale / MathF.Sqrt(m2 > floor ? m2 : floor);
                    }
                    else
                    {
                        float g = e * whiteGain[b];
                        gl = gr = g;
                    }
                    float ol = bl * gl, or = br * gr;
                    float mag = (ol < 0 ? -ol : ol) + (or < 0 ? -or : or);
                    outLevel[b] += os * (mag - outLevel[b]);
                    if ((b & 1) == 0) { oL += ol; oR += or; }
                    else { eL -= ol; eR -= or; }   // neighbouring bands add with opposite signs: their overlap then sums level-flat (about +-1 dB) instead of cancelling
                }
                oddL[i] = oL; evenL[i] = eL; oddR[i] = oR; evenR[i] = eR;
            }
        }

        /// <summary>Denormals can't ring out: the states are flushed at the end of every block.</summary>
        void Flush()
        {
            for (int i = 0; i < mz.Length; i++)
            {
                mz[i] = DspMath.Flush(mz[i]);
                lz[i] = DspMath.Flush(lz[i]);
                rz[i] = DspMath.Flush(rz[i]);
            }
            for (int i = 0; i < MaxBands; i++)
            {
                env[i] = DspMath.Flush(env[i]);
                detect[i] = DspMath.Flush(detect[i]);
                detect2[i] = DspMath.Flush(detect2[i]);
                pL[i] = DspMath.Flush(pL[i]);
                pR[i] = DspMath.Flush(pR[i]);
                sL[i] = DspMath.Flush(sL[i]);
                sR[i] = DspMath.Flush(sR[i]);
                outLevel[i] = DspMath.Flush(outLevel[i]);
            }
        }

        // ------------------------------------------------------------------------------------------------ for tests and editors

        /// <summary>The magnitude of the modulator's band <paramref name="band"/> (both stages) at <paramref name="hz"/>, as designed now.</summary>
        public double ModulatorResponse(int band, double hz) => Response(mB0[band], mA1[band], mA2[band], hz);

        /// <summary>The magnitude of the carrier's band <paramref name="band"/> (both stages) at <paramref name="hz"/>, as designed now.</summary>
        public double CarrierResponse(int band, double hz) => Response(cB0[band], cA1[band], cA2[band], hz);

        /// <summary>The complex response of the carrier's band <paramref name="band"/> (both stages) at <paramref name="hz"/>.</summary>
        public void CarrierComplexResponse(int band, double hz, out double re, out double im)
        {
            double w = 2 * Math.PI * hz / rate, b0 = cB0[band], a1 = cA1[band], a2 = cA2[band];
            double c1 = PortableMath.Cos(w), s1 = -PortableMath.Sin(w), c2 = PortableMath.Cos(2 * w), s2 = -PortableMath.Sin(2 * w);
            double nr = b0 * (1 - c2), ni = -b0 * s2;
            double dr = 1 + a1 * c1 + a2 * c2, di = a1 * s1 + a2 * s2;
            double den = dr * dr + di * di;
            double hr = (nr * dr + ni * di) / den, hi = (ni * dr - nr * di) / den;
            re = hr * hr - hi * hi;
            im = 2 * hr * hi;
        }

        double Response(double b0, double a1, double a2, double hz)
        {
            double w = 2 * Math.PI * hz / rate;
            double cr = PortableMath.Cos(w), ci = -PortableMath.Sin(w), c2r = PortableMath.Cos(2 * w), c2i = -PortableMath.Sin(2 * w);
            double nr = b0 * (1 - c2r), ni = -b0 * c2i;
            double dr = 1 + a1 * cr + a2 * c2r, di = a1 * ci + a2 * c2i;
            double one = Math.Sqrt((nr * nr + ni * ni) / (dr * dr + di * di));
            return one * one;
        }
    }
}
